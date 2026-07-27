using System.Net;
using System.Text.Json;

namespace pizzad;

public sealed class RemoteTranscriptionHealthService : BackgroundService
{
    private const string ServiceKey = "remote-transcription";
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private const int ConfirmedFailureCount = 2;
    private readonly EngineConfig _config;
    private readonly CredentialStore _credentials;
    private readonly EngineDatabase _database;
    private readonly ILogger<RemoteTranscriptionHealthService> _logger;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _gate = new();
    private RemoteTranscriptionHealthSnapshot _snapshot;
    private bool _outageEmailSent;
    private long? _openOutageId;

    public RemoteTranscriptionHealthService(EngineConfig config, CredentialStore credentials, EngineDatabase database, ILogger<RemoteTranscriptionHealthService> logger)
    {
        _config = config;
        _credentials = credentials;
        _database = database;
        _logger = logger;
        _snapshot = NewInitialSnapshot();
    }

    public RemoteTranscriptionHealthSnapshot GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    public void ReportRequestFailure(Exception exception)
    {
        if (IsConfigured())
            RecordFailure(exception.GetBaseException().Message);
    }

    public async Task WaitForAvailabilityAsync(CancellationToken ct)
    {
        while (GetSnapshot().OutageConfirmed)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RestoreOpenOutageAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);
            try
            {
                await Task.Delay(ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        if (!IsConfigured())
        {
            lock (_gate)
                _snapshot = NewInitialSnapshot();
            return;
        }

        try
        {
            var healthUrl = BuildHealthUrl(_config.Transcription.OpenAiBaseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.Transcription.OpenAiApiKey);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(ct);
            var reportedModel = ValidateHealthPayload(payload);
            await RecordSuccessAsync(reportedModel, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordFailure(ex.GetBaseException().Message);
            await PersistConfirmedOutageAsync(ct);
        }

        await SendAdministrativeNotificationIfNeededAsync(ct);
    }

    private async Task RecordSuccessAsync(string reportedModel, CancellationToken ct)
    {
        RemoteTranscriptionHealthSnapshot previous;
        lock (_gate)
        {
            previous = _snapshot;
            _snapshot = new RemoteTranscriptionHealthSnapshot(
                true, true, false, 0, DateTimeOffset.UtcNow, null,
                previous.OutageStartedAtUtc.HasValue ? DateTimeOffset.UtcNow : previous.LastRecoveredAtUtc,
                string.Empty, BuildHealthUrl(_config.Transcription.OpenAiBaseUrl));
        }

        if (!previous.OutageConfirmed)
            return;

        await _database.ResolveRemoteServiceOutageAsync(ServiceKey, reportedModel, DateTime.UtcNow, ct);
        _openOutageId = null;
        _logger.LogInformation("Remote transcription endpoint recovered after an outage that began at {OutageStart}", previous.OutageStartedAtUtc);
        if (_outageEmailSent)
            SendAdministrativeEmail("PizzaWave remote transcription recovered", $"The remote transcription endpoint <b>{WebUtility.HtmlEncode(previous.Endpoint)}</b> is reachable again.");
        _outageEmailSent = false;
    }

    private void RecordFailure(string error)
    {
        lock (_gate)
        {
            var failures = _snapshot.ConsecutiveFailures + 1;
            _snapshot = new RemoteTranscriptionHealthSnapshot(
                true, false, failures >= ConfirmedFailureCount, failures, _snapshot.LastSuccessAtUtc,
                _snapshot.OutageStartedAtUtc ?? DateTimeOffset.UtcNow, _snapshot.LastRecoveredAtUtc,
                Trim(error, 300), BuildHealthUrl(_config.Transcription.OpenAiBaseUrl));
        }
    }

    private async Task SendAdministrativeNotificationIfNeededAsync(CancellationToken ct)
    {
        var snapshot = GetSnapshot();
        var delay = TimeSpan.FromMinutes(Math.Clamp(_config.Alerts.AdministrativeOutageDelayMinutes, 1, 60));
        if (!snapshot.OutageConfirmed || !snapshot.OutageStartedAtUtc.HasValue || DateTimeOffset.UtcNow - snapshot.OutageStartedAtUtc < delay || _outageEmailSent)
            return;
        if (!_config.Alerts.AdministrativeEmailEnabled || string.IsNullOrWhiteSpace(_config.Alerts.AdministrativeEmailRecipients))
            return;

        var duration = DateTimeOffset.UtcNow - snapshot.OutageStartedAtUtc.Value;
        var body = $"PizzaWave cannot reach the remote transcription endpoint <b>{WebUtility.HtmlEncode(snapshot.Endpoint)}</b>. " +
                   $"The outage has lasted about {Math.Max(1, (int)duration.TotalMinutes)} minute(s). " +
                   $"Last error: <code>{WebUtility.HtmlEncode(snapshot.LastError)}</code>";
        if (SendAdministrativeEmail("PizzaWave remote transcription outage", body))
        {
            _outageEmailSent = true;
            if (_openOutageId.HasValue)
                await _database.MarkRemoteServiceOutageEmailSentAsync(_openOutageId.Value, ct);
        }
    }

    private async Task RestoreOpenOutageAsync(CancellationToken ct)
    {
        if (!IsConfigured())
            return;
        var outage = await _database.GetOpenRemoteServiceOutageAsync(ServiceKey, ct);
        if (outage == null)
            return;
        _openOutageId = outage.Id;
        _outageEmailSent = outage.AdministrativeEmailSent;
        lock (_gate)
        {
            _snapshot = new RemoteTranscriptionHealthSnapshot(
                true, false, true, Math.Max(ConfirmedFailureCount, outage.FailureCount), null,
                new DateTimeOffset(outage.StartedAtUtc), null, outage.LastError, outage.Endpoint);
        }
    }

    private async Task PersistConfirmedOutageAsync(CancellationToken ct)
    {
        var snapshot = GetSnapshot();
        if (!snapshot.OutageConfirmed || !snapshot.OutageStartedAtUtc.HasValue)
            return;
        var outage = await _database.UpsertRemoteServiceOutageAsync(
            ServiceKey,
            snapshot.Endpoint,
            ExpectedModel(),
            string.Empty,
            snapshot.OutageStartedAtUtc.Value.UtcDateTime,
            DateTime.UtcNow,
            snapshot.LastError,
            snapshot.ConsecutiveFailures,
            ct);
        _openOutageId = outage.Id;
        _outageEmailSent = outage.AdministrativeEmailSent;
    }

    private string ValidateHealthPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Remote transcription health response did not report ok=true.");
        var reportedModel = root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? model.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        var expectedModel = ExpectedModel();
        if (!string.IsNullOrWhiteSpace(expectedModel) &&
            !string.IsNullOrWhiteSpace(reportedModel) &&
            !string.Equals(expectedModel, reportedModel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Remote transcription health response reported model '{reportedModel}', but PizzaWave is configured for '{expectedModel}'.");
        return reportedModel;
    }

    private string ExpectedModel() => string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel)
        ? "whisper-1"
        : _config.Transcription.OpenAiModel.Trim();

    private bool SendAdministrativeEmail(string subject, string body)
    {
        if (!_config.Alerts.AdministrativeEmailEnabled)
            return false;
        try
        {
            foreach (var recipient in SplitRecipients(_config.Alerts.AdministrativeEmailRecipients))
                SmtpEmailSender.SendHtml(_config.Alerts, "PizzaWave administration", recipient, subject, body, _credentials.ResolveAlertEmailPassword());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send remote transcription administrative notification");
            return false;
        }
    }

    private bool IsConfigured() =>
        string.Equals(_config.Transcription.Provider, "remote-faster-whisper", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_config.Transcription.OpenAiBaseUrl);

    private RemoteTranscriptionHealthSnapshot NewInitialSnapshot() =>
        new(IsConfigured(), false, false, 0, null, null, null, string.Empty,
            IsConfigured() ? BuildHealthUrl(_config.Transcription.OpenAiBaseUrl) : string.Empty);

    private static string BuildHealthUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return baseUrl;
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/health").Uri.ToString();
    }

    private static IEnumerable<string> SplitRecipients(string value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Trim(string value, int max) => string.IsNullOrWhiteSpace(value) ? "Unknown endpoint failure" : value.Length <= max ? value : value[..max];

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}

public sealed record RemoteTranscriptionHealthSnapshot(
    bool Configured,
    bool Healthy,
    bool OutageConfirmed,
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? OutageStartedAtUtc,
    DateTimeOffset? LastRecoveredAtUtc,
    string LastError,
    string Endpoint);

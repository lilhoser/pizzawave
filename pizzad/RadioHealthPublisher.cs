using System.Text.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace pizzad;

public sealed class RadioHealthPublisherOptions
{
    public bool Enabled { get; set; }
    public string BrokerHost { get; set; } = "";
    public int BrokerPort { get; set; } = 8883;
    public string Username { get; set; } = "";
    public string PasswordFile { get; set; } = "";

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(BrokerHost) || BrokerHost.Contains('/')
            || BrokerPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(Username)
            || !Path.IsPathFullyQualified(PasswordFile))
            throw new InvalidOperationException("Invalid Radio publisher configuration.");
    }
}

public sealed class RadioHealthPublishSchedule
{
    private string? _lastAssessment;
    private DateTime _lastPublished;

    public bool IsDue(RadioHealthSummary summary, DateTime now)
        => AssessmentKey(summary) != _lastAssessment
            || now - _lastPublished >= TimeSpan.FromMinutes(5);

    public void Published(RadioHealthSummary summary, DateTime now)
    {
        _lastAssessment = AssessmentKey(summary);
        _lastPublished = now;
    }

    private static string AssessmentKey(RadioHealthSummary summary)
        => JsonSerializer.Serialize(new
        {
            summary.Condition, summary.Confidence, summary.Impact, summary.Coverage,
            summary.ExceptionCount,
            Exceptions = summary.Exceptions.Select(item => new { item.Id, item.Severity, item.Summary }),
            summary.Protection
        });
}

/// <summary>Optional background export. Failure cannot stop primary radio work.</summary>
public sealed class RadioHealthPublisher(RadioHealthSummaryService summaries,
    ILogger<RadioHealthPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never run credential/file/network work synchronously on host startup.
        await Task.Yield();
        RadioHealthPublisherOptions options;
        try
        {
            var path = Environment.GetEnvironmentVariable("PIZZAD_RADIO_HEALTH_REPORTING_CONFIG");
            if (string.IsNullOrWhiteSpace(path)) return;
            options = JsonSerializer.Deserialize<RadioHealthPublisherOptions>(
                await File.ReadAllTextAsync(path, stoppingToken), EngineConfig.JsonOptions())
                ?? throw new InvalidOperationException();
            options.Validate();
            if (!options.Enabled) return;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogError("Radio reporting disabled: invalid configuration ({ErrorType})", ex.GetType().Name);
            return;
        }

        using var client = new MqttFactory().CreateMqttClient();
        var schedule = new RadioHealthPublishSchedule();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                if (!client.IsConnected)
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        var mode = File.GetUnixFileMode(options.PasswordFile);
                        if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                            throw new InvalidOperationException("Publisher credential permissions are too broad.");
                    }
                    var password = (await File.ReadAllTextAsync(options.PasswordFile, deadline.Token)).Trim();
                    if (password.Length < 24) throw new InvalidOperationException("Invalid publisher credential.");
                    var connection = new MqttClientOptionsBuilder()
                        .WithClientId("whiteoak-radio-aor-ot")
                        .WithProtocolVersion(MqttProtocolVersion.V500)
                        .WithTcpServer(options.BrokerHost, options.BrokerPort)
                        .WithCredentials(options.Username, password)
                        .WithTlsOptions(tls => tls.UseTls()
                            // Existing private CA publishes no CRL/OCSP. Match its established
                            // chain/name/expiry validation; do not add an external revocation service.
                            .WithRevocationMode(X509RevocationMode.NoCheck)
                            .WithCertificateValidationHandler(validation =>
                        {
                            if (validation.SslPolicyErrors != SslPolicyErrors.None)
                                logger.LogWarning("Radio broker TLS rejected ({PolicyErrors}; {ChainStatus})",
                                    validation.SslPolicyErrors,
                                    string.Join(",", validation.Chain.ChainStatus.Select(item => item.Status)));
                            return validation.SslPolicyErrors == SslPolicyErrors.None;
                        }))
                        .WithCleanSession()
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                        .WithWillTopic(RadioHealthSummary.AvailabilityTopic)
                        .WithWillPayload("offline")
                        .WithWillRetain()
                        .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();
                    await client.ConnectAsync(connection, deadline.Token);
                    schedule = new RadioHealthPublishSchedule();
                }

                var summary = await summaries.GetAsync(deadline.Token);
                var now = DateTime.UtcNow;
                if (schedule.IsDue(summary, now))
                {
                    await PublishAsync(RadioHealthSummary.Topic, summary.Serialize(), deadline.Token);
                    await PublishAsync(RadioHealthSummary.AvailabilityTopic, "online"u8.ToArray(), deadline.Token);
                    schedule.Published(summary, now);
                    logger.LogInformation("Radio summary published ({Condition}; {Bytes} bytes)",
                        summary.Condition, summary.Serialize().Length);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Log neither credentials nor arbitrary service/broker exception text.
                logger.LogWarning("Radio reporting attempt failed ({ErrorType}); primary processing continues",
                    ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        // Disposal drops the reporting connection; the broker's Will handles availability.
        async Task PublishAsync(string topic, byte[] payload, CancellationToken ct)
        {
            var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload).WithRetainFlag()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
            if ((int)result.ReasonCode >= 128)
                throw new InvalidOperationException("Broker rejected the Radio report.");
        }
    }
}

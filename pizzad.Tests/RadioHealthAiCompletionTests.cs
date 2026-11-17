using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RadioHealthAiCompletionTests : IDisposable
{
    private const string Endpoint = "http://localhost:1234/v1/chat/completions";
    private const string Model = "qwen/qwen3.6-35b-a3b@q8_0";
    private static readonly DateTime Now = new(2026, 11, 17, 18, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-radio-ai-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"OK"}}]}""", true)]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"OK"}}]}""", false)]
    [InlineData("""{"choices":[]}""", false)]
    public async Task OnlyPublisherProbesWhenMatchingProductionEvidenceIsStale(string response, bool expectedProbeOk)
    {
        var config = new EngineConfig();
        config.Storage.DatabasePath = Path.Combine(_root, "engine.db");
        config.Storage.AudioRoot = Path.Combine(_root, "audio");
        config.AiInsights.Enabled = true;
        config.AiInsights.ExecutionMode = "lmlink";
        config.AiInsights.OpenAiBaseUrl = "http://localhost:1234/v1";
        config.AiInsights.OpenAiModel = Model;
        config.AiInsights.OpenAiApiKey = "test-token";
        var db = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await db.InitializeAsync(CancellationToken.None);
        await RecordAsync(Now.AddMinutes(-3));
        await RecordAsync(Now.AddMinutes(-1), model: "another-model");
        await RecordAsync(Now.AddMinutes(-1), endpoint: "http://localhost:9999/v1/chat/completions");
        await RecordAsync(Now.AddMinutes(-1), activity: "radio health probe");
        await RecordAsync(Now.AddMinutes(-1), success: false);

        var http = new ProbeHandler(response);
        using var client = new HttpClient(http);
        var monitor = new RadioAiCompletionMonitor(config, db, client);
        var fresh = await ObserveAsync(Now, allowProbe: true);
        Assert.Equal(Now.AddMinutes(-3), fresh.LastSuccessAt);
        Assert.Null(fresh.ProbeOk);
        Assert.Equal(0, http.Calls);

        var probeTime = Now.AddMinutes(3);
        await ObserveAsync(probeTime, allowProbe: false);
        Assert.Equal(0, http.Calls);

        var probed = await ObserveAsync(probeTime, allowProbe: true);
        Assert.Equal(expectedProbeOk, probed.ProbeOk);
        Assert.Equal(probeTime, probed.ProbeAt);
        Assert.Equal(fresh.LastSuccessAt, probed.LastSuccessAt);
        Assert.Equal(1, http.Calls);

        await ObserveAsync(probeTime.AddMinutes(1), allowProbe: true);
        Assert.Equal(1, http.Calls);
        await RecordAsync(probeTime.AddMinutes(4));
        var recovered = await ObserveAsync(probeTime.AddMinutes(5), allowProbe: true);
        Assert.Equal(probeTime.AddMinutes(4), recovered.LastSuccessAt);
        Assert.Equal(probeTime, recovered.ProbeAt);
        Assert.Equal(1, http.Calls);

        Task<RadioAiCompletionEvidence> ObserveAsync(DateTime now, bool allowProbe) =>
            monitor.GetAsync(now, allowProbe, CancellationToken.None);

        Task RecordAsync(DateTime timestamp, string model = Model, string endpoint = Endpoint,
            string activity = "automatic insights", bool success = true) =>
            db.AddLmUsageAsync(new(0, timestamp, activity, "chat.completions", success, "", endpoint,
                model, model, "stop", 1, 1, 1, 1, 2), CancellationToken.None);
    }

    private sealed class ProbeHandler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Endpoint, request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.Contains(Model, await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

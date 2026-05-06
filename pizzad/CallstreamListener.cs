using pizzalib;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace pizzad;

public sealed class CallstreamListener : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EnginePipeline _pipeline;
    private readonly ILogger<CallstreamListener> _logger;
    private readonly SemaphoreSlim _clientSlots;

    public CallstreamListener(EngineConfig config, EnginePipeline pipeline, ILogger<CallstreamListener> logger)
    {
        _config = config;
        _pipeline = pipeline;
        _logger = logger;
        _clientSlots = new SemaphoreSlim(Math.Max(1, _config.Ingest.MaxConcurrentClients));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = IPAddress.Parse(_config.Ingest.CallstreamBind);
        var endpoint = new IPEndPoint(address, _config.Ingest.CallstreamPort);
        var listener = new TcpListener(endpoint);
        var active = new ConcurrentDictionary<int, Task>();

        listener.Start();
        _logger.LogInformation("callstream ingest listening on {Endpoint}", endpoint);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                await _clientSlots.WaitAsync(stoppingToken);
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(client, stoppingToken);
                    }
                    finally
                    {
                        _clientSlots.Release();
                    }
                }, stoppingToken);
                active[task.Id] = task;
                _ = task.ContinueWith(_ => active.TryRemove(task.Id, out var _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(active.Values);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("callstream connection from {Remote}", remote);
        using (client)
        using (var stream = client.GetStream())
        {
            var settings = new pizzalib.Settings
            {
                analogSamplingRate = _config.Transcription.AnalogSampleRate,
                listenPort = _config.Ingest.CallstreamPort
            };
            var raw = new RawCallData(settings);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ok = await raw.ProcessClientData(stream, cts);
                if (!ok || ct.IsCancellationRequested)
                {
                    raw.Dispose();
                    return;
                }

                await _pipeline.IngestRawCallAsync(raw, imported: false, ct);
            }
            catch
            {
                raw.Dispose();
                throw;
            }
        }
    }
}

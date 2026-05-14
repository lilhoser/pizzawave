using System.Threading.Channels;

namespace pizzad;

public sealed class TranscriptPostProcessingService : BackgroundService
{
    private readonly Channel<TranscriptPostProcessingItem> _queue = Channel.CreateUnbounded<TranscriptPostProcessingItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly EngineDatabase _database;
    private readonly PoliceCodeService _policeCodes;
    private readonly TranscriptLocationService _locations;
    private readonly GeocodingService _geocoding;
    private readonly ILogger<TranscriptPostProcessingService> _logger;

    public TranscriptPostProcessingService(
        EngineDatabase database,
        PoliceCodeService policeCodes,
        TranscriptLocationService locations,
        GeocodingService geocoding,
        ILogger<TranscriptPostProcessingService> logger)
    {
        _database = database;
        _policeCodes = policeCodes;
        _locations = locations;
        _geocoding = geocoding;
        _logger = logger;
    }

    public void Enqueue(EngineCall call)
    {
        if (!_queue.Writer.TryWrite(new TranscriptPostProcessingItem(call)))
            _logger.LogWarning("Unable to enqueue transcript post-processing for call {CallId}", call.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var call = item.Call;
                var locations = _locations.ExtractCallLocations(call);
                await _database.ReplaceCallAnnotationsAsync(call.Id, _policeCodes.Detect(call.Transcription), stoppingToken);
                await _database.ReplaceCallLocationsAsync(call.Id, locations, stoppingToken);
                foreach (var location in locations)
                {
                    var area = _locations.ResolveAreaById(location.AreaId);
                    if (area != null)
                        await _geocoding.ResolveAsync(location.LocationText, area, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transcript post-processing failed for call {CallId}", item.Call.Id);
            }
        }
    }

    private sealed record TranscriptPostProcessingItem(EngineCall Call);
}

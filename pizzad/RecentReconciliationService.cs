namespace pizzad;

public sealed class RecentReconciliationService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly SftpImportService _sftp;
    private readonly LocalImportService _local;
    private readonly ILogger<RecentReconciliationService> _logger;

    public RecentReconciliationService(EngineConfig config, SftpImportService sftp, LocalImportService local, ILogger<RecentReconciliationService> logger)
    {
        _config = config;
        _sftp = sftp;
        _local = local;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var end = DateTime.Now;
                var start = end.AddHours(-Math.Max(1, _config.SftpImport.QuickImportMaxHours));
                var localJob = await _local.StartRecentReconciliationAsync(start, end, stoppingToken);
                if (localJob is not null)
                    _logger.LogInformation("Recent 48h local TR reconciliation queued job {JobId}", localJob.Id);

                if (_config.SftpImport.Enabled)
                {
                    var job = await _sftp.StartRecentReconciliationAsync(start, end, stoppingToken);
                    if (job is not null)
                        _logger.LogInformation("Recent 48h SFTP reconciliation queued job {JobId}", job.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recent 48h reconciliation did not start");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

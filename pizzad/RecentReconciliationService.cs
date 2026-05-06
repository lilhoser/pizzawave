namespace pizzad;

public sealed class RecentReconciliationService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly SftpImportService _sftp;
    private readonly ILogger<RecentReconciliationService> _logger;

    public RecentReconciliationService(EngineConfig config, SftpImportService sftp, ILogger<RecentReconciliationService> logger)
    {
        _config = config;
        _sftp = sftp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.SftpImport.Enabled)
                {
                    var end = DateTime.Now;
                    var start = end.AddHours(-Math.Max(1, _config.SftpImport.QuickImportMaxHours));
                    await _sftp.StartImportAsync(new SftpImportRequest(start, end, ConfirmLargeImport: false, CallCap: null, ByteCap: null), stoppingToken);
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

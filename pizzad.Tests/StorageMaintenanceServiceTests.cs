using Microsoft.Extensions.Logging.Abstractions;
using pizzad;

namespace pizzad.Tests;

public sealed class StorageMaintenanceServiceTests
{
    [Fact]
    public async Task DueRunOptimizesAndPrunesOldCompletedJobWithHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-storage-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio"),
                    AppDataRoot = root
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var oldJobId = await database.AddJobAsync(new JobDto
            {
                Type = "old_completed_work",
                Status = "completed",
                Total = 1,
                Completed = 1,
                Message = "old",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-31)
            }, CancellationToken.None);
            await database.AddJobLogAsync(oldJobId, "info", "old log", CancellationToken.None);

            var service = new StorageMaintenanceService(database, new EventStream(), NullLogger<StorageMaintenanceService>.Instance);
            var result = await service.RunIfDueAsync(DateTime.UtcNow, true, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("completed", result!.Status);
            Assert.Equal(2, result.Completed);
            Assert.Contains("pruned 1 old job(s) with 1 log row(s)", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(await database.GetJobAsync(oldJobId, CancellationToken.None));

            var logs = await database.ListJobLogsAsync(result.Id, 0, CancellationToken.None);
            Assert.Contains(logs, row => row.Text.Contains("Vacuum is not run", StringComparison.Ordinal));
            Assert.Contains(logs, row => row.Text.Contains("PRAGMA optimize completed", StringComparison.Ordinal));
            Assert.Null(await service.RunIfDueAsync(DateTime.UtcNow, false, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void StorageUiHasNoManualMaintenanceControls()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "pizzad", "web", "src", "App.tsx"));
        Assert.Contains("Automatic Maintenance", source, StringComparison.Ordinal);
        Assert.Contains("PizzaWave runs lightweight SQLite optimization", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Vacuum Database", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Optimize Statistics", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Prune Old Jobs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/system/storage/maintenance/", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, ".git")) &&
               !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

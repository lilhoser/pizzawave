using Microsoft.Extensions.Logging.Abstractions;
using pizzad;

namespace pizzad.Tests;

public sealed class IncidentDecisionPerformanceTests
{
    [Fact]
    public async Task PerformanceBucketsCoverCompleteWindowWithoutLoadingAuditMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-incident-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = root
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var end = DateTime.UtcNow;
            await AddAsync(database, end.AddHours(-3), true, "accepted:create incident");
            await AddAsync(database, end.AddMinutes(-90), false, "rejected:server safeguard");
            await AddAsync(database, end.AddMinutes(-30), true, "accepted:update incident");
            await AddAsync(database, end.AddHours(-8), false, "outside window");

            var report = await database.GetIncidentDecisionPerformanceAsync(end.AddHours(-4), end, 3600, CancellationToken.None);

            Assert.Equal(3, report.Total);
            Assert.Equal(2, report.Accepted);
            Assert.Equal(1, report.Rejected);
            Assert.True(report.Buckets.Count >= 4);
            Assert.Equal(report.Total, report.Buckets.Sum(row => row.Accepted + row.Rejected));
            Assert.All(report.Buckets, row => Assert.Equal(0, row.Start % 3600));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task DecisionChainsCountCandidatesOnceAndKeepTheirOrderedSteps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-incident-chains-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig { Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root } }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTime.UtcNow;
            await database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(0, now.AddMinutes(-3), "site-a", "new", "reconcile", true, "accepted:membership retained", .6, "[1]", "{}", "trace-a"), CancellationToken.None);
            await database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(0, now.AddMinutes(-2), "site-a", "incident-a", "upsert_incident", true, "accepted:create incident", .8, "[1,2]", "{}", "trace-a"), CancellationToken.None);
            await database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(0, now.AddMinutes(-1), "site-a", "new", "reject_incident", false, "rejected:no evidence", .2, "[3]", "{}", "trace-b"), CancellationToken.None);
            await database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(0, now.AddSeconds(-30), "site-a", "incident-a", "upsert_incident", true, "accepted:update incident", .9, "[1,2]", "{}", "trace-c"), CancellationToken.None);

            var report = await database.ListIncidentDecisionChainsAsync(now.AddHours(-1), now, 900, 1, 20, CancellationToken.None);

            Assert.Equal(3, report.TotalChains);
            Assert.Equal(2, report.TotalGroups);
            Assert.Equal(1, report.Created);
            Assert.Equal(1, report.Updated);
            Assert.Equal(1, report.Dropped);
            var created = Assert.Single(report.Chains, chain => chain.Outcome == "created");
            Assert.True(created.CompleteTrace);
            Assert.Equal(2, created.Steps.Count);
            Assert.Equal("reconcile", created.Steps[0].Operation);
            var persistedGroup = Assert.Single(report.Groups, group => group.CreatedCount == 1);
            Assert.Equal(2, persistedGroup.Chains.Count);
            Assert.Equal(1, persistedGroup.UpdatedCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task QualityCheckCountsAllIncidentActionsBeyondDisplayedReasonLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-quality-operations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = root
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTime.UtcNow;

            for (var reasonIndex = 0; reasonIndex < 31; reasonIndex++)
            {
                for (var occurrence = 0; occurrence < 3; occurrence++)
                    await AddAsync(database, now.AddMinutes(-10), true, $"accepted:diagnostic reason {reasonIndex}");
            }

            await AddAsync(database, now.AddMinutes(-9), true, "accepted:create incident");
            await AddAsync(database, now.AddMinutes(-8), true, "accepted:create incident; identity:new server-owned incident");
            await AddAsync(database, now.AddMinutes(-7), true, "accepted:update incident");
            await AddAsync(database, now.AddMinutes(-6), true, "accepted:server sibling merge repair");
            await AddAsync(database, now.AddMinutes(-5), false, "rejected:first reason");
            await AddAsync(database, now.AddMinutes(-4), false, "rejected:second reason");

            var start = new DateTimeOffset(now.AddHours(-1)).ToUnixTimeSeconds();
            var end = new DateTimeOffset(now.AddMinutes(1)).ToUnixTimeSeconds();
            var report = await database.GetQualityCheckSnapshotAsync(start, end, CancellationToken.None);

            Assert.Equal(30, report.IncidentOperations.Count);
            Assert.Equal(2, report.Incidents.Creates);
            Assert.Equal(2, report.Incidents.Updates);
            Assert.Equal(2, report.Incidents.Rejects);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static Task AddAsync(EngineDatabase database, DateTime timestampUtc, bool accepted, string reason) =>
        database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
            0,
            timestampUtc,
            "site-a",
            accepted ? "incident-a" : "new",
            accepted ? "upsert_incident" : "reject_incident",
            accepted,
            reason,
            accepted ? 0.8 : 0.4,
            "[1,2]",
            "{\"large\":\"metadata is not part of the aggregate query\"}"), CancellationToken.None);
}

using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TrLogServiceTests
{
    [Fact]
    public async Task RejectsInvalidRangesBeforeStartingJournalctl()
    {
        var service = new TrLogService(new EngineConfig(), NullLogger<TrLogService>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(100, 100, 250, null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUnsafeCursorTextBeforeStartingJournalctl()
    {
        var service = new TrLogService(new EngineConfig(), NullLogger<TrLogService>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(100, 200, 250, "cursor\n--unit=other", CancellationToken.None));
    }
}

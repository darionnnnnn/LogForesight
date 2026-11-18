using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>快照診斷的「本小時已記過的略過原因」記憶體集合與文件同一個保留期（第 77 輪 M-2）：站台長期不重啟時不得只增不減。</summary>
public sealed class PrtgSnapshotDiagnosticsRetentionTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void 超過保留期的時段從記憶體集合移除()
    {
        var store = new PrtgSnapshotDiagnosticsStore(_fx.Blob("prtg_snapshot_diagnostics_v1"), () => 2);
        var start = new DateTime(2026, 10, 1);

        // 連續 10 天、每小時兩種原因：保留 2 天時，集合最多是截止日起算的 3 個日曆日 × 24 小時 × 2 種
        for (var h = 0; h < 10 * 24; h++)
        {
            store.Record(start.AddHours(h), "skip", reason: "no-targets");
            store.Record(start.AddHours(h), "skip", reasonCode: "paused");
        }

        Assert.True(store.RecordedSkipCount <= 3 * 24 * 2, $"集合有 {store.RecordedSkipCount} 筆");
        Assert.True(store.RecordedSkipCount > 0);
    }

    [Fact]
    public void 同一小時同一原因只記一次()
    {
        var store = new PrtgSnapshotDiagnosticsStore(_fx.Blob("prtg_snapshot_diagnostics_v1"));
        var at = new DateTime(2026, 10, 1, 9, 5, 0);

        store.Record(at, "skip", reason: "no-targets");
        store.Record(at.AddMinutes(10), "skip", reason: "no-targets");

        Assert.Equal(1, store.RecordedSkipCount);
        Assert.Equal(1, store.ReadHours().Single().Skips);
    }
}

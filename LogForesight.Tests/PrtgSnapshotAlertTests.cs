using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 75 輪 G-4：保守策略下快照停止或逾時沒有成功取樣的警示判定與健康摘要。</summary>
public sealed class PrtgSnapshotAlertTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 20, 0, 0);
    private static readonly DateTime LongAgo = Now.AddDays(-2);

    private static PrtgSnapshotStatus Status(DateTime? lastSuccess = null, string? stop = null, int? targets = 10, DateTime? started = null) =>
        new(lastSuccess, 10, 15, 0, 0, null, stop, targets, started ?? LongAgo);

    [Fact]
    public void 未啟用_不警示()
    {
        var alert = PrtgSnapshotAlert.Evaluate(false, PrtgFetchStrategy.Conservative, Status(stop: "database-mismatch"), Now);
        Assert.False(alert.Any);
    }

    [Fact]
    public void 激進策略_即使停止或很久沒成功也不適用()
    {
        Assert.False(PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Aggressive, Status(stop: "database-mismatch"), Now).Any);
        Assert.False(PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Aggressive, Status(lastSuccess: LongAgo), Now).Any);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void 目標為空_不警示_維持no_targets(int? targets)
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: LongAgo, targets: targets), Now);
        Assert.False(alert.Any);
    }

    [Fact]
    public void 六小時內有成功_不警示()
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: Now.AddHours(-2)), Now);
        Assert.False(alert.Any);
    }

    [Theory]
    [InlineData(5 * 60 + 59, false)]
    [InlineData(6 * 60, false)]
    [InlineData(6 * 60 + 1, true)]
    public void 距離最近成功超過六小時才警示_兩側(int minutesSinceSuccess, bool warns)
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative,
            Status(lastSuccess: Now.AddMinutes(-minutesSinceSuccess)), Now);
        Assert.Equal(warns, alert.NoRecentSuccess);
        Assert.False(alert.Stopped);
    }

    [Fact]
    public void 待寫檔停止_立即警示_不必等六小時()
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative,
            Status(lastSuccess: Now.AddMinutes(-1), stop: "database-mismatch", started: Now.AddMinutes(-1)), Now);
        Assert.True(alert.Stopped);
        Assert.Equal("database-mismatch", alert.StopCode);
    }

    [Fact]
    public void 剛啟動未滿六小時_不警示_即使最近成功很久以前()
    {
        // 最近成功在 2 天前，但站台剛在 1 小時前啟動：起算點取晚者
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative,
            Status(lastSuccess: LongAgo, started: Now.AddHours(-1)), Now);
        Assert.False(alert.Any);
        // 沒有任何成功紀錄也一樣從啟動起算
        Assert.False(PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative,
            Status(lastSuccess: null, started: Now.AddHours(-5)), Now).Any);
        Assert.True(PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative,
            Status(lastSuccess: null, started: Now.AddHours(-7)), Now).NoRecentSuccess);
    }

    [Fact]
    public void 六種情境只有最後兩種警示()
    {
        var results = new[]
        {
            PrtgSnapshotAlert.Evaluate(false, PrtgFetchStrategy.Conservative, Status(stop: "x"), Now).Any,
            PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Aggressive, Status(lastSuccess: LongAgo), Now).Any,
            PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: LongAgo, targets: 0), Now).Any,
            PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: Now.AddHours(-1)), Now).Any,
            PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: LongAgo), Now).Any,
            PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(stop: "database-mismatch"), Now).Any
        };
        Assert.Equal(new[] { false, false, false, false, true, true }, results);
    }

    [Fact]
    public void 健康摘要_待寫檔停止_立即警示並指向鏡像狀態()
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(stop: "database-mismatch"), Now);

        var health = HealthService.BuildPrtgSnapshotHealth(true, Array.Empty<PrtgSnapshotHourDiagnostic>(), alert);

        Assert.True(health.Warning);
        Assert.Equal("stopped", health.State);
        Assert.Contains("鏡像狀態", health.Message);
        Assert.Contains("封存", health.Message);
    }

    [Fact]
    public void 健康摘要_逾時無成功_警示_並可併入整體狀態的Warning旗標()
    {
        var alert = PrtgSnapshotAlert.Evaluate(true, PrtgFetchStrategy.Conservative, Status(lastSuccess: Now.AddHours(-7)), Now);

        var health = HealthService.BuildPrtgSnapshotHealth(true, Array.Empty<PrtgSnapshotHourDiagnostic>(), alert);

        Assert.True(health.Warning);
        Assert.Equal("no-recent-success", health.State);
    }

    [Fact]
    public void 健康摘要_沒有警示時維持原行為()
    {
        var health = HealthService.BuildPrtgSnapshotHealth(true, Array.Empty<PrtgSnapshotHourDiagnostic>(), PrtgSnapshotAlert.None);
        Assert.False(health.Warning);
        Assert.Equal("unknown", health.State);
    }
}

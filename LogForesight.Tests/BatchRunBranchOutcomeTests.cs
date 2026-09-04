using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次D 階段3：執行紀錄的分路結構化欄位（本機／NetIQ／PRTG）單元測試。
/// 驗證舊格式 JSON-line 相容性、各路 outcome 寫入隔離性、0 值記錄與 DaysAnalyzed 語意維持。
/// </summary>
public class BatchRunBranchOutcomeTests
{
    [Fact]
    public void 舊格式JsonLine反序列化後新欄位為null且既有欄位正確()
    {
        using var fixture = new EfSqliteFixture();
        var runsLogStore = fixture.LogStore("runs");

        // 舊格式 JSON：完全不含 Local/Netiq/Prtg 分路結構化欄位
        const string oldJson = "{\"RunId\":42,\"HostName\":\"TEST-HOST\",\"StartedAt\":\"2026-09-04T08:00:00\"," +
                               "\"FinishedAt\":\"2026-09-04T08:15:00\",\"ExitCode\":0,\"AppVersion\":\"1.0.0\"," +
                               "\"Args\":\"--all\",\"DaysAnalyzed\":5,\"AiCalls\":12,\"AiFailures\":2," +
                               "\"WarnCount\":1,\"ErrorCount\":0,\"Trigger\":\"schedule\",\"Stopped\":false,\"JobType\":null}";

        runsLogStore.AppendLine(oldJson);

        var store = new BatchRunStore(runsLogStore, fixture.LogStore("run_logs"));
        var run = store.GetRun(42);

        Assert.NotNull(run);
        // 驗證既有欄位值正確
        Assert.Equal(42, run!.RunId);
        Assert.Equal("TEST-HOST", run.HostName);
        Assert.Equal(5, run.DaysAnalyzed);
        Assert.Equal(12, run.AiCalls);
        Assert.Equal(2, run.AiFailures);
        Assert.Equal(1, run.WarnCount);
        Assert.Equal(0, run.ErrorCount);
        Assert.Equal("schedule", run.Trigger);
        Assert.False(run.Stopped);
        Assert.Null(run.JobType);

        // 驗證九個新增的分路結構化欄位皆為 null
        Assert.Null(run.LocalDaysAnalyzed);
        Assert.Null(run.LocalDaysFailed);
        Assert.Null(run.NetiqDaysAnalyzed);
        Assert.Null(run.NetiqDaysFailed);
        Assert.Null(run.NetiqHostsSkipped);
        Assert.Null(run.PrtgOutcome);
        Assert.Null(run.PrtgSensorsFetched);
        Assert.Null(run.PrtgSensorsFailed);
        Assert.Null(run.PrtgTriggeredHosts);
    }

    [Fact]
    public void 三個Record方法各自只寫自己的欄位()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        // 僅呼叫 RecordLocalOutcome
        using (var recorder1 = new BatchRunRecorder(store, "host-1", Array.Empty<string>()))
        {
            recorder1.RecordLocalOutcome(3, 0);
            recorder1.Finish(0);
        }

        var run1 = store.GetRun(1);
        Assert.NotNull(run1);
        Assert.Equal(3, run1!.LocalDaysAnalyzed);
        Assert.Equal(0, run1.LocalDaysFailed);
        // NetIQ 與 PRTG 欄位仍為 null
        Assert.Null(run1.NetiqDaysAnalyzed);
        Assert.Null(run1.NetiqDaysFailed);
        Assert.Null(run1.NetiqHostsSkipped);
        Assert.Null(run1.PrtgOutcome);
        Assert.Null(run1.PrtgSensorsFetched);
        Assert.Null(run1.PrtgSensorsFailed);
        Assert.Null(run1.PrtgTriggeredHosts);

        // 三個方法都呼叫過之後，九個欄位皆有值
        using (var recorder2 = new BatchRunRecorder(store, "host-2", Array.Empty<string>()))
        {
            recorder2.RecordLocalOutcome(7, 0);
            recorder2.RecordNetiqOutcome(15, 2, 4);
            recorder2.RecordPrtgOutcome(BatchRun.PrtgOutcomePartial, 25, 3, 5);
            recorder2.Finish(0);
        }

        var run2 = store.GetRun(2);
        Assert.NotNull(run2);
        Assert.Equal(7, run2!.LocalDaysAnalyzed);
        Assert.Equal(0, run2.LocalDaysFailed);
        Assert.Equal(15, run2.NetiqDaysAnalyzed);
        Assert.Equal(2, run2.NetiqDaysFailed);
        Assert.Equal(4, run2.NetiqHostsSkipped);
        Assert.Equal(BatchRun.PrtgOutcomePartial, run2.PrtgOutcome);
        Assert.Equal(25, run2.PrtgSensorsFetched);
        Assert.Equal(3, run2.PrtgSensorsFailed);
        Assert.Equal(5, run2.PrtgTriggeredHosts);
    }

    [Fact]
    public void RecordPrtgOutcome的0值會被寫入而不是留null()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            recorder.RecordPrtgOutcome("success", 0, 0, 0);
            recorder.Finish(0);
        }

        var run = store.GetRun(1);
        Assert.NotNull(run);
        Assert.Equal("success", run!.PrtgOutcome);
        Assert.NotNull(run.PrtgSensorsFetched);
        Assert.Equal(0, run.PrtgSensorsFetched);
        Assert.NotNull(run.PrtgSensorsFailed);
        Assert.Equal(0, run.PrtgSensorsFailed);
        Assert.NotNull(run.PrtgTriggeredHosts);
        Assert.Equal(0, run.PrtgTriggeredHosts);
    }

    [Fact]
    public void DaysAnalyzed語意未變()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            recorder.RecordDayAnalyzed();
            recorder.RecordDayAnalyzed();

            // 呼叫三個 Record 方法
            recorder.RecordLocalOutcome(10, 0);
            recorder.RecordNetiqOutcome(20, 1, 3);
            recorder.RecordPrtgOutcome(BatchRun.PrtgOutcomeSuccess, 50, 0, 2);

            recorder.Finish(0);
        }

        var run = store.GetRun(1);
        Assert.NotNull(run);
        // DaysAnalyzed 仍只由 RecordDayAnalyzed() 累加為 2，不受 RecordLocalOutcome / RecordNetiqOutcome 影響
        Assert.Equal(2, run!.DaysAnalyzed);
        Assert.Equal(10, run.LocalDaysAnalyzed);
        Assert.Equal(20, run.NetiqDaysAnalyzed);
    }

    [Fact]
    public void 登記失敗時呼叫三個Record方法安全不拋例外()
    {
        var recorder = new BatchRunRecorder(null, "test-host", Array.Empty<string>());
        // 當 _store 為 null 時，直接 return，不拋例外
        recorder.RecordLocalOutcome(5, 0);
        recorder.RecordNetiqOutcome(10, 1, 2);
        recorder.RecordPrtgOutcome(BatchRun.PrtgOutcomeSuccess, 20, 0, 1);
    }

    [Fact]
    public void PrtgOutcome常數定義符合規格()
    {
        Assert.Equal("disabled", BatchRun.PrtgOutcomeDisabled);
        Assert.Equal("success", BatchRun.PrtgOutcomeSuccess);
        Assert.Equal("partial", BatchRun.PrtgOutcomePartial);
        Assert.Equal("failed", BatchRun.PrtgOutcomeFailed);
    }
}
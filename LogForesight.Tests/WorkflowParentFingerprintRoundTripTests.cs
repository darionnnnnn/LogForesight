using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 77 輪批次 G：工作流「PRTG 待補」追查。
/// 分析當下以記憶體裡的紀錄算父指紋；AI 排程與工作流復原以資料庫讀回的紀錄再算一次。
/// 兩者不同時，<see cref="HostDayWorkflowService.ParentSucceeded"/> 會把已完成的 PRTG 腿重設成等待。
/// </summary>
public sealed class WorkflowParentFingerprintRoundTripTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    private static DailyAnalysisRecord LowRiskRecord(long hostId, string host, DateTime day) => new()
    {
        LogSource = AnalysisLogSource.Netiq, HostId = hostId, Host = host, Date = day,
        RiskLevel = RiskLevels.Low, LatestNetiqAttemptStatus = "success", LatestNetiqAttemptAtUtc = DateTime.UtcNow,
        SuppressedTrendAlerts = new List<string> { "suppressed trend" },
        SuppressedCorrelationAlerts = new List<string> { "suppressed correlation" },
        TopIssues = new List<LogIssueSignature>
        {
            new()
            {
                LogName = "System", Source = "Disk", EventId = 7, EntryType = LogEntryType.Warning, Count = 3,
                SampleMessages = new List<string> { "The device has a bad block." },
                KeyDetails = "disk0",
                KeyAccounts = new List<string> { "svc-backup" },
                KeyIps = new List<string> { "10.0.0.8" }
            }
        }
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-g-" + Guid.NewGuid().ToString("N"));
    private HostDayWorkflowService Workflow() =>
        new(new HostDayWorkflowStore(new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root)));

    private (DailyAnalysisRecord AtAnalysis, DailyAnalysisRecord Persisted) Persist(long hostId, string risk)
    {
        var day = DateTime.Today.AddDays(-1);
        var key = new HostKey { HostId = hostId, HostName = "G-" + hostId };
        var store = new EfAnalysisRecordStore(_fixture.NewContext, "sqlite-in-memory", key);
        var record = LowRiskRecord(hostId, key.HostName, day);
        record.RiskLevel = risk;
        store.Append(record);
        return (record, store.GetOne(new[] { key }, day)!);
    }

    [Theory]
    [InlineData(RiskLevels.Low)]
    [InlineData(RiskLevels.Medium)]
    public void 寫入後讀回_父指紋與分析當下相同(string risk)
    {
        var (atAnalysis, persisted) = Persist(risk == RiskLevels.Low ? 7701 : 7702, risk);

        Assert.True(persisted.RecordId > 0);
        Assert.Equal(persisted.RecordId, atAnalysis.RecordId);
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), HostDayWorkflowFingerprint.ForParentRecord(persisted));
    }

    /// <summary>G-1／G-3：NetIQ 完成、PRTG 管線對帳記成功之後，AI 排程以讀回的紀錄還原，PRTG 腿不得退回等待</summary>
    [Fact]
    public void PRTG對帳成功後_AI排程還原不會把PRTG腿退回待補()
    {
        var (atAnalysis, persisted) = Persist(7703, RiskLevels.Low);
        var workflow = Workflow();
        workflow.ParentSucceeded(7703, atAnalysis.Host, atAnalysis.Date, "run-1", 3,
            HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), prtgEnabled: true, aiEnabled: true,
            parentRecordId: atAnalysis.RecordId);
        // 與 PrtgDailyPipeline 收尾對帳同一個呼叫形狀：該日評估完成＝成功、不帶證據指紋
        workflow.SetPrtg(7703, atAnalysis.Date, WorkflowLegState.Succeeded, evidenceReady: true);
        var before = workflow.Get(7703, atAnalysis.Date)!;

        workflow.RestoreFromRecord(persisted, aiEnabled: true);

        var after = workflow.Get(7703, atAnalysis.Date)!;
        Assert.Equal(WorkflowLegState.Succeeded, after.Prtg);
        Assert.Equal(before.DecisionVersion, after.DecisionVersion);
    }

    /// <summary>G-2：PRTG 當日沒評估完時仍是待補，還原不會把它變成成功</summary>
    [Fact]
    public void PRTG當日未評估_還原後仍為待補()
    {
        var (atAnalysis, persisted) = Persist(7704, RiskLevels.Low);
        var workflow = Workflow();
        workflow.ParentSucceeded(7704, atAnalysis.Host, atAnalysis.Date, "run-1", 3,
            HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), prtgEnabled: true, aiEnabled: true,
            parentRecordId: atAnalysis.RecordId);
        workflow.SetPrtg(7704, atAnalysis.Date, WorkflowLegState.Waiting, evidenceReady: false, failure: "prtg-day-not-evaluated");

        workflow.RestoreFromRecord(persisted, aiEnabled: true);

        Assert.Equal(WorkflowLegState.Waiting, workflow.Get(7704, atAnalysis.Date)!.Prtg);
    }

    /// <summary>邊界另一側：父紀錄的內容真的變了（重新分析），PRTG 腿照舊要重來</summary>
    [Fact]
    public void 父紀錄內容真的改變_PRTG腿仍重設為待補()
    {
        var (atAnalysis, persisted) = Persist(7705, RiskLevels.Low);
        var workflow = Workflow();
        workflow.ParentSucceeded(7705, atAnalysis.Host, atAnalysis.Date, "run-1", 3,
            HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), prtgEnabled: true, aiEnabled: true,
            parentRecordId: atAnalysis.RecordId);
        workflow.SetPrtg(7705, atAnalysis.Date, WorkflowLegState.Succeeded, evidenceReady: true);

        persisted.ErrorCount += 1;
        workflow.RestoreFromRecord(persisted, aiEnabled: true);

        Assert.Equal(WorkflowLegState.Waiting, workflow.Get(7705, atAnalysis.Date)!.Prtg);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

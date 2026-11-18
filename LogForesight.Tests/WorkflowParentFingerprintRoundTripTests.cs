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

    /// <summary>
    /// 低風險日寫入真實 store 後讀回：已抑制的告警與告警的結構化對應都還在
    /// （抑制後那天通常就是低風險日，這幾欄在精簡時被丟掉的話，詳情頁「已抑制的告警」在最需要的日子是空的）。
    /// </summary>
    [Fact]
    public void 低風險日寫入後讀回_已抑制的告警與告警對應仍在()
    {
        var day = DateTime.Today.AddDays(-1);
        var key = new HostKey { HostId = 7740, HostName = "G-7740" };
        var store = new EfAnalysisRecordStore(_fixture.NewContext, "sqlite-in-memory", key);
        var record = LowRiskRecord(7740, key.HostName, day);
        record.TrendAlerts = new List<string> { "trend" };
        record.TrendAlertRefs = new List<TrendAlertRef> { new() { Text = "trend", IssueKey = "System|Disk|7|Warning" } };
        record.CorrelationAlertRefs = new List<CorrelationAlertRef> { new() { Text = "corr", PatternId = "pattern-1" } };
        store.Append(record);

        var persisted = store.GetOne(new[] { key }, day)!;

        Assert.Equal(new[] { "suppressed trend" }, persisted.SuppressedTrendAlerts);
        Assert.Equal(new[] { "suppressed correlation" }, persisted.SuppressedCorrelationAlerts);
        Assert.Equal("System|Disk|7|Warning", Assert.Single(persisted.TrendAlertRefs).IssueKey);
        Assert.Equal("pattern-1", Assert.Single(persisted.CorrelationAlertRefs).PatternId);
        Assert.Empty(Assert.Single(persisted.TopIssues).SampleMessages);   // 仍是精簡過的紀錄
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(record), HostDayWorkflowFingerprint.ForParentRecord(persisted));
    }

    /// <summary>
    /// 風險等級在寫入之後跨界（體檢區 1 探針重現）：本機路徑先追加 PRTG finding（低→中）再算父指紋，
    /// 記憶體是完整內容、資料庫是寫入當下精簡過的內容；指紋不得因「當下風險是不是低」而不同。
    /// 反方向（中→低，例如事後的風險複核）同理：資料庫留著完整內容、風險已是低。
    /// </summary>
    [Theory]
    [InlineData(RiskLevels.Low, RiskLevels.Medium)]
    [InlineData(RiskLevels.Low, RiskLevels.High)]
    [InlineData(RiskLevels.Medium, RiskLevels.Low)]
    public void 寫入後風險等級跨界_父指紋不變_PRTG腿不被重設(string storedAs, string laterRisk)
    {
        var hostId = 7710 + Array.IndexOf(new[] { RiskLevels.Medium, RiskLevels.High, RiskLevels.Low }, laterRisk)
            + (storedAs == RiskLevels.Medium ? 10 : 0);
        var (atAnalysis, persisted) = Persist(hostId, storedAs);
        // 兩邊同步變更風險等級：記憶體物件內容不變（完整），資料庫讀回的內容維持寫入當下的形狀
        atAnalysis.RiskLevel = laterRisk;
        persisted.RiskLevel = laterRisk;

        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), HostDayWorkflowFingerprint.ForParentRecord(persisted));

        var workflow = Workflow();
        workflow.ParentSucceeded(hostId, atAnalysis.Host, atAnalysis.Date, "run-1", 3,
            HostDayWorkflowFingerprint.ForParentRecord(atAnalysis), prtgEnabled: true, aiEnabled: true,
            parentRecordId: atAnalysis.RecordId);
        workflow.SetPrtg(hostId, atAnalysis.Date, WorkflowLegState.Succeeded, evidenceReady: true);
        var before = workflow.Get(hostId, atAnalysis.Date)!.DecisionVersion;

        workflow.RestoreFromRecord(persisted, aiEnabled: true);

        var after = workflow.Get(hostId, atAnalysis.Date)!;
        Assert.Equal(WorkflowLegState.Succeeded, after.Prtg);
        Assert.Equal(before, after.DecisionVersion);
    }

    /// <summary>
    /// 升級相容：升級前存的是舊算法（完整內容）的指紋。中風險以上的列資料庫內容就是完整的，
    /// 還原時要認得舊指紋、不重設任何腿，並把存的指紋換成新的（第二次還原走一般比對）。
    /// </summary>
    [Fact]
    public void 升級前的舊指紋_中風險列還原時不重設並換成新指紋()
    {
        var (atAnalysis, persisted) = Persist(7730, RiskLevels.Medium);
        var legacy = HostDayWorkflowFingerprint.LegacyParentFingerprint(atAnalysis);
        Assert.NotEqual(legacy, HostDayWorkflowFingerprint.ForParentRecord(atAnalysis));
        var workflow = Workflow();
        workflow.ParentSucceeded(7730, atAnalysis.Host, atAnalysis.Date, "run-1", 3, legacy,
            prtgEnabled: true, aiEnabled: true, parentRecordId: atAnalysis.RecordId);
        workflow.SetPrtg(7730, atAnalysis.Date, WorkflowLegState.Succeeded, evidenceReady: true);
        var before = workflow.Get(7730, atAnalysis.Date)!.DecisionVersion;

        workflow.RestoreFromRecord(persisted, aiEnabled: true);

        var after = workflow.Get(7730, atAnalysis.Date)!;
        Assert.Equal(WorkflowLegState.Succeeded, after.Prtg);
        Assert.Equal(before, after.DecisionVersion);
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(persisted), after.DecisionFingerprint);
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

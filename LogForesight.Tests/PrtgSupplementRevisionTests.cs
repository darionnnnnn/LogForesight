using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSupplementRevisionTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly DateTime _day = DateTime.Today.AddDays(-1);
    private EfAnalysisRecordStore Store => new(_fx.NewContext, "fixture");
    private LogIssueSignature Finding(string generation = "resource") => PrtgFindingMapper.ToSignature(
        new(1, 123, "down", "trusted failure", 60,
            new KnownIssueRule { Id = "down", Severity = IssueSeverity.High, ElevatesDayRisk = true })
        { SourceGeneration = "source", ResourceGeneration = generation, IncidentStartedAt = new DateTimeOffset(_day) }, _day);
    private void Parent() => Store.Append(new DailyAnalysisRecord { HostId = 11, Host = "H", Date = _day,
        LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success", AiAnalyzed = false, RiskLevel = RiskLevels.Low });

    [Fact]
    public async Task 資料庫修訂擋獨立Context過期JSON回寫_同步非同步皆有保護()
    {
        Parent();
        using var stale = _fx.NewContext();
        var old = Assert.Single(stale.DailyRecords);
        Store.AttachPrtgFindings(11, _day, [Finding()], new HashSet<string>(), out _);
        old.ContentJson = "{}";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        using var another = _fx.NewContext();
        var older = Assert.Single(another.DailyRecords);
        Store.ReconcilePrtgStateFindings(11, _day, new Dictionary<long, string> { [123] = "resource" }, "source", new HashSet<string>());
        older.ContentJson = "{}";
        Assert.Throws<DbUpdateConcurrencyException>(() => another.SaveChanges());
        Assert.Empty(Assert.Single(Store.ReadRecent(_day, 1)).TopIssues);
    }

    [Fact]
    public void 撤回可信狀態同步移除失效佐證_保留Netiq關聯()
    {
        var parent = new DailyAnalysisRecord { HostId = 11, Host = "H", Date = _day,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success", AiAnalyzed = false,
            RiskLevel = RiskLevels.Low, TopIssues = [new() { Source = "disk", EventId = 153, LogName = "System", Count = 3 }],
            CorrelationAlerts = ["保留原事件關聯"] };
        Store.Append(parent);
        var finding = Finding(); finding.PrtgSensorCategory = PrtgSensorCategories.Hardware;
        Store.AttachPrtgFindings(11, _day, [finding], new HashSet<string>(), out _);
        Assert.Equal(2, Assert.Single(Store.ReadRecent(_day, 1)).CorrelationAlerts.Count);
        Store.ReconcilePrtgStateFindings(11, _day, new Dictionary<long, string> { [123] = "resource" }, "source", new HashSet<string>());
        var current = Assert.Single(Store.ReadRecent(_day, 1));
        Assert.Equal("保留原事件關聯", Assert.Single(current.CorrelationAlerts));
        Assert.Empty(current.CorrelationAlertRefs);
    }

    [Fact]
    public void 舊PRTG案件標籤顯示不帶世代鍵_未知或人工文字保留()
    {
        Assert.Equal("PRTG 持續故障（感測器 #123）", PrtgFindingMapper.DisplayStoredLabel("PRTG:down（prtg:down:123:source:resource）"));
        Assert.Equal("人工問題說明", PrtgFindingMapper.DisplayStoredLabel("人工問題說明"));
        Assert.Equal("PRTG:down（prtg:down:123）", PrtgFindingMapper.DisplayStoredLabel("PRTG:down（prtg:down:123）"));
    }

    [Fact]
    public void 完整重評撤回舊判定_保留Netiq風險下限及判定版本_重試冪等()
    {
        Parent(); var finding = Finding();
        Assert.True(Store.AttachPrtgFindings(11, _day, [finding], new HashSet<string>(), out _));
        Assert.Equal(RiskLevels.High, Assert.Single(Store.ReadRecent(_day, 1)).RiskLevel);
        Assert.False(Store.ReconcilePrtgStateFindings(11, _day, new Dictionary<long,string> { [123] = "other" }, "source", new HashSet<string>()));
        Assert.True(Store.ReconcilePrtgStateFindings(11, _day, new Dictionary<long,string> { [123] = "resource" }, "source", new HashSet<string>()));
        var current = Assert.Single(Store.ReadRecent(_day, 1));
        Assert.Empty(current.TopIssues); Assert.Equal(RiskLevels.Low, current.RiskLevel);
        Assert.False(Store.ReconcilePrtgStateFindings(11, _day, new Dictionary<long,string> { [123] = "resource" }, "source", new HashSet<string>()));
    }

    [Fact]
    public void AI執行期間新證據追加_舊結果不得覆蓋現況且保留重新補跑()
    {
        Parent(); var input = PrtgFindingMapper.Fingerprint([]);
        Store.AttachPrtgFindings(11, _day, [Finding()], new HashSet<string>(), out _, aiConfigured: true);
        Store.AttachAiResult(_day, new("stale headline", "stale summary", "", "", RiskLevels.Low, null,
            true, 0, [], null, [], InputPrtgFingerprint: input));
        var current = Assert.Single(Store.ReadRecent(_day, 1));
        Assert.NotEqual("stale headline", current.Headline); Assert.True(current.AiPending);
        Assert.Equal(RiskLevels.High, current.RiskLevel);
    }
    [Fact]
    public void 磁碟撤回須明確完成磁碟重評_狀態重評不會誤撤回()
    {
        Parent();
        var finding = PrtgFindingMapper.ToSignature(new(1, 123, PrtgDiskRuleDecision.RuleCode, "capacity", 60,
            new KnownIssueRule { Id = "disk", Severity = IssueSeverity.High, ElevatesDayRisk = true })
        { SourceGeneration = "source", ResourceGeneration = "resource", IncidentStartedAt = new DateTimeOffset(_day) }, _day);
        Store.AttachPrtgFindings(11, _day, [finding], new HashSet<string>(), out _);
        var resources = new Dictionary<long, string> { [123] = "resource" };
        Assert.False(Store.ReconcilePrtgStateFindings(11, _day, resources, "source", new HashSet<string>()));
        Assert.True(Store.ReconcilePrtgStateFindings(11, _day, resources, "source", new HashSet<string>(),
            new HashSet<string> { PrtgDiskRuleDecision.RuleCode }));
        Assert.Equal(RiskLevels.Low, Assert.Single(Store.ReadRecent(_day, 1)).RiskLevel);
    }

    [Fact]
    public void 批次追溯不破壞相同內容去重_修訂完成後舊版本可清理()
    {
        var observations = new LogForesight.Core.Persistence.Sql.EfPrtgObservationStore(_fx.NewContext);
        var rule = new KnownIssueRule { Id = "down", Severity = IssueSeverity.High, ElevatesDayRisk = true };
        var finding = new PrtgFinding(1, 123, "down", "original", 60, rule)
        { SourceGeneration = "source", ResourceGeneration = "resource", IncidentStartedAt = new DateTimeOffset(_day) };
        Assert.Equal(1, observations.Capture(11, _day, "r1", [(finding, PrtgFindingMapper.ToSignature(finding, _day))], runId: 123));
        Assert.Equal(0, observations.Capture(11, _day, "r1", [(finding, PrtgFindingMapper.ToSignature(finding, _day))], runId: 456));
        finding = finding with { Detail = "revised" };
        observations.Capture(11, _day, "r1", [(finding, PrtgFindingMapper.ToSignature(finding, _day))], runId: 789);
        using (var db = _fx.NewContext())
        {
            var old = Assert.Single(db.PrtgObservations.Where(o => o.ActiveKey == null));
            Assert.Equal(123, old.RunId); Assert.Equal("superseded", old.SupplementStatus);
        }
        Assert.Equal(1, observations.PruneCompleted(30, DateTime.UtcNow.AddDays(31)));
        using var current = _fx.NewContext();
        Assert.Equal(789, Assert.Single(current.PrtgObservations).RunId);
    }

    public void Dispose() => _fx.Dispose();
}

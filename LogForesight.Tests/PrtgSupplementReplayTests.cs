using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSupplementReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-replay-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly DateTime _day = DateTime.Today.AddDays(-1);
    public PrtgSupplementReplayTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        _hosts = new(_backend.Blob("hosts"));
        for (var i = 1; i <= 4; i++) _hosts.Upsert(new WebHost { HostName = "H" + i, Source = "netiq", Active = true });
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        { s.PrtgEnabled = true; s.PrtgUrl = "https://fixture.example"; s.AutoDispatchEnabled = true; });
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.Revision = "r1"; p.CoreSystemId = "core"; p.SourceGeneration = "source";
            p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor("https://fixture.example");
            p.ValidFrom = DateTimeOffset.Now.AddDays(-3); p.HostIds = [1, 2, 3, 4]; p.SensorIds = [102, 103];
        });
    }
    private PrtgSupplementReplay Replay()
    {
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        return new(_backend, _hosts, cases, orders, new Candidates());
    }
    private sealed class Candidates : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            PoolMemberCount = 1, ActivePoolMemberCount = 1,
            ByUserId = new Dictionary<long, DispatchCandidate>
            { [10] = new() { UserId = 10, Account = "handler", InPool = true, VisibleHostIds = new HashSet<long> { 1, 2, 3, 4 } } }
        };
    }
    private void Parent(long id, AnalysisLogSource source = AnalysisLogSource.Netiq, string? attempt = "success", bool issue = false) =>
        _backend.RecordStore(new HostKey { HostId = id, HostName = "H" + id }).Append(new()
        {
            HostId = id, Host = "H" + id, Date = _day, LogSource = source,
            LatestNetiqAttemptStatus = attempt, RiskLevel = RiskLevels.Low,
            TopIssues = issue ? [new() { LogName = "System", Source = "Disk", EventId = 153, EventKey = "netiq-disk", Count = 1, Severity = IssueSeverity.High }] : []
        });
    private void Observe(long id)
    {
        var rule = new KnownIssueRule { Id = "down", Platform = "prtg", Enabled = true, Severity = IssueSeverity.High, ElevatesDayRisk = true };
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent { Rules = [rule] });
        var finding = new PrtgFinding(1, 100 + id, "down", "confirmed sensor failure", 60, rule)
        { SourceGeneration = "source", ResourceGeneration = "sensor-" + id, IncidentStartedAt = new DateTimeOffset(_day) };
        new EfPrtgObservationStore(_backend.CreateContext).Capture(id, _day, "r1", [(finding, PrtgFindingMapper.ToSignature(finding, _day))]);
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + (100 + id))).Update(e =>
        {
            e.SensorId = 100 + id; e.HostId = id; e.SourceGeneration = "source"; e.ResourceGeneration = "sensor-" + id;
            e.ValidFrom = new DateTimeOffset(_day.AddHours(-1));
            e.Accept(e.ValidFrom, new DateTimeOffset(_day.AddDays(1)),
                [new(e.SensorId, e.ValidFrom, "Down", e.SourceGeneration, e.ResourceGeneration)]);
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 等待父紀錄時規則刪除或門檻更改_不能補掛舊規則的高風險(bool remove)
    {
        Observe(2); Parent(2);
        var store = new KnownIssueRuleStore(_backend.Blob("rules"));
        store.Save(new RuleFileContent { Rules = remove ? [] : [new KnownIssueRule { Id = "down", Platform = "prtg", Enabled = true,
            Severity = IssueSeverity.High, ElevatesDayRisk = true, PrtgThreshold = 999 }] });
        Assert.Equal(0, Replay().RunBatch());
        using var db = _backend.CreateContext();
        Assert.Empty(db.TopIssues); Assert.Empty(db.IssueCases);
        Assert.Equal("rules-changed", Assert.Single(db.PrtgObservations).SupplementStatus);
    }

    [Fact]
    public void 四主機矩陣_缺Netiq不建立正式資料_晚到零事件成功才追加_重啟五次不重派()
    {
        Parent(1, issue: true); Parent(3, issue: true); Observe(2); Observe(3);
        Replay().RunBatch();
        using (var db = _backend.CreateContext())
        {
            Assert.Equal(2, db.DailyRecords.Count()); Assert.Equal(3, db.TopIssues.Count());
            Assert.Single(db.IssueCases); Assert.Single(db.WorkOrders);
            Assert.DoesNotContain(db.IssueCases, c => c.HostName == "H2");
        }
        Parent(2); // 明確成功且零事件，仍是 NetIQ 基礎。
        for (var i = 0; i < 5; i++) Replay().RunBatch();
        using (var db = _backend.CreateContext())
        {
            Assert.Equal(3, db.DailyRecords.Count()); Assert.Equal(4, db.TopIssues.Count());
            Assert.Equal(2, db.IssueCases.Count()); Assert.Single(db.WorkOrders);
            Assert.All(db.PrtgObservations, o => Assert.Equal("applied", o.SupplementStatus));
            Assert.All(db.IssueCases, c => Assert.Contains("confirmed sensor failure", c.PrtgEvidenceJson));
        }
    }
    [Theory]
    [InlineData(AnalysisLogSource.Local, "success")]
    [InlineData(AnalysisLogSource.Unknown, "success")]
    [InlineData(AnalysisLogSource.Netiq, "failed")]
    [InlineData(AnalysisLogSource.Netiq, "partial")]
    [InlineData(AnalysisLogSource.Netiq, "running")]
    public void 不合格父紀錄不追加不派工(AnalysisLogSource source, string status)
    {
        Parent(2, source, status); Observe(2); Replay().RunBatch();
        using var db = _backend.CreateContext();
        Assert.Empty(db.TopIssues); Assert.Empty(db.IssueCases); Assert.Empty(db.WorkOrders);
        Assert.Equal("waiting-netiq", Assert.Single(db.PrtgObservations).SupplementStatus);
    }
    [Fact]
    public void 刪除父列後重建_保留原案證據且不建立孤兒或重複案件()
    {
        Parent(2); Observe(2); Replay().RunBatch();
        _backend.RecordStore(new HostKey { HostId = 2, HostName = "H2" }).DeleteDays([_day]);
        Replay().RunBatch();
        using (var db = _backend.CreateContext())
        { Assert.Empty(db.TopIssues); Assert.NotNull(Assert.Single(db.IssueCases).PrtgEvidenceJson); }
        _backend.RecordStore(new HostKey { HostId = 2, HostName = "H2" }).DeleteDays([_day]);
        Parent(2); Replay().RunBatch();
        using var final = _backend.CreateContext();
        Assert.Single(final.TopIssues); Assert.Single(final.IssueCases); Assert.Single(final.WorkOrders);
    }
    [Fact]
    public void 失敗重試保留舊分析但阻擋補追加_成功重跑後可恢復()
    {
        Parent(2); NetiqSourceAttempt.Mark(_backend, 2, _day, "failed"); Observe(2); Replay().RunBatch();
        using (var db = _backend.CreateContext()) Assert.Empty(db.TopIssues);
        _backend.RecordStore(new HostKey { HostId = 2, HostName = "H2" }).DeleteDays([_day]);
        Parent(2); Replay().RunBatch();
        using var final = _backend.CreateContext(); Assert.Single(final.TopIssues);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}

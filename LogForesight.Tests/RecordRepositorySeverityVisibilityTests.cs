using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/HISTORY.md S1：問題嚴重度可見性收斂到 RecordRepository 單一咽喉點。
/// 這組測試釘住「SiteHidden 模式下 Query／QueryPage／GetOne 都會過濾 TopIssues，
/// DefaultHidden（GetVisibleSeverities 回 null）不過濾」的契約，取代原本分散在
/// DashboardService／ReportService／RecordQueryService 各自判斷、且查詢頁分組視圖／
/// 簽章查詢完全漏掉這道過濾的舊行為。
/// </summary>
public class RecordRepositorySeverityVisibilityTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly WebHost _host;

    public RecordRepositorySeverityVisibilityTests()
    {
        _store = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _host = _hosts.Upsert(new WebHost { HostName = "SRV-A" });

        _store.Append(new DailyAnalysisRecord
        {
            HostId = _host.HostId,
            Host = _host.HostName,
            Date = DateTime.Today,
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                Issue("disk", 153, IssueSeverity.Critical),
                Issue("app", 1000, IssueSeverity.Medium),
                Issue("svc", 7001, IssueSeverity.Low)
            }
        });
    }

    public void Dispose() => _fixture.Dispose();

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity) =>
        new() { LogName = "System", Source = source, EventId = eventId, Severity = severity, Count = 1 };

    private RecordRepository CreateRepository(HashSet<string>? visibleSeverities) =>
        new(_store, _hosts, new AlwaysVisibleService(_hosts), new FakeSystemSettingsService { VisibleSeverities = visibleSeverities });

    [Fact]
    public void DefaultHidden模式_Query不過濾TopIssues()
    {
        var repository = CreateRepository(visibleSeverities: null);

        var result = repository.Query(new RecordQueryFilter());

        Assert.Equal(3, result.Single().TopIssues.Count);
    }

    [Fact]
    public void SiteHidden模式_Query只保留可見層級的TopIssues()
    {
        var repository = CreateRepository(visibleSeverities: new HashSet<string> { "Critical", "High" });

        var result = repository.Query(new RecordQueryFilter());

        var issues = result.Single().TopIssues;
        Assert.Single(issues);
        Assert.Equal("disk", issues[0].Source);
    }

    [Fact]
    public void SiteHidden模式_QueryPage也套用同一份過濾()
    {
        var repository = CreateRepository(visibleSeverities: new HashSet<string> { "Critical", "High" });

        var page = repository.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10);

        Assert.Single(page.Items.Single().TopIssues);
    }

    [Fact]
    public void SiteHidden模式_GetOne也套用同一份過濾()
    {
        var repository = CreateRepository(visibleSeverities: new HashSet<string> { "Critical", "High" });

        var record = repository.GetOne(_host.HostId, DateTime.Today);

        Assert.NotNull(record);
        Assert.Single(record!.TopIssues);
    }

    [Fact]
    public void SiteHidden模式_全部層級都被隱藏時TopIssues為空清單而非整筆消失()
    {
        var repository = CreateRepository(visibleSeverities: new HashSet<string>());

        var result = repository.Query(new RecordQueryFilter());

        // 紀錄本身（風險日）仍在——只有問題層級的可見性受影響，不影響紀錄是否存在
        Assert.Empty(result.Single().TopIssues);
    }

    /// <summary>
    /// docs/archive/HISTORY.md #1（B1 三級化）：舊資料相容的單一咽喉點——三級化之前
    /// 寫入的 Severity=Critical，讀取時正規化為 High＋ElevatesDayRisk=true，可解釋性
    /// （「重大」徽章）不因三級化而消失。只在讀取時於記憶體正規化，不回寫資料庫。
    /// </summary>
    [Fact]
    public void 讀取時正規化舊Critical嚴重度為High並標記ElevatesDayRisk()
    {
        var repository = CreateRepository(visibleSeverities: null);

        var record = repository.GetOne(_host.HostId, DateTime.Today);

        Assert.NotNull(record);
        var disk = record!.TopIssues.Single(i => i.Source == "disk");
        Assert.Equal(IssueSeverity.High, disk.Severity);
        Assert.True(disk.ElevatesDayRisk);

        // 非 Critical 的問題不受影響
        var app = record.TopIssues.Single(i => i.Source == "app");
        Assert.Equal(IssueSeverity.Medium, app.Severity);
        Assert.False(app.ElevatesDayRisk);
    }
}

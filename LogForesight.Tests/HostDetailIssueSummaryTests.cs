using LogForesight.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/FEEDBACK-3-PLAN.md #4：<see cref="RecordQueryService.GetHostDetail"/> 的
/// 「重點問題（期間彙總）」（<see cref="LogForesight.Web.Models.Dto.HostDetailDto.TopSignatures"/>）。
/// 依 Source+EventId 分組，與 <see cref="RecordQueryServiceSearchTests"/> 同樣的實測套接方式
/// （真正的 EfAnalysisRecordStore＋RecordRepository，不重新實作一份簡化邏輯）。
/// </summary>
public class HostDetailIssueSummaryTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly RecordQueryService _service;

    public HostDetailIssueSummaryTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        _service = new RecordQueryService(
            repository,
            new NullReportReader(),
            _hosts,
            _users,
            new FakeHostGroupStore(),
            visibility,
            new FakeHandlingStore(),
            new FakeIssueHandlingStore(),
            new FakeNoiseMarkStore(),
            new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(),
            _settingsStore);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => _hosts.Upsert(new WebHost { HostName = name });

    private void AddRecord(WebHost host, DateTime date, string risk, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            Headline = $"{host.HostName} {date:MM-dd}",
            TopIssues = issues.ToList()
        });

    private static LogIssueSignature Issue(
        string source, int eventId, IssueSeverity severity, IssueCategory category, int count, string? knownIssue = null) =>
        new()
        {
            LogName = "System", Source = source, EventId = eventId,
            Severity = severity, Category = category, Count = count, KnownIssue = knownIssue
        };

    [Fact]
    public void 依SourceEventId分組_次數天數與最近出現日正確()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today.AddDays(-2), "中", Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource, count: 3));
        AddRecord(host, DateTime.Today.AddDays(-1), "低", Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource, count: 2));
        // 同一天出現兩次同簽章：DaysSeen 只算「有出現的天數」，不是出現次數
        AddRecord(host, DateTime.Today, "中",
            Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource, count: 5),
            Issue("disk", 153, IssueSeverity.High, IssueCategory.Storage, count: 1));

        var detail = _service.GetHostDetail(host.HostId, days: 30);

        var appSignature = Assert.Single(detail.TopSignatures, s => s.Source == "app" && s.EventId == 1000);
        Assert.Equal(10, appSignature.TotalCount);   // 3+2+5
        Assert.Equal(3, appSignature.DaysSeen);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), appSignature.LastSeenDate);
        Assert.Equal("Medium", appSignature.MaxSeverity);
    }

    [Fact]
    public void 最高嚴重度取期間內最嚴重的一次不是最近一次()
    {
        var host = AddHost("HOST-A");
        // 最近一次是 Low，但期間內出現過 High——彙總要反映「這問題曾經有多嚴重」
        AddRecord(host, DateTime.Today.AddDays(-1), "高", Issue("app", 1000, IssueSeverity.High, IssueCategory.Resource, count: 1));
        AddRecord(host, DateTime.Today, "低", Issue("app", 1000, IssueSeverity.Low, IssueCategory.Resource, count: 1, knownIssue: "最新說明"));

        var detail = _service.GetHostDetail(host.HostId, days: 30);

        var signature = Assert.Single(detail.TopSignatures);
        Assert.Equal("High", signature.MaxSeverity);
        // 說明取最近一天的（規則說明可能被事後編輯，最近的較有參考價值）
        Assert.Equal("最新說明", signature.KnownIssue);
    }

    [Fact]
    public void 排序依最高嚴重度後總次數降冪()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高",
            Issue("low-freq", 1, IssueSeverity.High, IssueCategory.Other, count: 1),
            Issue("high-freq-medium", 2, IssueSeverity.Medium, IssueCategory.Other, count: 100),
            Issue("high-freq-high", 3, IssueSeverity.High, IssueCategory.Other, count: 50));

        var detail = _service.GetHostDetail(host.HostId, days: 30);

        Assert.Equal(
            new[] { "high-freq-high", "low-freq", "high-freq-medium" },
            detail.TopSignatures.Select(s => s.Source));
    }

    /// <summary>已併入存活主機的墓碑列歷史要一併算進彙總——與時間軸的別名展開同一套規則</summary>
    [Fact]
    public void 含已併入的墓碑主機歷史()
    {
        var survivor = AddHost("HOST-NEW");
        var tombstone = _hosts.Upsert(new WebHost { HostName = "HOST-OLD", MergedInto = survivor.HostId });

        AddRecord(tombstone, DateTime.Today.AddDays(-5), "中", Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource, count: 2));
        AddRecord(survivor, DateTime.Today, "中", Issue("app", 1000, IssueSeverity.Medium, IssueCategory.Resource, count: 3));

        var detail = _service.GetHostDetail(survivor.HostId, days: 30);

        var signature = Assert.Single(detail.TopSignatures);
        Assert.Equal(5, signature.TotalCount);
        Assert.Equal(2, signature.DaysSeen);
    }

    /// <summary>SiteHidden 模式下被全站顯示設定隱藏的嚴重度，彙總也不該看到——
    /// 與 RecordRepository 的問題可見性過濾是同一個咽喉點，這裡固定住彙總確實繼承了它</summary>
    [Fact]
    public void SiteHidden模式下被隱藏層級不出現在彙總()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高",
            Issue("disk", 153, IssueSeverity.High, IssueCategory.Storage, count: 1),
            Issue("app", 1000, IssueSeverity.Low, IssueCategory.Resource, count: 1));
        _severityVisibility.VisibleSeverities = new HashSet<string> { "High" };

        var detail = _service.GetHostDetail(host.HostId, days: 30);

        var signature = Assert.Single(detail.TopSignatures);
        Assert.Equal("disk", signature.Source);
    }

    [Fact]
    public void 期間內無問題時彙總為空清單()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "低");

        var detail = _service.GetHostDetail(host.HostId, days: 30);

        Assert.Empty(detail.TopSignatures);
    }
}

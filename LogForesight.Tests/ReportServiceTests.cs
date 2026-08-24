using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/HISTORY.md #6：報表新增的管理者指標（TotalHosts／Handling）。
/// 與 <see cref="RecordQueryServiceSearchTests"/> 同一套慣例——串接真正的
/// EfAnalysisRecordStore＋RecordRepository，而非重新實作一份簡化邏輯。
/// </summary>
public class ReportServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly EfRecordHandlingStore _handlingStore;
    private readonly EfIssueHandlingStore _issueHandlingStore;
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly RecordRepository _repository;
    private readonly HandlingHistoryQueryService _handling;
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _handlingStore = new EfRecordHandlingStore(_fixture.NewContext, new EfJsonLogStore(_fixture.NewContext, "handling"));
        _issueHandlingStore = new EfIssueHandlingStore(_fixture.NewContext);
        var visibility = new AlwaysVisibleService(_hosts);
        _repository = new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility);

        var progress = new HandlingProgressCalculator(_issueHandlingStore, _handlingStore, _caseStore, _settingsStore);

        // 排行／風險類型分布自 P4 起走 SQL 端投影（lf_top_issues），與儀表板查詢共用同一份
        // EF fixture。實作，讓「排行只含可見主機」這類測試測得到下推路徑
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);

        _handling = new HandlingHistoryQueryService(
            _handlingStore, _issueHandlingStore, _caseStore, _hosts, _users, visibility, _settingsStore, _repository, progress, aggregates);

        var issueRanking = new IssueRankingBuilder(aggregates, _hosts);

        _service = new ReportService(_repository, _hosts, visibility, _handling, issueRanking, _settingsStore, aggregates, _severityVisibility);
    }

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

    private void AddRecord(WebHost host, DateTime date, string risk, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = risk, TopIssues = issues.ToList()
        });

    /// <summary>
    /// 主機排行不是只有兩個天數欄位：關聯天數、最新風險等級、最新標題也要帶回來。
    ///
    /// 這條存在的理由是實作時真的踩過：KPI 下推 SQL 之後，排行一度改用
    /// 「把聚合結果展開成每個風險日一個假紀錄、再餵回 RecordStatsBuilder」的作法——
    /// 天數對得上，但假紀錄沒有關聯訊號也沒有標題，那幾欄會**靜默變空**，
    /// 而當時的測試只驗天數所以全綠。
    /// </summary>
    [Fact]
    public void GetSummary_主機排行帶回關聯天數與最新標題()
    {
        var host = AddHost("HOST-RANK");
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = DateTime.Today.AddDays(-1),
            RiskLevel = "高", Headline = "舊的標題",
            CorrelationAlerts = new List<string> { "測試用關聯訊號" }
        });
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = DateTime.Today,
            RiskLevel = "中", Headline = "最新的標題"
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        var row = Assert.Single(result.HostRanking);
        Assert.Equal(1, row.HighRiskDays);
        Assert.Equal(1, row.MediumRiskDays);
        Assert.Equal(1, row.CorrelationDays);            // 假紀錄的作法這裡會是 0
        Assert.Equal("最新的標題", row.LatestHeadline);   // 假紀錄的作法這裡會是空字串
        Assert.Equal("中", row.LatestRiskLevel);
    }

    [Fact]
    public void GetSummary_TotalHosts與可見主機數一致()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(2, result.TotalHosts);
    }

    /// <summary>Handling 與儀表板待辦同一套 GetTodo 規則：母體只算高＋中風險日，低風險日不進母體</summary>
    [Fact]
    public void GetSummary_Handling母體只計高中風險日()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高");
        AddRecord(host, DateTime.Today.AddDays(-1), "低");

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.OpenCount);
    }

    [Fact]
    public void GetSummary_顯示範圍unresolved_排除已結案的風險日()
    {
        // §5：scope=unresolved 先過濾再聚合——已結案的高風險日不進 KPI/趨勢母體
        var openHost = AddHost("HOST-OPEN");
        var resolvedHost = AddHost("HOST-RES");
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153, Severity = IssueSeverity.High, Count = 1,
            EntryType = System.Diagnostics.EventLogEntryType.Error
        };
        AddRecord(openHost, DateTime.Today, "高", issue);        // 未處理
        AddRecord(resolvedHost, DateTime.Today, "高", issue);    // 已結案
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = resolvedHost.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

        var all = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "all");
        Assert.Equal(2, all.Kpi.HighRiskDays);

        var unresolved = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unresolved");
        Assert.Equal("unresolved", unresolved.HandlingScope);
        Assert.Equal(1, unresolved.Kpi.HighRiskDays);   // 只剩未處理那台
    }

    [Fact]
    public void GetSummary_顯示範圍unassigned_只留無處理人的風險日()
    {
        var assignedHost = AddHost("HOST-ASSIGNED");
        var unassignedHost = AddHost("HOST-UNASSIGNED");
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\h", DisplayName = "小陳" });
        AddRecord(assignedHost, DateTime.Today, "高");
        AddRecord(unassignedHost, DateTime.Today, "高");
        _handlingStore.Save(new RecordHandling
        {
            HostName = assignedHost.HostName, Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, HandlerId = handler.UserId, UpdatedAt = DateTime.Now
        });

        var unassigned = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unassigned");
        Assert.Equal(1, unassigned.Kpi.HighRiskDays);   // 只剩沒有處理人那台
    }

    /// <summary>docs/archive/FEEDBACK-3-PLAN.md #8：日風險等級顯示設定套用在 RecordRepository 單一咽喉，
    /// ReportService 不自己過濾——這裡固定住「吃 repository 結果」的契約，KPI/趨勢母體
    /// 隨顯示設定縮小，而不是報表自己額外判斷一次</summary>
    [Fact]
    public void GetSummary_中風險日被隱藏時KPI與趨勢母體縮小()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高");
        AddRecord(host, DateTime.Today.AddDays(-1), "中");
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { "高" };

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Kpi.HighRiskDays);
        // 中風險日已被顯示設定藏起來，趨勢圖對應日期的中風險計數應為 0
        var hiddenDayPoint = result.Trend.Single(p => p.Date == DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        Assert.Equal(0, hiddenDayPoint.MediumRisk);
    }

    /// <summary>
    /// 回饋十三輪新增項3：「風險類型」卡片沒有像風險日詳情頁那樣的手動展開入口，不論
    /// SeverityDisplayMode 為 DefaultHidden 或 SiteHidden，一律只計入 UnhandledSeverities
    /// （預設 High+Medium，不含 Low）——「其他」類別本來就是低嚴重度雜訊大宗，
    /// 修正前最容易讓人以為「明明沒勾 Low，其他類別怎麼還在顯示」。
    /// </summary>
    [Fact]
    public void GetSummary_風險類型卡預設排除Low嚴重度問題_只剩Low的類別整卡消失()
    {
        var host = AddHost("HOST-A");
        AddRecord(host, DateTime.Today, "高",
            new LogIssueSignature
            {
                LogName = "System", Source = "sec", EventId = 4625,
                Severity = IssueSeverity.High, Category = IssueCategory.Security, Count = 1
            },
            new LogIssueSignature
            {
                LogName = "System", Source = "noisy", EventId = 9999,
                Severity = IssueSeverity.Low, Category = IssueCategory.Other, Count = 1
            });

        _severityVisibility.VisibleSeverities = new HashSet<string> { "High", "Medium" };
        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        // 「其他」類別只有一個 Low 嚴重度問題，預設未勾選 Low 時整卡不應出現
        Assert.DoesNotContain(result.Categories, c => c.Category == "Other");
        var security = Assert.Single(result.Categories);
        Assert.Equal("Security", security.Category);
    }

    [Fact]
    public void GetSummary_問題全數結案後計入ResolvedCount()
    {
        var host = AddHost("HOST-A");
        var issue = new LogIssueSignature
        {
            LogName = "System", Source = "disk", EventId = 153, Severity = IssueSeverity.High, Count = 1
        };
        AddRecord(host, DateTime.Today, "高", issue);
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today,
            IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.ResolvedCount);
        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(0, result.Handling.OpenCount);
    }

    // ── 問題排行（docs/archive/FEEDBACK-11-PLAN.md §8-2）───────────────────────────────

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity, int count) => new()
    {
        LogName = "System", Source = source, EventId = eventId, Severity = severity, Count = count
    };

    /// <summary>
    /// 依 Source＋EventId 聚合（與依問題視角同一把鍵）：跨主機跨日的次數加總、
    /// 相異主機數、相異主機日數；排序＝最高嚴重度 → 主機數 → 總次數。
    /// </summary>
    [Fact]
    public void GetSummary_問題排行跨主機跨日聚合()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, DateTime.Today, "高", Issue("disk", 153, IssueSeverity.High, 3));
        AddRecord(a, DateTime.Today.AddDays(-1), "高", Issue("disk", 153, IssueSeverity.High, 2));
        AddRecord(b, DateTime.Today, "中", Issue("disk", 153, IssueSeverity.Medium, 5),
                                           Issue("svc", 7031, IssueSeverity.Medium, 1));

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        var top = result.IssueRanking[0];
        Assert.Equal("disk", top.Source);
        Assert.Equal(153, top.EventId);
        Assert.Equal("High", top.MaxSeverity);   // 期間內出現過的最高嚴重度
        Assert.Equal(2, top.HostCount);
        Assert.Equal(3, top.DayCount);           // 主機日：A 兩天＋B 一天
        Assert.Equal(10, top.TotalCount);
        Assert.Equal(2, result.RankedIssueCount);
        Assert.Null(result.IssueOthers);         // 只有兩個問題，沒有 Top 10 之外的
    }

    /// <summary>可見範圍過濾天生繼承自 repository——排行不是另一條繞過授權的查詢路徑</summary>
    [Fact]
    public void GetSummary_問題排行只含可見主機()
    {
        var visible = AddHost("HOST-A");
        AddRecord(visible, DateTime.Today, "高", Issue("disk", 153, IssueSeverity.High, 1));

        // 未登錄於主機清單的紀錄（等同不可見）不該出現在排行中
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = 9999, Host = "HOST-GHOST", Date = DateTime.Today, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { Issue("ghost", 1, IssueSeverity.High, 99) }
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.DoesNotContain(result.IssueRanking, i => i.Source == "ghost");
    }

    [Fact]
    public void GetSummary_主機名稱大小寫不同時仍能正確對應到處理狀態()
    {
        var host = AddHost("HoSt-CaSe");
        AddRecord(host, DateTime.Today, "高");
        // 存檔用全小寫
        _handlingStore.Save(new RecordHandling
        {
            HostName = "host-case", Date = DateTime.Today,
            Status = HandlingStatuses.InProgress, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);
        // 如果有抓到狀態，就不會是 Open（預設）
        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.InProgressCount);
        Assert.Equal(0, result.Handling.OpenCount);
    }

    [Fact]
    public void GetSummary_同一天有多個問題層級標記時推導結果一致()
    {
        var host = AddHost("HOST-MULTI");
        var issue1 = Issue("disk", 1, IssueSeverity.High, 1);
        var issue2 = Issue("disk", 2, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today, "高", issue1, issue2);

        // 兩個問題分別有狀態
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue1),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = DateTime.Today, IssueKey = IssueSignatureKey.For(issue2),
            Status = IssueHandlingStatuses.InProgress, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.InProgressCount);
    }

    [Fact]
    public void GetSummary_unassigned範圍_日層級無處理人但問題屬進行中案件時不算未指派()
    {
        var host = AddHost("HOST-CASE-ASSIGN");
        var issue = Issue("disk", 1, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today, "高", issue);

        // 日層級沒有處理人，但有一個針對該問題的案件，有指派處理人
        var handler = _users.Upsert(new WebUser { Account = "DOMAIN\\x", DisplayName = "案件負責人" });
        _caseStore.Save(new IssueCase
        {
            HostName = host.HostName, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress, HandlerId = handler.UserId,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, "unassigned");
        // 有案件負責人，所以不算 unassigned，因此高風險日應為 0
        Assert.Equal(0, result.Kpi.HighRiskDays);
    }
    [Fact]
    public void GetSummary_AllScope_KPI欄位正確且包含或條件CoverageGap()
    {
        var host = AddHost("HOST-KPI");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));
        AddRecord(host, DateTime.Today.AddDays(-1), "中", Issue("disk", 2, IssueSeverity.Medium, 1));

        var rec1 = new DailyAnalysisRecord { HostId = host.HostId, Host = host.HostName, Date = DateTime.Today.AddDays(-2), RiskLevel = "低", DataIncomplete = true, TopIssues = new List<LogIssueSignature>() };
        var rec2 = new DailyAnalysisRecord { HostId = host.HostId, Host = host.HostName, Date = DateTime.Today.AddDays(-3), RiskLevel = "低", SecurityLogAvailable = false, TopIssues = new List<LogIssueSignature>() };
        _recordStore.Append(rec1);
        _recordStore.Append(rec2);

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(2, result.Kpi.TotalIssues);
        Assert.Equal(1, result.Kpi.HighRiskDays);
        Assert.Equal(1, result.Kpi.AffectedHosts);
        Assert.Equal(2, result.Kpi.CoverageGapDays);
    }

    [Fact]
    public void GetSummary_AllScope_合併主機舊識別算進存活主機()
    {
        var hostA = AddHost("HOST-A-MERGE");
        var hostB = AddHost("HOST-B-MERGE");
        AddRecord(hostA, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));
        AddRecord(hostB, DateTime.Today.AddDays(-1), "高", Issue("disk", 1, IssueSeverity.High, 1));

        _hosts.Merge(hostB.HostId, hostA.HostId);

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Kpi.AffectedHosts); // A and B merged to A
        Assert.Single(result.HostRanking);
        Assert.Equal(2, result.HostRanking[0].HighRiskDays);
    }

    [Fact]
    public void GetSummary_AllScope_不可見主機不計入任何數字()
    {
        var visible = AddHost("HOST-VIS");
        AddRecord(visible, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));

        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = 9999, Host = "HOST-GHOST", Date = DateTime.Today, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { Issue("ghost", 1, IssueSeverity.High, 1) }
        });

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Kpi.TotalIssues);
        Assert.Equal(1, result.Kpi.HighRiskDays);
        Assert.Equal(1, result.Kpi.AffectedHosts);
        Assert.Single(result.HostRanking);
        Assert.Equal(1, result.RankedHostCount);
        Assert.Equal(1, result.Trend.Count(t => t.HighRisk > 0));
    }

    [Fact]
    public void GetSummary_AllScope_日風險等級顯示範圍限制()
    {
        var host = AddHost("HOST-RISK");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));
        AddRecord(host, DateTime.Today.AddDays(-1), "中", Issue("disk", 2, IssueSeverity.Medium, 1));

        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { "高" };

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        // 只顯示高，中風險日被濾掉
        Assert.Equal(1, result.Kpi.TotalIssues);
        Assert.Equal(1, result.Kpi.HighRiskDays);
        Assert.Equal(1, result.Kpi.AffectedHosts);
    }

    [Fact]
    public void GetSummary_AllScope_不可見嚴重度不計入TotalIssues但不影響風險日()
    {
        var host = AddHost("HOST-SEV");
        AddRecord(host, DateTime.Today, "高",
            Issue("disk", 1, IssueSeverity.High, 1),
            Issue("disk", 2, IssueSeverity.Low, 1));

        // SiteHidden 隱藏 Low
        _severityVisibility.VisibleSeverities = new HashSet<string> { "High", "Medium" };

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Kpi.TotalIssues); // Low is hidden
        Assert.Equal(1, result.Kpi.HighRiskDays); // 仍是高風險日
    }

    [Fact]
    public void GetSummary_AllScope_趨勢補零()
    {
        var host = AddHost("HOST-TREND");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(7, result.Trend.Count);
        Assert.Equal(1, result.Trend.Count(t => t.HighRisk == 1));
        Assert.Equal(6, result.Trend.Count(t => t.HighRisk == 0));
    }

    // ── 待辦事項 (GetTodoByRange) 測試 ─────────────────────────────────────────────

    [Fact]
    public void GetTodoByRange_與GetTodo記憶體聚合四計數等價()
    {
        var from = DateTime.Today.AddDays(-9);
        var to = DateTime.Today;

        // 建立 20 台主機
        var hosts = Enumerable.Range(1, 20).Select(i => AddHost($"HOST-EQ-{i}")).ToList();

        // 建立假資料：各種狀態組合
        foreach (var host in hosts)
        {
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var issues = new[] { Issue("disk", 1, IssueSeverity.High, 1), Issue("cpu", 2, IssueSeverity.Medium, 1) };
                AddRecord(host, d, "高", issues);

                // 隨機（依據主機跟日期分派不同狀態組合）
                int mod = (int)((host.HostId + d.DayOfYear) % 7);
                switch (mod)
                {
                    case 0: // 未標記
                        break;
                    case 1: // 問題層級結案
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[0]), Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now });
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[1]), Status = IssueHandlingStatuses.KnownNoise, UpdatedAt = DateTime.Now });
                        break;
                    case 2: // 部分結案
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[0]), Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now });
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[1]), Status = IssueHandlingStatuses.InProgress, UpdatedAt = DateTime.Now });
                        break;
                    case 3: // InProgress (問題層級)
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[0]), Status = IssueHandlingStatuses.InProgress, UpdatedAt = DateTime.Now });
                        break;
                    case 4: // Observing (問題層級)
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[0]), Status = IssueHandlingStatuses.Observing, DueDate = DateTime.Today.AddDays(1), UpdatedAt = DateTime.Now });
                        break;
                    case 5: // Escalated
                        _issueHandlingStore.Save(new IssueHandling { HostName = host.HostName, Date = d, IssueKey = IssueSignatureKey.For(issues[0]), Status = IssueHandlingStatuses.Escalated, UpdatedAt = DateTime.Now });
                        break;
                    case 6: // 日層級 wont_fix
                        _handlingStore.Save(new RecordHandling { HostName = host.HostName, Date = d, Status = HandlingStatuses.WontFix, UpdatedAt = DateTime.Now });
                        break;
                }
            }
        }

        var filter = new RecordQueryFilter { From = from, To = to, Hosts = hosts.Select(HostKey.Of).ToList() };
        var records = _repository.QueryLightweight(filter);
        var memTodo = _handling.GetTodo(records);
        var sqlTodo = _handling.GetTodoByRange(from, to);

        Assert.Equal(memTodo.TotalCount, sqlTodo.TotalCount);
        Assert.Equal(memTodo.OpenCount, sqlTodo.OpenCount);
        Assert.Equal(memTodo.InProgressCount, sqlTodo.InProgressCount);
        Assert.Equal(memTodo.ResolvedCount, sqlTodo.ResolvedCount);
    }

    [Fact]
    public void GetTodoByRange_墓碑主機合併後舊識別的風險日仍被正確計數()
    {
        var hostA = AddHost("HOST-A-TODO");
        var hostB = AddHost("HOST-B-TODO");
        var from = DateTime.Today.AddDays(-2);
        var to = DateTime.Today;

        AddRecord(hostB, DateTime.Today.AddDays(-1), "高", Issue("disk", 1, IssueSeverity.High, 1));

        _hosts.Merge(hostB.HostId, hostA.HostId);

        var todo = _handling.GetTodoByRange(from, to);

        Assert.Equal(1, todo.TotalCount);
        Assert.Equal(1, todo.OpenCount);
    }

    [Fact]
    public void GetTodoByRange_逾期規則一_日層級逾期未結案_計入OverdueCount()
    {
        var host = AddHost("HOST-OVERDUE-1");
        AddRecord(host, DateTime.Today.AddDays(-1), "高", Issue("disk", 1, IssueSeverity.High, 1));

        _handlingStore.Save(new RecordHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            Status = HandlingStatuses.InProgress,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        var todo = _handling.GetTodoByRange(DateTime.Today.AddDays(-2), DateTime.Today);
        Assert.Equal(1, todo.OverdueCount);
    }

    [Fact]
    public void GetTodoByRange_逾期規則二_問題層級InProgress且逾期_計入OverdueCount()
    {
        var host = AddHost("HOST-OVERDUE-2");
        var issue = Issue("disk", 1, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today.AddDays(-1), "高", issue);

        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        var todo = _handling.GetTodoByRange(DateTime.Today.AddDays(-2), DateTime.Today);
        Assert.Equal(1, todo.OverdueCount);
    }

    [Fact]
    public void GetTodoByRange_逾期規則三_問題層級Observing且逾期_計入OverdueCount()
    {
        var host = AddHost("HOST-OVERDUE-3");
        var issue = Issue("disk", 1, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today.AddDays(-1), "高", issue);

        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        var todo = _handling.GetTodoByRange(DateTime.Today.AddDays(-2), DateTime.Today);
        Assert.Equal(1, todo.OverdueCount);
    }

    [Fact]
    public void GetTodoByRange_逾期不重複計算_同一天多條逾期規則同時滿足時只算一次()
    {
        var host = AddHost("HOST-OVERDUE-4");
        var issue1 = Issue("disk", 1, IssueSeverity.High, 1);
        var issue2 = Issue("disk", 2, IssueSeverity.High, 1);
        AddRecord(host, DateTime.Today.AddDays(-1), "高", issue1, issue2);

        _handlingStore.Save(new RecordHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            Status = HandlingStatuses.InProgress,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            IssueKey = IssueSignatureKey.For(issue1),
            Status = IssueHandlingStatuses.InProgress,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            IssueKey = IssueSignatureKey.For(issue2),
            Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(-1),
            UpdatedAt = DateTime.Now
        });

        var todo = _handling.GetTodoByRange(DateTime.Today.AddDays(-2), DateTime.Today);
        Assert.Equal(1, todo.OverdueCount);
    }

    [Fact]
    public void ReportsController_Summary_367天丟出驗證錯誤()
    {
        var controller = new LogForesight.Web.Controllers.Api.ReportsController(_service);
        var from = DateTime.Today.AddDays(-366).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        var ex = Assert.Throws<LogForesight.Web.Models.DomainException>(() => controller.Summary(from, to, null));
        Assert.Contains("366", ex.Message);
    }

    [Fact]
    public void ReportsController_Summary_366天可以通過()
    {
        var controller = new LogForesight.Web.Controllers.Api.ReportsController(_service);
        var from = DateTime.Today.AddDays(-365).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        var result = controller.Summary(from, to, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void GetSummary_CompareYoy_比較期為去年同期()
    {
        var host = AddHost("HOST-YOY");
        var from = DateTime.Today.AddDays(-10);
        var to = DateTime.Today;

        var prevFrom = from.AddYears(-1);
        var prevTo = to.AddYears(-1);

        AddRecord(host, prevTo, "高", Issue("disk", 1, IssueSeverity.High, 1));

        var result = _service.GetSummary(from, to, null, "yoy");

        Assert.Equal("yoy", result.ComparisonMode);
        Assert.Equal(1, result.Kpi.HighRiskDaysPrevious);
    }

    [Fact]
    public void GetSummary_ComparePrevious_維持現行緊鄰前期行為()
    {
        var host = AddHost("HOST-PREV");
        var from = DateTime.Today.AddDays(-10);
        var to = DateTime.Today;

        var prevTo = from.AddDays(-1);

        AddRecord(host, prevTo, "高", Issue("disk", 1, IssueSeverity.High, 1));

        var result1 = _service.GetSummary(from, to, null, "previous");
        var result2 = _service.GetSummary(from, to, null, null);

        Assert.Equal("previous", result1.ComparisonMode);
        Assert.Equal(1, result1.Kpi.HighRiskDaysPrevious);
        Assert.Equal("previous", result2.ComparisonMode);
        Assert.Equal(1, result2.Kpi.HighRiskDaysPrevious);
    }

    [Fact]
    public void GetSummary_CompareUnrecognized_當成previous不丟錯()
    {
        var host = AddHost("HOST-UNREC");
        var from = DateTime.Today.AddDays(-10);
        var to = DateTime.Today;

        var prevTo = from.AddDays(-1);

        AddRecord(host, prevTo, "高", Issue("disk", 1, IssueSeverity.High, 1));

        var result = _service.GetSummary(from, to, null, "invalid_mode");

        Assert.Equal("previous", result.ComparisonMode);
        Assert.Equal(1, result.Kpi.HighRiskDaysPrevious);
    }

    [Fact]
    public void GetSummary_CompareYoy_閏年2月29的去年比較期終點是2月28()
    {
        var host = AddHost("HOST-LEAP");
        var from = new DateTime(2024, 2, 29);
        var to = new DateTime(2024, 2, 29);

        AddRecord(host, new DateTime(2023, 2, 28), "高", Issue("disk", 1, IssueSeverity.High, 1));

        var result = _service.GetSummary(from, to, null, "yoy");

        Assert.Equal(1, result.Kpi.HighRiskDaysPrevious);
    }

    [Fact]
    public void GetSummary_ComparisonOutOfRetention_依比較期終點是否落在保留期之外判定()
    {
        var fromOutside = DateTime.Today.AddDays(-120);
        var toOutside = DateTime.Today.AddDays(-119);

        var fromInside = DateTime.Today.AddDays(-119);
        var toInside = DateTime.Today.AddDays(-118);

        var resultOutside = _service.GetSummary(fromOutside, toOutside, null, "previous");
        var resultInside = _service.GetSummary(fromInside, toInside, null, "previous");

        Assert.True(resultOutside.ComparisonOutOfRetention);
        Assert.False(resultInside.ComparisonOutOfRetention);
    }

    [Fact]
    public void GetSummary_日風險等級只顯示高時_中風險日的待辦不計入Todo()
    {
        var host = AddHost("HOST-RISK-TODO");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));
        AddRecord(host, DateTime.Today.AddDays(-1), "中", Issue("disk", 2, IssueSeverity.Medium, 1));

        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { "高" };

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(1, result.Handling.TotalCount);
        Assert.Equal(1, result.Handling.OpenCount);
    }

    [Fact]
    public void GetSummary_日風險等級全部隱藏時_Todo為零()
    {
        var host = AddHost("HOST-ALL-HIDDEN");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));
        AddRecord(host, DateTime.Today.AddDays(-1), "中", Issue("disk", 2, IssueSeverity.Medium, 1));

        _severityVisibility.VisibleDayRiskLevels = new HashSet<string>();

        var result = _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.Equal(0, result.Kpi.HighRiskDays);
        Assert.Equal(0, result.Handling.TotalCount);
        Assert.Equal(0, result.Handling.OpenCount);
    }

    // ── 回饋二十七輪作業 F：效能驗收 ──────────────────────────────────────────
    //
    // 這兩條先於實作寫下，訂出「改完之後必須成立」的條件，之後充當回歸護欄。
    //
    // 上限 4 是實測後訂的契約值，不是理論下限：改動前是 13 次，主要來自
    // EfIssueAggregateQuery 的每個聚合方法各自重建一次主機別名索引（一次報表請求會呼叫
    // 八個以上聚合方法）。索引改為依主機資料版本快取後降到 4——剩下的四次分別來自
    // 可見性服務、處理狀態彙總的主機索引，以及索引首次建立，各自服務邊界不同，
    // 硬要收斂成一次得把主機快照穿過好幾層 API，代價大於效益。
    // **這個數字只能往下調，不能往上**：往上代表又有人在請求路徑上加了一次全表走訪。

    private const int MaxHostGetAllPerSummary = 4;

    [Fact]
    public void GetSummary_整份主機表的載入次數不得超過契約上限()
    {
        var host = AddHost("HOST-GETALL");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));

        _hosts.ResetCallCounts();
        _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today);

        Assert.True(_hosts.GetAllCallCount <= MaxHostGetAllPerSummary,
            $"整份主機表被載入 {_hosts.GetAllCallCount} 次，超過契約上限 {MaxHostGetAllPerSummary} 次");
    }

    /// <summary>非 All 顯示範圍走記憶體分支，同樣受同一個上限約束。</summary>
    [Fact]
    public void GetSummary_非全部顯示範圍時載入次數同樣不得超過契約上限()
    {
        var host = AddHost("HOST-GETALL-SCOPE");
        AddRecord(host, DateTime.Today, "高", Issue("disk", 1, IssueSeverity.High, 1));

        _hosts.ResetCallCounts();
        _service.GetSummary(DateTime.Today.AddDays(-6), DateTime.Today, handlingScope: "unhandled");

        Assert.True(_hosts.GetAllCallCount <= MaxHostGetAllPerSummary,
            $"整份主機表被載入 {_hosts.GetAllCallCount} 次，超過契約上限 {MaxHostGetAllPerSummary} 次");
    }
}

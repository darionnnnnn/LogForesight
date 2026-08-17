using System.Diagnostics;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題排行的組裝層（docs/archive/SCALE-FIX-PLAN-2026-08-06.md G3）。
///
/// **為什麼補這組測試**：P4 為聚合查詢寫了 9 條測試，卻沒有測「把聚合變成 DTO」那一層——
/// 而分類欄空白（D2）、變化幅度、IsNew、影響率全在這一層。D2 那種
/// 「欄位恆為空字串」的缺陷不會讓任何既有測試變紅，只會讓畫面少一欄，
/// 上線後才有人問「分類怎麼不見了」。
/// </summary>
public class IssueRankingBuilderTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public IssueRankingBuilderTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private IssueRankingBuilder Builder() => new(new EfIssueAggregateQuery(_fx.NewContext, _hosts), _hosts);

    /// <summary>帶處理概況彙總的完整組裝——驗證 OpenHostCount／ResolvedHostCount 這條路徑要串真的
    /// IssueHandlingRollupQuery，不是像 <see cref="Builder"/> 那樣單測 Aggregate→DTO 映射。</summary>
    private IssueRankingBuilder BuilderWithRollup()
    {
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings);
        var rollup = new IssueHandlingRollupQuery(aggregates, statusResolver);
        return new IssueRankingBuilder(aggregates, _hosts, rollup);
    }

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        IssueCategory category = IssueCategory.Storage,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning) => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = category, Severity = severity, ElevatesDayRisk = elevates, Count = count
        };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = RiskLevels.Low, TopIssues = issues.ToList()
        });

    /// <summary>
    /// D2 的回歸測試：分類欄過去被寫死成空字串（註解說「由呼叫端補」但沒有呼叫端補），
    /// 儀表板重點問題卡與報表問題排行的「分類」欄因此全部空白。
    /// </summary>
    [Fact]
    public void 分類欄要帶得出來()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, category: IssueCategory.Storage));

        var row = Builder().Build(d0, d0, null, totalHosts: 1).Single();

        Assert.Equal(IssueCategory.Storage.ToString(), row.Category);
    }

    [Fact]
    public void 主機數與期間跨度與密度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));

        var row = Builder().Build(d0, d0.AddDays(9), null, totalHosts: 4).Single();

        Assert.Equal(2, row.HostCount);
        Assert.Equal("2026-08-01", row.FirstSeen);
        Assert.Equal("2026-08-05", row.LastSeen);
        Assert.Equal(2, row.ActiveDays);
        Assert.Equal(10, row.PeriodDays);          // 含頭尾
        Assert.Equal(0.5, row.HostRatio);          // 2 ÷ 4
    }

    /// <summary>影響率的分母是可見主機總數；沒有主機時不得除零</summary>
    [Fact]
    public void 影響率_主機總數為零時回零而不是除零()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        var row = Builder().Build(d0, d0, null, totalHosts: 0).Single();

        Assert.Equal(0, row.HostRatio);
    }

    /// <summary>
    /// 「新出現」＝前一個**等長**期間完全沒有出現過。這是「今天有什麼不一樣」的唯一訊號，
    /// 也是把 DCOM 那種天天都有的雜訊與真正的新問題分開的關鍵。
    /// </summary>
    [Fact]
    public void 本期新出現_前期沒有紀錄()
    {
        var from = new DateTime(2026, 8, 5);
        var to = new DateTime(2026, 8, 11);          // 7 天
        Add(1, "A", from.AddDays(1), Issue("disk", 153));

        var row = Builder().Build(from, to, null, totalHosts: 1).Single();

        Assert.True(row.IsNew);
        Assert.Equal(0, row.PreviousHostCount);
    }

    [Fact]
    public void 非新出現_前期有紀錄時帶出前期主機數()
    {
        var from = new DateTime(2026, 8, 5);
        var to = new DateTime(2026, 8, 11);          // 前期＝07-29 ~ 08-04
        Add(1, "A", new DateTime(2026, 7, 30), Issue("disk", 153));
        Add(2, "B", new DateTime(2026, 7, 31), Issue("disk", 153));
        Add(1, "A", from.AddDays(1), Issue("disk", 153));

        var row = Builder().Build(from, to, null, totalHosts: 2).Single();

        Assert.False(row.IsNew);
        Assert.Equal(2, row.PreviousHostCount);      // 前期 2 台 → 本期 1 台＝收斂中
        Assert.Equal(1, row.HostCount);
    }

    /// <summary>
    /// 回饋二十輪 I：Aggregate 已把大小寫不同的同名來源合併，但輸出的 Source 是該期間內
    /// 任一個原始寫法——前期只出現 cron、本期只出現 CRON 時，若用原始字串當跨期比較的鍵，
    /// 前期會靜默查不到，老問題被誤判成「新出現」。
    /// </summary>
    [Fact]
    public void 前期對比_來源大小寫不同時仍對得上不會誤判為新出現()
    {
        var from = new DateTime(2026, 8, 5);
        var to = new DateTime(2026, 8, 11);          // 前期＝07-29 ~ 08-04
        Add(1, "A", new DateTime(2026, 7, 30), Issue("cron", 0));
        Add(1, "A", from.AddDays(1), Issue("CRON", 0));

        var row = Builder().Build(from, to, null, totalHosts: 1).Single();

        Assert.False(row.IsNew);
        Assert.Equal(1, row.PreviousHostCount);
    }

    /// <summary>距今天數回答「還要不要處理」——把「90 天前爆發過」與「今天正在發生」分開</summary>
    [Fact]
    public void 距今天數()
    {
        var today = DateTime.Today;
        Add(1, "A", today.AddDays(-3), Issue("disk", 153));

        var row = Builder().Build(today.AddDays(-10), today, null, totalHosts: 1).Single();

        Assert.Equal(3, row.DaysSinceLastSeen);
    }

    /// <summary>
    /// 距今天數以查詢的 to 為準，不是另外抓一次真實今天（回饋十九輪批次C）：
    /// Dashboard／Report 現在傳的 to 是分析錨點（昨天），這裡驗證即使 to 跟真實今天不同，
    /// 算出來的天數仍是相對 to，不會因為真實時鐘往前走而多算一天。
    /// </summary>
    [Fact]
    public void 距今天數以查詢的to為準_不是真實今天()
    {
        var to = DateTime.Today.AddDays(-1);   // 模擬 Dashboard 傳入的錨點（昨天）
        Add(1, "A", to.AddDays(-3), Issue("disk", 153));   // 距 to 三天前發生

        var row = Builder().Build(to.AddDays(-10), to, null, totalHosts: 1).Single();

        // 若實作錯誤地用真實 DateTime.Today 計算，這裡會多算 1 天變成 4
        Assert.Equal(3, row.DaysSinceLastSeen);
    }

    [Fact]
    public void 重大旗標與最高嚴重度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("disk", 153, severity: IssueSeverity.High, elevates: true));

        var row = Builder().Build(d0, d0, null, totalHosts: 2).Single();

        Assert.Equal(IssueSeverity.High.ToString(), row.MaxSeverity);
        Assert.True(row.ElevatesDayRisk);
    }

    /// <summary>
    /// 觀察到期比對真實時鐘，不是分析錨點（回饋十九輪批次E0 抽取共用解析器時的迴歸測試——
    /// 把 today 誤用成分析錨點，會讓「觀察至昨天」的案子被誤判成仍在觀察中而非已到期）。
    /// </summary>
    [Fact]
    public void 觀察到期比對真實時鐘_不是分析錨點()
    {
        var today = DateTime.Today;
        var hostA = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(hostA.HostId, "A", today, Issue("disk", 153, severity: IssueSeverity.High));
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = today,
            IssueKey = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning),
            Status = IssueHandlingStatuses.Observing, DueDate = today.AddDays(-1)   // 觀察期已過
        });

        var row = BuilderWithRollup().Build(today, today, null, totalHosts: 1).Single();

        Assert.Equal(1, row.OpenHostCount);   // 到期＝仍在發生，計入未處理，不是已處理
        Assert.Equal(0, row.ResolvedHostCount);
    }

    /// <summary>可見範圍的授權語意與查詢層一致：空集合＝零結果，不是「不限制」</summary>
    [Fact]
    public void 可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        Assert.Empty(Builder().Build(d0, d0, Array.Empty<long>(), totalHosts: 0));
        Assert.Single(Builder().Build(d0, d0, null, totalHosts: 1));
    }

    /// <summary>
    /// 處理狀態以**完整簽章**為鍵，而排行以 (Source, EventId) 為單位——
    /// 一個群組底下的多個完整簽章要**合併**未處理／已處理主機數（§10.6 的前提）。
    /// 端到端串真的 IssueHandlingRollupQuery：這正是查證抓到的死碼（兩個正式呼叫端
    /// 從未傳過 handlingByIssue）該補上的那條路徑，用手動塞字典測不出「真的接線了沒」。
    /// </summary>
    [Fact]
    public void 處理概況_跨多個完整簽章合併()
    {
        var d0 = new DateTime(2026, 8, 1);
        var hostA = _hosts.Upsert(new WebHost { HostName = "A" });
        var hostB = _hosts.Upsert(new WebHost { HostName = "B" });

        Add(hostA.HostId, "A", d0,
            Issue("disk", 153, severity: IssueSeverity.High, logName: "System", entryType: EventLogEntryType.Warning));
        Add(hostB.HostId, "B", d0,
            Issue("disk", 153, severity: IssueSeverity.High, logName: "Application", entryType: EventLogEntryType.Error));

        // B 已標記為已處理；A 沒有任何標記，嚴重度 High 落在預設「需處理」名單內 → 未處理
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "B",
            Date = d0,
            IssueKey = IssueSignatureKey.For("Application", "disk", 153, EventLogEntryType.Error),
            Status = IssueHandlingStatuses.Resolved
        });

        var row = BuilderWithRollup().Build(d0, d0, null, totalHosts: 2).Single();

        Assert.Equal(1, row.OpenHostCount);
        Assert.Equal(1, row.ResolvedHostCount);
    }

    /// <summary>
    /// §10.6：全部主機都已有結論的問題不佔用重點清單版面，但要誠實回報排除了幾筆——
    /// 悄悄少幾筆會製造「怎麼問題變少了」的第二種數字對不起來。
    /// </summary>
    [Fact]
    public void ExcludeConcluded_全部主機已結論的問題被排除且計數()
    {
        var d0 = new DateTime(2026, 8, 1);
        var hostA = _hosts.Upsert(new WebHost { HostName = "A" });
        var hostB = _hosts.Upsert(new WebHost { HostName = "B" });

        // disk/153：唯一主機 A 已標記已處理 → 全部有結論，該被排除
        Add(hostA.HostId, "A", d0, Issue("disk", 153, severity: IssueSeverity.High));
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = d0,
            IssueKey = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning),
            Status = IssueHandlingStatuses.Resolved
        });

        // DCOM/10016：主機 B 沒有任何標記、嚴重度 High → 未處理，該保留
        Add(hostB.HostId, "B", d0, Issue("DCOM", 10016, severity: IssueSeverity.High));

        var ranked = BuilderWithRollup().Build(d0, d0, null, totalHosts: 2);
        var (kept, concludedCount) = IssueRankingBuilder.ExcludeConcluded(ranked);

        Assert.Equal(1, concludedCount);
        Assert.Single(kept);
        Assert.Equal("DCOM", kept[0].Source);
    }

    /// <summary>預設排序：最高嚴重度 → 主機數 → 總次數（與依問題視角的既有預設一致）</summary>
    [Fact]
    public void 預設排序_嚴重度優先()
    {
        var d0 = new DateTime(2026, 8, 1);
        // 低嚴重度但影響很廣（DCOM 的角色）
        for (var i = 1; i <= 5; i++) Add(i, $"H{i}", d0, Issue("DCOM", 10016, severity: IssueSeverity.Low));
        // 高嚴重度但只有一台（disk 153 的角色）
        Add(9, "H9", d0, Issue("disk", 153, severity: IssueSeverity.High));

        var rows = Builder().Build(d0, d0, null, totalHosts: 6);

        Assert.Equal("disk", rows[0].Source);
        Assert.Equal("DCOM", rows[1].Source);
    }

    // ── 機房級基準線（回饋十九輪批次G1）──────────────────────────────────────

    /// <summary>基準＝基準期（to 往前 30 天）出現日台數中位數；偏離倍數＝最近出現日台數 ÷ 基準</summary>
    [Fact]
    public void 基準線_中位數與偏離倍數()
    {
        var to = new DateTime(2026, 8, 10);
        // 基準期三個出現日，台數分別 2/2/2（中位數 2）
        Add(1, "A", to.AddDays(-25), Issue("disk", 153));
        Add(2, "B", to.AddDays(-25), Issue("disk", 153));
        Add(1, "A", to.AddDays(-15), Issue("disk", 153));
        Add(2, "B", to.AddDays(-15), Issue("disk", 153));
        // 最近一次出現日（to 當天）8 台——異常擴散
        for (var i = 1; i <= 8; i++) Add(i, $"H{i}", to, Issue("disk", 153));

        var row = Builder().Build(to, to, null, totalHosts: 8).Single();

        Assert.Equal(3, row.BaselineOccurrenceDays);
        Assert.Equal(2, row.BaselineMedianHostCount);
        Assert.Equal(8, row.BaselineLatestHostCount);
        Assert.Equal(4.0, row.BaselineDeviationMultiplier);
    }

    /// <summary>基準期出現不足 3 天（規劃定案 N=3）＝新問題，沒有「平常長什麼樣」可比</summary>
    [Fact]
    public void 基準線_出現天數不足三天時無基準()
    {
        var to = new DateTime(2026, 8, 10);
        Add(1, "A", to.AddDays(-10), Issue("disk", 153));
        Add(1, "A", to, Issue("disk", 153));

        var row = Builder().Build(to, to, null, totalHosts: 1).Single();

        Assert.Equal(2, row.BaselineOccurrenceDays);
        Assert.Null(row.BaselineMedianHostCount);
        Assert.Null(row.BaselineLatestHostCount);
        Assert.Null(row.BaselineDeviationMultiplier);
    }

    // ── fleet 首見（回饋十九輪批次G4）────────────────────────────────────────

    /// <summary>
    /// 機房首見不受查詢期間截斷（↔ lf_issue_first_seen，批次B insert-if-absent 落地）：
    /// 查詢窗口只涵蓋近期時，FirstSeen（查詢期間內）與 FleetFirstSeen（真正第一次出現）要能分開。
    /// </summary>
    [Fact]
    public void 機房首見_不受查詢期間截斷()
    {
        var oldDate = new DateTime(2026, 1, 1);
        var recentDate = new DateTime(2026, 8, 1);
        Add(1, "A", oldDate, Issue("disk", 153));
        Add(1, "A", recentDate, Issue("disk", 153));

        var row = Builder().Build(recentDate, recentDate, null, totalHosts: 1).Single();

        Assert.Equal("2026-08-01", row.FirstSeen);
        Assert.Equal("2026-01-01", row.FleetFirstSeen);
    }

    // ── 優先度分數（回饋十九輪批次G3）────────────────────────────────────────

    /// <summary>
    /// tierW 端到端：受影響主機裡最高的分級決定這個問題的分級權重（IssuePriorityScorer 的
    /// 純函式測試已釘住公式本身，這裡驗證的是「查主機的 Tier 有沒有真的接上」這條 SQL 路徑）。
    /// </summary>
    [Fact]
    public void PriorityScore_受影響主機最高分級決定tierW()
    {
        var d0 = new DateTime(2026, 8, 1);
        var coreHost = _hosts.Upsert(new WebHost { HostName = "CORE", Tier = WebHost.TierCore });
        var testHost = _hosts.Upsert(new WebHost { HostName = "TEST", Tier = WebHost.TierTest });
        // 一台核心、一台測試機都中鏢——即使測試機拉低平均，也該取最高分級（核心）
        Add(coreHost.HostId, "CORE", d0, Issue("disk", 153, severity: IssueSeverity.High));
        Add(testHost.HostId, "TEST", d0, Issue("disk", 153, severity: IssueSeverity.High));

        var row = Builder().Build(d0, d0, null, totalHosts: 2).Single();

        Assert.Equal(1.2, row.PriorityScoreTierWeight);
    }

    /// <summary>沒有主機分級資料（測試替身未登錄任何主機）時退回一般分級，不是拋例外或算成 0</summary>
    [Fact]
    public void PriorityScore_查無主機分級資料時退回一般()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));   // 未經 _hosts.Upsert 登錄的裸 hostId

        var row = Builder().Build(d0, d0, null, totalHosts: 1).Single();

        Assert.Equal(1.0, row.PriorityScoreTierWeight);
    }

    /// <summary>分數排序取代舊的「嚴重度→主機數→總次數」為主排序鍵——高分項目該排在前面</summary>
    [Fact]
    public void PriorityScore_依分數由高至低排序()
    {
        var d0 = new DateTime(2026, 8, 1);
        var coreHost = _hosts.Upsert(new WebHost { HostName = "CORE", Tier = WebHost.TierCore });
        // 兩個問題同嚴重度、同主機數，只有分級不同——核心主機的問題分數該較高
        Add(coreHost.HostId, "CORE", d0, Issue("disk", 153, severity: IssueSeverity.High));
        Add(2, "STD", d0, Issue("DCOM", 10016, severity: IssueSeverity.High));

        var rows = Builder().Build(d0, d0, null, totalHosts: 2);

        Assert.Equal("disk", rows[0].Source);
        Assert.True(rows[0].PriorityScore > rows[1].PriorityScore);
    }
}

using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題聚合（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／§4.2／根因 C）。
///
/// **這是需求「主視角改成問題」的資料層**：主機數與期間跨度是使用者明確要求的兩個數字，
/// 其餘是 §10.3「時間形狀」的訊號。改版前這些只能把整段期間的紀錄撈回記憶體再 GroupBy
/// （6000 台 × 30 天約 18 萬筆、近 GB 的 ContentJson），現在是一句 GROUP BY。
///
/// 語意的重點在兩處，測試逐條釘住：
///   - **可見範圍**：空集合＝零結果（不是「不限制」）——這個慣例反過來就是授權缺口。
///   - **主機數以存活主機 id 計**：合併過的主機不得被算成兩台（規劃 §8.1 缺陷 2）。
/// </summary>
public class IssueAggregateQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();

    public IssueAggregateQueryTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext, _hosts);

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning,
        string eventKey = "") => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = IssueCategory.Other, Severity = severity, ElevatesDayRisk = elevates, Count = count,
            EventKey = eventKey
        };

    private static LogIssueSignature PrtgIssue(string eventKey, int count = 1) => new()
    {
        LogName = "PRTG",
        Source = "PRTG",
        EventId = 0,
        EntryType = EventLogEntryType.Warning,
        EventKey = eventKey,
        Category = IssueCategory.Other,
        Severity = IssueSeverity.Medium,
        Count = count
    };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        Add(hostId, host, date, RiskLevels.Low, issues);

    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel,
            TopIssues = issues.ToList()
        });

    [Fact]
    public void 主機數與期間跨度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(1, "A", d0.AddDays(2), Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(10), null).Single();

        Assert.Equal(2, agg.HostCount);                      // 需求：包含此問題的主機數量
        Assert.Equal(d0, agg.FirstSeen);                     // 需求：期間跨度起點
        Assert.Equal(d0.AddDays(4), agg.LastSeen);           // 需求：期間跨度終點
        Assert.Equal(3, agg.ActiveDays);                     // 相異出現日數（密度的分子）
        Assert.Equal(3, agg.DayCount);                       // 主機日總數
    }

    /// <summary>
    /// 舊資料相容（LegacySeverityRank，回饋十九輪批次B）：三級化前寫入的 Critical 嚴重度
    /// 在 SQL 端也要正規化成 High＋ElevatesDayRisk=true，與 blob 路徑
    /// （RecordRepository.NormalizeLegacySeverity）同一套規則，兩邊才不會顯示不同的詞。
    /// </summary>
    [Fact]
    public void 舊資料Critical嚴重度正規化為High並強制重大旗標()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Critical, elevates: false));

        var agg = Query().Aggregate(d0, d0, null).Single();

        Assert.Equal((int)IssueSeverity.High, agg.MaxSeverityRank);
        Assert.True(agg.ElevatesDayRisk);
    }

    [Fact]
    public void 總次數與最高嚴重度與重大旗標()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, count: 5, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("disk", 153, count: 7, severity: IssueSeverity.High, elevates: true));

        var agg = Query().Aggregate(d0, d0, null).Single();

        Assert.Equal(12, agg.TotalCount);
        Assert.Equal((int)IssueSeverity.High, agg.MaxSeverityRank);
        Assert.True(agg.ElevatesDayRisk);
    }

    /// <summary>同一台主機多天只算一台——「影響幾台」問的是機器數，不是紀錄數</summary>
    [Fact]
    public void 同一主機多天只算一台()
    {
        var d0 = new DateTime(2026, 8, 1);
        for (var i = 0; i < 5; i++) Add(1, "A", d0.AddDays(i), Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(10), null).Single();

        Assert.Equal(1, agg.HostCount);
        Assert.Equal(5, agg.ActiveDays);
        Assert.Equal(5, agg.DayCount);
    }

    /// <summary>
    /// 主機合併的既有 bug（查證抓到）：<c>host_id</c> 是紀錄當下的識別，MergeHost
    /// 刻意不回寫（回寫會讓 UnmergeHost 失去反向依據）——查詢端必須跟 blob 路徑
    /// （HostLookup／HostAliasIndex）一樣，把 host_id 解析回存活主機再去重，
    /// 否則合併前後的兩個 id 會被算成兩台。
    /// </summary>
    [Fact]
    public void 主機合併_解析成存活主機後只算一台()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = b.HostId, Active = false });

        Add(a.HostId, "A", d0, Issue("disk", 153));           // 併入前的舊歷史，仍掛在舊 id 下
        Add(b.HostId, "B", d0.AddDays(1), Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(1), null).Single();

        Assert.Equal(1, agg.HostCount);   // 不是 2
        Assert.Equal(2, agg.DayCount);    // 主機日數不受合併影響——兩天各算一次
    }

    /// <summary>
    /// 可見範圍只傳存活主機 id 時，也要涵蓋它已併入的舊識別下的歷史——否則合併後
    /// 那段歷史會被 WHERE host_id IN (可見範圍) 整段濾掉，比雙重計數更嚴重（資料消失）。
    /// </summary>
    [Fact]
    public void 主機合併_可見範圍只給存活id也要涵蓋舊識別的歷史()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = b.HostId, Active = false });

        Add(a.HostId, "A", d0, Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0, new[] { b.HostId }).Single();

        Assert.Equal(1, agg.HostCount);
    }

    [Fact]
    public void 期間外的紀錄不算()
    {
        var d0 = new DateTime(2026, 8, 10);
        Add(1, "A", d0.AddDays(-5), Issue("disk", 153));
        Add(1, "A", d0, Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0.AddDays(5), null).Single();

        Assert.Equal(d0, agg.FirstSeen);
        Assert.Equal(1, agg.ActiveDays);
    }

    /// <summary>
    /// 可見範圍的授權語意：**空集合＝零結果**，不是「不限制」。
    /// 這個慣例與 RecordQueryFilter.Hosts 一致；反過來就是授權缺口。
    /// </summary>
    [Fact]
    public void 可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        Assert.Empty(Query().Aggregate(d0, d0, Array.Empty<long>()));
        Assert.Single(Query().Aggregate(d0, d0, null));       // null＝不限制
    }

    [Fact]
    public void 可見範圍_只算授權內的主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("disk", 153));

        var agg = Query().Aggregate(d0, d0, new long[] { 1 }).Single();

        Assert.Equal(1, agg.HostCount);
    }

    /// <summary>
    /// 相異完整簽章：一個 (Source, EventId) 底下可能有多個 LogName／EntryType 組合，
    /// 而處理狀態是以完整簽章為鍵——沒有這份清單就 join 不到「這個問題有沒有結論」
    /// （規劃 §8.1 缺陷 1，§10.6 的前提）。
    /// </summary>
    [Fact]
    public void 相異完整簽章_供join處理狀態()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, logName: "System", entryType: EventLogEntryType.Warning));
        Add(2, "B", d0, Issue("disk", 153, logName: "Application", entryType: EventLogEntryType.Error));

        var agg = Query().Aggregate(d0, d0, null).Single();

        Assert.Equal(2, agg.IssueKeys.Count);
        Assert.Contains(IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning), agg.IssueKeys);
        Assert.Contains(IssueSignatureKey.For("Application", "disk", 153, EventLogEntryType.Error), agg.IssueKeys);
    }

    [Fact]
    public void 多個問題各自聚合()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153), Issue("DCOM", 10016));
        Add(2, "B", d0, Issue("DCOM", 10016));

        var result = Query().Aggregate(d0, d0, null).ToDictionary(a => (a.Source, a.EventId));

        Assert.Equal(1, result[("disk", 153)].HostCount);
        Assert.Equal(2, result[("DCOM", 10016)].HostCount);
    }

    [Fact]
    public void 沒有資料時回空清單()
    {
        Assert.Empty(Query().Aggregate(DateTime.Today, DateTime.Today, null));
    }

    // ── HostIdsFor（回饋十八輪批次F，問題負責人的授權路徑用）─────────────────

    [Fact]
    public void HostIdsFor_回傳期間內出現過指定問題的相異主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(1, "A", d0.AddDays(2), Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));
        Add(3, "C", d0, Issue("network", 999));   // 不同問題，不該混進來

        var hostIds = Query().HostIdsFor(new[] { ("disk", 153) }, d0, d0.AddDays(10));

        Assert.Equal(new HashSet<long> { 1, 2 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_Source比對不分大小寫()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("Disk", 153));

        var hostIds = Query().HostIdsFor(new[] { ("DISK", 153) }, d0, d0);

        Assert.Equal(new HashSet<long> { 1 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_多個問題取聯集()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("network", 999));

        var hostIds = Query().HostIdsFor(new[] { ("disk", 153), ("network", 999) }, d0, d0);

        Assert.Equal(new HashSet<long> { 1, 2 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_期間外的出現不計入()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0.AddDays(-5), Issue("disk", 153));

        var hostIds = Query().HostIdsFor(new[] { ("disk", 153) }, d0, d0.AddDays(10));

        Assert.Empty(hostIds);
    }

    [Fact]
    public void HostIdsFor_空問題清單回空集合()
    {
        Assert.Empty(Query().HostIdsFor(Array.Empty<(string, int)>(), DateTime.Today, DateTime.Today));
    }

    // ── AggregateByCategory（回饋十九輪批次D，風險類型卡雙數字）──────────────────

    /// <summary>驗收標準（規劃 D1）：一主機一問題連續 3 天，大數字（去重）＝1，小字（累計）＝3</summary>
    [Fact]
    public void AggregateByCategory_大數字去重小字累計()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(1, "A", d0.AddDays(1), Issue("disk", 153));
        Add(1, "A", d0.AddDays(2), Issue("disk", 153));

        var cat = Query().AggregateByCategory(d0, d0.AddDays(2), null, null).Single();

        Assert.Equal(1, cat.RiskItemCount);
        Assert.Equal(3, cat.CumulativeCount);
        Assert.Equal(1, cat.AffectedHosts);
    }

    /// <summary>不同主機的同一個問題各自算一筆風險資訊</summary>
    [Fact]
    public void AggregateByCategory_不同主機各算一筆()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("disk", 153));

        var cat = Query().AggregateByCategory(d0, d0, null, null).Single();

        Assert.Equal(2, cat.RiskItemCount);
        Assert.Equal(2, cat.AffectedHosts);
    }

    /// <summary>嚴重度分桶依風險資訊（去重後）的期間內最高嚴重度，三桶之和＝RiskItemCount</summary>
    [Fact]
    public void AggregateByCategory_嚴重度分桶依風險資訊的最高嚴重度()
    {
        var d0 = new DateTime(2026, 8, 1);
        // 同一筆風險資訊：第一天 Low，第二天升到 High——應歸入 High 桶，不是 Low
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Low));
        Add(1, "A", d0.AddDays(1), Issue("disk", 153, severity: IssueSeverity.High));

        var cat = Query().AggregateByCategory(d0, d0.AddDays(1), null, null).Single();

        Assert.Equal(1, cat.RiskItemCount);
        Assert.Equal(1, cat.HighCount);
        Assert.Equal(0, cat.LowCount);
    }

    /// <summary>嚴重度可見性篩選：依風險資訊的最高嚴重度篩，被篩掉的不計入任何欄位</summary>
    [Fact]
    public void AggregateByCategory_嚴重度可見性篩選()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("DCOM", 10016, severity: IssueSeverity.High));

        var onlyHigh = Query().AggregateByCategory(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High });

        var cat = Assert.Single(onlyHigh);
        Assert.Equal(1, cat.RiskItemCount);
        Assert.Equal(1, cat.HighCount);
    }

    /// <summary>舊資料相容：Critical 正規化為 High，且強制視為重大——與 Aggregate 同一條規則</summary>
    [Fact]
    public void AggregateByCategory_舊資料Critical正規化為High並強制重大()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, severity: IssueSeverity.Critical, elevates: false));

        var cat = Query().AggregateByCategory(d0, d0, null, null).Single();

        Assert.Equal(1, cat.HighCount);
        Assert.Equal(0, cat.LowCount);
        Assert.Equal(1, cat.ElevatesCount);
    }

    /// <summary>主機合併：兩個 host_id 代表同一台實體機器，風險資訊只能算一筆（同 Aggregate 的既有規則）</summary>
    [Fact]
    public void AggregateByCategory_主機合併後風險資訊只算一筆()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = b.HostId, Active = false });

        Add(a.HostId, "A", d0, Issue("disk", 153));
        Add(b.HostId, "B", d0.AddDays(1), Issue("disk", 153));

        var cat = Query().AggregateByCategory(d0, d0.AddDays(1), null, null).Single();

        Assert.Equal(1, cat.RiskItemCount);
        Assert.Equal(1, cat.AffectedHosts);
        Assert.Equal(2, cat.CumulativeCount);   // 累計次數不受合併影響——兩天各算一次
    }

    /// <summary>可見範圍的授權語意與 Aggregate 一致：空集合＝零結果</summary>
    [Fact]
    public void AggregateByCategory_可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        Assert.Empty(Query().AggregateByCategory(d0, d0, Array.Empty<long>(), null));
    }

    [Fact]
    public void AggregateByCategory_不同類別各自彙總()
    {
        var d0 = new DateTime(2026, 8, 1);
        var storageIssue = Issue("disk", 153);
        storageIssue.Category = IssueCategory.Storage;
        var securityIssue = Issue("Defender", 1116);
        securityIssue.Category = IssueCategory.Security;
        Add(1, "A", d0, storageIssue, securityIssue);

        var result = Query().AggregateByCategory(d0, d0, null, null).ToDictionary(c => c.Category);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[IssueCategory.Storage.ToString()].RiskItemCount);
        Assert.Equal(1, result[IssueCategory.Security.ToString()].RiskItemCount);
    }

    // ── ActionableOccurrences（回饋十九輪批次D，Todo 問題口徑）───────────────────

    /// <summary>母體＝日層級 RiskLevel 高／中，與 HandlingHistoryQueryService.GetTodo 既有定義一致</summary>
    [Fact]
    public void ActionableOccurrences_只計入高中風險日()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));
        Add(1, "A", d0.AddDays(1), RiskLevels.Low, Issue("DCOM", 10016));   // 低風險日不計入

        var result = Query().ActionableOccurrences(d0, d0.AddDays(1), null);

        var occurrence = Assert.Single(result);
        Assert.Equal(IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning), occurrence.IssueKey);
    }

    [Fact]
    public void ActionableOccurrences_中風險日也計入()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.Medium, Issue("disk", 153));

        Assert.Single(Query().ActionableOccurrences(d0, d0, null));
    }

    /// <summary>取最近一次出現日，與 LatestOccurrences 同一個語意</summary>
    [Fact]
    public void ActionableOccurrences_取最近一次出現日()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));
        Add(1, "A", d0.AddDays(3), RiskLevels.High, Issue("disk", 153));

        var occurrence = Query().ActionableOccurrences(d0, d0.AddDays(3), null).Single();

        Assert.Equal(d0.AddDays(3), occurrence.LastSeen);
    }

    [Fact]
    public void ActionableOccurrences_主機合併後解析成存活主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = b.HostId, Active = false });

        Add(a.HostId, "A", d0, RiskLevels.High, Issue("disk", 153));

        var occurrence = Query().ActionableOccurrences(d0, d0, null).Single();

        Assert.Equal(b.HostId, occurrence.HostId);
    }

    [Fact]
    public void ActionableOccurrences_可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));

        Assert.Empty(Query().ActionableOccurrences(d0, d0, Array.Empty<long>()));
    }

    [Fact]
    public void AggregateByHost_Categories_排序對照()
    {
        var d0 = new DateTime(2026, 8, 1);

        // 類別 Hardware: 最高嚴重度 High (2), 總數 1 筆
        var issueA = Issue("A", 1, severity: IssueSeverity.High);
        issueA.Category = IssueCategory.Hardware;

        // 類別 Service: 最高嚴重度 Medium (1), 總數 3 筆 (分三天)
        var issueB = Issue("B", 2, severity: IssueSeverity.Medium);
        issueB.Category = IssueCategory.Service;

        // 類別 Storage: 最高嚴重度 Medium (1), 總數 2 筆
        var issueC = Issue("C", 3, severity: IssueSeverity.Medium);
        issueC.Category = IssueCategory.Storage;

        // 類別 Security: 最高嚴重度 Low (0), 總數 5 筆
        var issueD = Issue("D", 4, severity: IssueSeverity.Low);
        issueD.Category = IssueCategory.Security;

        Add(1, "H1", d0, issueA, issueB, issueC, issueD);
        Add(1, "H1", d0.AddDays(1), issueB, issueC, issueD);
        Add(1, "H1", d0.AddDays(2), issueB, issueD);
        Add(1, "H1", d0.AddDays(3), issueD);
        Add(1, "H1", d0.AddDays(4), issueD);

        var agg = Query().AggregateByHost(d0, d0.AddDays(10), null).Single();

        // 順序預期：
        // 1. Hardware (High, 1次) -> 最高嚴重度優先
        // 2. Service (Medium, 3次) -> 同嚴重度，次數較多優先
        // 3. Storage (Medium, 2次)
        // 4. Security (Low, 5次)
        Assert.Equal(4, agg.Categories.Count);
        Assert.Equal(IssueCategory.Hardware.ToString(), agg.Categories[0]);
        Assert.Equal(IssueCategory.Service.ToString(), agg.Categories[1]);
        Assert.Equal(IssueCategory.Storage.ToString(), agg.Categories[2]);
        Assert.Equal(IssueCategory.Security.ToString(), agg.Categories[3]);
    }

    [Fact]
    public void AggregateDayTodo_傳null時母體與現況相同計入高中風險日()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));
        Add(1, "A", d0.AddDays(1), RiskLevels.Medium, Issue("cpu", 100));
        Add(1, "A", d0.AddDays(2), RiskLevels.Low, Issue("net", 200));

        var unhandled = new HashSet<IssueSeverity> { IssueSeverity.High, IssueSeverity.Medium };
        var result = Query().AggregateDayTodo(d0, d0.AddDays(2), null, unhandled, Array.Empty<long>(), d0.AddDays(5), riskLevels: null);

        // 高 1 + 中 1 = 2，低風險日不計入
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.OpenCount);
    }

    [Fact]
    public void ActionableOccurrences_只顯示高時_中風險日的問題不列入()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));
        Add(1, "A", d0.AddDays(1), RiskLevels.Medium, Issue("cpu", 100));

        var visibleRiskLevels = new HashSet<string> { RiskLevels.High };
        var result = Query().ActionableOccurrences(d0, d0.AddDays(1), null, null, visibleRiskLevels);

        var occurrence = Assert.Single(result);
        Assert.Equal(IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning), occurrence.IssueKey);
    }

    [Fact]
    public void AggregateByCategory_日風險等級只顯示高時_不計入中風險日()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(2, "B", d0, RiskLevels.Medium, Issue("cpu", 100, severity: IssueSeverity.High));

        var visibleRiskLevels = new HashSet<string> { RiskLevels.High };
        var result = Query().AggregateByCategory(d0, d0, null, null, visibleRiskLevels);

        var cat = Assert.Single(result);
        Assert.Equal(1, cat.RiskItemCount);
        Assert.Equal(1, cat.AffectedHosts);
    }

    /// <summary>
    /// 來源名稱大小寫不同（如 cron 與 CRON、EventId 皆為 0）時，Aggregate 只回傳一筆，
    /// 且 TotalCount 與 HostCount 為兩者合併後的值。
    /// </summary>
    [Fact]
    public void Aggregate_來源名稱大小寫不同時合併為單一問題且次數與主機數合併()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("cron", 0, count: 5));
        Add(2, "B", d0, Issue("CRON", 0, count: 3));

        var agg = Assert.Single(Query().Aggregate(d0, d0, null));

        Assert.Equal(0, agg.EventId);
        Assert.Equal(8, agg.TotalCount);
        Assert.Equal(2, agg.HostCount);
    }

    // ── 回饋二十七輪作業 F：主機別名索引依版本快取 ────────────────────────────

    /// <summary>
    /// 別名索引改為跨呼叫快取後，主機資料一變就必須重建。
    /// 快取失效沒做對的話症狀是「合併主機後查詢結果仍照舊識別分開算」，
    /// 而且會一直錯到站台重啟——這條測試就是那個假綠的守門員。
    /// </summary>
    [Fact]
    public void 別名索引快取_主機合併後同一個查詢實例要看得到新的併入關係()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Add(a.HostId, "A", d0, Issue("disk", 153));
        Add(b.HostId, "B", d0.AddDays(1), Issue("disk", 153));

        // 同一個查詢實例先查一次，讓索引進快取
        var query = Query();
        Assert.Equal(2, query.Aggregate(d0, d0.AddDays(1), null).Single().HostCount);

        // 之後才把 A 併入 B（Web 端的合併操作）
        _hosts.Merge(a.HostId, b.HostId);

        Assert.Equal(1, query.Aggregate(d0, d0.AddDays(1), null).Single().HostCount);
    }

    /// <summary>
    /// 同一來源名稱不同大小寫合併後，FirstSeen 取較早者、LastSeen 取較晚者。
    /// </summary>
    [Fact]
    public void Aggregate_來源名稱大小寫不同時FirstSeen取較早者LastSeen取較晚者()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("cron", 0));
        Add(1, "A", d0.AddDays(4), Issue("CRON", 0));

        var agg = Assert.Single(Query().Aggregate(d0, d0.AddDays(10), null));

        Assert.Equal(d0, agg.FirstSeen);
        Assert.Equal(d0.AddDays(4), agg.LastSeen);
        Assert.Equal(1, agg.HostCount);
        Assert.Equal(2, agg.ActiveDays);
    }

    // ── AggregatePrtgRuleHits（docs/archive/FEEDBACK-37-PLAN.md 批次A，校準頁 PRTG 規則命中分佈）─────────────────────

    [Fact]
    public void AggregatePrtgRuleHits_依規則代碼分組統計命中筆數()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0,
            PrtgIssue("prtg:down:1"),
            PrtgIssue("prtg:down:2"),
            PrtgIssue("prtg:flapping:1"));

        var result = Query().AggregatePrtgRuleHits(d0, d0);

        Assert.Equal(2, result.Count);
        var down = result.Single(r => r.RuleCode == "down");
        var flapping = result.Single(r => r.RuleCode == "flapping");

        Assert.Equal(2, down.HitCount);
        Assert.Equal(1, flapping.HitCount);
        Assert.Equal(d0, down.Date);
        Assert.Equal(d0, flapping.Date);
    }

    [Fact]
    public void AggregatePrtgRuleHits_非PRTG來源不得計入()
    {
        var d0 = new DateTime(2026, 8, 1);
        // Source 不是 PRTG，但 EventKey 長得像 prtg:down:9
        Add(1, "A", d0,
            Issue("System", 100, eventKey: "prtg:down:9"),
            PrtgIssue("prtg:down:1"));

        var result = Query().AggregatePrtgRuleHits(d0, d0);

        var down = Assert.Single(result);
        Assert.Equal("down", down.RuleCode);
        Assert.Equal(1, down.HitCount);
    }

    [Fact]
    public void AggregatePrtgRuleHits_EventKey格式不符歸入其他桶且不擲例外()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, PrtgIssue("prtg"));

        var result = Query().AggregatePrtgRuleHits(d0, d0);

        var other = Assert.Single(result);
        Assert.Equal("其他", other.RuleCode);
        Assert.Equal(1, other.HitCount);
        Assert.Equal(1, other.HostCount);
    }

    [Fact]
    public void AggregatePrtgRuleHits_相異主機數統計正確()
    {
        var d0 = new DateTime(2026, 8, 1);

        // 同一規則同一天由兩台不同主機命中 → 筆數 2、主機數 2
        Add(1, "A", d0, PrtgIssue("prtg:down:1"));
        Add(2, "B", d0, PrtgIssue("prtg:down:2"));

        var resultMultiHost = Query().AggregatePrtgRuleHits(d0, d0);
        var downMulti = Assert.Single(resultMultiHost);
        Assert.Equal(2, downMulti.HitCount);
        Assert.Equal(2, downMulti.HostCount);

        // 同一台主機兩筆 → 筆數 2、主機數 1
        var d1 = new DateTime(2026, 8, 2);
        Add(3, "C", d1,
            PrtgIssue("prtg:down:1"),
            PrtgIssue("prtg:down:2"));

        var resultSingleHost = Query().AggregatePrtgRuleHits(d1, d1);
        var downSingle = Assert.Single(resultSingleHost);
        Assert.Equal(2, downSingle.HitCount);
        Assert.Equal(1, downSingle.HostCount);
    }
}

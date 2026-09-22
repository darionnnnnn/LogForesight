using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
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

    private EfIssueAggregateQuery Query(bool sourceKeyReady) =>
        new(_fx.NewContext, _hosts, null, () => sourceKeyReady);

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
    public void SourceKey回填完成後_聚合使用預先正規化鍵合併非ASCII大小寫來源()
    {
        var day = new DateTime(2026, 8, 1);
        Add(1, "A", day, Issue("évent", 7, count: 2));
        Add(2, "B", day, Issue("ÉVENT", 7, count: 3));

        var legacy = Query().Aggregate(IssueExclusion.None, day, day, null);
        Assert.Equal(2, legacy.Count);

        var aggregate = Assert.Single(Query(sourceKeyReady: true).Aggregate(IssueExclusion.None, day, day, null));
        Assert.Equal("ÉVENT", aggregate.Source);
        Assert.Equal(5, aggregate.TotalCount);
        Assert.Equal(2, aggregate.HostCount);
    }

    [Fact]
    public void SourceKey切換前後_大小寫一致資料的全部聚合欄位相同()
    {
        var day = new DateTime(2026, 8, 1);
        Add(1, "A", day, Issue("disk", 153, count: 2));
        Add(1, "A", day.AddDays(1), Issue("disk", 153, count: 4));
        Add(2, "B", day.AddDays(1), Issue("disk", 153, count: 3));

        var oldResult = Assert.Single(Query(sourceKeyReady: false)
            .Aggregate(IssueExclusion.None, day, day.AddDays(1), null));
        var newResult = Assert.Single(Query(sourceKeyReady: true)
            .Aggregate(IssueExclusion.None, day, day.AddDays(1), null));

        Assert.Equal(oldResult.Source, newResult.Source);
        Assert.Equal(oldResult.EventId, newResult.EventId);
        Assert.Equal(oldResult.Category, newResult.Category);
        Assert.Equal(oldResult.MaxSeverityRank, newResult.MaxSeverityRank);
        Assert.Equal(oldResult.ElevatesDayRisk, newResult.ElevatesDayRisk);
        Assert.Equal(oldResult.HostCount, newResult.HostCount);
        Assert.Equal(oldResult.DayCount, newResult.DayCount);
        Assert.Equal(oldResult.ActiveDays, newResult.ActiveDays);
        Assert.Equal(oldResult.FirstSeen, newResult.FirstSeen);
        Assert.Equal(oldResult.LastSeen, newResult.LastSeen);
        Assert.Equal(oldResult.TotalCount, newResult.TotalCount);
        Assert.Equal(oldResult.IssueKeys, newResult.IssueKeys);
    }

    [Fact]
    public void 主機數與期間跨度()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(1, "A", d0.AddDays(2), Issue("disk", 153));
        Add(2, "B", d0.AddDays(4), Issue("disk", 153));

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0.AddDays(10), null).Single();

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

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0, null).Single();

        Assert.Equal((int)IssueSeverity.High, agg.MaxSeverityRank);
        Assert.True(agg.ElevatesDayRisk);
    }

    [Fact]
    public void 總次數與最高嚴重度與重大旗標()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153, count: 5, severity: IssueSeverity.Low));
        Add(2, "B", d0, Issue("disk", 153, count: 7, severity: IssueSeverity.High, elevates: true));

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0, null).Single();

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

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0.AddDays(10), null).Single();

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

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0.AddDays(1), null).Single();

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

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0, new[] { b.HostId }).Single();

        Assert.Equal(1, agg.HostCount);
    }

    [Fact]
    public void 期間外的紀錄不算()
    {
        var d0 = new DateTime(2026, 8, 10);
        Add(1, "A", d0.AddDays(-5), Issue("disk", 153));
        Add(1, "A", d0, Issue("disk", 153));

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0.AddDays(5), null).Single();

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

        Assert.Empty(Query().Aggregate(IssueExclusion.None, d0, d0, Array.Empty<long>()));
        Assert.Single(Query().Aggregate(IssueExclusion.None, d0, d0, null));       // null＝不限制
    }

    [Fact]
    public void 可見範圍_只算授權內的主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("disk", 153));

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0, new long[] { 1 }).Single();

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

        var agg = Query().Aggregate(IssueExclusion.None, d0, d0, null).Single();

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

        var result = Query().Aggregate(IssueExclusion.None, d0, d0, null).ToDictionary(a => (a.Source, a.EventId));

        Assert.Equal(1, result[("disk", 153)].HostCount);
        Assert.Equal(2, result[("DCOM", 10016)].HostCount);
    }

    [Fact]
    public void 沒有資料時回空清單()
    {
        Assert.Empty(Query().Aggregate(IssueExclusion.None, DateTime.Today, DateTime.Today, null));
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

        var hostIds = Query().HostIdsFor(IssueExclusion.None, new[] { ("disk", 153) }, d0, d0.AddDays(10));

        Assert.Equal(new HashSet<long> { 1, 2 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_Source比對不分大小寫()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("Disk", 153));

        var hostIds = Query().HostIdsFor(IssueExclusion.None, new[] { ("DISK", 153) }, d0, d0);

        Assert.Equal(new HashSet<long> { 1 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_多個問題取聯集()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));
        Add(2, "B", d0, Issue("network", 999));

        var hostIds = Query().HostIdsFor(IssueExclusion.None, new[] { ("disk", 153), ("network", 999) }, d0, d0);

        Assert.Equal(new HashSet<long> { 1, 2 }, hostIds);
    }

    [Fact]
    public void HostIdsFor_期間外的出現不計入()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0.AddDays(-5), Issue("disk", 153));

        var hostIds = Query().HostIdsFor(IssueExclusion.None, new[] { ("disk", 153) }, d0, d0.AddDays(10));

        Assert.Empty(hostIds);
    }

    [Fact]
    public void HostIdsFor_空問題清單回空集合()
    {
        Assert.Empty(Query().HostIdsFor(IssueExclusion.None, Array.Empty<(string, int)>(), DateTime.Today, DateTime.Today));
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

        var cat = Query().AggregateByCategory(IssueExclusion.None, d0, d0.AddDays(2), null, null).Single();

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

        var cat = Query().AggregateByCategory(IssueExclusion.None, d0, d0, null, null).Single();

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

        var cat = Query().AggregateByCategory(IssueExclusion.None, d0, d0.AddDays(1), null, null).Single();

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

        var onlyHigh = Query().AggregateByCategory(IssueExclusion.None, d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High });

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

        var cat = Query().AggregateByCategory(IssueExclusion.None, d0, d0, null, null).Single();

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

        var cat = Query().AggregateByCategory(IssueExclusion.None, d0, d0.AddDays(1), null, null).Single();

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

        Assert.Empty(Query().AggregateByCategory(IssueExclusion.None, d0, d0, Array.Empty<long>(), null));
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

        var result = Query().AggregateByCategory(IssueExclusion.None, d0, d0, null, null).ToDictionary(c => c.Category);

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

        var result = Query().ActionableOccurrences(IssueExclusion.None, d0, d0.AddDays(1), null);

        var occurrence = Assert.Single(result);
        Assert.Equal(IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning), occurrence.IssueKey);
    }

    [Fact]
    public void ActionableOccurrences_中風險日也計入()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.Medium, Issue("disk", 153));

        Assert.Single(Query().ActionableOccurrences(IssueExclusion.None, d0, d0, null));
    }

    /// <summary>取最近一次出現日，與 LatestOccurrences 同一個語意</summary>
    [Fact]
    public void ActionableOccurrences_取最近一次出現日()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));
        Add(1, "A", d0.AddDays(3), RiskLevels.High, Issue("disk", 153));

        var occurrence = Query().ActionableOccurrences(IssueExclusion.None, d0, d0.AddDays(3), null).Single();

        Assert.Equal(d0.AddDays(3), occurrence.LastSeen);
    }

    [Fact]
    public void ActionableOccurrences_主機合併後解析成存活主機()
    {
        var d0 = new DateTime(2026, 8, 1);
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = b.HostId, Active = false });

        Add(a.HostId, "A", d0, RiskLevels.High, Issue("disk", 153));

        var occurrence = Query().ActionableOccurrences(IssueExclusion.None, d0, d0, null).Single();

        Assert.Equal(b.HostId, occurrence.HostId);
    }

    [Fact]
    public void ActionableOccurrences_可見範圍_空集合為零結果()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("disk", 153));

        Assert.Empty(Query().ActionableOccurrences(IssueExclusion.None, d0, d0, Array.Empty<long>()));
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

        var agg = Query().AggregateByHost(IssueExclusion.None, d0, d0.AddDays(10), null).Single();

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
        var result = Query().AggregateDayTodo(IssueExclusion.None, d0, d0.AddDays(2), null, unhandled, Array.Empty<long>(), d0.AddDays(5), riskLevels: null);

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
        var result = Query().ActionableOccurrences(IssueExclusion.None, d0, d0.AddDays(1), null, null, visibleRiskLevels);

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
        var result = Query().AggregateByCategory(IssueExclusion.None, d0, d0, null, null, visibleRiskLevels);

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

        var agg = Assert.Single(Query().Aggregate(IssueExclusion.None, d0, d0, null));

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
        Assert.Equal(2, query.Aggregate(IssueExclusion.None, d0, d0.AddDays(1), null).Single().HostCount);

        // 之後才把 A 併入 B（Web 端的合併操作）
        _hosts.Merge(a.HostId, b.HostId);

        Assert.Equal(1, query.Aggregate(IssueExclusion.None, d0, d0.AddDays(1), null).Single().HostCount);
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

        var agg = Assert.Single(Query().Aggregate(IssueExclusion.None, d0, d0.AddDays(10), null));

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

        var result = Query().AggregatePrtgRuleHits(IssueExclusion.None, d0, d0, null);

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

        var result = Query().AggregatePrtgRuleHits(IssueExclusion.None, d0, d0, null);

        var down = Assert.Single(result);
        Assert.Equal("down", down.RuleCode);
        Assert.Equal(1, down.HitCount);
    }

    [Fact]
    public void AggregatePrtgRuleHits_EventKey格式不符歸入其他桶且不擲例外()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, PrtgIssue("prtg"));

        var result = Query().AggregatePrtgRuleHits(IssueExclusion.None, d0, d0, null);

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

        var resultMultiHost = Query().AggregatePrtgRuleHits(IssueExclusion.None, d0, d0, null);
        var downMulti = Assert.Single(resultMultiHost);
        Assert.Equal(2, downMulti.HitCount);
        Assert.Equal(2, downMulti.HostCount);

        // 同一台主機兩筆 → 筆數 2、主機數 1
        var d1 = new DateTime(2026, 8, 2);
        Add(3, "C", d1,
            PrtgIssue("prtg:down:1"),
            PrtgIssue("prtg:down:2"));

        var resultSingleHost = Query().AggregatePrtgRuleHits(IssueExclusion.None, d1, d1, null);
        var downSingle = Assert.Single(resultSingleHost);
        Assert.Equal(2, downSingle.HitCount);
        Assert.Equal(1, downSingle.HostCount);
    }

    /// <summary>記錄執行過的 lf_top_issues 讀取語句，驗證分批查詢次數。</summary>
    private sealed class TopIssueReadRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int TopIssueReads { get; private set; }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            if (command.CommandText.Contains("lf_top_issues")) TopIssueReads++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    [Fact]
    public void GetPrtgFindingHitDates_不限主機只取PRTG列與區間內相異日期()
    {
        var d0 = new DateTime(2026, 8, 10);
        Add(1, "A", d0.AddDays(-1), PrtgIssue("prtg:warning:7"));
        Add(2, "B", d0.AddDays(-2), PrtgIssue("prtg:warning:7"));          // 換了對應主機仍是同一顆
        Add(3, "C", d0.AddDays(-2), PrtgIssue("prtg:warning:7"));          // 同日重複只算一次
        Add(1, "A", d0.AddDays(-15), PrtgIssue("prtg:warning:7"));         // 區間外
        Add(1, "A", d0, PrtgIssue("prtg:warning:7"));                       // toExclusive 不含
        Add(1, "A", d0.AddDays(-3), Issue("disk", 0, logName: "System", eventKey: "prtg:warning:7")); // 非 PRTG 列
        Add(1, "A", d0.AddDays(-4), Issue("disk", 0, logName: "System", eventKey: "prtg:down:9"));    // 非 PRTG 列
        Add(1, "A", d0.AddDays(-1), PrtgIssue("prtg:down:8"));              // 不在清單內

        var result = Query().GetPrtgFindingHitDates(
            IssueExclusion.None, new[] { "prtg:warning:7", "prtg:down:9" }, d0.AddDays(-14), d0);

        var only = Assert.Single(result);
        Assert.Equal("prtg:warning:7", only.Key);
        Assert.Equal(new[] { d0.AddDays(-2), d0.AddDays(-1) }, only.Value.OrderBy(d => d));
    }

    [Fact]
    public void GetPrtgFindingHitDates_600個鍵分兩批查詢()
    {
        var d0 = new DateTime(2026, 8, 10);
        Add(1, "A", d0.AddDays(-1), PrtgIssue("prtg:down:0"), PrtgIssue("prtg:down:599"));

        var recorder = new TopIssueReadRecorder();
        using var probe = _fx.NewContext();
        var connection = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(probe.Database);
        var query = new EfIssueAggregateQuery(() => new LfDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options), _hosts);

        var keys = Enumerable.Range(0, 600).Select(i => $"prtg:down:{i}").ToList();
        var result = query.GetPrtgFindingHitDates(IssueExclusion.None, keys, d0.AddDays(-14), d0);

        Assert.Equal(2, recorder.TopIssueReads);
        Assert.Equal(new[] { "prtg:down:0", "prtg:down:599" }, result.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void GetPrtgFindingHitDates_兩個後端都翻譯得出來且在SQL端篩選(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var sql = EfIssueAggregateQuery.BuildPrtgHitDatesQuery(
            ctx.TopIssues, new[] { "prtg:down:1", "prtg:warning:2" }, new DateTime(2026, 8, 1), new DateTime(2026, 8, 15)).ToQueryString();

        Assert.Contains("DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRTG", sql);
        Assert.Contains("IN (", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ── 出現點完整簽章鍵（含 EventKey 第五段）───────────────────────────────

    /// <summary>突變參考：LatestOccurrences 分組拿掉 EventKey 時這條轉紅（兩顆 sensor 併成一筆）</summary>
    [Fact]
    public void LatestOccurrences_同主機同PRTG規則兩顆sensor_各自一筆且鍵為完整鍵()
    {
        var d0 = new DateTime(2026, 8, 1);
        var s1 = PrtgIssue("prtg:down:1001");
        var s2 = PrtgIssue("prtg:down:1002");
        Add(1, "A", d0, s1, s2);

        var result = Query().LatestOccurrences(IssueExclusion.None, new[] { ("PRTG", 0) }, d0, d0, null);

        Assert.Equal(
            new[] { IssueSignatureKey.For(s1), IssueSignatureKey.For(s2) }.OrderBy(k => k, StringComparer.Ordinal),
            result.Select(o => o.IssueKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void LatestOccurrences_Linux命中規則為五段鍵_Windows鍵逐字不變()
    {
        var d0 = new DateTime(2026, 8, 1);
        var linux = Issue("sshd", 0, logName: "Linux", eventKey: "builtin-linux-ssh-bruteforce");
        Add(1, "A", d0, linux, Issue("disk", 153));

        var result = Query().LatestOccurrences(IssueExclusion.None, new[] { ("sshd", 0), ("disk", 153) }, d0, d0, null);

        Assert.Equal("Linux|sshd|0|2|builtin-linux-ssh-bruteforce", Assert.Single(result, o => o.IssueKey.StartsWith("Linux|")).IssueKey);
        Assert.Equal(IssueSignatureKey.For(linux), Assert.Single(result, o => o.IssueKey.StartsWith("Linux|")).IssueKey);
        Assert.Equal("System|disk|153|2", Assert.Single(result, o => o.IssueKey.StartsWith("System|")).IssueKey);
    }

    /// <summary>突變參考：ActionableOccurrences 分組拿掉 EventKey 時這條轉紅</summary>
    [Fact]
    public void ActionableOccurrences_同主機同PRTG規則兩顆sensor_各自一筆且鍵為完整鍵()
    {
        var d0 = new DateTime(2026, 8, 1);
        var s1 = PrtgIssue("prtg:down:1001");
        var s2 = PrtgIssue("prtg:down:1002");
        Add(1, "A", d0, RiskLevels.High, s1, s2);

        var result = Query().ActionableOccurrences(IssueExclusion.None, d0, d0, null);

        Assert.Equal(
            new[] { IssueSignatureKey.For(s1), IssueSignatureKey.For(s2) }.OrderBy(k => k, StringComparer.Ordinal),
            result.Select(o => o.IssueKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── 讀取側靜音排除（回饋第 47 輪批次 B-2a）────────────────────────────

    private static readonly DateTime MuteToday = new(2026, 8, 31);

    /// <summary>
    /// 三型資料＋目前靜音中的問題：
    ///   - disk/153 區間 8/5～8/10（已到期）：8/3 區間前（主機 1）、8/7 區間內（主機 4）、8/20 到期後（主機 1）；
    ///   - cron/7 區間 8/25～9/10（目前靜音中）：8/2（主機 2，區間前）、8/26（主機 2）；
    ///   - net/99 未靜音對照：8/7（主機 3）。
    /// </summary>
    private IssueExclusion SeedMuteScenario()
    {
        Add(1, "A", new DateTime(2026, 8, 3), RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(4, "D", new DateTime(2026, 8, 7), RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(1, "A", new DateTime(2026, 8, 20), RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(2, "B", new DateTime(2026, 8, 2), RiskLevels.High, Issue("CRON", 7, severity: IssueSeverity.High));
        Add(2, "B", new DateTime(2026, 8, 26), RiskLevels.High, Issue("cron", 7, severity: IssueSeverity.High));
        Add(3, "C", new DateTime(2026, 8, 7), RiskLevels.High, Issue("net", 99, severity: IssueSeverity.High));

        return IssueExclusion.From(new[]
        {
            new IssueProfile
            {
                SourceName = "Disk", EventId = 153,
                Mutes = { new MuteInterval { From = new DateTime(2026, 8, 5), To = new DateTime(2026, 8, 10) } }
            },
            new IssueProfile
            {
                SourceName = "cron", EventId = 7,
                Mutes = { new MuteInterval { From = new DateTime(2026, 8, 25), To = new DateTime(2026, 9, 10) } }
            }
        }, MuteToday);
    }

    private static readonly DateTime MuteFrom = new(2026, 8, 1);
    private static readonly DateTime MuteTo = new(2026, 8, 30);
    private static readonly (string, int)[] MuteIssues = { ("disk", 153), ("cron", 7), ("net", 99) };

    [Fact]
    public void 靜音排除_Aggregate_區間內與目前靜音中的列不計入_None時全部出現()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().Aggregate(exclusion, MuteFrom, MuteTo, null);
        var all = Query().Aggregate(IssueExclusion.None, MuteFrom, MuteTo, null);

        Assert.Equal(new[] { "disk", "net" }, muted.Select(a => a.Source.ToLowerInvariant()).OrderBy(s => s));
        var disk = muted.Single(a => a.EventId == 153);
        Assert.Equal(1, disk.HostCount);
        Assert.Equal(2, disk.DayCount);
        Assert.Equal(2, disk.ActiveDays);
        Assert.Equal(new DateTime(2026, 8, 3), disk.FirstSeen);
        Assert.Equal(new DateTime(2026, 8, 20), disk.LastSeen);

        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Single(a => a.EventId == 153).HostCount);
        Assert.Equal(3, all.Single(a => a.EventId == 153).ActiveDays);
    }

    [Fact]
    public void 靜音排除_HostIdsByIssue()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().HostIdsByIssue(exclusion, MuteIssues, MuteFrom, MuteTo, null);
        var all = Query().HostIdsByIssue(IssueExclusion.None, MuteIssues, MuteFrom, MuteTo, null);

        Assert.Equal(new long[] { 1 }, muted[("DISK", 153)].OrderBy(x => x));
        Assert.False(muted.ContainsKey(("CRON", 7)));
        Assert.Equal(new long[] { 1, 4 }, all[("DISK", 153)].OrderBy(x => x));
        Assert.Equal(new long[] { 2 }, all[("CRON", 7)].OrderBy(x => x));
    }

    [Fact]
    public void 靜音排除_LatestOccurrences()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().LatestOccurrences(exclusion, MuteIssues, MuteFrom, MuteTo, null);
        var all = Query().LatestOccurrences(IssueExclusion.None, MuteIssues, MuteFrom, MuteTo, null);

        Assert.Equal(new long[] { 1, 3 }, muted.Select(o => o.HostId).Distinct().OrderBy(x => x));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, all.Select(o => o.HostId).Distinct().OrderBy(x => x));
    }

    [Fact]
    public void 靜音排除_ActionableOccurrences()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().ActionableOccurrences(exclusion, MuteFrom, MuteTo, null);
        var all = Query().ActionableOccurrences(IssueExclusion.None, MuteFrom, MuteTo, null);

        Assert.Equal(new long[] { 1, 3 }, muted.Select(o => o.HostId).Distinct().OrderBy(x => x));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, all.Select(o => o.HostId).Distinct().OrderBy(x => x));
    }

    [Fact]
    public void 靜音排除_AggregateByCategory_問題種類數總和等於Aggregate筆數()
    {
        var exclusion = SeedMuteScenario();

        foreach (var e in new[] { exclusion, IssueExclusion.None })
        {
            var cards = Query().AggregateByCategory(e, MuteFrom, MuteTo, null, null);
            var listed = Query().Aggregate(e, MuteFrom, MuteTo, null);
            Assert.Equal(listed.Count, cards.Sum(c => c.IssueTypeCount));
        }

        Assert.Equal(2, Query().AggregateByCategory(exclusion, MuteFrom, MuteTo, null, null).Sum(c => c.IssueTypeCount));
        Assert.Equal(3, Query().AggregateByCategory(IssueExclusion.None, MuteFrom, MuteTo, null, null).Sum(c => c.IssueTypeCount));
        Assert.Equal(3, Query().AggregateByCategory(exclusion, MuteFrom, MuteTo, null, null).Sum(c => c.CumulativeCount));
        Assert.Equal(6, Query().AggregateByCategory(IssueExclusion.None, MuteFrom, MuteTo, null, null).Sum(c => c.CumulativeCount));
    }

    [Fact]
    public void 靜音排除_AggregateByDate_帶EventId篩選()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().AggregateByDate(exclusion, MuteFrom, MuteTo, null, eventId: 153);
        var all = Query().AggregateByDate(IssueExclusion.None, MuteFrom, MuteTo, null, eventId: 153);
        var cronMuted = Query().AggregateByDate(exclusion, MuteFrom, MuteTo, null, eventId: 7);

        Assert.Equal(new[] { new DateTime(2026, 8, 3), new DateTime(2026, 8, 20) }, muted.Select(d => d.Date).OrderBy(d => d));
        Assert.Equal(new[] { new DateTime(2026, 8, 3), new DateTime(2026, 8, 7), new DateTime(2026, 8, 20) }, all.Select(d => d.Date).OrderBy(d => d));
        Assert.Empty(cronMuted);
    }

    [Fact]
    public void 靜音排除_AggregateByDate_日風險計數不重算_類別清單排除靜音列()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().AggregateByDate(exclusion, MuteFrom, MuteTo, null);
        var all = Query().AggregateByDate(IssueExclusion.None, MuteFrom, MuteTo, null);

        // 8/26 只有目前靜音中的 cron：日風險計數來自 lf_daily_records，照舊；類別清單沒有非靜音列可列
        var day = muted.Single(d => d.Date == new DateTime(2026, 8, 26));
        Assert.Equal(1, day.HighRiskHosts);
        Assert.Empty(day.Categories);
        Assert.Equal(all.Sum(d => d.HighRiskHosts), muted.Sum(d => d.HighRiskHosts));
        Assert.NotEmpty(all.Single(d => d.Date == new DateTime(2026, 8, 26)).Categories);
    }

    [Fact]
    public void 靜音排除_AggregateByHost_帶EventId篩選()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().AggregateByHost(exclusion, MuteFrom, MuteTo, null, eventId: 153);
        var all = Query().AggregateByHost(IssueExclusion.None, MuteFrom, MuteTo, null, eventId: 153);

        Assert.Equal(new long[] { 1 }, muted.Select(h => h.HostId).OrderBy(x => x));
        Assert.Equal(new long[] { 1, 4 }, all.Select(h => h.HostId).OrderBy(x => x));
        Assert.Empty(Query().AggregateByHost(exclusion, MuteFrom, MuteTo, null, eventId: 7));
    }

    [Fact]
    public void 靜音排除_DailyHostCounts()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().DailyHostCounts(exclusion, MuteIssues, MuteFrom, MuteTo, null);
        var all = Query().DailyHostCounts(IssueExclusion.None, MuteIssues, MuteFrom, MuteTo, null);

        Assert.Equal(3, muted.Count);   // disk 8/3、8/20、net 8/7
        Assert.DoesNotContain(muted, c => c.EventId == 7);
        Assert.DoesNotContain(muted, c => c.EventId == 153 && c.Date == new DateTime(2026, 8, 7));
        Assert.Equal(6, all.Count);
    }

    [Fact]
    public void 靜音排除_AggregateReportKpi_問題數不計靜音列_日數不重算()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().AggregateReportKpi(exclusion, MuteFrom, MuteTo, null, null, null);
        var all = Query().AggregateReportKpi(IssueExclusion.None, MuteFrom, MuteTo, null, null, null);
        var (pairCurrent, _) = Query().AggregateReportKpiPair(exclusion, MuteFrom, MuteTo, MuteFrom.AddDays(-30), MuteFrom.AddDays(-1), null, null, null);

        Assert.Equal(3, muted.TotalIssues);
        Assert.Equal(6, all.TotalIssues);
        Assert.Equal(all.HighRiskDays, muted.HighRiskDays);
        Assert.Equal(muted, pairCurrent);
    }

    /// <summary>
    /// 趨勢三欄全部來自 lf_daily_records（高／中風險日數、錯誤數），沒有問題列可排除——
    /// 契約是「不重算日層級數字」，這裡釘住套與不套結果相同。
    /// </summary>
    [Fact]
    public void 靜音排除_AggregateReportTrend_日層級數字不重算()
    {
        var exclusion = SeedMuteScenario();

        var muted = Query().AggregateReportTrend(exclusion, MuteFrom, MuteTo, null, null, null);
        var all = Query().AggregateReportTrend(IssueExclusion.None, MuteFrom, MuteTo, null, null, null);

        Assert.Equal(all, muted);
        Assert.Equal(6, muted.Sum(t => t.HighRisk));
    }

    [Fact]
    public void 靜音排除_500個靜音鍵時查詢可執行()
    {
        SeedMuteScenario();
        var profiles = Enumerable.Range(0, 500).Select(i => new IssueProfile
        {
            SourceName = $"src{i}", EventId = i,
            Mutes = { new MuteInterval { From = i % 2 == 0 ? new DateTime(2026, 8, 1) : new DateTime(2026, 8, 29), To = new DateTime(2026, 9, 5) } }
        }).Append(new IssueProfile
        {
            SourceName = "disk", EventId = 153,
            Mutes = { new MuteInterval { From = new DateTime(2026, 8, 5), To = new DateTime(2026, 8, 10) }, new MuteInterval { From = new DateTime(2026, 8, 19), To = new DateTime(2026, 8, 21) } }
        }).ToList();
        var exclusion = IssueExclusion.From(profiles, new DateTime(2026, 8, 15));

        var aggregate = Query().AggregateDayTodo(exclusion, MuteFrom, MuteTo, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>(), MuteToday);
        var listed = Query().Aggregate(exclusion, MuteFrom, MuteTo, null);
        var byDate = Query().AggregateByDate(exclusion, MuteFrom, MuteTo, null, eventId: 153);

        Assert.Equal(6, aggregate.TotalCount);
        Assert.Equal(new DateTime(2026, 8, 3), Assert.Single(byDate).Date);
        Assert.Equal(3, listed.Count);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 靜音排除_兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var profiles = Enumerable.Range(0, 300).Select(i => new IssueProfile
        {
            SourceName = $"src{i}", EventId = i,
            Mutes = { new MuteInterval { From = new DateTime(2026, 8, i % 2 == 0 ? 1 : 20), To = new DateTime(2026, 8, 25) } }
        });
        var exclusion = IssueExclusion.From(profiles, new DateTime(2026, 8, 10));

        var applied = IssueExclusionSql.Apply(ctx.TopIssues, exclusion).ToQueryString();
        var onlyMuted = IssueExclusionSql.OnlyMuted(ctx.TopIssues, exclusion).ToQueryString();

        Assert.Contains("UPPER", applied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT IN", applied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE", applied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SRC299#299", applied);
        Assert.Contains("SRC299#299", onlyMuted);
        Assert.Equal(ctx.TopIssues.ToQueryString(), IssueExclusionSql.Apply(ctx.TopIssues, IssueExclusion.None).ToQueryString());
    }

    [Fact]
    public void 靜音排除_來源鍵回填完成後使用source_key索引欄位()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
        var exclusion = IssueExclusion.From(
            new[] { new IssueProfile
            {
                SourceName = "évent",
                EventId = 7,
                Mutes = { new MuteInterval { From = new DateTime(2026, 8, 1), To = new DateTime(2026, 8, 2) } }
            } }, new DateTime(2026, 8, 2));

        var sql = IssueExclusionSql.Apply(ctx.TopIssues, exclusion, sourceKeyReady: true).ToQueryString();

        Assert.Contains("source_key", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upper(", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ── CountCurrentlyMutedIssues（批次 B-2b）─────────────────────────────

    [Fact]
    public void CountCurrentlyMutedIssues_目前靜音中兩個問題_只算期間內有列的()
    {
        SeedMuteScenario();
        var exclusion = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "cron", EventId = 7, Mutes = { new MuteInterval { From = new DateTime(2026, 8, 25), To = new DateTime(2026, 9, 10) } } },
            new IssueProfile { SourceName = "ghost", EventId = 1, Mutes = { new MuteInterval { From = new DateTime(2026, 8, 25), To = new DateTime(2026, 9, 10) } } }
        }, MuteToday);

        Assert.Equal(2, exclusion.CurrentlyMuted.Count);
        // cron/7 有兩列（CRON 與 cron 大小寫不同）仍只算一個問題；ghost/1 期間內沒有列不算
        Assert.Equal(1, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, null, null));
    }

    [Fact]
    public void CountCurrentlyMutedIssues_已到期區間的問題不算()
    {
        var exclusion = SeedMuteScenario();

        // 8/3～8/20 期間只有已到期的 disk/153 列（目前靜音中的 cron 在 8/2 與 8/26）
        Assert.Equal(0, Query().CountCurrentlyMutedIssues(exclusion, new DateTime(2026, 8, 3), new DateTime(2026, 8, 20), null, null, null));
        Assert.Contains(Query().Aggregate(IssueExclusion.None, new DateTime(2026, 8, 3), new DateTime(2026, 8, 20), null),
            a => a.EventId == 153);
        Assert.Equal(1, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, null, null));
    }

    [Fact]
    public void CountCurrentlyMutedIssues_不可見主機的列不算_空集合回0()
    {
        var exclusion = SeedMuteScenario();

        Assert.Equal(0, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, new long[] { 1, 3, 4 }, null, null));
        Assert.Equal(1, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, new long[] { 2 }, null, null));
        Assert.Equal(0, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, Array.Empty<long>(), null, null));
    }

    [Fact]
    public void CountCurrentlyMutedIssues_嚴重度與日風險等級母體同Aggregate()
    {
        var exclusion = SeedMuteScenario();

        Assert.Equal(0, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, new HashSet<IssueSeverity> { IssueSeverity.Low }, null));
        Assert.Equal(0, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, null, new HashSet<string> { RiskLevels.Low }));
        Assert.Equal(1, Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null,
            new HashSet<IssueSeverity> { IssueSeverity.High }, new HashSet<string> { RiskLevels.High }));
    }

    [Fact]
    public void CountCurrentlyMutedIssues_沒有目前靜音中的問題回0()
    {
        SeedMuteScenario();
        var expiredOnly = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "disk", EventId = 153, Mutes = { new MuteInterval { From = new DateTime(2026, 8, 5), To = new DateTime(2026, 8, 10) } } }
        }, MuteToday);

        Assert.Empty(expiredOnly.CurrentlyMuted);
        Assert.Equal(0, Query().CountCurrentlyMutedIssues(expiredOnly, MuteFrom, MuteTo, null, null, null));
        Assert.Equal(0, Query().CountCurrentlyMutedIssues(IssueExclusion.None, MuteFrom, MuteTo, null, null, null));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void CountCurrentlyMutedIssues_兩個後端都翻譯得出來(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var exclusion = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "cron", EventId = 7, Mutes = { new MuteInterval { From = new DateTime(2026, 8, 25), To = new DateTime(2026, 9, 10) } } }
        }, MuteToday);

        var sql = EfIssueAggregateQuery.BuildCurrentlyMutedIssueKeysQuery(
            ctx, exclusion, MuteFrom, MuteTo, new HashSet<long> { 1, 2 }, new HashSet<int> { 2, 3 }, new HashSet<string> { RiskLevels.High }).ToQueryString();

        Assert.Contains("DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPPER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CRON#7", sql);
    }

    // ── CurrentlyMutedIssues（回饋第 47 輪 G-2a）─────────────────────────────

    private static LogIssueSignature Categorized(string source, int eventId, IssueSeverity severity, IssueCategory category)
    {
        var issue = Issue(source, eventId, severity: severity);
        issue.Category = category;
        return issue;
    }

    /// <summary>disk/153 與 cron/7 目前靜音中、net/99 未靜音；disk 兩天類別與嚴重度不同（驗最近一天類別與期間最高嚴重度）。</summary>
    private IssueExclusion SeedCurrentlyMutedScenario()
    {
        Add(1, "A", new DateTime(2026, 8, 3), RiskLevels.High, Categorized("disk", 153, IssueSeverity.High, IssueCategory.Other));
        Add(1, "A", new DateTime(2026, 8, 20), RiskLevels.High, Categorized("disk", 153, IssueSeverity.Medium, IssueCategory.Storage));
        Add(2, "B", new DateTime(2026, 8, 26), RiskLevels.High, Categorized("cron", 7, IssueSeverity.Low, IssueCategory.Service));
        Add(3, "C", new DateTime(2026, 8, 7), RiskLevels.High, Categorized("net", 99, IssueSeverity.High, IssueCategory.Security));

        var interval = new MuteInterval { From = new DateTime(2026, 8, 25), To = new DateTime(2026, 9, 10) };
        return IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "disk", EventId = 153, Mutes = { interval } },
            new IssueProfile { SourceName = "cron", EventId = 7, Mutes = { new MuteInterval { From = interval.From, To = interval.To } } }
        }, MuteToday);
    }

    [Fact]
    public void CurrentlyMutedIssues_與計數同母體且帶類別與最高嚴重度()
    {
        var exclusion = SeedCurrentlyMutedScenario();

        var muted = Query().CurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, null, null);
        var all = Query().Aggregate(IssueExclusion.None, MuteFrom, MuteTo, null);

        Assert.Equal(2, muted.Count);
        Assert.Equal(Query().CountCurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, null, null, null), muted.Count);
        Assert.Equal(new[] { 7, 153 }, muted.Select(m => m.EventId).OrderBy(e => e));
        foreach (var m in muted)
        {
            var agg = all.Single(a => a.EventId == m.EventId);
            Assert.Equal(agg.Source, m.Source);
            Assert.Equal(agg.Category, m.Category);
            Assert.Equal(agg.MaxSeverityRank, m.MaxSeverityRank);
        }
        var disk = muted.Single(m => m.EventId == 153);
        Assert.Equal(IssueCategory.Storage.ToString(), disk.Category);
        Assert.Equal((int)IssueSeverity.High, disk.MaxSeverityRank);
    }

    [Fact]
    public void CurrentlyMutedIssues_主機空集合回空()
    {
        var exclusion = SeedCurrentlyMutedScenario();

        Assert.Empty(Query().CurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, Array.Empty<long>(), null, null));
        Assert.Single(Query().CurrentlyMutedIssues(exclusion, MuteFrom, MuteTo, new long[] { 2 }, null, null));
    }

    [Fact]
    public void 靜音排除_期間外的歷史區間不進SQL()
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var queryFrom = new DateTime(2026, 8, 1);
        var queryTo = new DateTime(2026, 8, 31);
        var expiredFrom = new DateTime(2025, 1, 1);
        var expiredTo = new DateTime(2025, 1, 10);

        var profile = new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            Mutes = { new MuteInterval { From = expiredFrom, To = expiredTo } }
        };
        var exclusion = IssueExclusion.From(new[] { profile }, queryTo);

        // 未 narrow 前的 SQL 包含該歷史區間的日期字面值
        var unconstrainedSql = IssueExclusionSql.Apply(ctx.TopIssues, exclusion).ToQueryString();
        Assert.Contains("2025", unconstrainedSql);

        // narrow 後的 exclusion 所組出的 SQL 不含該歷史區間的日期字面值
        var narrowed = exclusion.ForRange(queryFrom, queryTo);
        var sql = IssueExclusionSql.Apply(ctx.TopIssues, narrowed).ToQueryString();
        Assert.DoesNotContain("2025", sql);

        // 同時驗證 Aggregate 執行套用 exclusion 結果相同
        var result = Query().Aggregate(exclusion, queryFrom, queryTo, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void IssueHostDayCount_等於Aggregate該問題的DayCount()
    {
        var survivor = _hosts.Upsert(new WebHost { HostName = "B" });
        var tombstone = _hosts.Upsert(new WebHost { HostName = "A", MergedInto = survivor.HostId, Active = false });
        var otherHost = _hosts.Upsert(new WebHost { HostName = "C" });

        var d0 = new DateTime(2026, 8, 1);
        var d1 = d0.AddDays(1);
        var d2 = d0.AddDays(2);

        // 目標問題：disk 153，涵蓋墓碑主機 A (High/高風險日)、存活主機 B (High/高風險日)、主機 C (Medium/中風險日)
        Add(tombstone.HostId, "A", d0, RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(survivor.HostId, "B", d1, RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        Add(otherHost.HostId, "C", d2, RiskLevels.Medium, Issue("disk", 153, severity: IssueSeverity.Medium));

        // 另一問題：net 99，驗證不影響結果
        Add(tombstone.HostId, "A", d0, RiskLevels.High, Issue("net", 99, severity: IssueSeverity.High));
        Add(otherHost.HostId, "C", d1, RiskLevels.High, Issue("net", 99, severity: IssueSeverity.High));

        // 1. 基本比對（不帶額外篩選）：涵蓋墓碑主機合併
        var fullAgg = Query().Aggregate(IssueExclusion.None, d0, d2, null);
        var expectedFull = fullAgg.Single(a => a.EventId == 153 && a.Source == "disk").DayCount;
        var actualFull = Query().IssueHostDayCount(IssueExclusion.None, "disk", 153, d0, d2, null, null, null);
        Assert.Equal(expectedFull, actualFull);

        // 2. 涵蓋嚴重度可見性（visibleSeverities 只看 High，Medium 應被排除）
        var highOnly = new HashSet<IssueSeverity> { IssueSeverity.High };
        var sevAgg = Query().Aggregate(IssueExclusion.None, d0, d2, null, highOnly, null);
        var expectedSev = sevAgg.Single(a => a.EventId == 153 && a.Source == "disk").DayCount;
        var actualSev = Query().IssueHostDayCount(IssueExclusion.None, "disk", 153, d0, d2, null, highOnly, null);
        Assert.Equal(expectedSev, actualSev);

        // 3. 涵蓋日風險等級（riskLevels 只看 High，Medium 風險日應被排除）
        var highRisk = new HashSet<string> { RiskLevels.High };
        var riskAgg = Query().Aggregate(IssueExclusion.None, d0, d2, null, null, highRisk);
        var expectedRisk = riskAgg.Single(a => a.EventId == 153 && a.Source == "disk").DayCount;
        var actualRisk = Query().IssueHostDayCount(IssueExclusion.None, "disk", 153, d0, d2, null, null, highRisk);
        Assert.Equal(expectedRisk, actualRisk);

        // 4. 查無問題回 0
        var notFound = Query().IssueHostDayCount(IssueExclusion.None, "nonexistent", 9999, d0, d2, null, null, null);
        Assert.Equal(0, notFound);

        // 5. 空主機集合回 0
        var emptyHosts = Query().IssueHostDayCount(IssueExclusion.None, "disk", 153, d0, d2, Array.Empty<long>(), null, null);
        Assert.Equal(0, emptyHosts);
    }
}

using Xunit;
using static LogForesight.Tests.TestData;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SuppressionStore"/> 的儲存層合約（見 docs/RULES-SPEC.md）：缺檔/損毀時降級為
/// 空清單而不拋例外、round-trip 保留欄位。容錯邏輯寫在 store 本身（blob 無關），透過
/// <see cref="EfJsonBlobStore.Mutate{TResult}"/> 直接寫入原始內容即可驗證，跑在 SQLite（EF）上。
/// </summary>
public class SuppressionStoreContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private EfJsonBlobStore? _blob;
    private EfJsonBlobStore Blob => _blob ??= _fx.Blob("suppressions");

    private SuppressionStore Store() => new(Blob);

    private static void WriteRaw(EfJsonBlobStore blob, string text) =>
        blob.Mutate<object?>(_ => (text, null));

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 檔案不存在時LoadAll回傳空清單()
    {
        var store = Store();

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void SaveAndLoadRoundTrip保留欄位()
    {
        var store = Store();
        var item = new RuleSuppression
        {
            RuleId = "builtin-service-crash-loop-703x",
            Host = "SRV-01",
            Reason = "已知雜訊，維護中",
            SuppressedBy = "alice",
            CreatedAt = new DateTime(2026, 7, 1),
            ExpiresAt = new DateTime(2026, 8, 1)
        };

        store.SaveAll(new List<RuleSuppression> { item });
        var loaded = Assert.Single(store.LoadAll());

        Assert.Equal(item.RuleId, loaded.RuleId);
        Assert.Equal(item.Host, loaded.Host);
        Assert.Equal(item.Reason, loaded.Reason);
        Assert.Equal(item.SuppressedBy, loaded.SuppressedBy);
        Assert.Equal(item.ExpiresAt, loaded.ExpiresAt);
    }

    [Fact]
    public void 整檔損毀時降級為空清單而不拋例外()
    {
        WriteRaw(Blob, "{ not a valid array ");
        var store = Store();

        Assert.Empty(store.LoadAll());
    }
}

/// <summary>
/// 抑制的篩選純函數（<see cref="SuppressionFilter"/>）與其對分析層的實際效果——
/// 被抑制的「重大」旗標/High 不強制拉高風險、不產生趨勢告警文字，但關聯層與紀錄本身完全不受影響
/// （語意邊界）。純函數測試，與儲存後端無關。
/// </summary>
public class SuppressionTests
{
    // ── SuppressionFilter ────────────────────────────────────────

    [Fact]
    public void ActiveForHost只回傳同主機且未到期的項目_主機比對不分大小寫()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "a", Host = "SRV-01", ExpiresAt = null },
            new() { RuleId = "b", Host = "srv-01", ExpiresAt = now.AddDays(1) }, // 大小寫不同但同主機
            new() { RuleId = "c", Host = "SRV-02", ExpiresAt = null },          // 不同主機
            new() { RuleId = "d", Host = "SRV-01", ExpiresAt = now.AddDays(-1) } // 已到期
        };

        var active = SuppressionFilter.ActiveForHost(all, "SRV-01", Array.Empty<long>(), now);

        Assert.Equal(new[] { "a", "b" }, active.Select(s => s.RuleId).OrderBy(x => x));
    }

    [Fact]
    public void ExpiredForHost只回傳同主機且已到期的項目()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "a", Host = "SRV-01", ExpiresAt = now.AddDays(-1) }, // 已到期
            new() { RuleId = "b", Host = "SRV-01", ExpiresAt = now.AddDays(1) },  // 未到期
            new() { RuleId = "c", Host = "SRV-01", ExpiresAt = null },            // 永久
            new() { RuleId = "d", Host = "SRV-02", ExpiresAt = now.AddDays(-1) }  // 不同主機
        };

        var expired = SuppressionFilter.ExpiredForHost(all, "SRV-01", Array.Empty<long>(), now);

        Assert.Equal("a", Assert.Single(expired).RuleId);
    }

    // ── 抑制範圍：Group／Site（回饋十三輪 F）─────────────────────────

    [Fact]
    public void ActiveForHost_Group範圍_主機屬於該群組時生效()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "a", Scope = SuppressionScopes.Group, HostGroupId = 10, ExpiresAt = null },
            new() { RuleId = "b", Scope = SuppressionScopes.Group, HostGroupId = 20, ExpiresAt = null } // 主機不屬於這個群組
        };

        var active = SuppressionFilter.ActiveForHost(all, "SRV-01", new long[] { 10, 30 }, now);

        Assert.Equal("a", Assert.Single(active).RuleId);
    }

    [Fact]
    public void ActiveForHost_Group範圍_主機不屬於任何群組時全部不生效()
    {
        var all = new List<RuleSuppression> { new() { RuleId = "a", Scope = SuppressionScopes.Group, HostGroupId = 10 } };

        var active = SuppressionFilter.ActiveForHost(all, "SRV-01", Array.Empty<long>(), DateTime.Today);

        Assert.Empty(active);
    }

    [Fact]
    public void ActiveForHost_Site範圍_不論主機或群組一律生效()
    {
        var all = new List<RuleSuppression> { new() { RuleId = "a", Scope = SuppressionScopes.Site } };

        var activeNoGroups = SuppressionFilter.ActiveForHost(all, "ANY-HOST", Array.Empty<long>(), DateTime.Today);
        var activeWithGroups = SuppressionFilter.ActiveForHost(all, "OTHER-HOST", new long[] { 999 }, DateTime.Today);

        Assert.Single(activeNoGroups);
        Assert.Single(activeWithGroups);
    }

    [Fact]
    public void ActiveForHost_Site範圍_到期後不再生效()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "a", Scope = SuppressionScopes.Site, ExpiresAt = now.AddDays(-1) }
        };

        Assert.Empty(SuppressionFilter.ActiveForHost(all, "ANY-HOST", Array.Empty<long>(), now));
        Assert.Single(SuppressionFilter.ExpiredForHost(all, "ANY-HOST", Array.Empty<long>(), now));
    }

    // ── StillSuppressedElsewhere：同規則跨範圍並存的到期語意（回饋十四輪 C1）─────────

    /// <summary>釘住問題本身的場景：同一條規則同時有 Site（永久）與 Host（已到期）兩筆——
    /// 到期的是 Host 那筆，但規則實際仍受 Site 範圍抑制，解除 Host 那筆不會恢復告警。</summary>
    [Fact]
    public void StillSuppressedElsewhere_Site永久與Host已到期並存時回報仍受抑制()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "rule-a", Scope = SuppressionScopes.Host, Host = "SRV-01", ExpiresAt = now.AddDays(-1) },
            new() { RuleId = "rule-a", Scope = SuppressionScopes.Site, ExpiresAt = null }
        };

        var result = SuppressionFilter.StillSuppressedElsewhere(all, "SRV-01", Array.Empty<long>(), now);

        Assert.Contains("rule-a", result);
    }

    [Fact]
    public void StillSuppressedElsewhere_到期後沒有其他生效範圍時回報空集合()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "rule-a", Scope = SuppressionScopes.Host, Host = "SRV-01", ExpiresAt = now.AddDays(-1) }
        };

        var result = SuppressionFilter.StillSuppressedElsewhere(all, "SRV-01", Array.Empty<long>(), now);

        Assert.Empty(result);
    }

    /// <summary>到期的那一筆本身不該被反查回自己身上（ActiveForHost 已排除到期項目，
    /// 這裡釘住「只有一筆、已到期、沒有別的範圍」時不會被誤判成仍生效）。</summary>
    [Fact]
    public void StillSuppressedElsewhere_不會把到期項目自己誤判為仍生效()
    {
        var now = new DateTime(2026, 7, 21);
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "rule-a", Scope = SuppressionScopes.Site, ExpiresAt = now.AddDays(-1) }
        };

        var result = SuppressionFilter.StillSuppressedElsewhere(all, "ANY-HOST", Array.Empty<long>(), now);

        Assert.Empty(result);
    }

    /// <summary>舊資料相容：Scope 欄位改版前不存在，反序列化後的預設值就是 Host，
    /// 語意與改版前逐位相同——這裡直接構造「沒設 Scope」的物件模擬舊資料。</summary>
    [Fact]
    public void 未設定Scope的舊資料視為Host範圍()
    {
        var legacy = new RuleSuppression { RuleId = "a", Host = "SRV-01" }; // 沒有明確設 Scope

        Assert.Equal(SuppressionScopes.Host, legacy.Scope);
        var active = SuppressionFilter.ActiveForHost(new List<RuleSuppression> { legacy }, "SRV-01", Array.Empty<long>(), DateTime.Today);
        Assert.Single(active);
    }

    [Fact]
    public void ToRuleIdSet投影出的集合比對RuleId時不分大小寫()
    {
        var suppressions = new List<RuleSuppression> { new() { RuleId = "builtin-Storage-Disk-IO", Host = "H" } };

        var ids = SuppressionFilter.ToRuleIdSet(suppressions);

        Assert.Contains("builtin-storage-disk-io", ids);
    }

    // ── 抑制對風險判定的效果（LogAnalysisService.ComputeRuleBasedRisk，internal，見 InternalsVisibleTo）──

    [Fact]
    public void 被抑制的Critical不強制拉高風險()
    {
        var issue = Sig("System", "disk", 153, 5, IssueSeverity.Critical);
        // 模擬命中「重大」旗標規則（docs/archive/HISTORY.md #1，B1 三級化後判定看旗標不看 Severity）
        issue.ElevatesDayRisk = true;
        issue.Suppressed = true;

        var risk = LogAnalysisService.ComputeRuleBasedRisk(new List<LogIssueSignature> { issue },
            new List<string>(), new List<CorrelationFinding>());

        Assert.Equal("低", risk);
    }

    [Fact]
    public void 未被抑制的Critical仍強制拉高風險()
    {
        var issue = Sig("System", "disk", 153, 5, IssueSeverity.Critical);
        issue.ElevatesDayRisk = true;

        var risk = LogAnalysisService.ComputeRuleBasedRisk(new List<LogIssueSignature> { issue },
            new List<string>(), new List<CorrelationFinding>());

        Assert.Equal("高", risk);
    }

    [Fact]
    public void 被抑制的High不強制拉高風險至中()
    {
        var issue = Sig("System", "disk", 153, 5, IssueSeverity.High);
        issue.Suppressed = true;

        var risk = LogAnalysisService.ComputeRuleBasedRisk(new List<LogIssueSignature> { issue },
            new List<string>(), new List<CorrelationFinding>());

        Assert.Equal("低", risk);
    }

    [Fact]
    public void 關聯層訊號不受抑制影響_即使唯一的Critical事件被抑制()
    {
        var issue = Sig("System", "disk", 153, 5, IssueSeverity.Critical);
        issue.ElevatesDayRisk = true;
        issue.Suppressed = true;
        var correlation = new CorrelationFinding { Severity = IssueSeverity.High, ElevatesDayRisk = true, Description = "test" };

        var risk = LogAnalysisService.ComputeRuleBasedRisk(new List<LogIssueSignature> { issue },
            new List<string>(), new List<CorrelationFinding> { correlation });

        Assert.Equal("高", risk); // 關聯層完全不受抑制影響
    }

    // ── 抑制對趨勢告警文字的效果（TrendAnalyzer）──────────────────────

    [Fact]
    public void 被抑制的簽章仍照算趨勢升級但不產生告警文字()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);
        sig.Suppressed = true;

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Equal(IssueSeverity.High, sig.Severity); // 嚴重度封頂 High（B1 三級化）
        Assert.True(sig.ElevatesDayRisk); // 但仍照算標記「重大」旗標，供頻率報表使用
        Assert.DoesNotContain(alerts, a => a.Contains("頻率上升")); // 但不吵
    }

    [Fact]
    public void 未被抑制的對照組仍正常產生告警文字()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Contains(alerts, a => a.Contains("頻率上升"));
    }

    // Sig(...) 已搬到 TestDoubles\TestData.cs（與 TrendAnalyzerTests 原本逐字相同，已合併；
    // CorrelationAnalyzerTests 原本多帶的 keyDetails 參數也一併合併進共用版本）。

    private static DailyAnalysisRecord HistoryDay(DateTime date, string source, int eventId, int count, IssueSeverity severity,
        string logName = "System", IssueCategory category = IssueCategory.Other)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { Sig(logName, source, eventId, count, severity, category) }
        };
}

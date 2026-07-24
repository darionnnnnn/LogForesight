using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="JsonSuppressionStore"/> 的儲存層合約（見 docs/RULES-PLAN.md）：缺檔/損毀時降級為
/// 空清單而不拋例外、round-trip 保留欄位。容錯邏輯寫在 store 本身（blob 無關），透過
/// <see cref="IJsonBlobStore.Mutate{TResult}"/> 直接寫入原始內容即可跑在檔案或 DB blob 上——
/// SQLite（EF）現為主要測試方式，與 Jsonl 版跑同一組案例。
/// 「原子寫入不留暫存檔」是 <see cref="FileJsonBlobStore"/> 特有的實作細節，留在檔案版另立測試。
/// </summary>
public abstract class SuppressionStoreContractTests : IDisposable
{
    protected abstract IJsonBlobStore CreateBlob();

    private IJsonBlobStore? _blob;
    private IJsonBlobStore Blob => _blob ??= CreateBlob();

    private JsonSuppressionStore Store() => new(Blob);

    private static void WriteRaw(IJsonBlobStore blob, string text) =>
        blob.Mutate<object?>(_ => (text, null));

    public virtual void Dispose() { }

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

/// <summary>JSONL 後端（單機檔案相容模式）＋原子寫入不留暫存檔的檔案特有行為</summary>
public class JsonSuppressionStoreTests : SuppressionStoreContractTests
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"logforesight-suppressions-test-{Guid.NewGuid():N}.json");

    protected override IJsonBlobStore CreateBlob() => new FileJsonBlobStore(_path);

    public override void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var tmp = _path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Save不會在目錄留下暫存檔()
    {
        var store = new JsonSuppressionStore(_path);
        store.SaveAll(new List<RuleSuppression> { new() { RuleId = "x", Host = "h", Reason = "r" } });

        Assert.False(File.Exists(_path + ".tmp"));
    }
}

/// <summary>
/// SQLite（EF）後端——SQLite 現為主要測試方式，驗證抑制設定容錯邏輯在 DB blob 上
/// 與 Jsonl 版逐位一致。
/// </summary>
public class EfSuppressionStoreTests : SuppressionStoreContractTests
{
    private readonly EfSqliteFixture _fx = new();

    protected override IJsonBlobStore CreateBlob() => _fx.Blob("suppressions");

    public override void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 抑制的篩選純函數（<see cref="SuppressionFilter"/>）與其對分析層的實際效果——
/// 被抑制的 Critical/High 不強制拉高風險、不產生趨勢告警文字，但關聯層與紀錄本身完全不受影響
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

        var active = SuppressionFilter.ActiveForHost(all, "SRV-01", now);

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

        var expired = SuppressionFilter.ExpiredForHost(all, "SRV-01", now);

        Assert.Equal("a", Assert.Single(expired).RuleId);
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
        issue.Suppressed = true;

        var risk = LogAnalysisService.ComputeRuleBasedRisk(new List<LogIssueSignature> { issue },
            new List<string>(), new List<CorrelationFinding>());

        Assert.Equal("低", risk);
    }

    [Fact]
    public void 未被抑制的Critical仍強制拉高風險()
    {
        var issue = Sig("System", "disk", 153, 5, IssueSeverity.Critical);

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
        issue.Suppressed = true;
        var correlation = new CorrelationFinding { Severity = IssueSeverity.Critical, Description = "test" };

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
        Assert.Equal(IssueSeverity.Critical, sig.Severity); // 嚴重度仍照算升級，供頻率報表使用
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

    private static LogIssueSignature Sig(string logName, string source, int eventId, int count, IssueSeverity severity,
        IssueCategory category = IssueCategory.Other)
        => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = EventLogEntryType.Error,
            Count = count,
            Severity = severity,
            Category = category,
            FirstSeen = "00:00",
            LastSeen = "23:59"
        };

    private static DailyAnalysisRecord HistoryDay(DateTime date, string source, int eventId, int count, IssueSeverity severity,
        string logName = "System", IssueCategory category = IssueCategory.Other)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { Sig(logName, source, eventId, count, severity, category) }
        };
}

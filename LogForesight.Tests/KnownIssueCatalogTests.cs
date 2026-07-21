using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 逐條規則自動驗證：規則層是換環境時最需要驗證的部分（Source 字串比對、EventId 門檻），
/// 用整張規則表自動產生案例，而不是手寫少數幾條——規則表增修時測試自動涵蓋新規則，不用手動補案例。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class KnownIssueCatalogTests : IDisposable
{
    // 這個測試類別裡只有「推導出的SecurityAuditWatchlist涵蓋原始寫死清單」會呼叫
    // KnownIssueCatalog.Initialize；用完整種子呼叫本身就等同預設狀態，但仍在每個測試後
    // 明確重置一次，避免任何一次呼叫方式的疏漏影響到同 collection 內其他測試類別
    // （見 KnownIssueCatalogStateCollection 的說明）。
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    // 改吃 KnownIssueSeed.CreateRules() 而非 KnownIssueCatalog.Rules（2026-07-21 規則外部化）：
    // KnownIssueCatalog.Rules 現在是可被 Initialize() 覆寫的可變靜態狀態，若其他測試呼叫過
    // Initialize（例如未來測規則載入/匯入流程），會讓這裡的 MemberData 隨測試執行順序漂移。
    // 種子本身才是這個測試檔案真正要驗證的對象，直接吃種子與可變全域狀態脫鉤。
    public static IEnumerable<object[]> AllRules() =>
        KnownIssueSeed.CreateRules().Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(AllRules))]
    public void 達到門檻時分類與嚴重度符合規則表(KnownIssueRule rule)
    {
        var eventId = rule.EventIds.Length > 0 ? rule.EventIds[0] : 9999;
        var entryType = InferEntryType(rule.SourcePattern, eventId);

        var logs = MakeEntries(rule.CountThreshold, rule.SourcePattern, eventId, entryType);
        var signature = LogAggregator.Aggregate(logs).FirstOrDefault(s => s.Source == rule.SourcePattern && s.EventId == eventId);

        Assert.NotNull(signature);
        Assert.Equal(rule.Category, signature!.Category);
        Assert.Equal(rule.Severity, signature.Severity);
    }

    [Theory]
    [MemberData(nameof(AllRules))]
    public void 未達門檻時嚴重度降一級(KnownIssueRule rule)
    {
        if (rule.CountThreshold <= 1)
        {
            return; // 門檻為 1 的規則沒有「未達門檻」的情境
        }

        var eventId = rule.EventIds.Length > 0 ? rule.EventIds[0] : 9999;
        var entryType = InferEntryType(rule.SourcePattern, eventId);

        var logs = MakeEntries(1, rule.SourcePattern, eventId, entryType);
        var signature = LogAggregator.Aggregate(logs).FirstOrDefault(s => s.Source == rule.SourcePattern && s.EventId == eventId);

        var expected = rule.Severity == IssueSeverity.Low ? IssueSeverity.Low : rule.Severity - 1;
        Assert.NotNull(signature);
        Assert.Equal(expected, signature!.Severity);
    }

    [Fact]
    public void 未命中任何規則時歸類為Other且Low()
    {
        var logs = MakeEntries(1, "TotallyUnknownSource", 1, EventLogEntryType.Error);
        var signature = Assert.Single(LogAggregator.Aggregate(logs));

        Assert.Equal(IssueCategory.Other, signature.Category);
        Assert.Equal(IssueSeverity.Low, signature.Severity);
        Assert.Null(signature.KnownIssue);
    }

    /// <summary>
    /// 每條規則的靜態知識庫欄位（2026-07-20 AI 角色轉換新增）不可為空——
    /// 這些內容在規則命中時直接取代 AI 深入分析呼叫（見 RiskReportService.BuildStaticOutcome），
    /// 缺一個欄位就會讓報告的「處置參考」區塊靜默漏掉該問題。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRules))]
    public void 每條規則都有完整的白話知識庫內容(KnownIssueRule rule)
    {
        Assert.False(string.IsNullOrWhiteSpace(rule.PlainExplanation));
        Assert.False(string.IsNullOrWhiteSpace(rule.Impact));
        Assert.NotEmpty(rule.LikelyCauses);
        Assert.All(rule.LikelyCauses, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.NotEmpty(rule.NextSteps);
        Assert.All(rule.NextSteps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    [Fact]
    public void FindRule可依SourceEventId查回規則且與Classify比對邏輯一致()
    {
        var rule = KnownIssueCatalog.FindRule("disk", 153);

        Assert.NotNull(rule);
        Assert.Equal(IssueCategory.Storage, rule!.Category);
        Assert.Equal(IssueSeverity.Critical, rule.Severity);
    }

    [Fact]
    public void FindRule對未命中規則的來源回傳null()
    {
        Assert.Null(KnownIssueCatalog.FindRule("TotallyUnknownSource", 1));
    }

    // ── 規則外部化（2026-07-21）新增：種子本身的地基完整性 ─────────────────

    [Fact]
    public void 種子規則的Id全部唯一且以builtin開頭()
    {
        var rules = KnownIssueSeed.CreateRules();

        var ids = rules.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r => Assert.StartsWith("builtin-", r.Id));
        Assert.All(rules, r => Assert.Equal("builtin", r.Origin));
        Assert.All(rules, r => Assert.True(r.Enabled));
        Assert.All(rules, r => Assert.Equal("all", r.Scope));
        Assert.All(rules, r => Assert.Null(r.MatchFilter));
    }

    [Fact]
    public void 種子規則通過RuleValidator且零警告()
    {
        var outcome = RuleValidator.Validate(KnownIssueSeed.CreateRules());

        Assert.Empty(outcome.SkippedRules);
        Assert.Empty(outcome.ShadowWarnings);
    }

    /// <summary>
    /// 推導出的 watchlist（KnownIssueCatalog.SecurityAuditWatchlist，見 docs/RULES-PLAN.md 陷阱 1）
    /// 至少要涵蓋規則外部化前寫死維護的原始清單——這是確保「改用推導」沒有不小心漏掉的唯一保障，
    /// 與 SelfTestRunner 執行期驗證用同一份基準集、同一種「涵蓋」語意（不要求逐項相等：
    /// 推導改成以 EventId 為準、不再區分 FailureAudit/SuccessAudit 型別，比原始清單多涵蓋
    /// 4625 屬無害的超集，不是漏算）。
    /// </summary>
    [Fact]
    public void 推導出的SecurityAuditWatchlist涵蓋原始寫死清單()
    {
        var legacyBaseline = new HashSet<int>
        {
            1102, 4719, 4720, 4722, 4724, 4728, 4732, 4756, 4729, 4733, 4757,
            4697, 4698, 4740, 4670, 4907, 4717, 4718, 4704, 4705, 4703, 4735, 4739, 4731, 4734
        };

        // 顯式用完整種子呼叫 Initialize（結果與預設狀態相同，所有規則皆 Enabled），
        // 讓這個測試不依賴「目前沒有其他測試呼叫過 Initialize」的執行順序假設
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

        Assert.All(legacyBaseline, id => Assert.Contains(id, KnownIssueCatalog.SecurityAuditWatchlist));
    }

    private static EventLogEntryType InferEntryType(string source, int eventId)
    {
        if (!source.Equals("Security-Auditing", StringComparison.OrdinalIgnoreCase))
        {
            return EventLogEntryType.Error;
        }
        return eventId is 4625 or 4740 ? EventLogEntryType.FailureAudit : EventLogEntryType.SuccessAudit;
    }

    private static List<EventLogEntryData> MakeEntries(int count, string source, int eventId, EventLogEntryType entryType)
        => Enumerable.Range(0, count)
            .Select(i => new EventLogEntryData
            {
                TimeGenerated = DateTime.Today.AddMinutes(i),
                LogName = "System",
                Source = source,
                EventId = eventId,
                EntryType = entryType,
                Message = $"test event #{i}",
                InstanceId = eventId
            })
            .ToList();
}

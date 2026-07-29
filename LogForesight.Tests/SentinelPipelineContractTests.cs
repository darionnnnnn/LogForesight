using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 合約測試（docs/NETIQ-API-PLAN.md 決策 B2）：Sentinel 事件經 <see cref="SentinelEventMapper"/>
/// 映射後，餵進與本機路徑完全相同的 <see cref="LogAggregator"/>／<see cref="KnownIssueCatalog"/>，
/// 必須產出結構相同的 <see cref="LogIssueSignature"/>——這是「整條五層偵測零改動重用」設計主張
/// 的實際驗證，不是文件宣稱。同一邏輯事件分別以「本機路徑會產出的 EventLogEntryData」與
/// 「Sentinel 路徑：SentinelEvent → SentinelEventMapper」兩條路construct，跑同一段既有聚合/分類邏輯，
/// 比對結果同構。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class SentinelPipelineContractTests : IDisposable
{
    public SentinelPipelineContractTests() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    [Fact]
    public void Sentinel路徑與本機路徑餵同一邏輯事件_聚合分類結果同構()
    {
        const string source = "Microsoft-Windows-Security-Auditing";
        const int eventId = 4625;   // builtin-security-bruteforce-4625：SourcePattern="Security-Auditing"
        var when = new DateTime(2026, 7, 29, 10, 0, 0);

        // 本機路徑：EventRecordMapper 會產出的形狀（FailureAudit，來源含 Security-Auditing）
        var localEntries = new List<EventLogEntryData>
        {
            new()
            {
                TimeGenerated = when,
                EntryType = EventLogEntryType.FailureAudit,
                LogName = "Security",
                Source = source,
                Message = "帳戶無法登入。",
                EventId = eventId
            }
        };

        // Sentinel 路徑：SentinelEvent → SentinelEventMapper（xdasoutcome != 0 → FailureAudit，
        // 與本機的 Keywords 判定殊途同歸）
        var sentinelFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dt"] = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["sev"] = "4",
            ["rv150"] = "Security",
            ["rv40"] = eventId.ToString(),
            ["obssvcname"] = source,
            ["msg"] = "帳戶無法登入。",
            ["xdasoutcome"] = "1"
        };
        var (mappedFromSentinel, skipped) = SentinelEventMapper.MapAll(new[] { new SentinelEvent(sentinelFields) });

        Assert.Equal(0, skipped);

        var localSignature = LogAggregator.Aggregate(localEntries).Single();
        var sentinelSignature = LogAggregator.Aggregate(mappedFromSentinel).Single();

        KnownIssueCatalog.Classify(localSignature);
        KnownIssueCatalog.Classify(sentinelSignature);

        // 分類結果必須同構：同一條規則命中、同一類別/嚴重度/旗標
        Assert.Equal(localSignature.RuleId, sentinelSignature.RuleId);
        Assert.Equal("builtin-security-bruteforce-4625", sentinelSignature.RuleId);
        Assert.Equal(localSignature.Category, sentinelSignature.Category);
        Assert.Equal(localSignature.Severity, sentinelSignature.Severity);
        Assert.Equal(localSignature.ElevatesDayRisk, sentinelSignature.ElevatesDayRisk);

        // 聚合分組鍵（LogName/Source/EventId/EntryType）也必須一致，否則報告上的簽章列會對不起來
        Assert.Equal(localSignature.LogName, sentinelSignature.LogName);
        Assert.Equal(localSignature.Source, sentinelSignature.Source);
        Assert.Equal(localSignature.EventId, sentinelSignature.EventId);
        Assert.Equal(localSignature.EntryType, sentinelSignature.EntryType);
    }

    [Fact]
    public void 未命中任何規則的事件_兩條路徑皆歸類為Other_Low()
    {
        var localEntries = new List<EventLogEntryData>
        {
            new() { TimeGenerated = DateTime.Now, EntryType = EventLogEntryType.Error, LogName = "Application", Source = "Unknown-Provider", EventId = 99999 }
        };
        var sentinelFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["sev"] = "4",
            ["rv150"] = "Application",
            ["rv40"] = "99999",
            ["obssvcname"] = "Unknown-Provider"
        };
        var (mappedFromSentinel, _) = SentinelEventMapper.MapAll(new[] { new SentinelEvent(sentinelFields) });

        var localSignature = LogAggregator.Aggregate(localEntries).Single();
        var sentinelSignature = LogAggregator.Aggregate(mappedFromSentinel).Single();
        KnownIssueCatalog.Classify(localSignature);
        KnownIssueCatalog.Classify(sentinelSignature);

        Assert.Equal(IssueCategory.Other, sentinelSignature.Category);
        Assert.Equal(IssueSeverity.Low, sentinelSignature.Severity);
        Assert.Equal(localSignature.Category, sentinelSignature.Category);
        Assert.Equal(localSignature.Severity, sentinelSignature.Severity);
    }
}

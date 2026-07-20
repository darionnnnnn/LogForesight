using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 逐條規則自動驗證：規則層是換環境時最需要驗證的部分（Source 字串比對、EventId 門檻），
/// 用整張規則表自動產生案例，而不是手寫少數幾條——規則表增修時測試自動涵蓋新規則，不用手動補案例。
/// </summary>
public class KnownIssueCatalogTests
{
    public static IEnumerable<object[]> AllRules() =>
        KnownIssueCatalog.Rules.Select(r => new object[] { r });

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

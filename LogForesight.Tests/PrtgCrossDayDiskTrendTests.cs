using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Value-based disk findings do not inherit status finding cross-day escalation.</summary>
public class PrtgCrossDayDiskTrendTests
{
    private static readonly DateTime Day = new(2026, 9, 15);

    private static LogIssueSignature Sig(string code, IssueSeverity severity = IssueSeverity.Medium) => new()
    {
        LogName = PrtgFindingMapper.PrtgLogName,
        Source = $"PRTG:{code}",
        EventId = 0,
        EntryType = EventLogEntryType.Warning,
        EventKey = $"prtg:{code}:2001",
        SampleMessages = new List<string> { "原始說明" },
        Severity = severity,
        ElevatesDayRisk = true
    };

    private static Dictionary<string, HashSet<DateTime>> Hits(LogIssueSignature sig, int count) => new()
    {
        [sig.EventKey] = Enumerable.Range(1, count).Select(n => Day.AddDays(-n)).ToHashSet()
    };

    [Fact]
    public void DiskFreeTrend連續三日不升級也不追加跨日註記()
    {
        var sig = Sig(PrtgRuleEvaluator.RuleDiskFreeTrend);

        var result = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 2), Day);

        Assert.Equal((0, 0), result);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.True(sig.ElevatesDayRisk);
        Assert.Equal("原始說明", sig.SampleMessages[0]);
    }

    [Fact]
    public void DiskFreeTrend長期重複不套用ChronicDown封頂或註記()
    {
        var sig = Sig(PrtgRuleEvaluator.RuleDiskFreeTrend, IssueSeverity.High);

        var result = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 13), Day);

        Assert.Equal((0, 0), result);
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.True(sig.ElevatesDayRisk);
        Assert.Equal("原始說明", sig.SampleMessages[0]);
    }
}

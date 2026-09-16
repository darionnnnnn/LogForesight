using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG finding 跨日判定純函式：14 日窗口的命中次數 N、連續日數 M、升級與長期 Down。
/// </summary>
public class PrtgCrossDayTests
{
    private static readonly DateTime Day = new(2026, 9, 15);

    private static LogIssueSignature Sig(string ruleCode, IssueSeverity severity, bool elevates = false, long objid = 2001) => new()
    {
        LogName = PrtgFindingMapper.PrtgLogName,
        Source = $"PRTG:{ruleCode}",
        EventId = 0,
        EntryType = EventLogEntryType.Warning,
        EventKey = $"prtg:{ruleCode}:{objid}",
        SampleMessages = new List<string> { "原始說明" },
        Severity = severity,
        ElevatesDayRisk = elevates
    };

    private static Dictionary<string, HashSet<DateTime>> Hits(LogIssueSignature sig, params int[] daysAgo) => new()
    {
        [sig.EventKey] = daysAgo.Select(n => Day.AddDays(-n)).ToHashSet()
    };

    [Fact]
    public void 無歷史時不改任何欄位()
    {
        var sig = Sig("warning", IssueSeverity.Medium);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, new Dictionary<string, HashSet<DateTime>>(), Day);

        Assert.Equal((0, 0), (escalated, chronic));
        Assert.Equal("原始說明", sig.SampleMessages[0]);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
    }

    [Fact]
    public void 連續三日升級並標註第3次連續第3日()
    {
        var sig = Sig("warning", IssueSeverity.Medium);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 1, 2), Day);

        Assert.Equal((1, 0), (escalated, chronic));
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.Equal("原始說明；近 14 日第 3 次，連續第 3 日", sig.SampleMessages[0]);
    }

    [Fact]
    public void 窗口內命中三次但不連續仍升級()
    {
        var sig = Sig("flapping", IssueSeverity.Low);

        var (escalated, _) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 1, 5), Day);

        Assert.Equal(1, escalated);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.EndsWith("近 14 日第 3 次，連續第 2 日", sig.SampleMessages[0]);
    }

    [Fact]
    public void 命中兩次不連續只標註不升級()
    {
        var sig = Sig("warning", IssueSeverity.Medium);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 3), Day);

        Assert.Equal((0, 0), (escalated, chronic));
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.EndsWith("近 14 日第 2 次，連續第 1 日", sig.SampleMessages[0]);
    }

    [Fact]
    public void 長期Down原嚴重度為高時封頂為中()
    {
        var sig = Sig("down", IssueSeverity.High, elevates: true);

        var (_, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, Enumerable.Range(1, 13).ToArray()), Day);

        Assert.Equal(1, chronic);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
    }

    [Fact]
    public void Down連續14日視為長期Down_不拉高日風險且不升級()
    {
        var sig = Sig("down", IssueSeverity.Medium, elevates: true);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, Enumerable.Range(1, 13).ToArray()), Day);

        Assert.Equal((0, 1), (escalated, chronic));
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal(IssueSeverity.Medium, sig.Severity);
        Assert.Contains("近 14 日第 14 次，連續第 14 日", sig.SampleMessages[0]);
        Assert.EndsWith("；已連續 14 日，建議在 PRTG 暫停該 sensor 或建立抑制", sig.SampleMessages[0]);
    }

    [Fact]
    public void Down連續13日照常升級且保留重大旗標()
    {
        var sig = Sig("down", IssueSeverity.Medium, elevates: true);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, Enumerable.Range(1, 12).ToArray()), Day);

        Assert.Equal((1, 0), (escalated, chronic));
        Assert.True(sig.ElevatesDayRisk);
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.DoesNotContain("建議在 PRTG 暫停", sig.SampleMessages[0]);
        Assert.EndsWith("連續第 13 日", sig.SampleMessages[0]);
    }

    [Fact]
    public void 非Down規則連續14日不算長期Down()
    {
        var sig = Sig("warning", IssueSeverity.Medium, elevates: true);

        var (escalated, chronic) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, Enumerable.Range(1, 13).ToArray()), Day);

        Assert.Equal((1, 0), (escalated, chronic));
        Assert.True(sig.ElevatesDayRisk);
    }

    [Fact]
    public void High不再升級也不計入升級筆數()
    {
        var sig = Sig("warning", IssueSeverity.High);

        var (escalated, _) = PrtgCrossDay.Apply(new[] { sig }, Hits(sig, 1, 2), Day);

        Assert.Equal(0, escalated);
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.EndsWith("近 14 日第 3 次，連續第 3 日", sig.SampleMessages[0]);
    }

    [Fact]
    public void 窗口外與當日以後的命中不算()
    {
        var sig = Sig("warning", IssueSeverity.Medium);
        // day-15 在窗口外；day-14 是窗口內最舊一天；當日與隔日不算歷史
        var hits = new Dictionary<string, HashSet<DateTime>>
        {
            [sig.EventKey] = new() { Day.AddDays(-15), Day, Day.AddDays(1) }
        };

        PrtgCrossDay.Apply(new[] { sig }, hits, Day);
        Assert.Equal("原始說明", sig.SampleMessages[0]);

        var sig14 = Sig("warning", IssueSeverity.Medium);
        PrtgCrossDay.Apply(new[] { sig14 }, Hits(sig14, 14), Day);
        Assert.EndsWith("近 14 日第 2 次，連續第 1 日", sig14.SampleMessages[0]);
    }

    [Fact]
    public void 以EventKey區分不同sensor()
    {
        var a = Sig("warning", IssueSeverity.Medium, objid: 1);
        var b = Sig("warning", IssueSeverity.Medium, objid: 2);

        var (escalated, _) = PrtgCrossDay.Apply(new[] { a, b }, Hits(a, 1, 2), Day);

        Assert.Equal(1, escalated);
        Assert.Equal(IssueSeverity.High, a.Severity);
        Assert.Equal(IssueSeverity.Medium, b.Severity);
        Assert.Equal("原始說明", b.SampleMessages[0]);
    }
}

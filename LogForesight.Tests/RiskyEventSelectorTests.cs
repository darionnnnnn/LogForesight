using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

public class RiskyEventSelectorTests
{
    private static readonly DateTime Date = new(2026, 7, 31);

    [Fact]
    public void 命中規則的簽章即使嚴重度為Low也入庫()
    {
        var sig = Sig("System", "TerminalServices-LocalSessionManager", 21, ruleId: "builtin-rdp-logon", severity: IssueSeverity.Low);
        var logs = RawLogs(sig, count: 3);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 5, Date);

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.Equal("builtin-rdp-logon", e.RuleId));
        Assert.All(result, e => Assert.Equal(5, e.HostId));
        Assert.All(result, e => Assert.Equal(Date, e.Date));
    }

    [Fact]
    public void 被抑制的規則命中簽章仍然入庫()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        sig.Suppressed = true;
        var logs = RawLogs(sig, count: 2);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 未命中規則但趨勢New的簽章也入庫()
    {
        var sig = Sig("Application", "MyApp", 9999, ruleId: null, severity: IssueSeverity.Medium);
        sig.Trend = IssueTrend.New;
        var logs = RawLogs(sig, count: 1);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Single(result);
        Assert.Null(result[0].RuleId);
    }

    [Fact]
    public void 未命中規則且趨勢Rising的簽章也入庫()
    {
        var sig = Sig("Application", "MyApp", 9999, ruleId: null, severity: IssueSeverity.Medium);
        sig.Trend = IssueTrend.Rising;
        var logs = RawLogs(sig, count: 1);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Single(result);
    }

    [Fact]
    public void 未命中規則且趨勢Recurring的簽章不入庫()
    {
        var sig = Sig("Application", "MyApp", 9999, ruleId: null, severity: IssueSeverity.Medium);
        sig.Trend = IssueTrend.Recurring;
        var logs = RawLogs(sig, count: 5);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Empty(result);
    }

    [Fact]
    public void 沒有任何簽章符合資格時完全不掃描原始log()
    {
        var sig = Sig("Application", "MyApp", 9999, ruleId: null, severity: IssueSeverity.Low);
        sig.Trend = IssueTrend.Declining;
        var logs = RawLogs(sig, count: 5);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Empty(result);
    }

    [Fact]
    public void 單一簽章超過每簽章上限時取頭尾各半()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        // 70 筆依時間序，訊息用索引標記方便驗證頭尾切點
        var logs = Enumerable.Range(0, 70)
            .Select(i => new EventLogEntryData
            {
                TimeGenerated = Date.AddMinutes(i),
                EntryType = EventLogEntryType.Error,
                LogName = sig.LogName,
                Source = sig.Source,
                EventId = sig.EventId,
                Message = $"msg-{i}"
            }).ToList();

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Equal(RiskyEventSelector.MaxPerSignature, result.Count);
        // 頭 25（0~24）＋尾 25（45~69），中段 25~44 應被捨棄
        var messages = result.Select(e => e.Message).ToHashSet();
        for (int i = 0; i < 25; i++) Assert.Contains($"msg-{i}", messages);
        for (int i = 45; i < 70; i++) Assert.Contains($"msg-{i}", messages);
        for (int i = 25; i < 45; i++) Assert.DoesNotContain($"msg-{i}", messages);
    }

    [Fact]
    public void 未超過每簽章上限時原樣保留()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        var logs = RawLogs(sig, count: 10);

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void 訊息超過2000字時截斷()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        var longMessage = new string('x', 3000);
        var logs = new List<EventLogEntryData>
        {
            new()
            {
                TimeGenerated = Date,
                EntryType = EventLogEntryType.Error,
                LogName = sig.LogName,
                Source = sig.Source,
                EventId = sig.EventId,
                Message = longMessage
            }
        };

        var result = RiskyEventSelector.Select(new List<LogIssueSignature> { sig }, logs, hostId: 1, Date);

        Assert.Single(result);
        Assert.Equal(RiskyEventSelector.MaxMessageChars + 3, result[0].Message.Length); // TextTruncation 補 "..."
    }

    [Fact]
    public void 每主機日合計超過上限時依嚴重度排序後截斷()
    {
        // 11 個簽章，每個各 50 筆（皆達每簽章上限），合計 550 > MaxPerHostDay(500)——
        // 全部同嚴重度/同次數，靠 OrderByDescending 的穩定排序保留原始清單順序
        var issues = Enumerable.Range(0, 11)
            .Select(i => Sig("System", $"source-{i}", 1000 + i, ruleId: $"rule-{i}", severity: IssueSeverity.High, count: RiskyEventSelector.MaxPerSignature))
            .ToList();
        var logs = issues.SelectMany(sig => RawLogs(sig, count: RiskyEventSelector.MaxPerSignature)).ToList();

        var result = RiskyEventSelector.Select(issues, logs, hostId: 1, Date);

        Assert.Equal(RiskyEventSelector.MaxPerHostDay, result.Count);
        // 第一個簽章（source-0）的 50 筆應全數保留
        Assert.Equal(RiskyEventSelector.MaxPerSignature, result.Count(e => e.Source == "source-0"));
        // 第 11 個簽章（source-10，500 之後）應完全被截斷掉
        Assert.DoesNotContain(result, e => e.Source == "source-10");
    }

    private static LogIssueSignature Sig(string logName, string source, int eventId, string? ruleId, IssueSeverity severity, int count = 1)
        => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = EventLogEntryType.Error,
            Count = count,
            Severity = severity,
            RuleId = ruleId,
            FirstSeen = "00:00",
            LastSeen = "23:59"
        };

    private static List<EventLogEntryData> RawLogs(LogIssueSignature sig, int count) =>
        Enumerable.Range(0, count).Select(i => new EventLogEntryData
        {
            TimeGenerated = Date.AddMinutes(i),
            EntryType = sig.EntryType,
            LogName = sig.LogName,
            Source = sig.Source,
            EventId = sig.EventId,
            Message = $"{sig.Source}-{i}"
        }).ToList();
}

using System.Diagnostics;
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

    // ── SelectSourceEvents（回饋十三輪 B1）：與 Select 共用同一套資格判定與截斷，
    //    只是回傳原始 EventLogEntryData 而非轉換後的 RiskyEvent ──────────────────

    [Fact]
    public void SelectSourceEvents_資格判定與Select完全一致()
    {
        var ruleSig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        var trendNewSig = Sig("Application", "MyApp", 9999, ruleId: null, severity: IssueSeverity.Medium);
        trendNewSig.Trend = IssueTrend.New;
        var recurringSig = Sig("Application", "Other", 1, ruleId: null, severity: IssueSeverity.Medium);
        recurringSig.Trend = IssueTrend.Recurring;   // 不合格：無規則且非 New/Rising

        var issues = new List<LogIssueSignature> { ruleSig, trendNewSig, recurringSig };
        var logs = RawLogs(ruleSig, count: 3)
            .Concat(RawLogs(trendNewSig, count: 2))
            .Concat(RawLogs(recurringSig, count: 5))
            .ToList();

        var riskyEvents = RiskyEventSelector.Select(issues, logs, hostId: 1, Date);
        var sourceEvents = RiskyEventSelector.SelectSourceEvents(issues, logs);

        // 兩者選中的筆數必須一致（同一套資格判定＋截斷），recurringSig 的 5 筆都不該出現在任一邊
        Assert.Equal(5, riskyEvents.Count);
        Assert.Equal(5, sourceEvents.Count);
        Assert.DoesNotContain(sourceEvents, e => e.Source == "Other");
    }

    [Fact]
    public void SelectSourceEvents_未合格時回傳原始EventLogEntryData而非RiskyEvent()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
        var logs = RawLogs(sig, count: 3);

        var result = RiskyEventSelector.SelectSourceEvents(new List<LogIssueSignature> { sig }, logs);

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.Equal("disk", e.Source));
        Assert.All(result, e => Assert.Equal(153, e.EventId));
        // 未截斷訊息（RiskyEvent.Message 才會截 2000 字），確認回傳的是原始物件而非轉換結果
        Assert.All(result, e => Assert.StartsWith("disk-", e.Message));
    }

    [Fact]
    public void SelectSourceEvents_單一簽章超過每簽章上限時取頭尾各半_與Select相同切點()
    {
        var sig = Sig("System", "disk", 153, ruleId: "builtin-disk-error", severity: IssueSeverity.High);
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

        var result = RiskyEventSelector.SelectSourceEvents(new List<LogIssueSignature> { sig }, logs);

        Assert.Equal(RiskyEventSelector.MaxPerSignature, result.Count);
        var messages = result.Select(e => e.Message).ToHashSet();
        for (int i = 0; i < 25; i++) Assert.Contains($"msg-{i}", messages);
        for (int i = 45; i < 70; i++) Assert.Contains($"msg-{i}", messages);
        for (int i = 25; i < 45; i++) Assert.DoesNotContain($"msg-{i}", messages);
    }

    [Fact]
    public void SelectSourceEvents_每主機日合計超過上限時依嚴重度排序後截斷_與Select相同筆數()
    {
        var issues = Enumerable.Range(0, 11)
            .Select(i => Sig("System", $"source-{i}", 1000 + i, ruleId: $"rule-{i}", severity: IssueSeverity.High, count: RiskyEventSelector.MaxPerSignature))
            .ToList();
        var logs = issues.SelectMany(sig => RawLogs(sig, count: RiskyEventSelector.MaxPerSignature)).ToList();

        var result = RiskyEventSelector.SelectSourceEvents(issues, logs);

        Assert.Equal(RiskyEventSelector.MaxPerHostDay, result.Count);
        Assert.Equal(RiskyEventSelector.MaxPerSignature, result.Count(e => e.Source == "source-0"));
        Assert.DoesNotContain(result, e => e.Source == "source-10");
    }

    // ── 保留期閘門（回補超過保留期的日子不寫暫存，寫了下次執行就被 Prune 清掉）──────────

    [Fact]
    public void WithinRetention_保留期內與邊界日回傳true()
    {
        var today = new DateTime(2026, 7, 31);

        Assert.True(RiskyEventSelector.WithinRetention(today.AddDays(-1), 14, today));   // 平常：昨天
        Assert.True(RiskyEventSelector.WithinRetention(today.AddDays(-14), 14, today));  // 邊界：剛好第 14 天
    }

    [Fact]
    public void WithinRetention_超過保留期回傳false()
    {
        var today = new DateTime(2026, 7, 31);

        Assert.False(RiskyEventSelector.WithinRetention(today.AddDays(-15), 14, today));
        Assert.False(RiskyEventSelector.WithinRetention(today.AddDays(-120), 14, today)); // 首次執行深度回補的典型日期
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

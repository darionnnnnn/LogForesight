namespace LogForesight;

/// <summary>
/// 風險 log 暫存的入庫資格與呈現量上限（docs/WEB-SCHEDULER-PLAN.md §2.2.2，2026-07-31 定案）：
/// 純函數，不做 I/O——呼叫端（Program.cs／NetiqPipelineService）在
/// <see cref="LogAnalysisService.AnalyzeDayAsync"/> 返回後，用回傳的 <c>record.TopIssues</c>
/// （已經過規則分類與趨勢比對）與當日原始 <c>logs</c> 呼叫本函數，再交給
/// <see cref="IRiskyEventStore.ReplaceDay"/> 寫入。
///
/// 資格（任一命中即入庫，2026-07-31 定案 #3/#7/#8）：
///   1. 命中規則表的簽章（<see cref="LogIssueSignature.RuleId"/> 非 null），含 Low「收集用」
///      規則與被抑制的——「規則命中就存」字面套用，日常 RDP/SSH 成功登入也照存。
///   2. 出現在 <see cref="TrendAnalyzer"/> 頻率異常的簽章（<see cref="IssueTrend.New"/>／
///      <see cref="IssueTrend.Rising"/>）——補齊未命中規則的 Other 類問題原文覆蓋。
/// </summary>
public static class RiskyEventSelector
{
    /// <summary>每簽章最多入庫則數：首尾各半（開始樣態＋最新狀態）</summary>
    public const int MaxPerSignature = 50;

    /// <summary>每主機日合計上限，依嚴重度排序後截斷</summary>
    public const int MaxPerHostDay = 500;

    /// <summary>單筆訊息截斷長度</summary>
    public const int MaxMessageChars = 2000;

    public static List<RiskyEvent> Select(List<LogIssueSignature> issues, List<EventLogEntryData> logs, long hostId, DateTime date)
    {
        var qualifying = issues
            .Where(i => i.RuleId != null || i.Trend is IssueTrend.New or IssueTrend.Rising)
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.Count)
            .ToList();
        if (qualifying.Count == 0) return new List<RiskyEvent>();

        var bySignature = logs
            .GroupBy(l => (l.LogName, l.Source, l.EventId, l.EntryType))
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.TimeGenerated).ToList());

        var result = new List<RiskyEvent>();
        var createdAt = DateTime.Now;

        foreach (var sig in qualifying)
        {
            if (!bySignature.TryGetValue((sig.LogName, sig.Source, sig.EventId, sig.EntryType), out var entries)) continue;

            foreach (var e in PickHeadAndTail(entries, MaxPerSignature))
            {
                result.Add(new RiskyEvent
                {
                    HostId = hostId,
                    Date = date.Date,
                    LogName = e.LogName,
                    Source = e.Source,
                    EventId = e.EventId,
                    EntryType = e.EntryType,
                    EventTime = e.TimeGenerated,
                    Message = TextTruncation.Truncate(e.Message, MaxMessageChars),
                    RuleId = sig.RuleId,
                    CreatedAt = createdAt
                });
            }
        }

        // 每主機日合計上限：qualifying 已依嚴重度排序，result 依此順序累積，直接截斷即維持「嚴重度優先」
        return result.Count > MaxPerHostDay ? result.Take(MaxPerHostDay).ToList() : result;
    }

    /// <summary>超過上限時取頭尾各半（首端看開始樣態、尾端看最新狀態），未超過原樣回傳</summary>
    private static List<EventLogEntryData> PickHeadAndTail(List<EventLogEntryData> chronological, int max)
    {
        if (chronological.Count <= max) return chronological;

        var headCount = (max + 1) / 2;
        var tailCount = max - headCount;
        return chronological.Take(headCount)
            .Concat(chronological.Skip(chronological.Count - tailCount))
            .ToList();
    }
}

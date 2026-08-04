namespace LogForesight.Core.Analysis;

/// <summary>
/// 風險 log 暫存的入庫資格與呈現量上限（docs/archive/WEB-SCHEDULER-PLAN.md §2.2.2，2026-07-31 定案）：
/// 純函數，不做 I/O——呼叫端（批次的每日分析迴圈與 NetiqPipelineService，與案件掛接
/// AttachNewDay 同一個掛接模式）在每日分析返回後，用回傳的 <c>record.TopIssues</c>
/// （已經過規則分類與趨勢比對）與當日原始 <c>logs</c> 呼叫本函數，再交給
/// <see cref="IRiskyEventStore.ReplaceDay"/> 寫入。
///
/// 資格（任一命中即入庫，2026-07-31 定案 #3/#7/#8）：
///   1. 命中規則表的簽章（<see cref="LogIssueSignature.RuleId"/> 非 null），含 Low「收集用」
///      規則與被抑制的——「規則命中就存」字面套用，日常 RDP/SSH 成功登入也照存。
///   2. 趨勢異常的簽章（<see cref="IssueTrend.New"/>／<see cref="IssueTrend.Rising"/>）——
///      補齊未命中規則的 Other 類問題原文覆蓋。以結構化的 Trend 欄位判定而非回頭比對
///      TrendAlerts 告警字串（字串比對脆弱且反向解析），涵蓋面因此略寬於告警清單本身：
///      未達告警門檻的 New（低嚴重度首次出現）與暖身期的 Rising 也入庫——這類「未知型態」
///      恰好是問 AI 時最需要原文的，多存的量仍受下方兩層上限約束。
/// </summary>
public static class RiskyEventSelector
{
    /// <summary>每簽章最多入庫則數：首尾各半（開始樣態＋最新狀態）</summary>
    public const int MaxPerSignature = 50;

    /// <summary>每主機日合計上限，依嚴重度排序後截斷</summary>
    public const int MaxPerHostDay = 500;

    /// <summary>單筆訊息截斷長度</summary>
    public const int MaxMessageChars = 2000;

    /// <summary>
    /// 該分析日是否仍在暫存保留期內。呼叫端在寫入前先以此閘門判斷——回補超過保留期的
    /// 日子（首次執行的 120 天深度回補最典型）寫進去的列，下次執行的 Prune 就會清掉，
    /// 寫入純屬浪費；那些日期的暫存本來就不會被查（AI 對話問的是保留期內的日子）。
    /// today 由呼叫端傳入，維持純函數可測。
    /// </summary>
    public static bool WithinRetention(DateTime date, int retentionDays, DateTime today) =>
        date.Date >= today.Date.AddDays(-retentionDays);

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

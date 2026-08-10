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
        var createdAt = DateTime.Now;
        var result = new List<RiskyEvent>();

        foreach (var (sig, entries) in SelectQualifyingEntries(issues, logs))
        {
            foreach (var e in entries)
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

        // 每主機日合計上限：SelectQualifyingEntries 已依嚴重度排序，result 依此順序累積，
        // 直接截斷即維持「嚴重度優先」
        return result.Count > MaxPerHostDay ? result.Take(MaxPerHostDay).ToList() : result;
    }

    /// <summary>
    /// 選取結果的原始事件版本（回饋十三輪 B1）：與 <see cref="Select"/> 共用同一套資格判定、
    /// 分組與頭尾截斷邏輯（<see cref="SelectQualifyingEntries"/>），只是不轉成
    /// <see cref="RiskyEvent"/>——不需要落庫用的 RuleId／CreatedAt 這些欄位。
    ///
    /// 供 <c>AiWorkItem.Logs</c> 入列前過濾使用（回饋十四輪 A2，呼叫端移進
    /// <see cref="Service.LogAnalysisService.BuildStatisticalRecordAsync"/> 本身，型別內建構時
    /// 就窄化，不再由呼叫端事後補）：AI 深析報告最終只從這裡挑 20 筆
    /// （<see cref="Service.RiskReportService.MaxRawLogsPerReport"/>），沒有理由讓
    /// <see cref="Service.AiFollowupQueue{T}"/> 帶著全量事件（含未命中規則、非趨勢異常的雜訊，
    /// 單一 Sentinel job 上限可達 <c>NetiqOptions.MaxResultsPerJob</c>＝10 萬筆）在記憶體裡排隊等消化——
    /// 佇列容量 200 件原本只界件數，真正吃記憶體的是這裡。選取點（哪些事件入選、依嚴重度排序、
    /// 每簽章頭尾各半、每主機日合計 500 筆上限）與 <see cref="Select"/> 寫進
    /// <c>lf_risky_events</c> 暫存的內容逐位一致（同一次計算的兩種輸出形狀），差別只在這裡
    /// 額外把 <see cref="EventLogEntryData.Message"/> 截到 <see cref="MaxMessageChars"/>
    /// （與落庫版對齊——深析報告最長只取用 500 字，2000 字上限純為封頂單筆記憶體，不影響內容）。
    /// 回傳全新物件（不修改傳入的 <paramref name="logs"/>），呼叫端後續若還要用原始未截斷的
    /// 事件（如寫入風險 log 暫存），拿到的仍是完整訊息。
    /// </summary>
    public static List<EventLogEntryData> SelectSourceEvents(List<LogIssueSignature> issues, List<EventLogEntryData> logs)
    {
        var result = SelectQualifyingEntries(issues, logs)
            .SelectMany(pair => pair.Entries)
            .Select(e => new EventLogEntryData
            {
                TimeGenerated = e.TimeGenerated,
                EntryType = e.EntryType,
                LogName = e.LogName,
                Source = e.Source,
                Message = TextTruncation.Truncate(e.Message, MaxMessageChars),
                InstanceId = e.InstanceId,
                EventId = e.EventId
            })
            .ToList();
        return result.Count > MaxPerHostDay ? result.Take(MaxPerHostDay).ToList() : result;
    }

    /// <summary>
    /// 共用核心：資格判定（規則命中或趨勢 New/Rising）→ 依嚴重度排序 → 依簽章分組原始事件
    /// → 每簽章頭尾各半截斷。<see cref="Select"/> 與 <see cref="SelectSourceEvents"/> 從這裡
    /// 各自映射成不同的輸出形狀，但保證是同一份選取結果（含順序），下游的 500 筆合計截斷
    /// 因此在兩邊落在同一個切點。
    /// </summary>
    private static List<(LogIssueSignature Signature, List<EventLogEntryData> Entries)> SelectQualifyingEntries(
        List<LogIssueSignature> issues, List<EventLogEntryData> logs)
    {
        var qualifying = issues
            .Where(i => i.RuleId != null || i.Trend is IssueTrend.New or IssueTrend.Rising)
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.Count)
            .ToList();
        if (qualifying.Count == 0) return new();

        // 分組鍵與 LogAggregator.Aggregate 用同一套（含 EventKey）：只看 (LogName, Source,
        // EventId, EntryType) 四元組的話，Linux「同 program 命中不同規則」（如 sshd 底下的
        // ssh-bruteforce 與 ssh-accept）會反查到同一批原始事件，兩個簽章各自把對方的事件也
        // 撈進自己的風險 log 暫存——EventKey 恆空的 Windows 事件不受影響。
        var bySignature = logs
            .GroupBy(LogAggregator.GroupKeyFor)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.TimeGenerated).ToList());

        var result = new List<(LogIssueSignature, List<EventLogEntryData>)>();
        foreach (var sig in qualifying)
        {
            if (!bySignature.TryGetValue((sig.LogName, sig.Source, sig.EventId, sig.EntryType, sig.EventKey), out var entries)) continue;
            result.Add((sig, PickHeadAndTail(entries, MaxPerSignature)));
        }
        return result;
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

namespace LogForesight.Core.Persistence;

/// <summary>靜音區間的扁平化形狀：鍵為正規化大寫 Source＋EventId，日期只取 <c>.Date</c>（含首尾）。</summary>
public sealed record MuteSpan(string SourceKey, int EventId, DateTime From, DateTime To);

/// <summary>
/// 讀取側的靜音排除條件（回饋第 47 輪批次 B-2a）。
///
/// **單一規則**：某一列問題（Source, EventId, 紀錄日 D）算「靜音」，當且僅當
///   - 該問題**目前**靜音中（今天落在它的某個區間），或
///   - D 落在該問題的任一區間內（含首尾）。
/// 效果：靜音中整個問題消失（含靜音前的日子）；到期後區間內的日子仍不出現、區間前的日子回來；
/// 提前解除（To 改成昨天）從解除當天起恢復。
///
/// 記憶體判定只有 <see cref="IsMuted"/> 一份，SQL 條件只有 <c>IssueExclusionSql</c> 一份，
/// 兩者都從本物件的 <see cref="Spans"/>／<see cref="CurrentlyMuted"/> 取資料，不各自重算。
/// </summary>
public sealed class IssueExclusion
{
    private static readonly IReadOnlyDictionary<(string, int), IReadOnlyList<MuteSpan>> NoSpans =
        new Dictionary<(string, int), IReadOnlyList<MuteSpan>>();

    /// <summary>不排除任何列。呼叫端明寫它，代表「這條路徑刻意不套靜音」。</summary>
    public static IssueExclusion None { get; } = new(
        DateTime.MinValue, Array.Empty<MuteSpan>(), new HashSet<(string, int)>(), NoSpans, "mute:none");

    private readonly IReadOnlyDictionary<(string SourceKey, int EventId), IReadOnlyList<MuteSpan>> _spansByKey;

    private IssueExclusion(
        DateTime today, IReadOnlyList<MuteSpan> spans, IReadOnlySet<(string SourceKey, int EventId)> currentlyMuted,
        IReadOnlyDictionary<(string SourceKey, int EventId), IReadOnlyList<MuteSpan>> spansByKey, string cacheToken)
    {
        Today = today;
        Spans = spans;
        CurrentlyMuted = currentlyMuted;
        _spansByKey = spansByKey;
        CacheToken = cacheToken;
    }

    /// <summary>自問題檔案建立：收集所有有區間的問題，鍵為 (SourceName.ToUpperInvariant(), EventId)。</summary>
    public static IssueExclusion From(IEnumerable<IssueProfile> profiles, DateTime today)
    {
        var day = today.Date;
        var spans = profiles
            .SelectMany(p => p.Mutes.Select(m => new MuteSpan(p.SourceName.ToUpperInvariant(), p.EventId, m.From.Date, m.To.Date)))
            // 排序讓 Spans 與 CacheToken 對同一份設定穩定（問題檔案的儲存順序不影響結果）
            .OrderBy(s => s.SourceKey, StringComparer.Ordinal)
            .ThenBy(s => s.EventId)
            .ThenBy(s => s.From)
            .ThenBy(s => s.To)
            .ToList();

        var byKey = spans
            .GroupBy(s => (s.SourceKey, s.EventId))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MuteSpan>)g.ToList());

        var current = byKey
            .Where(kv => kv.Value.Any(s => MuteInterval.Covers(s.From, s.To, day)))
            .Select(kv => kv.Key)
            .ToHashSet();

        var token = $"mute:{day:yyyyMMdd}|" + string.Join(";",
            spans.Select(s => $"{s.SourceKey}#{s.EventId}#{s.From:yyyyMMdd}-{s.To:yyyyMMdd}"));

        return new IssueExclusion(day, spans, current, byKey, token);
    }

    /// <summary>判定「目前靜音中」所用的今天（日期）。</summary>
    public DateTime Today { get; }

    /// <summary>所有靜音區間（已排序）。</summary>
    public IReadOnlyList<MuteSpan> Spans { get; }

    /// <summary>今天落在某個區間內的問題鍵。</summary>
    public IReadOnlySet<(string SourceKey, int EventId)> CurrentlyMuted { get; }

    /// <summary>沒有任何區間＝不排除任何列。</summary>
    public bool IsEmpty => Spans.Count == 0;

    /// <summary>
    /// 快取鍵片段：<see cref="None"/> 為 <c>mute:none</c>；其餘為今天日期＋全部區間的穩定字串。
    /// 同設定換日也會變（目前靜音中的集合可能跟著變）。
    /// </summary>
    public string CacheToken { get; }

    public bool IsCurrentlyMuted(string source, int eventId) =>
        !IsEmpty && CurrentlyMuted.Contains((source.ToUpperInvariant(), eventId));

    /// <summary>單一規則的唯一記憶體實作（見類別註解）。日期判定呼叫 <see cref="MuteInterval.Covers"/>。</summary>
    public bool IsMuted(string source, int eventId, DateTime recordDate)
    {
        if (IsEmpty) return false;
        var key = (source.ToUpperInvariant(), eventId);
        if (CurrentlyMuted.Contains(key)) return true;
        return _spansByKey.TryGetValue(key, out var spans) && spans.Any(s => MuteInterval.Covers(s.From, s.To, recordDate));
    }
}

/// <summary>
/// 風險日處理狀態階梯的唯一一份（SQL 推導 <c>DeriveDayHandling</c>／<c>AggregateDayTodo</c> 與
/// 記憶體推導 <c>DayHandlingDerivation.Derive</c> 共用）。
/// </summary>
public static class DayStatusRule
{
    /// <param name="total">計入的（非靜音）問題數</param>
    /// <param name="closed">計入者中已結案的數量</param>
    /// <param name="anyInProgress">非靜音問題是否有處理中／觀察中／上報的標記</param>
    /// <param name="mutedCounted">靜音問題中「原本會被計入」者的數量</param>
    /// <param name="dayLevelStatus">日層級狀態（fallback）</param>
    public static string Resolve(int total, int closed, bool anyInProgress, int mutedCounted, string? dayLevelStatus)
    {
        if (total > 0 && closed == total) return HandlingStatuses.Resolved;
        if (closed > 0 || anyInProgress) return HandlingStatuses.InProgress;
        // 原本會被計入的問題全部都靜音：這一天視同已有結論，不再掛在待辦上
        if (total == 0 && mutedCounted > 0) return HandlingStatuses.Resolved;
        return string.IsNullOrEmpty(dayLevelStatus) ? HandlingStatuses.Open : dayLevelStatus;
    }
}

using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services.Mail;

/// <summary>
/// 郵件問題優先摘要（回饋十九輪批次H1）：把一個收件人可見範圍內「這次該優先看哪些問題」
/// 組成一份問題行清單，分四區——新出現／擴散中／逾期／其他高風險。取代舊版逐主機日一行的
/// 郵件內容（H2 消費這裡的結果），與網頁端 <see cref="IssueRankingBuilder"/> 的完整排行分開：
/// 那是互動頁面用的投影（含 PriorityScore／基準線等這裡不需要的欄位），批次寄信要的是輕量、
/// 可對大量收件人各自重複呼叫一次的路徑。
///
/// **Singleton-safe**：<see cref="MailNotificationService"/> 是 Singleton（背景排程觸發，不經
/// HTTP 請求 Scope），這裡的三個相依（<see cref="IIssueAggregateQuery"/>／
/// <see cref="OccurrenceStatusResolver"/>／<see cref="ISystemSettingsStore"/>）皆已註冊為 Singleton（見
/// ServiceCollectionExtensions），沒有 captive dependency 疑慮。刻意不注入 Scoped 服務
/// 或 <see cref="IssueRankingBuilder"/>——即使只是不用它的欄位，注入本身就會在 DI 容器的
/// scope 驗證下出錯。
/// </summary>
public class MailIssueDigest
{
    private readonly IIssueAggregateQuery _aggregates;
    private readonly OccurrenceStatusResolver _statusResolver;
    private readonly ISystemSettingsStore _settingsStore;

    public MailIssueDigest(
        IIssueAggregateQuery aggregates,
        OccurrenceStatusResolver statusResolver,
        ISystemSettingsStore settingsStore)
    {
        _aggregates = aggregates;
        _statusResolver = statusResolver;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// 期間內、可見範圍內的問題優先清單。<paramref name="visibleHostIds"/> 語意同查詢層——
    /// null＝不限制，空集合＝零結果。
    ///
    /// 分區優先序（一個問題只落一區，符合多個條件時取最急迫的）：逾期 &gt; 新出現 &gt; 擴散中 &gt;
    /// 其他高風險——逾期代表已經有人認領卻拖過頭，是最需要被看見的；新出現要求立即分診；
    /// 擴散中是持續惡化的訊號；其他高風險是「沒有特殊時間訊號、但嚴重度仍高」的兜底分區，
    /// 因此**只有這一區**依嚴重度（高）過濾——前三區的訊號本身（新／擴散／逾期）已經是
    /// 「該優先看」的理由，不需要再疊加嚴重度門檻，否則低嚴重度的新問題會被悄悄濾掉，
    /// 而「這是全新的東西」正是使用者最需要知道的事。
    /// </summary>
    public List<MailIssueRow> Build(DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds)
    {
        if (visibleHostIds != null && visibleHostIds.Count == 0) return new List<MailIssueRow>();

        var settings = _settingsStore.Get();
        var visibleSeverities = RecordRepository.ParseVisibleSeverities(
            SystemSettingsService.ResolveVisibleSeverities(settings));
        var riskLevels = RecordRepository.ResolveDayRiskLevels(
            SystemSettingsService.ResolveVisibleDayRiskLevels(settings), null);

        var periodDays = Math.Max(1, (to.Date - from.Date).Days + 1);

        // 前期對比用**等長**的前一個期間——與 IssueRankingBuilder.Build 同一套規則
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-periodDays + 1);
        // 鍵正規化成大寫（回饋二十輪 I）：Aggregate 輸出的 Source 是該期間內任一個原始寫法，
        // 本期與前期可能取到不同大小寫，用原始字串當鍵會讓前期對比靜默落空
        var previous = _aggregates.Aggregate(
            previousFrom, previousTo, visibleHostIds,
            visibleSeverities: visibleSeverities, riskLevels: riskLevels)
            .ToDictionary(a => IssueProfile.KeyOf(a.Source, a.EventId));

        var current = _aggregates.Aggregate(
            from, to, visibleHostIds,
            visibleSeverities: visibleSeverities, riskLevels: riskLevels);
        if (current.Count == 0) return new List<MailIssueRow>();

        var overdueKeys = ResolveOverdueKeys(from, to, visibleHostIds, visibleSeverities, riskLevels);

        var rows = new List<MailIssueRow>();
        foreach (var a in current)
        {
            // 兩份查表都用正規化鍵：Aggregate 輸出的 Source 是該期間任一原始寫法，
            // 而 overdueKeys 來自 IssueKey 字串解析（帶處理狀態列當時的原始大小寫）——
            // cron／CRON 併存時兩邊必然可能對不上，逾期問題會靜默掉出「逾期」區
            var key = IssueProfile.KeyOf(a.Source, a.EventId);
            previous.TryGetValue(key, out var prev);
            var previousHostCount = prev?.HostCount ?? 0;

            string bucket;
            if (overdueKeys.Contains(key)) bucket = MailIssueBucket.Overdue;
            else if (prev == null) bucket = MailIssueBucket.New;
            else if (a.HostCount > previousHostCount) bucket = MailIssueBucket.Spreading;
            else if ((IssueSeverity)a.MaxSeverityRank >= IssueSeverity.High) bucket = MailIssueBucket.OtherHighRisk;
            else continue;   // 不屬於任何一區的問題不進郵件——這是「優先」摘要，不是全量清單

            rows.Add(new MailIssueRow(a.Source, a.EventId, a.Category, a.HostCount, previousHostCount, bucket));
        }

        return rows;
    }

    /// <summary>逾期問題的 (Source,EventId) 集合——母體與判定規則與 <see cref="IssueTodoQuery"/>
    /// 共用同一個口徑（<see cref="IssueTodoQuery.IsOverdueInProgress"/>），不是另訂一套規則。</summary>
    private HashSet<(string SourceUpper, int EventId)> ResolveOverdueKeys(
        DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities,
        IReadOnlySet<string>? riskLevels)
    {
        var actionable = _aggregates.ActionableOccurrences(
            from, to, visibleHostIds,
            visibleSeverities: visibleSeverities, riskLevels: riskLevels);
        if (actionable.Count == 0) return new HashSet<(string, int)>();

        var resolved = _statusResolver.Resolve(actionable, from, to);

        // 鍵一律正規化（回饋二十輪 I 終檢補上）：呼叫端用 Aggregate 的 Source 查這個集合，
        // 那是任一原始寫法，不正規化就會漏
        var overdue = new HashSet<(string, int)>();
        foreach (var r in resolved)
        {
            if (!IssueTodoQuery.IsOverdueInProgress(r)) continue;
            var signature = IssueSignatureKey.TryParseSignature(r.Occurrence.IssueKey);
            if (signature != null) overdue.Add(IssueProfile.KeyOf(signature.Value.Source, signature.Value.EventId));
        }
        return overdue;
    }
}

/// <summary>問題優先分區（回饋十九輪批次H1）</summary>
public static class MailIssueBucket
{
    public const string New = "new";
    public const string Spreading = "spreading";
    public const string Overdue = "overdue";
    public const string OtherHighRisk = "other_high_risk";

    public static string Label(string bucket) => bucket switch
    {
        New => "新出現",
        Spreading => "擴散中",
        Overdue => "逾期",
        OtherHighRisk => "高風險",
        _ => bucket
    };
}

/// <summary>一行問題優先摘要（回饋十九輪批次H1）</summary>
public sealed record MailIssueRow(string Source, int EventId, string Category, int HostCount, int PreviousHostCount, string Bucket)
{
    /// <summary>信件本文的一行文字：<c>{Source}/{EventId}（{Category}）｜影響 N 台（前期 M）｜{區塊標記}</c>。
    /// 類別中文名走 Core 的唯一字典 <see cref="IssueCategoryNames"/>（批次I 體檢收斂——
    /// 這裡原本是第三份 switch 拷貝，正是 BACKLOG S13 記錄的分歧風險）。</summary>
    public string FormatLine() =>
        $"{Source}/{EventId}（{IssueCategoryNames.Zh(Category)}）｜影響 {HostCount} 台（前期 {PreviousHostCount}）｜{MailIssueBucket.Label(Bucket)}";
}

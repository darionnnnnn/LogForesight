namespace LogForesight.Core.Persistence;

/// <summary>
/// 派工脈絡：一趟執行（或一次試跑）建一份，把決策要查的資料一次讀進記憶體，
/// 之後 <see cref="WorkOrderDispatcher.Decide"/> 逐（主機, 問題）判斷時只查字典。
/// 本類別不寫任何資料；本趟狀態（已選中的人、負載增量、新登記的單、略過計數）只活在記憶體。
/// </summary>
public sealed class DispatchContext
{
    /// <summary>延續性回看天數（暫定）：（主機, 問題）這段期間內最後一件 resolved 案件的處理人優先</summary>
    internal const int ContinuityDays = 30;

    private readonly Dictionary<(string SourceUpper, int EventId), IssueProfile> _profiles;
    private readonly Dictionary<(string SourceKey, int EventId), List<WorkOrder>> _activeOrders;
    private readonly Dictionary<long, (int ActiveMembers, int ActiveWorkOrders)> _loads;
    private readonly Dictionary<string, HashSet<string>> _noiseByHost;
    private readonly Dictionary<string, Dictionary<(string SourceKey, int EventId), (DateTime ClosedAt, long HandlerId)>> _continuityByHost;
    private readonly Dictionary<(string SourceKey, int EventId), HashSet<long>> _chosen = new();
    private readonly List<WorkOrder> _registeredOrders = new();
    private readonly Dictionary<string, int> _skipCounts = new(StringComparer.Ordinal);
    private readonly IIssueCaseStore _cases;
    private readonly Dictionary<string, HashSet<string>> _dismissedByHost = new(StringComparer.OrdinalIgnoreCase);

    private DispatchContext(
        IIssueCaseStore cases,
        DispatchCandidatePool pool,
        Dictionary<(string SourceUpper, int EventId), IssueProfile> profiles,
        Dictionary<(string SourceKey, int EventId), List<WorkOrder>> activeOrders,
        Dictionary<long, (int ActiveMembers, int ActiveWorkOrders)> loads,
        Dictionary<string, HashSet<string>> noiseByHost,
        Dictionary<string, Dictionary<(string SourceKey, int EventId), (DateTime ClosedAt, long HandlerId)>> continuityByHost,
        HashSet<IssueSeverity> unhandledSeverities,
        bool autoDispatchEnabled,
        bool unavailable)
    {
        _cases = cases;
        Pool = pool;
        _profiles = profiles;
        _activeOrders = activeOrders;
        _loads = loads;
        _noiseByHost = noiseByHost;
        _continuityByHost = continuityByHost;
        UnhandledSeverities = unhandledSeverities;
        AutoDispatchEnabled = autoDispatchEnabled;
        Unavailable = unavailable;
    }

    public DispatchCandidatePool Pool { get; }

    public IReadOnlySet<IssueSeverity> UnhandledSeverities { get; }

    public bool AutoDispatchEnabled { get; }

    /// <summary>
    /// 派工脈絡建立失敗時的狀態（見 <see cref="CreateUnavailable"/>）：本趟一律不派工，
    /// 決策第一步就略過並計數，不查任何資料——派工是分析的附加步驟，不可以因為它讀不到資料就讓整趟分析失敗。
    /// </summary>
    public bool Unavailable { get; }

    /// <summary>
    /// 三路並行共用同一份脈絡時的互斥鎖：決策、建單、寫成員必須在同一把鎖內完成，
    /// 否則兩路同時對同人同問題各建一張單、負載增量也會互相覆蓋。
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// 靜音區間，鍵同 <see cref="IssueProfile.KeyOf"/>。本段恆為空字典（之後由靜音功能填入）；
    /// internal set 供測試手動塞區間。
    /// </summary>
    public IReadOnlyDictionary<(string SourceUpper, int EventId), IReadOnlyList<(DateTime From, DateTime To)>> MuteIntervals { get; internal set; }
        = new Dictionary<(string SourceUpper, int EventId), IReadOnlyList<(DateTime From, DateTime To)>>();

    /// <summary>本趟以 <see cref="RegisterOrder"/> 登記的單</summary>
    public IReadOnlyList<WorkOrder> RegisteredOrders => _registeredOrders;

    /// <summary>本趟各略過原因的次數（<see cref="Commit"/> 累計）</summary>
    public IReadOnlyDictionary<string, int> SkipCounts => _skipCounts;

    public static DispatchContext Build(
        DispatchCandidatePool pool, IIssueOwnerStore issueOwners, IWorkOrderStore orders, IIssueCaseStore cases,
        INoiseMarkStore noiseMarks, SystemSettings settings, DateTime now)
    {
        // 同鍵防禦性取第一筆（同 IssueProfile.IndexByKey 的既有慣例）
        var profiles = issueOwners.GetAll()
            .Where(p => p.OwnerUserIds.Count > 0)
            .GroupBy(p => IssueProfile.KeyOf(p.SourceName, p.EventId))
            .ToDictionary(g => g.Key, g => g.First());

        var activeOrders = new Dictionary<(string SourceKey, int EventId), List<WorkOrder>>();
        foreach (var order in orders.GetAllActive()) AddToIndex(activeOrders, order);

        var loads = orders.LoadBoard().ToDictionary(l => l.HandlerId, l => (l.ActiveMembers, l.ActiveWorkOrders));

        var noiseByHost = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mark in noiseMarks.GetAll())
        {
            if (!noiseByHost.TryGetValue(mark.HostName, out var keys))
                noiseByHost[mark.HostName] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(mark.IssueKey);
        }

        var continuityByHost = new Dictionary<string, Dictionary<(string SourceKey, int EventId), (DateTime ClosedAt, long HandlerId)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var c in cases.GetResolvedSince(now.Date.AddDays(-ContinuityDays)))
        {
            if (c.HandlerId == null || c.ClosedAt == null || c.SourceName == null || c.EventId == null) continue;

            if (!continuityByHost.TryGetValue(c.HostName, out var byIssue))
                continuityByHost[c.HostName] = byIssue = new();

            var key = (EfWorkOrderStore.SourceKeyOf(c.SourceName), c.EventId.Value);
            if (!byIssue.TryGetValue(key, out var existing) || c.ClosedAt.Value > existing.ClosedAt)
                byIssue[key] = (c.ClosedAt.Value, c.HandlerId.Value);
        }

        return new DispatchContext(cases, pool, profiles, activeOrders, loads, noiseByHost, continuityByHost,
            settings.ParseUnhandledSeverities(), settings.AutoDispatchEnabled, unavailable: false);
    }

    /// <summary>
    /// 錯誤處理用：<see cref="Build"/> 擲例外（例如問題檔案 blob 內容損毀）時，以此脈絡讓本趟分析照常完成、只是不派工。
    /// 決策遇到它一律回 <see cref="WorkOrderDispatcher.SkipUnavailable"/>，不會觸碰任何資料來源。
    /// </summary>
    public static DispatchContext CreateUnavailable(IIssueCaseStore cases) =>
        new(cases,
            new DispatchCandidatePool { ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0 },
            new Dictionary<(string SourceUpper, int EventId), IssueProfile>(),
            new Dictionary<(string SourceKey, int EventId), List<WorkOrder>>(),
            new Dictionary<long, (int ActiveMembers, int ActiveWorkOrders)>(),
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Dictionary<(string SourceKey, int EventId), (DateTime ClosedAt, long HandlerId)>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<IssueSeverity>(), autoDispatchEnabled: false, unavailable: true);

    /// <summary>紀錄日落在該問題任一靜音區間（含首尾）</summary>
    public bool IsMuted(string source, int eventId, DateTime recordDate) =>
        MuteIntervals.TryGetValue(IssueProfile.KeyOf(source, eventId), out var intervals)
        && intervals.Any(i => recordDate >= i.From && recordDate <= i.To);

    /// <summary>
    /// 「不再打擾」：該主機該鍵建立最晚的那件案件被以 wont_fix／false_positive／known_noise 結案
    /// ——處理的人已判定這個問題不值得處理，問題再出現不該再派出去（resolved 除外：修好後再出現是新事件）。
    /// 逐主機延遲載入：第一次查某主機時讀該主機全部案件並快取，同主機後續查詢不再打資料庫。
    /// </summary>
    public bool IsDismissed(string hostName, string issueKey)
    {
        if (!_dismissedByHost.TryGetValue(hostName, out var keys))
        {
            keys = DismissedKeys(_cases.GetMany(new[] { hostName }));
            _dismissedByHost[hostName] = keys;
        }
        return keys.Contains(issueKey);
    }

    /// <summary>
    /// 批次預載「不再打擾」：一次讀這些主機的全部案件，填入 <see cref="IsDismissed"/> 的逐主機快取；
    /// 之後對已預載主機的判斷不再查詢。沒有任何案件的主機也登記為空集合（同樣不再查）。
    /// </summary>
    public void PreloadDismissed(IReadOnlyCollection<string> hostNames)
    {
        var pending = hostNames
            .Where(h => !_dismissedByHost.ContainsKey(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pending.Count == 0) return;

        var casesByHost = _cases.GetMany(pending)
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var hostName in pending)
        {
            _dismissedByHost[hostName] = DismissedKeys(
                casesByHost.TryGetValue(hostName, out var cases) ? cases : new List<IssueCase>());
        }
    }

    /// <summary>「不再打擾」判定規則的唯一一份：每個鍵取建立最晚的案件，以不處理類狀態結案者</summary>
    private static HashSet<string> DismissedKeys(IEnumerable<IssueCase> casesOfHost) =>
        casesOfHost
            .GroupBy(c => c.IssueKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.CreatedAt).First())
            .Where(c => c.Status is IssueHandlingStatuses.WontFix
                or IssueHandlingStatuses.FalsePositive or IssueHandlingStatuses.KnownNoise)
            .Select(c => c.IssueKey)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>登記一張進行中單（真的建單後，或試跑時的負數 id 虛擬單）</summary>
    public void RegisterOrder(WorkOrder order)
    {
        AddToIndex(_activeOrders, order);
        _registeredOrders.Add(order);
        var load = LoadOf(order.HandlerId);
        _loads[order.HandlerId] = (load.ActiveMembers, load.ActiveWorkOrders + 1);
    }

    /// <summary>把決策計入本趟狀態：掛單／建單計成員數與已選中；略過計原因次數</summary>
    public void Commit(DispatchDecision decision, string source, int eventId)
    {
        if (decision.Kind == DispatchDecisionKind.Skip)
        {
            var reason = decision.SkipReason!;
            _skipCounts[reason] = _skipCounts.GetValueOrDefault(reason) + 1;
            return;
        }

        var handlerId = decision.HandlerId!.Value;
        var load = LoadOf(handlerId);
        _loads[handlerId] = (load.ActiveMembers + 1, load.ActiveWorkOrders);

        var key = IssueKeyOf(source, eventId);
        if (!_chosen.TryGetValue(key, out var set)) _chosen[key] = set = new HashSet<long>();
        set.Add(handlerId);
    }

    // ── 決策用唯讀查詢 ──────────────────────────────────────────────

    internal IssueProfile? ProfileFor(string source, int eventId) =>
        _profiles.GetValueOrDefault(IssueProfile.KeyOf(source, eventId));

    internal IReadOnlyList<WorkOrder> ActiveOrdersFor(string source, int eventId) =>
        _activeOrders.TryGetValue(IssueKeyOf(source, eventId), out var list) ? list : Array.Empty<WorkOrder>();

    /// <summary>不在看板上的人視為 (0, 0)</summary>
    internal (int ActiveMembers, int ActiveWorkOrders) LoadOf(long handlerId) =>
        _loads.GetValueOrDefault(handlerId);

    internal bool IsNoise(string hostName, string issueKey) =>
        _noiseByHost.TryGetValue(hostName, out var keys) && keys.Contains(issueKey);

    internal long? ContinuityHandlerFor(string hostName, string source, int eventId) =>
        _continuityByHost.TryGetValue(hostName, out var byIssue)
        && byIssue.TryGetValue(IssueKeyOf(source, eventId), out var entry)
            ? entry.HandlerId
            : null;

    internal IReadOnlySet<long> ChosenFor(string source, int eventId) =>
        _chosen.TryGetValue(IssueKeyOf(source, eventId), out var set) ? set : new HashSet<long>();

    private static (string SourceKey, int EventId) IssueKeyOf(string source, int eventId) =>
        (EfWorkOrderStore.SourceKeyOf(source), eventId);

    /// <summary>多問題單（問題欄為 null）不進索引</summary>
    private static void AddToIndex(Dictionary<(string SourceKey, int EventId), List<WorkOrder>> index, WorkOrder order)
    {
        if (order.SourceName == null || order.EventId == null) return;

        var key = IssueKeyOf(order.SourceName, order.EventId.Value);
        if (!index.TryGetValue(key, out var list)) index[key] = list = new List<WorkOrder>();
        list.Add(order);
    }
}

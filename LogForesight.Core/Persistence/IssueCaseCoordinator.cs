namespace LogForesight;

/// <summary>建案結果：Created=false 時代表該問題已有他人的進行中案件，未變更（2.1「只由一個人處理」）</summary>
public readonly record struct CaseBuildResult(bool Created, string? CaseId, long? ExistingHandlerId, int LinkedDayCount);

/// <summary>狀態同步結果：Applied=false 代表該問題目前沒有進行中案件，呼叫端應走既有的單日寫入</summary>
public readonly record struct CaseSyncResult(bool Applied, int SyncedDayCount, bool CaseClosed);

/// <summary>批次掛接結果：供 runRecorder / log 顯示掛接了幾個問題</summary>
public readonly record struct CaseAttachResult(int AttachedCount);

/// <summary>
/// 問題案件的建案／同步／掛接規則單點定義（docs/FEEDBACK-4-PLAN.md §0.4）。
///
/// 放 Core 而非 Web：console 批次（Program.cs 本機路徑、NetiqPipelineService）與 Web
/// （HandlingService）都要呼叫同一套規則——理由同 <c>DayHandlingDerivation</c>，
/// 語意分散在兩處遲早漂移。
///
/// 三個入口對應三個觸發點：
///   - <see cref="BuildCase"/>：指派處理人當下（2.1），建案並回溯關聯歷史風險日
///   - <see cref="SyncStatus"/>：標記問題狀態當下（2.2/2.3），若該問題已有進行中案件，
///     **整個狀態寫入（含觸發日）改由這裡統一處理**，呼叫端不再自己寫 IssueHandling
///   - <see cref="AttachNewDay"/>：批次排程每天寫入新紀錄後（2.4），把新的一天掛進進行中案件
///
/// 三者共用同一條「合格日」規則（<see cref="ResolveEligibleDays"/>）：**只排除已被使用者
/// 明確標成結案類的日子**（不論該列 CaseId 是否屬於較早、已結案的舊案件——結案就是結案，
/// 不因為案件重開而復活）。未標記、標記但未結案（含孤兒的舊案件殘留列）一律視為合格、
/// 可被目前的案件狀態覆蓋。
/// </summary>
public class IssueCaseCoordinator
{
    private readonly IIssueCaseStore _cases;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IRecordHandlingStore _handlingLog;
    private readonly IAnalysisRecordQuery _records;
    private readonly IHostStore _hosts;

    public IssueCaseCoordinator(
        IIssueCaseStore cases,
        IIssueHandlingStore issueHandlings,
        IRecordHandlingStore handlingLog,
        IAnalysisRecordQuery records,
        IHostStore hosts)
    {
        _cases = cases;
        _issueHandlings = issueHandlings;
        _handlingLog = handlingLog;
        _records = records;
        _hosts = hosts;
    }

    /// <summary>
    /// 建案（§0.4-A）：該（主機, 問題簽章）已有進行中案件時**保留原處理人、不搶走**（Q2），
    /// 回傳 Created=false＋原處理人 Id 供呼叫端提示「已由 ○○○ 的案件涵蓋」。
    /// 否則建立新案件，狀態固定為 in_progress（指派給人卻還是未處理語意矛盾，同日層級 Assign
    /// 的既有規則），並回溯關聯該主機全部留存歷史中含此問題的風險日（Q3：全部留存歷史，
    /// RetentionDays 天然設限）。
    /// </summary>
    public CaseBuildResult BuildCase(
        string hostName, string issueKey, string issueLabel, DateTime triggerDate,
        long handlerId, string? note, DateTime? dueDate,
        long? actorId, string actorAccount, DateTime occurredAt)
    {
        var existing = _cases.GetOpen(hostName, issueKey);
        if (existing != null)
            return new CaseBuildResult(Created: false, CaseId: null, ExistingHandlerId: existing.HandlerId, LinkedDayCount: 0);

        var host = _hosts.FindByName(hostName)
                   ?? throw new InvalidOperationException($"IssueCaseCoordinator：找不到主機「{hostName}」。");

        var caseId = Guid.NewGuid().ToString("n");
        const string status = IssueHandlingStatuses.InProgress;

        var eligibleDays = ResolveEligibleDays(host, hostName, issueKey);
        if (!eligibleDays.Contains(triggerDate.Date)) eligibleDays.Add(triggerDate.Date);
        eligibleDays.Sort();

        var toSave = new List<IssueHandling>(eligibleDays.Count);
        foreach (var date in eligibleDays)
        {
            toSave.Add(new IssueHandling
            {
                HostName = hostName, Date = date, IssueKey = issueKey, Status = status,
                Note = note, DueDate = dueDate, CaseId = caseId,
                ActorId = actorId, ActorAccount = actorAccount, UpdatedAt = occurredAt
            });

            _handlingLog.AppendLog(new RecordHandlingLog
            {
                HostName = hostName, Date = date, Status = status, IssueKey = issueKey, IssueLabel = issueLabel,
                Note = note, ActorId = actorId, ActorAccount = actorAccount,
                Action = date.Date == triggerDate.Date ? HandlingActions.CaseAssign : HandlingActions.CaseSync,
                CreatedAt = occurredAt
            });
        }
        _issueHandlings.SaveMany(toSave);

        _cases.Save(new IssueCase
        {
            CaseId = caseId, HostName = hostName, IssueKey = issueKey, IssueLabel = issueLabel,
            Status = status, HandlerId = handlerId, Note = note, DueDate = dueDate,
            FirstLinkedDate = eligibleDays[0], LastLinkedDate = eligibleDays[^1],
            CreatedAt = occurredAt, CreatedByAccount = actorAccount, UpdatedAt = occurredAt
        });

        return new CaseBuildResult(Created: true, CaseId: caseId, ExistingHandlerId: null, LinkedDayCount: toSave.Count);
    }

    /// <summary>
    /// 狀態同步（§0.4-B）：Applied=false 代表該問題目前沒有進行中案件，呼叫端（HandlingService.
    /// ApplyIssueStatus）應維持既有的「只寫觸發日」行為不變。Applied=true 時**這裡取得觸發日
    /// 寫入的完整主導權**——呼叫端不應該再自己寫 IssueHandling／歷程，包含觸發日本身：
    /// 觸發日與其餘合格日一起走同一份 <see cref="ResolveEligibleDays"/>，只是動作名稱不同
    /// （觸發日記「標記問題／清除問題標記」，其餘合格日記「案件同步」）——這樣使用者在
    /// 畫面上看到的仍是自己按下的那個動作，其餘天數才標「案件同步」的連動語意。
    ///
    /// clearing（調回未處理）時**不缺列清除**，一律落盤明確 open（既有 IssueHandlingStatuses.Open
    /// 的設計理由同構：缺列語意會讓下一次批次掛接把它自動蓋回 in_progress，操作等於沒發生）。
    ///
    /// 標成結案類時案件本身同步結案（ClosedAt=occurredAt）——之後同問題再出現即為全新的
    /// 未處理問題，不會被這個已結案的案件靜默吃掉（下次指派會走 BuildCase 開一個新案件）。
    /// </summary>
    public CaseSyncResult SyncStatus(
        string hostName, string issueKey, string issueLabel, DateTime triggerDate,
        string status, string? note, DateTime? dueDate, bool clearing,
        long? actorId, string actorAccount, DateTime occurredAt)
    {
        var openCase = _cases.GetOpen(hostName, issueKey);
        if (openCase == null) return new CaseSyncResult(Applied: false, SyncedDayCount: 0, CaseClosed: false);

        var host = _hosts.FindByName(hostName)
                   ?? throw new InvalidOperationException($"IssueCaseCoordinator：找不到主機「{hostName}」。");

        var effectiveStatus = clearing ? IssueHandlingStatuses.Open : status;
        var effectiveNote = clearing ? null : note;
        // DueDate 只在 InProgress／Observing 才有意義（後者是「觀察至」，docs/FEEDBACK-8-PLAN.md #4）
        var effectiveDueDate = effectiveStatus is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Observing
            ? dueDate : null;

        var eligibleDays = ResolveEligibleDays(host, hostName, issueKey);
        if (!eligibleDays.Contains(triggerDate.Date)) eligibleDays.Add(triggerDate.Date);

        var toSave = new List<IssueHandling>(eligibleDays.Count);
        foreach (var date in eligibleDays)
        {
            var isTrigger = date.Date == triggerDate.Date;

            toSave.Add(new IssueHandling
            {
                HostName = hostName, Date = date, IssueKey = issueKey, Status = effectiveStatus,
                Note = effectiveNote, DueDate = effectiveDueDate, CaseId = openCase.CaseId,
                ActorId = actorId, ActorAccount = actorAccount, UpdatedAt = occurredAt
            });

            var action = isTrigger
                ? (clearing ? HandlingActions.IssueStatusCleared : HandlingActions.IssueStatus)
                : HandlingActions.CaseSync;

            _handlingLog.AppendLog(new RecordHandlingLog
            {
                HostName = hostName, Date = date, Status = effectiveStatus, IssueKey = issueKey, IssueLabel = issueLabel,
                Note = effectiveNote, ActorId = actorId, ActorAccount = actorAccount,
                Action = action, CreatedAt = occurredAt
            });
        }
        _issueHandlings.SaveMany(toSave);

        var closing = !clearing && IssueHandlingStatuses.IsClosed(status);

        openCase.Status = effectiveStatus;
        openCase.Note = effectiveNote;
        openCase.DueDate = effectiveDueDate;
        openCase.UpdatedAt = occurredAt;
        if (closing) openCase.ClosedAt = occurredAt;
        _cases.Save(openCase);

        // SyncedDayCount 不含觸發日本身——呼叫端（HandlingService）用這個數字組「已同步案件涵蓋的
        // N 天」提示，使用者剛剛標記的那一天不該算進「連動」的天數裡，否則單一問題、無案件回溯
        // 涵蓋的最簡單情境也會顯示「已同步 1 天」，看起來像多做了什麼事
        var syncedOtherDays = eligibleDays.Count(d => d.Date != triggerDate.Date);
        return new CaseSyncResult(Applied: true, SyncedDayCount: syncedOtherDays, CaseClosed: closing);
    }

    /// <summary>
    /// 批次逐日掛接（§0.4-C）：console 排程每天寫入新的 DailyAnalysisRecord 後呼叫，
    /// 把當日 TopIssues 中「有進行中案件」的問題掛進案件——只掛進行中案件（已結案案件見
    /// <see cref="SyncStatus"/> 的重現語意），該日該問題已有標記時不覆蓋（防禦性，理論上不會發生），
    /// 內建冪等（已掛過的日子下次呼叫直接跳過），失敗由呼叫端決定是否吞掉（不擋分析主流程）。
    /// </summary>
    public CaseAttachResult AttachNewDay(string hostName, DateTime date, IReadOnlyCollection<LogIssueSignature> issues, DateTime occurredAt)
    {
        var openCases = _cases.GetOpenForHost(hostName);
        if (openCases.Count == 0 || issues.Count == 0) return new CaseAttachResult(0);

        var casesByIssueKey = openCases.ToDictionary(c => c.IssueKey, StringComparer.Ordinal);
        var existingForDay = _issueHandlings.GetForDay(hostName, date)
            .ToDictionary(h => h.IssueKey, StringComparer.Ordinal);

        var toSave = new List<IssueHandling>();
        foreach (var issue in issues)
        {
            var key = IssueSignatureKey.For(issue);
            if (!casesByIssueKey.TryGetValue(key, out var openCase)) continue;
            if (existingForDay.ContainsKey(key)) continue;   // 已有標記（防禦性）或已掛過（冪等）→ 不覆蓋

            toSave.Add(new IssueHandling
            {
                HostName = hostName, Date = date, IssueKey = key, Status = openCase.Status,
                Note = openCase.Note, DueDate = openCase.DueDate, CaseId = openCase.CaseId,
                ActorId = null, ActorAccount = string.Empty, UpdatedAt = occurredAt
            });

            _handlingLog.AppendLog(new RecordHandlingLog
            {
                HostName = hostName, Date = date, Status = openCase.Status, IssueKey = key, IssueLabel = openCase.IssueLabel,
                Note = openCase.Note, ActorId = null, ActorAccount = string.Empty,
                Action = HandlingActions.CaseAttach, CreatedAt = occurredAt
            });

            if (date.Date > openCase.LastLinkedDate.Date)
            {
                openCase.LastLinkedDate = date.Date;
                openCase.UpdatedAt = occurredAt;
                _cases.Save(openCase);
            }
        }

        _issueHandlings.SaveMany(toSave);
        return new CaseAttachResult(toSave.Count);
    }

    /// <summary>
    /// 合格日：主機全部留存歷史中含此問題簽章的風險日（<see cref="FindCandidateDays"/>），
    /// 扣掉已被使用者明確標成結案類的日子——結案就是結案，不論該列的 CaseId 是否屬於
    /// 更早、已經結案的舊案件（同問題重現後開新案件不該讓舊案件的結案紀錄復活）。
    /// 未標記、標記但未結案（含孤兒殘留列）一律視為合格，可被目前案件狀態覆蓋。
    /// </summary>
    private List<DateTime> ResolveEligibleDays(WebHost host, string hostName, string issueKey)
    {
        var candidates = FindCandidateDays(host, issueKey);
        if (candidates.Count == 0) return candidates;

        var existingByDate = _issueHandlings
            .GetMany(new[] { hostName }, candidates.Min(), candidates.Max())
            .Where(h => string.Equals(h.IssueKey, issueKey, StringComparison.Ordinal))
            .ToDictionary(h => h.Date.Date);

        return candidates
            .Where(d => !existingByDate.TryGetValue(d, out var existing) || !IssueHandlingStatuses.IsClosed(existing.Status))
            .ToList();
    }

    /// <summary>該主機（含已併入它的墓碑列別名展開）全部留存歷史中，TopIssues 含此問題簽章的風險日</summary>
    private List<DateTime> FindCandidateDays(WebHost host, string issueKey)
    {
        var hostKeys = HostIdentityResolver.Expand(_hosts.GetAll(), host.HostId);
        var filter = new RecordQueryFilter { Hosts = hostKeys };

        return _records.Query(filter)
            .Where(r => r.TopIssues.Any(i => IssueSignatureKey.For(i) == issueKey))
            .Select(r => r.Date.Date)
            .Distinct()
            .ToList();
    }
}

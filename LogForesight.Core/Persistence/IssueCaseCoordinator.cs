namespace LogForesight.Core.Persistence;

/// <summary>建案結果：Created=false 時代表該問題已有他人的進行中案件，未變更（2.1「只由一個人處理」）</summary>
public readonly record struct CaseBuildResult(bool Created, string? CaseId, long? ExistingHandlerId, int LinkedDayCount);

/// <summary>狀態同步結果：Applied=false 代表該問題目前沒有進行中案件，呼叫端應走既有的單日寫入</summary>
public readonly record struct CaseSyncResult(bool Applied, int SyncedDayCount, bool CaseClosed);

/// <summary>
/// 批次掛接結果：AttachedCount 供 runRecorder / log 顯示掛接了幾個問題；
/// Unassigned 為本次結束後當日仍無標記、也無進行中案件的問題，交給夜間派工
/// </summary>
public readonly record struct CaseAttachResult(int AttachedCount, IReadOnlyList<LogIssueSignature> Unassigned);

/// <summary>批次逐日寫入的送出結果：Inline=false 代表已存意圖交給背景，Rows 為（冪等跳過後的）目標列數</summary>
public readonly record struct CaseDaySubmitResult(bool Inline, int Rows, int PendingCases);

/// <summary>
/// 問題案件的建案／同步／掛接規則單點定義（docs/archive/FEEDBACK-4-PLAN.md §0.4）。
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
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly IIssueCaseStore _cases;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IRecordHandlingStore _handlingLog;
    private readonly IAnalysisRecordQuery _records;
    private readonly IHostStore _hosts;
    private readonly IIssueOwnerStore _issueProfiles;

    public IssueCaseCoordinator(
        IIssueCaseStore cases,
        IIssueHandlingStore issueHandlings,
        IRecordHandlingStore handlingLog,
        IAnalysisRecordQuery records,
        IHostStore hosts,
        IIssueOwnerStore issueProfiles)
    {
        _cases = cases;
        _issueHandlings = issueHandlings;
        _handlingLog = handlingLog;
        _records = records;
        _hosts = hosts;
        _issueProfiles = issueProfiles;
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

        _cases.Save(CreateOpenCase(
            caseId, hostName, issueKey, issueLabel,
            handlerId, note, dueDate,
            eligibleDays[0], eligibleDays[^1],
            occurredAt, actorAccount));

        return new CaseBuildResult(Created: true, CaseId: caseId, ExistingHandlerId: null, LinkedDayCount: toSave.Count);
    }

    /// <summary>
    /// 狀態同步（§0.4-B）：Applied=false 代表該問題目前沒有進行中案件，呼叫端（IssueHandlingCommandService.
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
        // DueDate 只在 InProgress／Observing 才有意義（後者是「觀察至」，docs/archive/FEEDBACK-8-PLAN.md #4）
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
                Note = IssueHandlingStatuses.ComposeLogNote(effectiveStatus, effectiveNote, effectiveDueDate),
                ActorId = actorId, ActorAccount = actorAccount,
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
    /// 批次逐日掛接（§0.4-C，回饋十九輪批次F 擴充機房結論自動套用，回饋二十一輪擴充問題負責人自動建案）：
    /// console 排程每天寫入新的 DailyAnalysisRecord 後呼叫，依優先序把當日 TopIssues 掛上狀態：
    ///   1. 該日該問題已有標記時不覆蓋（防禦性，理論上不會發生，或已掛過——內建冪等）。
    ///   2. 有進行中案件（既有行為）：掛進案件狀態，記 <see cref="HandlingActions.CaseAttach"/>。
    ///   3. 沒有進行中案件，但問題檔案（<see cref="IssueProfile"/>）有機房結論且
    ///      <see cref="IssueProfile.AutoApply"/>＝true：自動套用該結論，記
    ///      <see cref="HandlingActions.FleetApply"/>——這正是機房結論存在的主要情境
    ///      （多數問題從沒建過案件，不會走到分支 2）。
    /// 其餘問題回傳在 <see cref="CaseAttachResult.Unassigned"/>，由呼叫端交給夜間派工（NightlyDispatch：
    /// 負責人規則、續掛、自動派工皆走交辦單）。
    /// 只掛進行中案件／有 AutoApply 結論的問題（已結案案件見 <see cref="SyncStatus"/> 的重現語意），
    /// 失敗由呼叫端決定是否吞掉（不擋分析主流程）。
    /// </summary>
    public CaseAttachResult AttachNewDay(string hostName, DateTime date, IReadOnlyCollection<LogIssueSignature> issues, DateTime occurredAt)
    {
        if (issues.Count == 0) return new CaseAttachResult(0, Array.Empty<LogIssueSignature>());

        var openCases = _cases.GetOpenForHost(hostName);
        var casesByIssueKey = openCases.ToDictionary(c => c.IssueKey, StringComparer.Ordinal);

        var existingForDay = _issueHandlings.GetForDay(hostName, date)
            .ToDictionary(h => h.IssueKey, StringComparer.Ordinal);

        // fleet 結論索引：批次每天呼叫一次，profiles 整份 blob 讀本來就輕（一次性載入，
        // 不是逐問題查）。負責人派工改由交辦單派工（NightlyDispatch）處理，這裡只收 AutoApply 結論
        var profilesByKey = _issueProfiles.GetAll()
            .Where(p => p.AutoApply && p.ConclusionStatus != null)
            .GroupBy(p => IssueProfile.KeyOf(p.SourceName, p.EventId))
            .ToDictionary(g => g.Key, g => g.First());

        var toSave = new List<IssueHandling>();
        var casesToSave = new List<IssueCase>();
        foreach (var issue in issues)
        {
            var key = IssueSignatureKey.For(issue);
            if (existingForDay.ContainsKey(key)) continue;   // 已有標記（防禦性）或已掛過（冪等）→ 不覆蓋

            if (casesByIssueKey.TryGetValue(key, out var openCase))
            {
                toSave.Add(new IssueHandling
                {
                    HostName = hostName, Date = date, IssueKey = key, Status = openCase.Status,
                    Note = openCase.Note, DueDate = openCase.DueDate, CaseId = openCase.CaseId,
                    ActorId = null, ActorAccount = string.Empty, UpdatedAt = occurredAt
                });

                _handlingLog.AppendLog(new RecordHandlingLog
                {
                    HostName = hostName, Date = date, Status = openCase.Status, IssueKey = key, IssueLabel = openCase.IssueLabel,
                    Note = IssueHandlingStatuses.ComposeLogNote(openCase.Status, openCase.Note, openCase.DueDate),
                    ActorId = null, ActorAccount = string.Empty,
                    Action = HandlingActions.CaseAttach, CreatedAt = occurredAt
                });

                if (date.Date > openCase.LastLinkedDate.Date)
                {
                    openCase.LastLinkedDate = date.Date;
                    openCase.UpdatedAt = occurredAt;
                    // **累積後一次寫**（體檢 S4／docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3）：
                    // 原本在迴圈內逐案 Save，2000 台每晚約 4000 次寫入——blob 時代那是
                    // 4000 次整份讀改寫，且與 Web 端的標記操作搶同一把鎖
                    casesToSave.Add(openCase);
                }
                existingForDay[key] = toSave[^1];
                continue;
            }

            if (!profilesByKey.TryGetValue(IssueProfile.KeyOf(issue.Source, issue.EventId), out var profile)) continue;

            if (profile.AutoApply && profile.ConclusionStatus != null)
            {
                var note = $"〔機房結論〕{profile.ConclusionNote}";
                toSave.Add(new IssueHandling
                {
                    HostName = hostName, Date = date, IssueKey = key, Status = profile.ConclusionStatus!,
                    Note = note, DueDate = null, CaseId = null,
                    ActorId = null, ActorAccount = string.Empty, UpdatedAt = occurredAt
                });

                _handlingLog.AppendLog(new RecordHandlingLog
                {
                    HostName = hostName, Date = date, Status = profile.ConclusionStatus!, IssueKey = key,
                    IssueLabel = issue.SourceEventLabel, Note = note,
                    ActorId = null, ActorAccount = string.Empty,
                    Action = HandlingActions.FleetApply, CreatedAt = occurredAt
                });
                existingForDay[key] = toSave[^1];
            }
        }

        _issueHandlings.SaveMany(toSave);
        _cases.SaveMany(casesToSave);

        // 仍沒有當日列、也沒有進行中案件的問題交給派工（依問題鍵去重、保持輸入順序）
        var unassigned = new List<LogIssueSignature>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            var key = IssueSignatureKey.For(issue);
            if (!seen.Add(key) || existingForDay.ContainsKey(key) || casesByIssueKey.ContainsKey(key)) continue;
            unassigned.Add(issue);
        }

        return new CaseAttachResult(toSave.Count, unassigned);
    }

    /// <summary>
    /// 改派進行中案件的處理人（docs/archive/FEEDBACK-10-PLAN.md §9）。
    ///
    /// **不結案重開**：案件是「這個問題這一輪處理」的連續紀錄，換人只是換掛名的人。
    /// 結案再開會把回溯關聯過的日子重算一遍，也會讓「上次怎麼解的」（§9.3 第 15 項的
    /// 先前處理）誤以為這一輪已經結束過。逐日的 <c>IssueHandling</c> 列因此完全不動——
    /// 狀態沒變，變的只有案件層的 <c>HandlerId</c>。
    ///
    /// 回傳原處理人 Id 供呼叫端寫稽核與提示；沒有進行中案件時回 null（呼叫端應改走
    /// <see cref="BuildCase"/>——那是「開始有人處理」，與換人是不同的動作）。
    /// </summary>
    public long? ReassignCase(
        string hostName, string issueKey, long newHandlerId,
        long? actorId, string actorAccount, DateTime occurredAt)
    {
        var openCase = _cases.GetOpen(hostName, issueKey);
        if (openCase == null) return null;

        var previousHandlerId = openCase.HandlerId;
        openCase.HandlerId = newHandlerId;
        openCase.UpdatedAt = occurredAt;
        _cases.Save(openCase);

        // 歷程記在案件最近掛接的那一天：改派不是針對某一天的動作，沒有天然的「觸發日」，
        // 記在最新的一天最容易被看到——處理人打開的通常就是最近那筆風險日
        _handlingLog.AppendLog(new RecordHandlingLog
        {
            HostName = hostName,
            Date = openCase.LastLinkedDate,
            Status = openCase.Status,
            IssueKey = issueKey,
            IssueLabel = openCase.IssueLabel,
            Note = "變更案件處理人",
            ActorId = actorId,
            ActorAccount = actorAccount,
            Action = HandlingActions.CaseReassign,
            CreatedAt = occurredAt
        });

        return previousHandlerId;
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
            .Where(d => IsOverwritable(existingByDate.GetValueOrDefault(d)))
            .ToList();
    }

    /// <summary>
    /// 合格判定的唯一一份：該日此鍵沒有列，或有列但不是結案類——單案件方法（<see cref="ResolveEligibleDays"/>）
    /// 與批次逐日寫入（<see cref="SubmitCaseDays"/>／<see cref="ApplyPendingCaseDays"/>）共用。
    /// </summary>
    private static bool IsOverwritable(IssueHandling? existing) =>
        existing == null || !IssueHandlingStatuses.IsClosed(existing.Status);

    /// <summary>該主機（含已併入它的墓碑列別名展開）全部留存歷史中，TopIssues 含此問題簽章的風險日</summary>
    // 口徑：走 lf_top_issues 事實表，詳情已清除（PruneDetails 只清 ContentJson、事實表列保留）的日子也算候選日——
    // 與依問題視角／儀表板／待辦的 SQL 聚合同一口徑，案件標記才不會比畫面上看到的風險日少幾天。
    private List<DateTime> FindCandidateDays(WebHost host, string issueKey)
    {
        var hostKeys = HostIdentityResolver.Expand(_hosts.GetAll(), host.HostId);

        return _records.IssueDaysFor(hostKeys, new[] { issueKey })
            .Select(h => h.Date)
            .Distinct()
            .ToList();
    }

    // ── 批次逐日寫入（交辦單底層）─────────────────────────────────────────────

    /// <summary>就地寫入的逐日列數門檻（暫定）：超過就改存意圖交給背景服務分批寫</summary>
    public const int CaseDayInlineRowLimit = 5000;

    /// <summary>逐日列 GetMany 的主機名分批大小（避免 IN 清單過長）</summary>
    private const int CaseDayHostBatchSize = 500;

    /// <summary>取消模式 GetByCases 的案件 id 分批大小</summary>
    private const int CaseDayCaseIdBatchSize = 500;

    /// <summary>逐日列 SaveMany 的每批列數</summary>
    private const int CaseDaySaveBatchSize = 5000;

    /// <summary>
    /// 一次把多個案件的狀態展開到各自的合格日（三種模式見 <see cref="CaseDayModes"/>）。
    /// 預估列數 ≤ <paramref name="inlineRowLimit"/> 當場寫完；否則只把意圖存在案件上，由背景
    /// （<see cref="ApplyPendingCaseDays"/>）分批寫。兩條路都會把傳入的案件物件原樣存回。
    /// 冪等：既有列內容與目標相同且 UpdatedAt 等於 OccurredAt 的日子不寫列、不記歷程、不計列數。
    /// </summary>
    public CaseDaySubmitResult SubmitCaseDays(IReadOnlyList<IssueCase> cases, CaseDayIntent intent, int inlineRowLimit)
    {
        if (cases.Count == 0) return new CaseDaySubmitResult(Inline: true, Rows: 0, PendingCases: 0);

        var plans = PlanCaseDays(cases.Select(c => (c, intent)).ToList());
        var rows = plans.Sum(p => p.Rows.Count);

        if (rows <= inlineRowLimit)
        {
            WritePlans(plans);
            foreach (var plan in plans)
            {
                ApplyLinkedDates(plan.Case, plan);
                plan.Case.DaySyncPending = false;
                plan.Case.DaySyncIntent = null;
            }
            _cases.SaveMany(cases);
            return new CaseDaySubmitResult(Inline: true, Rows: rows, PendingCases: 0);
        }

        foreach (var issueCase in cases)
        {
            issueCase.DaySyncPending = true;
            issueCase.DaySyncIntent = intent;
        }
        _cases.SaveMany(cases);
        return new CaseDaySubmitResult(Inline: false, Rows: rows, PendingCases: cases.Count);
    }

    /// <summary>
    /// 背景一批：取待同步案件，依各自的意圖寫入（同一批合併查詢），寫完以「意圖未變更才清」清旗標。
    /// 指派模式要推進的 First/LastLinkedDate 以 store 讀新值後只改這兩欄再存——批次開頭讀到的
    /// 物件可能已經過時，整列覆寫會蓋掉使用者期間做的變更。回傳本批處理的案件數。
    /// </summary>
    public int ApplyPendingCaseDays(int take)
    {
        var pending = _cases.GetDaySyncPending(take);
        if (pending.Count == 0) return 0;

        var work = new List<(IssueCase Case, CaseDayIntent Intent)>();
        foreach (var issueCase in pending)
        {
            if (issueCase.DaySyncIntent != null)
            {
                work.Add((issueCase, issueCase.DaySyncIntent));
                continue;
            }

            // 髒列：旗標在但沒有意圖，無從寫起——清旗標免得每一批都撿到它
            Log.Warn("[案件逐日同步] 案件 {CaseId}（{Host}）待同步旗標為真但沒有意圖，清除旗標、不寫入",
                issueCase.CaseId, issueCase.HostName);
            var fresh = _cases.Get(issueCase.CaseId);
            if (fresh != null && fresh.DaySyncPending && fresh.DaySyncIntent == null)
            {
                fresh.DaySyncPending = false;
                _cases.Save(fresh);
            }
        }

        var plans = PlanCaseDays(work);
        WritePlans(plans);

        foreach (var plan in plans)
        {
            if (plan.LinkedMin != null)
            {
                var fresh = _cases.Get(plan.Case.CaseId);
                if (fresh != null && ApplyLinkedDates(fresh, plan)) _cases.Save(fresh);
            }
            _cases.ClearDaySyncPendingIfUnchanged(plan.Case.CaseId, plan.Intent);
        }

        return pending.Count;
    }

    /// <summary>單一案件的展開結果：要寫的逐日列與歷程（已排除冪等跳過的日子）</summary>
    private sealed class CaseDayPlan
    {
        public required IssueCase Case { get; init; }
        public required CaseDayIntent Intent { get; init; }
        public List<IssueHandling> Rows { get; } = new();
        public List<RecordHandlingLog> Logs { get; } = new();

        /// <summary>指派模式的合格日範圍（含冪等跳過的日子）；null＝不推進案件的關聯日期</summary>
        public DateTime? LinkedMin { get; set; }
        public DateTime? LinkedMax { get; set; }
    }

    /// <summary>
    /// 批次算出每個案件的目標列。查詢次數與案件數無關：主機清單讀一次、候選日一次
    /// <see cref="IAnalysisRecordQuery.IssueDaysFor"/>、既有逐日列依主機名分批 GetMany。
    /// </summary>
    private List<CaseDayPlan> PlanCaseDays(IReadOnlyList<(IssueCase Case, CaseDayIntent Intent)> work)
    {
        var plans = new List<CaseDayPlan>(work.Count);
        if (work.Count == 0) return plans;

        var allHosts = _hosts.GetAll();
        var aliasIndex = new HostAliasIndex(allHosts);
        var hostsByName = new Dictionary<string, WebHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in allHosts) hostsByName.TryAdd(host.HostName, host);

        // 候選日（取消模式不查）：全部案件主機的別名合併成一次查詢，命中再依別名 id 歸回案件主機
        var hostKeys = new Dictionary<long, HostKey>();
        var ownerIdsByAliasId = new Dictionary<long, HashSet<long>>();
        var issueKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (issueCase, intent) in work)
        {
            if (intent.Mode == CaseDayModes.Cancel) continue;
            issueKeys.Add(issueCase.IssueKey);
            if (!hostsByName.TryGetValue(issueCase.HostName, out var host)) continue;
            foreach (var alias in aliasIndex.Aliases(host.HostId))
            {
                hostKeys.TryAdd(alias.HostId, alias);
                if (!ownerIdsByAliasId.TryGetValue(alias.HostId, out var owners))
                    ownerIdsByAliasId[alias.HostId] = owners = new HashSet<long>();
                owners.Add(host.HostId);
            }
        }

        // （案件主機 id, 問題鍵）→ 候選日。host_id=0 的舊列無法歸回特定主機，批次路徑不採用
        var candidatesByOwner = new Dictionary<(long HostId, string IssueKey), HashSet<DateTime>>();
        if (hostKeys.Count > 0 && issueKeys.Count > 0)
        {
            foreach (var hit in _records.IssueDaysFor(hostKeys.Values.ToList(), issueKeys.ToList()))
            {
                if (!ownerIdsByAliasId.TryGetValue(hit.HostId, out var owners)) continue;
                foreach (var ownerId in owners)
                {
                    if (!candidatesByOwner.TryGetValue((ownerId, hit.IssueKey), out var days))
                        candidatesByOwner[(ownerId, hit.IssueKey)] = days = new HashSet<DateTime>();
                    days.Add(hit.Date.Date);
                }
            }
        }

        // 每個案件的候選日（指派／同步含觸發日；取消不查候選日，既有列依案件 id 精確查）
        var candidateDays = new List<List<DateTime>>(work.Count);
        DateTime? from = null, to = null;
        void Widen(DateTime d)
        {
            if (from == null || d < from) from = d;
            if (to == null || d > to) to = d;
        }
        foreach (var (issueCase, intent) in work)
        {
            var days = new List<DateTime>();
            if (intent.Mode != CaseDayModes.Cancel)
            {
                if (hostsByName.TryGetValue(issueCase.HostName, out var host)
                    && candidatesByOwner.TryGetValue((host.HostId, issueCase.IssueKey), out var found))
                    days.AddRange(found);
                if (intent.TriggerDate != null) Widen(intent.TriggerDate.Value.Date);
                foreach (var d in days) Widen(d);
            }
            candidateDays.Add(days);
        }

        // 取消模式的既有列：依案件 id 精確查——觸發日可能落在 First/LastLinkedDate 範圍外
        // （SyncStatus 會寫觸發日但不推進關聯日期），用日期範圍查會漏
        var ownedByCaseId = new Dictionary<string, List<IssueHandling>>(StringComparer.Ordinal);
        var cancelIds = work.Where(w => w.Intent.Mode == CaseDayModes.Cancel)
            .Select(w => w.Case.CaseId).Distinct(StringComparer.Ordinal).ToList();
        foreach (var batch in cancelIds.Chunk(CaseDayCaseIdBatchSize))
        {
            foreach (var row in _issueHandlings.GetByCases(batch))
            {
                if (!ownedByCaseId.TryGetValue(row.CaseId!, out var owned))
                    ownedByCaseId[row.CaseId!] = owned = new List<IssueHandling>();
                owned.Add(row);
            }
        }

        // 既有逐日列：主機名分批，記憶體依（主機, 鍵）→ 日 配對
        var existing = new Dictionary<(string HostName, string IssueKey), Dictionary<DateTime, IssueHandling>>(ExistingKeyComparer.Instance);
        if (from != null)
        {
            var names = work.Where(w => w.Intent.Mode != CaseDayModes.Cancel)
                .Select(w => w.Case.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var batch in names.Chunk(CaseDayHostBatchSize))
            {
                foreach (var row in _issueHandlings.GetMany(batch, from.Value, to!.Value))
                {
                    if (!existing.TryGetValue((row.HostName, row.IssueKey), out var byDate))
                        existing[(row.HostName, row.IssueKey)] = byDate = new Dictionary<DateTime, IssueHandling>();
                    byDate[row.Date.Date] = row;
                }
            }
        }

        for (var i = 0; i < work.Count; i++)
        {
            var (issueCase, intent) = work[i];
            var existingByDate = intent.Mode == CaseDayModes.Cancel
                ? OwnedByDate(ownedByCaseId.GetValueOrDefault(issueCase.CaseId), issueCase)
                : existing.GetValueOrDefault((issueCase.HostName, issueCase.IssueKey)) ?? new Dictionary<DateTime, IssueHandling>();
            IssueHandling? Existing(DateTime d) => existingByDate.GetValueOrDefault(d.Date);

            var plan = new CaseDayPlan { Case = issueCase, Intent = intent };
            plans.Add(plan);

            List<DateTime> days;
            if (intent.Mode == CaseDayModes.Cancel)
            {
                days = existingByDate
                    .Where(e => e.Value.CaseId == issueCase.CaseId && IsOverwritable(e.Value))
                    .Select(e => e.Key)
                    .ToList();
            }
            else
            {
                days = candidateDays[i].Where(d => IsOverwritable(Existing(d))).ToList();
                if (intent.TriggerDate != null && !days.Contains(intent.TriggerDate.Value.Date))
                    days.Add(intent.TriggerDate.Value.Date);
            }
            days.Sort();

            if (intent.Mode == CaseDayModes.Assign && days.Count > 0)
            {
                plan.LinkedMin = days[0];
                plan.LinkedMax = days[^1];
            }

            foreach (var date in days)
            {
                var current = Existing(date);
                var target = TargetRow(issueCase, intent, date, current);
                if (current != null
                    && current.Status == target.Status && current.Note == target.Note
                    && current.DueDate == target.DueDate && current.CaseId == target.CaseId
                    && current.UpdatedAt == intent.OccurredAt)
                    continue;

                plan.Rows.Add(target);
                plan.Logs.Add(new RecordHandlingLog
                {
                    HostName = issueCase.HostName, Date = date, Status = target.Status,
                    IssueKey = issueCase.IssueKey, IssueLabel = issueCase.IssueLabel,
                    Note = LogNote(intent, target),
                    ActorId = intent.ActorId, ActorAccount = intent.ActorAccount,
                    Action = LogAction(issueCase, intent, date),
                    CreatedAt = intent.OccurredAt
                });
            }
        }

        return plans;
    }

    /// <summary>取消模式：案件擁有的列（限本案件主機與問題鍵）依日期索引</summary>
    private static Dictionary<DateTime, IssueHandling> OwnedByDate(List<IssueHandling>? owned, IssueCase issueCase)
    {
        var byDate = new Dictionary<DateTime, IssueHandling>();
        if (owned == null) return byDate;
        foreach (var row in owned)
        {
            if (string.Equals(row.HostName, issueCase.HostName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.IssueKey, issueCase.IssueKey, StringComparison.Ordinal))
                byDate[row.Date.Date] = row;
        }
        return byDate;
    }

    private static IssueHandling TargetRow(IssueCase issueCase, CaseDayIntent intent, DateTime date, IssueHandling? current)
    {
        var row = new IssueHandling
        {
            HostName = issueCase.HostName, Date = date, IssueKey = issueCase.IssueKey,
            CaseId = issueCase.CaseId,
            ActorId = intent.ActorId, ActorAccount = intent.ActorAccount, UpdatedAt = intent.OccurredAt
        };

        switch (intent.Mode)
        {
            case CaseDayModes.Assign:
                row.Status = intent.Status;
                row.Note = intent.Note;
                row.DueDate = intent.DueDate;
                break;
            case CaseDayModes.Sync:
                row.Status = intent.Clearing ? IssueHandlingStatuses.Open : intent.Status;
                row.Note = intent.Clearing ? null : intent.Note;
                // 同 SyncStatus：DueDate 只在 InProgress／Observing 才有意義
                row.DueDate = row.Status is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Observing
                    ? intent.DueDate : null;
                break;
            case CaseDayModes.Cancel:
                row.Status = IssueHandlingStatuses.Open;
                row.Note = null;
                row.DueDate = null;
                // 取消只調回 open，列的出處保留原值（合格日本來就限定 CaseId＝本案件）
                row.CaseId = current!.CaseId;
                break;
            default:
                throw new InvalidOperationException($"IssueCaseCoordinator：不支援的逐日同步模式「{intent.Mode}」。");
        }
        return row;
    }

    private static string? LogNote(CaseDayIntent intent, IssueHandling target) => intent.Mode switch
    {
        CaseDayModes.Sync => IssueHandlingStatuses.ComposeLogNote(target.Status, target.Note, target.DueDate),
        _ => intent.Note
    };

    private static string LogAction(IssueCase issueCase, CaseDayIntent intent, DateTime date)
    {
        switch (intent.Mode)
        {
            case CaseDayModes.Assign:
                var assignDay = (intent.TriggerDate ?? issueCase.LastLinkedDate).Date;
                return date == assignDay ? HandlingActions.CaseAssign : HandlingActions.CaseSync;
            case CaseDayModes.Sync:
                if (intent.TriggerDate != null && date == intent.TriggerDate.Value.Date)
                    return intent.Clearing ? HandlingActions.IssueStatusCleared : HandlingActions.IssueStatus;
                return HandlingActions.CaseSync;
            default:
                return HandlingActions.IssueStatusCleared;
        }
    }

    private void WritePlans(List<CaseDayPlan> plans)
    {
        var rows = plans.SelectMany(p => p.Rows).ToList();
        foreach (var batch in rows.Chunk(CaseDaySaveBatchSize)) _issueHandlings.SaveMany(batch);
        foreach (var log in plans.SelectMany(p => p.Logs)) _handlingLog.AppendLog(log);
    }

    /// <summary>指派模式推進案件的 First/LastLinkedDate（原值與合格日取最小／最大），回傳是否有變</summary>
    private static bool ApplyLinkedDates(IssueCase issueCase, CaseDayPlan plan)
    {
        if (plan.LinkedMin == null || plan.LinkedMax == null) return false;
        var first = plan.LinkedMin.Value < issueCase.FirstLinkedDate ? plan.LinkedMin.Value : issueCase.FirstLinkedDate;
        var last = plan.LinkedMax.Value > issueCase.LastLinkedDate ? plan.LinkedMax.Value : issueCase.LastLinkedDate;
        if (first == issueCase.FirstLinkedDate && last == issueCase.LastLinkedDate) return false;
        issueCase.FirstLinkedDate = first;
        issueCase.LastLinkedDate = last;
        return true;
    }

    /// <summary>既有逐日列的配對鍵：主機名不分大小寫（同 store 的 host_name_key 語意），問題鍵 Ordinal</summary>
    private sealed class ExistingKeyComparer : IEqualityComparer<(string HostName, string IssueKey)>
    {
        public static readonly ExistingKeyComparer Instance = new();

        public bool Equals((string HostName, string IssueKey) x, (string HostName, string IssueKey) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.HostName, y.HostName)
            && StringComparer.Ordinal.Equals(x.IssueKey, y.IssueKey);

        public int GetHashCode((string HostName, string IssueKey) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.HostName),
                StringComparer.Ordinal.GetHashCode(key.IssueKey));
    }

    private static IssueCase CreateOpenCase(
        string caseId, string hostName, string issueKey, string issueLabel,
        long? handlerId, string? note, DateTime? dueDate,
        DateTime firstLinkedDate, DateTime lastLinkedDate,
        DateTime occurredAt, string createdByAccount)
    {
        return new IssueCase
        {
            CaseId = caseId,
            HostName = hostName,
            IssueKey = issueKey,
            IssueLabel = issueLabel,
            Status = IssueHandlingStatuses.InProgress,
            HandlerId = handlerId,
            Note = note,
            DueDate = dueDate,
            FirstLinkedDate = firstLinkedDate,
            LastLinkedDate = lastLinkedDate,
            CreatedAt = occurredAt,
            CreatedByAccount = createdByAccount,
            UpdatedAt = occurredAt
        };
    }
}

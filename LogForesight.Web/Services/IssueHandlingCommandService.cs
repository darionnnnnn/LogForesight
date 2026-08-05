using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題層級的處理狀態標記（單筆／批次）與問題案件的跨主機批次指派
/// （docs/WEB-SPEC.md §9.3、docs/archive/FEEDBACK-4-PLAN.md §4）。
/// 日層級操作見 <see cref="DayHandlingCommandService"/>；查詢/待辦/工作頁見
/// <see cref="HandlingHistoryQueryService"/>。
/// </summary>
public class IssueHandlingCommandService
{
    private readonly IRecordHandlingStore _store;
    private readonly IIssueHandlingStore _issueStore;
    private readonly IIssueCaseStore _cases;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly HandlingProgressCalculator _progress;

    public IssueHandlingCommandService(
        IRecordHandlingStore store,
        IIssueHandlingStore issueStore,
        IIssueCaseStore cases,
        IssueCaseCoordinator caseCoordinator,
        INoiseMarkStore noiseMarks,
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit,
        HandlingProgressCalculator progress)
    {
        _store = store;
        _issueStore = issueStore;
        _cases = cases;
        _caseCoordinator = caseCoordinator;
        _noiseMarks = noiseMarks;
        _repository = repository;
        _hosts = hosts;
        _users = users;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
        _progress = progress;
    }

    public IssueStatusResultDto SetIssueStatus(long hostId, DateTime date, SetIssueStatusRequest request)
    {
        var host = RequireVisibleHost(hostId);
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var clearing = string.IsNullOrWhiteSpace(request.Status);
        ValidateIssueStatus(request.Status, request.DueDate, clearing);

        // 問題必須真的存在於當日紀錄——否則會存下指向不存在問題的狀態
        var issue = record.TopIssues.FirstOrDefault(i => IssueSignatureKey.For(i) == request.IssueKey)
                    ?? throw DomainException.Validation("找不到這個問題，可能紀錄已更新，請重新整理。");

        RequireIssueAllowed(hostId, request.IssueKey);
        RequireNotHandledByOthers(host.HostName, request.IssueKey);

        var caseSync = ApplyIssueStatus(host, date, request.IssueKey, HandlingTextHelpers.IssueLabel(issue), request.Status, request.Note, request.DueDate, request.ForgetNoise, clearing, DateTime.Now);

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: clearing
                ? $"清除 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】的處理標記"
                : $"將 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】標為「{HandlingTextHelpers.IssueStatusText(request.Status)}」" +
                  (caseSync.Applied ? $"（已同步案件涵蓋的 {caseSync.SyncedDayCount} 天）" : ""),
            targetKind: "issue_handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}/{request.IssueKey}",
            detail: new { request.IssueKey, request.Status, request.Note, request.DueDate });

        // 回傳更新後的當日進度，讓前端就地更新「N/M 已處理」與日層級推導狀態
        var progress = _progress.ComputeProgress(host, date, record);

        return new IssueStatusResultDto
        {
            IssueKey = request.IssueKey,
            Status = clearing ? string.Empty : request.Status,
            StatusText = clearing ? "未處理" : HandlingTextHelpers.IssueStatusText(request.Status),
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            DayStatus = progress.DayStatus,
            DayStatusText = HandlingTextHelpers.StatusText(progress.DayStatus),
            CaseSyncedDayCount = caseSync.Applied ? caseSync.SyncedDayCount : 0
        };
    }

    public BatchIssueStatusResultDto SetIssueStatusBatch(long hostId, DateTime date, BatchSetIssueStatusRequest request)
    {
        var host = RequireVisibleHost(hostId);
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var clearing = string.IsNullOrWhiteSpace(request.Status);
        ValidateIssueStatus(request.Status, request.DueDate, clearing);

        // 只套用當日紀錄真的還有的問題——頁面沒重新整理時勾選的問題可能已經不在了。
        // GroupBy 防禦性地取第一筆，同 LoadGuidanceLookup 的寫法，避免壞資料的重複鍵讓整批炸掉
        var labelByKey = record.TopIssues
            .GroupBy(IssueSignatureKey.For, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => HandlingTextHelpers.IssueLabel(g.First()), StringComparer.Ordinal);
        var appliedKeys = request.IssueKeys.Distinct(StringComparer.Ordinal).Where(labelByKey.ContainsKey).ToList();
        if (appliedKeys.Count == 0)
            throw DomainException.Validation("找不到任何勾選的問題，可能紀錄已更新，請重新整理。");

        // 逐問題各寫一列歷程（D4：批次勾 N 項就要留下 N 列，不做彙總）——見 ApplyIssueStatus 內的 AppendIssueLog。
        // 整批共用同一個 occurredAt（不是逐次呼叫 DateTime.Now）：前端 timeline 靠「同一操作者＋
        // 同一時間戳」把這批列視覺分組成一塊，時間戳若逐次取值會因執行順序產生極小差異而分不了組
        // 先整批檢查再開始寫（§8）：中途才發現有一個問題是別人的案件，前面幾筆已經落盤，
        // 使用者會得到「一半成功一半失敗」的狀態，比整批擋下來難收拾得多
        foreach (var issueKey in appliedKeys)
        {
            RequireIssueAllowed(hostId, issueKey);
            RequireNotHandledByOthers(host.HostName, issueKey);
        }

        var occurredAt = DateTime.Now;
        var totalSyncedDays = 0;
        foreach (var issueKey in appliedKeys)
        {
            var caseSync = ApplyIssueStatus(host, date, issueKey, labelByKey[issueKey], request.Status, request.Note, request.DueDate, request.ForgetNoise, clearing, occurredAt);
            if (caseSync.Applied) totalSyncedDays += caseSync.SyncedDayCount;
        }

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: clearing
                ? $"批次清除 {host.HostName} {date:yyyy-MM-dd} 的 {appliedKeys.Count} 個問題的處理標記"
                : $"批次將 {host.HostName} {date:yyyy-MM-dd} 的 {appliedKeys.Count} 個問題標為「{HandlingTextHelpers.IssueStatusText(request.Status)}」" +
                  (totalSyncedDays > 0 ? $"（含案件同步共 {totalSyncedDays} 天）" : ""),
            targetKind: "issue_handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new { IssueKeys = appliedKeys, request.Status, request.Note, request.DueDate });

        var progress = _progress.ComputeProgress(host, date, record);

        return new BatchIssueStatusResultDto
        {
            UpdatedIssueKeys = appliedKeys,
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            DayStatus = progress.DayStatus,
            DayStatusText = HandlingTextHelpers.StatusText(progress.DayStatus),
            CaseSyncedDayCount = totalSyncedDays
        };
    }

    /// <summary>
    /// 案件授與者只能碰被交辦的那些問題（docs/archive/FEEDBACK-10-PLAN.md §7）。
    /// 前端已經只顯示那些問題，這裡擋的是「自己換一個 issueKey 直接打 API」——
    /// 沒有這道檢查，案件授與等於把整台主機當日的所有問題都交出去。
    /// 回 404 而非 403，與 <see cref="IVisibilityService.EnsureVisible"/> 同一個理由（不確認存在性）。
    /// </summary>
    private void RequireIssueAllowed(long hostId, string issueKey)
    {
        var allowed = _visibility.GetIssueKeyRestriction(hostId);
        if (allowed != null && !allowed.Contains(issueKey))
            throw DomainException.NotFound("找不到這個問題，或您沒有檢視權限。");
    }

    /// <summary>
    /// 別人的案件正在處理的問題，不接受直接改狀態（docs/archive/FEEDBACK-10-PLAN.md §8）。
    ///
    /// 理由是協調而非權限：案件的意義就是「這個問題目前歸這個人處理」，讓第二個人也能標，
    /// 後標的會靜默蓋掉先標的判斷，兩邊都不知道發生過什麼。要換人處理有正規路徑——
    /// 由具 <c>Assign</c> 能力者改派（§9），改派會留下歷程與稽核。
    ///
    /// **admin 也擋**：admin 的正確動作是改派，不是繞過協調機制直接寫。
    /// 前端已把這些列的 checkbox 停用（§8），這裡是防繞過的實際防線。
    /// </summary>
    private void RequireNotHandledByOthers(string hostName, string issueKey)
    {
        var openCase = _cases.GetOpen(hostName, issueKey);
        if (openCase?.HandlerId is not { } handlerId || handlerId == _currentUser.UserId) return;

        var handler = _users.Get(handlerId);
        var name = handler == null ? "其他人" : NameFormat.WithAccount(handler.DisplayName, handler.Account);
        throw DomainException.Validation(
            $"此問題由 {name} 的案件處理中，無法直接變更狀態。如需接手，請由管理者改派處理人。");
    }

    private static void ValidateIssueStatus(string status, DateTime? dueDate, bool clearing)
    {
        if (!clearing && !IssueHandlingStatuses.IsValid(status))
            throw DomainException.Validation($"未知的問題處理狀態「{status}」。");

        if (status == IssueHandlingStatuses.InProgress && dueDate.HasValue && dueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");

        // 觀察中一定要有觀察至日期（docs/archive/FEEDBACK-8-PLAN.md #4）——沒有終點的「觀察」沒有意義，
        // 前端固定送「今天 + N 天」（1~90 天），這裡防禦性驗證同一個範圍，不只信前端
        if (status == IssueHandlingStatuses.Observing)
        {
            if (!dueDate.HasValue)
                throw DomainException.Validation("標記為觀察中時必須指定觀察至日期。");
            if (dueDate.Value.Date < DateTime.Today)
                throw DomainException.Validation("觀察至日期不可早於今天。");
            if (dueDate.Value.Date > DateTime.Today.AddDays(90))
                throw DomainException.Validation("觀察至日期不可超過 90 天。");
        }
    }

    /// <summary>
    /// 寫入單一問題的處理狀態，並成對寫入處理歷程（不含稽核記錄——單筆與批次的稽核摘要不同，
    /// 由呼叫端各自記）。批次呼叫端逐一呼叫本方法，天然逐問題各留一列歷程（D4）。
    ///
    /// docs/archive/FEEDBACK-4-PLAN.md §0.4-B：先問 <see cref="IssueCaseCoordinator.SyncStatus"/> 這個
    /// 問題目前有沒有進行中案件——**有的話這裡完全不寫**（含觸發日本身），寫入的主導權整個交給
    /// Coordinator（它會把觸發日與案件涵蓋的其餘日子一起處理，觸發日記「標記問題」、其餘天記
    /// 「案件同步」）；沒有案件時維持既有行為（只寫觸發日）。已知雜訊記憶／forgetNoise 這兩個
    /// 副作用維持在這裡執行、只做一次——那是主機＋簽章跨日一筆的記憶，不隨案件同步逐日重複寫。
    /// </summary>
    private CaseSyncResult ApplyIssueStatus(
        WebHost host, DateTime date, string issueKey, string issueLabel, string status, string? note, DateTime? dueDate, bool forgetNoise, bool clearing, DateTime occurredAt)
    {
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var caseSync = _caseCoordinator.SyncStatus(
            host.HostName, issueKey, issueLabel, date,
            status, trimmedNote, dueDate, clearing,
            actorId, _currentUser.Account, occurredAt);

        if (!caseSync.Applied)
        {
            if (clearing)
            {
                _issueStore.Clear(host.HostName, date, issueKey);
                // 清除標記＝調回未處理，歷程狀態記 open 較有意義（而非空字串），STATUS_VARIANTS/StatusText 通用
                AppendIssueLog(host, date, issueKey, issueLabel, HandlingActions.IssueStatusCleared, status: HandlingStatuses.Open, note: null, occurredAt);
            }
            else
            {
                // 觀察中的歷程自動補「（觀察至 yyyy-MM-dd）」——歷程列沒有 DueDate 欄位（#4）
                AppendIssueLog(host, date, issueKey, issueLabel, HandlingActions.IssueStatus, status,
                    IssueHandlingStatuses.ComposeLogNote(status, trimmedNote, dueDate), occurredAt);

                _issueStore.Save(new IssueHandling
                {
                    HostName = host.HostName,
                    Date = date.Date,
                    IssueKey = issueKey,
                    Status = status,
                    Note = trimmedNote,
                    // 預計完成日只在「處理中」／「觀察中」（觀察至，docs/archive/FEEDBACK-8-PLAN.md #4）
                    // 才有意義，其餘狀態一律不存，避免舊資料殘留誤導
                    DueDate = status is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Observing ? dueDate : null,
                    ActorId = actorId,
                    ActorAccount = _currentUser.Account,
                    UpdatedAt = occurredAt
                });
            }
        }
        // caseSync.Applied：Coordinator 已經把觸發日（含歷程）與案件涵蓋的其餘日子全部寫好，
        // 這裡不重複寫——clearing 時 Coordinator 也已落盤明確 open（不缺列），行為同構於既有的
        // IssueHandlingStatuses.Open 設計理由（缺列會被下次批次掛接自動蓋回，操作等於沒發生）。

        // clearing 路徑到此結束（既有行為：forgetNoise 只在 status=open 顯式路徑才有意義，
        // 契約見 SetIssueStatusRequest.ForgetNoise 的文件註解，這裡維持原樣不擴大解讀）
        if (clearing) return caseSync;

        // 標「已知雜訊」＝寫入記憶，之後同主機同簽章的新問題自動判讀成雜訊（治標，供無規則命中的 Other 類別）
        if (status == IssueHandlingStatuses.KnownNoise)
        {
            _noiseMarks.Save(new NoiseMark
            {
                HostName = host.HostName,
                IssueKey = issueKey,
                MarkedByAccount = _currentUser.Account,
                MarkedAt = DateTime.Now,
                Note = trimmedNote
            });
        }
        // 「調回未處理」且使用者選擇一併刪除記憶——之後同簽章不再自動判讀成雜訊
        else if (status == IssueHandlingStatuses.Open && forgetNoise)
        {
            _noiseMarks.Delete(host.HostName, issueKey);
        }

        return caseSync;
    }

    // ── 問題案件跨主機批次指派（docs/archive/FEEDBACK-4-PLAN.md §4）────────────────────

    /// <summary>
    /// 批次指派 modal 開啟時的受影響主機預覽：查詢天生受可見範圍限制（走 IRecordRepository.Query），
    /// 使用者只會看到自己有權限的主機。已有進行中案件的主機標出既有處理人，讓使用者送出前
    /// 就知道哪些會被跳過（2.1，不搶走）。
    /// </summary>
    public List<IssueCasePreviewHostDto> PreviewIssueCaseAssign(string source, int eventId, DateTime? from, DateTime? to)
    {
        var occurrences = ResolveIssueOccurrences(source, eventId, from, to);

        return occurrences.Values
            .Select(o =>
            {
                var openCase = _cases.GetOpen(o.Host.HostName, IssueSignatureKey.For(o.Issue));
                var existing = openCase?.HandlerId.HasValue == true ? _users.Get(openCase.HandlerId.Value) : null;
                return new IssueCasePreviewHostDto
                {
                    HostId = o.Host.HostId,
                    HostName = o.Host.HostName,
                    ExistingHandlerName = existing?.DisplayName,
                    ExistingHandlerAccount = existing?.Account,
                    ExistingHandlerId = existing?.UserId
                };
            })
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 跨主機批次建案：對每台勾選主機的這個問題（取該主機在篩選區間內最近一次出現的確切
    /// 問題簽章）呼叫 BuildCase——已有他人進行中案件的主機依 2.1 保留原處理人、列入略過清單，
    /// 建案本身仍走全部留存歷史回溯關聯（Q6：受影響主機的認定範圍與回溯深度是兩件事）。
    /// </summary>
    public BulkAssignIssueCaseResultDto BulkAssignIssueCase(BulkAssignIssueCaseRequest request)
    {
        var occurrences = ResolveIssueOccurrences(request.Source, request.EventId, request.From, request.To);
        var wantedIds = request.HostIds.ToHashSet();
        var targets = occurrences.Values.Where(o => wantedIds.Contains(o.Host.HostId)).ToList();
        if (targets.Count == 0)
            throw DomainException.Validation("找不到任何符合條件、且您有權限的主機。");

        // 逐台的處理人（§12 群組分攤）：Assignments 沒帶就全部給同一個 HandlerId（單人指派）。
        // 只認勾選清單內的主機——Assignments 不能拿來偷渡沒被勾選的主機
        var handlerByHostId = request.Assignments
            .Where(a => wantedIds.Contains(a.HostId))
            .GroupBy(a => a.HostId)
            .ToDictionary(g => g.Key, g => g.Last().HandlerId);

        var handlerCache = new Dictionary<long, WebUser>();
        WebUser ResolveHandler(long handlerId)
        {
            if (handlerCache.TryGetValue(handlerId, out var cached)) return cached;

            var user = _users.Get(handlerId)
                       ?? throw DomainException.NotFound("找不到指定的處理人。");
            if (!user.Active)
                throw DomainException.Validation($"{NameFormat.WithAccount(user.DisplayName, user.Account)} 的帳號已停用，無法指派。");

            handlerCache[handlerId] = user;
            return user;
        }

        // 先把每台主機的處理人解析完（含停用檢查）再開始寫：寫到一半才發現有人停用，
        // 前面幾台已經建案，使用者得到一個半套的結果
        var plan = targets
            .Select(o => (Occurrence: o, Handler: ResolveHandler(handlerByHostId.TryGetValue(o.Host.HostId, out var id) ? id : request.HandlerId)))
            .ToList();

        var reassignIds = request.ReassignHostIds.ToHashSet();
        var visibleToAssignee = new Dictionary<long, IReadOnlySet<long>>();

        var occurredAt = DateTime.Now;
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;
        var created = 0;
        var skipped = new List<BulkAssignSkippedDto>();
        var reassigned = new List<BulkAssignReassignedDto>();
        var noAccess = new List<AssigneeNoAccessDto>();

        foreach (var (o, handler) in plan)
        {
            var key = IssueSignatureKey.For(o.Issue);
            var result = _caseCoordinator.BuildCase(
                o.Host.HostName, key, HandlingTextHelpers.IssueLabel(o.Issue), o.Date,
                handler.UserId, request.Note, request.DueDate,
                actorId, _currentUser.Account, occurredAt);

            if (result.Created)
            {
                created++;
            }
            else if (result.ExistingHandlerId.HasValue)
            {
                var existingName = ResolveDisplayName(result.ExistingHandlerId.Value);

                // 已有他人的進行中案件：只有明確勾了「改派」才換人（§9），
                // 否則維持既有語意——保留原處理人並回報略過，不靜默搶走
                if (reassignIds.Contains(o.Host.HostId) && result.ExistingHandlerId.Value != handler.UserId)
                {
                    _caseCoordinator.ReassignCase(o.Host.HostName, key, handler.UserId, actorId, _currentUser.Account, occurredAt);
                    reassigned.Add(new BulkAssignReassignedDto { HostName = o.Host.HostName, PreviousHandlerName = existingName });
                }
                else
                {
                    skipped.Add(new BulkAssignSkippedDto { HostName = o.Host.HostName, ExistingHandlerName = existingName });
                    continue;   // 沒換人＝這台主機的處理人沒變，不必檢查可見性
                }
            }

            // 被指派者看不到這台主機時提示執行指派的人（§7）：指派仍然成立，
            // 對方會以案件處理人身分取得該問題的範圍限定檢視權，但只看得到這一個問題
            if (!visibleToAssignee.TryGetValue(handler.UserId, out var visible))
            {
                visible = _visibility.GetVisibleHostIdsFor(handler.UserId);
                visibleToAssignee[handler.UserId] = visible;
            }
            if (!visible.Contains(o.Host.HostId))
            {
                noAccess.Add(new AssigneeNoAccessDto
                {
                    HostName = o.Host.HostName,
                    HandlerName = NameFormat.WithAccount(handler.DisplayName, handler.Account)
                });
            }
        }

        var handlerNames = plan.Select(p => p.Handler).DistinctBy(h => h.UserId)
            .Select(h => NameFormat.WithAccount(h.DisplayName, h.Account)).ToList();

        _audit.Record(
            action: AuditActions.HandlingAssign,
            summary: $"批次指派「{request.Source} {request.EventId}」給 {NameFormat.Join(handlerNames)}：" +
                     $"建立 {created} 個案件" +
                     (reassigned.Count > 0 ? $"，改派 {reassigned.Count} 台" : "") +
                     (skipped.Count > 0 ? $"，{skipped.Count} 台已由他人案件涵蓋" : ""),
            targetKind: "issue_case",
            targetId: $"{request.Source}/{request.EventId}",
            detail: new
            {
                request.Source, request.EventId, Handlers = handlerNames,
                Assignments = plan.Select(p => new { p.Occurrence.Host.HostName, Handler = p.Handler.Account }),
                Created = created, Skipped = skipped, Reassigned = reassigned
            });

        return new BulkAssignIssueCaseResultDto
        {
            Created = created,
            Skipped = skipped,
            Reassigned = reassigned,
            AssigneeNoAccess = noAccess
        };
    }

    /// <summary>
    /// 群組指派的候選處理人（docs/archive/FEEDBACK-10-PLAN.md §12）：指定使用者群組內**啟用中**的成員，
    /// 附上各自目前的進行中案件數（供「依現有負載分攤」與畫面顯示）。
    /// 停用成員直接排除——分攤給停用帳號等於那幾台沒人處理。
    /// </summary>
    public List<HandlerCandidateDto> GetHandlerCandidates(long userGroupId)
    {
        var members = _users.GetAll()
            .Where(u => u.Active && u.GroupIds.Contains(userGroupId))
            .ToList();

        return members
            .Select(u => new HandlerCandidateDto
            {
                UserId = u.UserId,
                DisplayName = u.DisplayName,
                Account = u.Account,
                OpenCaseCount = _cases.GetOpenByHandler(u.UserId).Count
            })
            .OrderBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 跨主機一次回覆同一個問題的處理狀態（docs/archive/FEEDBACK-10-PLAN.md §11）。
    ///
    /// 對象限定「**目前使用者名下**、這個問題的進行中案件」——這是處理人替自己手上的工作
    /// 一次交代結果，不是管理者代人回覆（§8 的規則同樣適用：別人的案件不給動）。
    /// admin 要換人掛名走 §9 的改派，不從這裡繞。
    ///
    /// 逐案走既有的 <c>SyncStatus</c>，案件跨日展開、歷程、結案語意全部沿用同一套規則——
    /// 這裡只是「一次呼叫多台」，不是第二套狀態機。
    /// </summary>
    public BulkIssueStatusResultDto BulkSetIssueStatusByHandler(BulkIssueStatusRequest request)
    {
        var clearing = string.IsNullOrWhiteSpace(request.Status);
        ValidateIssueStatus(request.Status, request.DueDate, clearing);

        if (_currentUser.UserId <= 0)
            throw DomainException.Validation("此帳號沒有可回覆的案件。");

        // 可見範圍過濾照舊（案件授與也算——被交辦的人本來就看得到自己的案件）
        var visibleHostIds = _visibility.GetVisibleHostIds();
        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        var targets = _cases.GetOpenByHandler(_currentUser.UserId)
            .Where(c => hostsByName.TryGetValue(c.HostName, out var host) &&
                        (visibleHostIds.Contains(host.HostId) || _visibility.IsCaseGrantOnly(host.HostId)) &&
                        MatchesSignature(c.IssueKey, request.Source, request.EventId))
            .ToList();

        if (targets.Count == 0)
            throw DomainException.Validation("找不到指派給您、且仍在進行中的這個問題。");

        // 整批共用同一個時間戳：前端的處理歷程 timeline 靠「同操作者＋同時間戳」分組，
        // 逐案取 DateTime.Now 的微小差異會讓一次操作在畫面上散成好幾筆
        var occurredAt = DateTime.Now;
        var actorId = (long?)_currentUser.UserId;
        var updatedDays = 0;

        foreach (var openCase in targets)
        {
            var sync = _caseCoordinator.SyncStatus(
                openCase.HostName, openCase.IssueKey, openCase.IssueLabel, openCase.LastLinkedDate,
                request.Status, request.Note, request.DueDate, clearing,
                actorId, _currentUser.Account, occurredAt);

            // SyncedDayCount 不含觸發日（見 Coordinator 的說明），這裡要的是「總共動到幾天」
            updatedDays += sync.SyncedDayCount + 1;
        }

        var hostNames = targets.Select(c => c.HostName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: clearing
                ? $"跨主機清除「{request.Source} {request.EventId}」的處理標記：{targets.Count} 台主機"
                : $"跨主機將「{request.Source} {request.EventId}」標為「{HandlingTextHelpers.IssueStatusText(request.Status)}」：" +
                  $"{targets.Count} 台主機、共 {updatedDays} 天",
            targetKind: "issue_case",
            targetId: $"{request.Source}/{request.EventId}",
            detail: new { request.Source, request.EventId, request.Status, request.Note, request.DueDate, HostNames = hostNames });

        return new BulkIssueStatusResultDto
        {
            UpdatedCaseCount = targets.Count,
            UpdatedDayCount = updatedDays,
            HostNames = hostNames
        };
    }

    /// <summary>
    /// 問題簽章鍵（<c>LogName|Source|EventId|EntryType</c>）是否屬於這個 Source＋EventId。
    /// 依問題視角以 Source＋EventId 分組，同組可能含多個完整簽章（不同 LogName／EntryType），
    /// 這裡因此比對鍵的中間兩段而不是整串相等。
    /// </summary>
    private static bool MatchesSignature(string issueKey, string source, int eventId)
    {
        var parts = issueKey.Split('|');
        return parts.Length >= 3 &&
               string.Equals(parts[1], source, StringComparison.OrdinalIgnoreCase) &&
               parts[2] == eventId.ToString();
    }

    private string ResolveDisplayName(long userId)
    {
        var user = _users.Get(userId);
        return user == null ? "（已刪除）" : NameFormat.WithAccount(user.DisplayName, user.Account);
    }

    /// <summary>
    /// 給定 Source+EventId（及可選日期區間），找出每台主機「最近一次出現」這個問題的確切
    /// 問題簽章——同 Source+EventId 但不同 LogName/EntryType 時視為不同問題，取各主機最新一次
    /// 出現的那個簽章最能代表「現在的狀態」。查詢天生受可見範圍限制（IRecordRepository.Query）。
    /// </summary>
    private Dictionary<string, (WebHost Host, LogIssueSignature Issue, DateTime Date)> ResolveIssueOccurrences(
        string source, int eventId, DateTime? from, DateTime? to)
    {
        var filter = new RecordQueryFilter { Source = source, EventId = eventId, From = from, To = to };
        var records = _repository.Query(filter);
        var lookup = new HostLookup(_hosts.GetAll());

        var result = new Dictionary<string, (WebHost, LogIssueSignature, DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.OrderBy(r => r.Date))
        {
            var host = lookup.For(record);
            if (host == null) continue;

            var issue = record.TopIssues.FirstOrDefault(i => i.Source == source && i.EventId == eventId);
            if (issue == null) continue;

            result[host.HostName] = (host, issue, record.Date);   // 由舊到新覆寫，最後留下最新一次
        }
        return result;
    }

    private WebHost RequireVisibleHost(long hostId)
    {
        _visibility.EnsureVisible(hostId);
        return _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");
    }

    /// <summary>
    /// 問題層級的歷程列（docs/archive/HISTORY.md #6/D4）：與日層級的歷程分開——
    /// 問題層級沒有「處理人」（那是日層級概念），且需要 IssueKey/IssueLabel 定位是哪個問題。
    /// </summary>
    private void AppendIssueLog(WebHost host, DateTime date, string issueKey, string issueLabel, string action, string status, string? note, DateTime occurredAt)
    {
        _store.AppendLog(new RecordHandlingLog
        {
            HostName = host.HostName,
            Date = date.Date,
            Status = status,
            IssueKey = issueKey,
            IssueLabel = issueLabel,
            Note = note,
            ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ActorAccount = _currentUser.Account,
            Action = action,
            CreatedAt = occurredAt
        });
    }
}

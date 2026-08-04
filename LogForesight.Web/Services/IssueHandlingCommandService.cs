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
                return new IssueCasePreviewHostDto
                {
                    HostId = o.Host.HostId,
                    HostName = o.Host.HostName,
                    ExistingHandlerName = openCase?.HandlerId.HasValue == true
                        ? _users.Get(openCase.HandlerId.Value)?.DisplayName
                        : null
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
        var handler = _users.Get(request.HandlerId)
                      ?? throw DomainException.NotFound("找不到指定的處理人。");
        if (!handler.Active)
            throw DomainException.Validation($"{handler.DisplayName} 的帳號已停用，無法指派。");

        var occurrences = ResolveIssueOccurrences(request.Source, request.EventId, request.From, request.To);
        var wantedIds = request.HostIds.ToHashSet();
        var targets = occurrences.Values.Where(o => wantedIds.Contains(o.Host.HostId)).ToList();
        if (targets.Count == 0)
            throw DomainException.Validation("找不到任何符合條件、且您有權限的主機。");

        var occurredAt = DateTime.Now;
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;
        var created = 0;
        var skipped = new List<BulkAssignSkippedDto>();

        foreach (var o in targets)
        {
            var key = IssueSignatureKey.For(o.Issue);
            var result = _caseCoordinator.BuildCase(
                o.Host.HostName, key, HandlingTextHelpers.IssueLabel(o.Issue), o.Date,
                request.HandlerId, request.Note, request.DueDate,
                actorId, _currentUser.Account, occurredAt);

            if (result.Created)
            {
                created++;
            }
            else if (result.ExistingHandlerId.HasValue)
            {
                skipped.Add(new BulkAssignSkippedDto
                {
                    HostName = o.Host.HostName,
                    ExistingHandlerName = _users.Get(result.ExistingHandlerId.Value)?.DisplayName ?? "（已刪除）"
                });
            }
        }

        _audit.Record(
            action: AuditActions.HandlingAssign,
            summary: $"批次指派「{request.Source} {request.EventId}」給 {handler.DisplayName}：" +
                     $"建立 {created} 個案件" + (skipped.Count > 0 ? $"，{skipped.Count} 台已由他人案件涵蓋" : ""),
            targetKind: "issue_case",
            targetId: $"{request.Source}/{request.EventId}",
            detail: new
            {
                request.Source, request.EventId, HandlerId = request.HandlerId, HandlerName = handler.DisplayName,
                request.HostIds, Created = created, Skipped = skipped
            });

        return new BulkAssignIssueCaseResultDto { Created = created, Skipped = skipped };
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

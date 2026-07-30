using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 風險日的處理狀態與指派（docs/WEB-SPEC.md §9.3 處理面板）。
///
/// 兩條寫入路徑的權限刻意不同：
///   - 狀態/說明/預計完成日：<c>Handle</c> 能力，限授權範圍內的主機（user 與 admin）
///   - **指派/改派處理人：<c>Assign</c> 能力，只有 admin**
///
/// 這對應到使用情境：主機 A 的負責人是 OOO，但主管認為這個問題緊急、
/// 先交給 XXX 處理——此時**負責人不變**（那是主機的長期屬性），
/// 變的是這個風險日的處理人。
///
/// <see cref="GetTodo"/>：全站待辦（儀表板 KPI／報表處理進度共用，docs/HISTORY.md S3）：
/// 未處理與逾期。**母體（高＋中風險日）由本方法內部強制套用**——呼叫端傳整批候選紀錄即可，
/// 不必（也不應該）自己先過濾，否則「待辦只算高＋中風險日」這條規則遲早在某個新呼叫端漏掉。
/// 低風險日全站一律不進待辦（全塞進來待辦永遠爆量，等於沒有待辦）。
/// **沒有 handling 列的風險日也要算未處理**（與問題查詢清單同一套語意），
/// 只數 handling.json 既有列會漏掉所有「從未被動過」的新問題。
/// </summary>
public class HandlingService
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
    private readonly ISystemSettingsStore _settings;

    public HandlingService(
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
        ISystemSettingsStore settings)
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
        _settings = settings;
    }

    public HandlingDto Get(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);
        var handling = _store.Get(host.HostName, date);
        // 補撈 record 算推導狀態（docs/HISTORY.md #6）：處理面板要顯示的是
        // 「由問題標記推導出的日狀態」，不是存的日層級快照——兩者在批次套用後會不同步
        var record = _repository.GetOne(hostId, date);

        var dto = ToDto(host, date, handling, TryComputeProgress(host, date, record));
        dto.OpenCaseCount = OpenCaseCount(host, record);
        return dto;
    }

    /// <summary>
    /// 本日問題中屬進行中案件的數量（docs/FEEDBACK-4-PLAN.md §2）：面板「目前狀態」下方顯示
    /// 「N 項屬進行中案件」，讓使用者知道為什麼某些問題狀態會「自己動」（案件同步的結果）。
    /// record 為 null（分析尚未跑過）時回 0。
    /// </summary>
    private int OpenCaseCount(WebHost host, DailyAnalysisRecord? record)
    {
        if (record == null || record.TopIssues.Count == 0) return 0;
        var openKeys = _cases.GetOpenForHost(host.HostName).Select(c => c.IssueKey).ToHashSet(StringComparer.Ordinal);
        return record.TopIssues.Count(i => openKeys.Contains(IssueSignatureKey.For(i)));
    }

    public HandlingDto Update(long hostId, DateTime date, UpdateHandlingRequest request)
    {
        var host = RequireVisibleHost(hostId);
        RequireRecordExists(hostId, date);

        if (!HandlingStatuses.IsValid(request.Status))
            throw DomainException.Validation($"未知的處理狀態「{request.Status}」。");

        if (request.DueDate.HasValue && request.DueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");

        var existing = _store.Get(host.HostName, date) ?? NewHandling(host.HostName, date);
        var previousStatus = existing.Status;
        var previousNote = existing.Note;

        existing.Status = request.Status;
        existing.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        existing.DueDate = request.DueDate;
        existing.UpdatedAt = DateTime.Now;

        _store.Save(existing);

        // 歷程與快照必須成對寫入，否則會出現「狀態變了但歷程沒記錄」的斷點
        var action = previousStatus != request.Status ? HandlingActions.StatusChange : HandlingActions.NoteUpdate;
        AppendLog(existing, action);

        _audit.Record(
            action: previousStatus != request.Status ? AuditActions.HandlingStatus : AuditActions.HandlingNote,
            summary: previousStatus != request.Status
                ? $"將 {host.HostName} {date:yyyy-MM-dd} 的處理狀態由「{StatusText(previousStatus)}」改為「{StatusText(request.Status)}」"
                : $"更新 {host.HostName} {date:yyyy-MM-dd} 的處理說明",
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new
            {
                Before = new { Status = previousStatus, Note = previousNote },
                After = new { existing.Status, existing.Note, existing.DueDate }
            });

        return ToDto(host, date, existing);
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

        var caseSync = ApplyIssueStatus(host, date, request.IssueKey, IssueLabel(issue), request.Status, request.Note, request.DueDate, request.ForgetNoise, clearing, DateTime.Now);

        _audit.Record(
            action: AuditActions.HandlingStatus,
            summary: clearing
                ? $"清除 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】的處理標記"
                : $"將 {host.HostName} {date:yyyy-MM-dd}【{issue.Source} {issue.EventId}】標為「{IssueStatusText(request.Status)}」" +
                  (caseSync.Applied ? $"（已同步案件涵蓋的 {caseSync.SyncedDayCount} 天）" : ""),
            targetKind: "issue_handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}/{request.IssueKey}",
            detail: new { request.IssueKey, request.Status, request.Note, request.DueDate });

        // 回傳更新後的當日進度，讓前端就地更新「N/M 已處理」與日層級推導狀態
        var progress = ComputeProgress(host, date, record);

        return new IssueStatusResultDto
        {
            IssueKey = request.IssueKey,
            Status = clearing ? string.Empty : request.Status,
            StatusText = clearing ? "未處理" : IssueStatusText(request.Status),
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            DayStatus = progress.DayStatus,
            DayStatusText = StatusText(progress.DayStatus),
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
            .ToDictionary(g => g.Key, g => IssueLabel(g.First()), StringComparer.Ordinal);
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
                : $"批次將 {host.HostName} {date:yyyy-MM-dd} 的 {appliedKeys.Count} 個問題標為「{IssueStatusText(request.Status)}」" +
                  (totalSyncedDays > 0 ? $"（含案件同步共 {totalSyncedDays} 天）" : ""),
            targetKind: "issue_handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new { IssueKeys = appliedKeys, request.Status, request.Note, request.DueDate });

        var progress = ComputeProgress(host, date, record);

        return new BatchIssueStatusResultDto
        {
            UpdatedIssueKeys = appliedKeys,
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            DayStatus = progress.DayStatus,
            DayStatusText = StatusText(progress.DayStatus),
            CaseSyncedDayCount = totalSyncedDays
        };
    }

    private static void ValidateIssueStatus(string status, DateTime? dueDate, bool clearing)
    {
        if (!clearing && !IssueHandlingStatuses.IsValid(status))
            throw DomainException.Validation($"未知的問題處理狀態「{status}」。");

        if (status == IssueHandlingStatuses.InProgress && dueDate.HasValue && dueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");
    }

    /// <summary>
    /// 寫入單一問題的處理狀態，並成對寫入處理歷程（不含稽核記錄——單筆與批次的稽核摘要不同，
    /// 由呼叫端各自記）。批次呼叫端逐一呼叫本方法，天然逐問題各留一列歷程（D4）。
    ///
    /// docs/FEEDBACK-4-PLAN.md §0.4-B：先問 <see cref="IssueCaseCoordinator.SyncStatus"/> 這個
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
                AppendIssueLog(host, date, issueKey, issueLabel, HandlingActions.IssueStatus, status, trimmedNote, occurredAt);

                _issueStore.Save(new IssueHandling
                {
                    HostName = host.HostName,
                    Date = date.Date,
                    IssueKey = issueKey,
                    Status = status,
                    Note = trimmedNote,
                    // 預計完成日只在「處理中」才有意義，其餘狀態一律不存，避免舊資料殘留誤導
                    DueDate = status == IssueHandlingStatuses.InProgress ? dueDate : null,
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

    private DayHandlingDerivation.DayProgress ComputeProgress(WebHost host, DateTime date, DailyAnalysisRecord record)
    {
        var handlings = _issueStore.GetForDay(host.HostName, date);
        var dayLevel = _store.Get(host.HostName, date)?.Status;
        return DayHandlingDerivation.Derive(record.TopIssues, handlings, dayLevel, _settings.Get().ParseUnhandledSeverities());
    }

    /// <summary>Get() 專用：record 可能不存在（分析尚未跑過），沒有紀錄就沒有推導狀態可算</summary>
    private DayHandlingDerivation.DayProgress? TryComputeProgress(WebHost host, DateTime date, DailyAnalysisRecord? record) =>
        record == null ? null : ComputeProgress(host, date, record);

    public HandlingDto Assign(long hostId, DateTime date, long? handlerId)
    {
        var host = RequireVisibleHost(hostId);
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        WebUser? handler = null;
        if (handlerId.HasValue)
        {
            handler = _users.Get(handlerId.Value)
                      ?? throw DomainException.NotFound("找不到指定的處理人。");

            if (!handler.Active)
                throw DomainException.Validation($"{handler.DisplayName} 的帳號已停用，無法指派。");
        }

        var existing = _store.Get(host.HostName, date) ?? NewHandling(host.HostName, date);
        var previousHandler = existing.HandlerId.HasValue ? _users.Get(existing.HandlerId.Value) : null;

        existing.HandlerId = handlerId;
        existing.UpdatedAt = DateTime.Now;

        // 指派給人卻還是「未處理」語意上矛盾，自動推進到處理中；
        // 已結案的狀態不動（改派結案問題可能是為了補資料）
        if (handlerId.HasValue && existing.Status == HandlingStatuses.Open)
            existing.Status = HandlingStatuses.InProgress;

        _store.Save(existing);
        AppendLog(existing, HandlingActions.Assign);

        var beforeName = previousHandler?.DisplayName ?? "（未指派）";
        var afterName = handler?.DisplayName ?? "（未指派）";

        // 建案（docs/FEEDBACK-4-PLAN.md §2/Q1）：指派給人時，對當日「未處理計算」等級內、
        // 未結案、無進行中案件的每個問題建案並回溯關聯歷史——落實 2.1「同主機同問題只由
        // 一個人處理」。取消指派（handlerId=null）不建案，那不是「開始有人處理」的動作。
        var (casesCreated, skippedHandlerNames) = handlerId.HasValue
            ? BuildCasesForDay(host, date, record, handlerId.Value)
            : (0, new List<string>());

        _audit.Record(
            action: AuditActions.HandlingAssign,
            // 摘要要說清楚「負責人不變」——這正是負責人與處理人分離的意義，
            // 事後查稽核的人必須看得出這一點
            summary: $"將 {host.HostName} {date:yyyy-MM-dd} 的處理人由「{beforeName}」改為「{afterName}」" +
                     $"（主機負責人不變：{OwnerNames(host)}）" +
                     (casesCreated > 0 ? $"；建立 {casesCreated} 個問題案件並回溯關聯歷史" : ""),
            targetKind: "handling",
            targetId: $"{host.HostName}/{date:yyyy-MM-dd}",
            detail: new { Before = beforeName, After = afterName, Owners = OwnerNames(host), CasesCreated = casesCreated, CasesSkipped = skippedHandlerNames });

        var dto = ToDto(host, date, existing);
        dto.CasesCreated = casesCreated;
        dto.CasesSkippedHandlerNames = skippedHandlerNames;
        dto.OpenCaseCount = OpenCaseCount(host, record);
        return dto;
    }

    /// <summary>
    /// 建案迴圈（Q1）：只對「未處理計算」等級內、未結案的問題建案——低風險預設不處理的問題
    /// 不該自動變成有人要處理的案件，已結案的問題也不建案。已有進行中案件的問題保留原處理人
    /// （Q2），回傳略過清單（去重的處理人姓名）供呼叫端提示「已由 ○○○ 的案件涵蓋」。
    /// </summary>
    private (int Created, List<string> SkippedHandlerNames) BuildCasesForDay(
        WebHost host, DateTime date, DailyAnalysisRecord record, long handlerId)
    {
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();
        var dayIssueHandlings = _issueStore.GetForDay(host.HostName, date)
            .ToDictionary(h => h.IssueKey, StringComparer.Ordinal);
        var occurredAt = DateTime.Now;
        var actorId = _currentUser.UserId > 0 ? (long?)_currentUser.UserId : null;

        var created = 0;
        var skippedHandlerIds = new HashSet<long>();

        foreach (var issue in record.TopIssues)
        {
            if (!unhandledSeverities.Contains(issue.Severity)) continue;

            var key = IssueSignatureKey.For(issue);
            if (dayIssueHandlings.TryGetValue(key, out var existingHandling) &&
                IssueHandlingStatuses.IsClosed(existingHandling.Status))
                continue;

            var result = _caseCoordinator.BuildCase(
                host.HostName, key, IssueLabel(issue), date,
                handlerId, note: null, dueDate: null,
                actorId, _currentUser.Account, occurredAt);

            if (result.Created) created++;
            else if (result.ExistingHandlerId.HasValue) skippedHandlerIds.Add(result.ExistingHandlerId.Value);
        }

        var skippedNames = skippedHandlerIds
            .Select(id => _users.Get(id)?.DisplayName ?? "（已刪除）")
            .ToList();

        return (created, skippedNames);
    }

    public List<HandlingLogDto> GetLogs(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);

        return _store.GetLogs(host.HostName, date)
            .Select(log => new HandlingLogDto
            {
                Action = log.Action,
                ActionText = ActionText(log.Action),
                Status = log.Status,
                StatusText = StatusText(log.Status),
                HandlerName = log.HandlerId.HasValue
                    ? _users.Get(log.HandlerId.Value)?.DisplayName ?? "（已刪除）"
                    : null,
                Note = log.Note,
                IssueLabel = log.IssueLabel,
                ActorAccount = log.ActorAccount,
                CreatedAt = log.CreatedAt
            })
            .ToList();
    }

    public HandlingTodoDto GetTodo(IReadOnlyCollection<DailyAnalysisRecord> records)
    {
        // 待辦母體＝高＋中風險日，全站唯一定義（S3）：呼叫端不必也不應該自己先過濾
        var actionable = records.Where(r => RiskLevels.IsActionable(r.RiskLevel)).ToList();
        if (actionable.Count == 0) return new HandlingTodoDto();

        // 紀錄 → 現行主機名稱：handling 以現行名稱為鍵（與 RecordQueryService 同一套規則）。
        // 可見範圍不在這裡過濾——傳入的紀錄已經過 RecordRepository 的可見範圍交集
        var lookup = new HostLookup(_hosts.GetAll());
        string NameOf(DailyAnalysisRecord record) => lookup.For(record)?.HostName ?? record.Host;

        var hostNames = actionable.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = actionable.Min(r => r.Date);
        var to = actionable.Max(r => r.Date);
        var handlings = _store.GetMany(hostNames, from, to);
        var issueHandlings = _issueStore.GetMany(hostNames, from, to);
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var todo = new HandlingTodoDto { TotalCount = actionable.Count };
        foreach (var record in actionable)
        {
            var name = NameOf(record);
            var handling = handlings.FirstOrDefault(h =>
                string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                h.Date.Date == record.Date.Date);

            // 日狀態由問題層級推導（方案 B，與問題清單同一套規則）——
            // 全部問題結案的風險日不再算未處理，即使日層級從沒被人動過
            var forDay = issueHandlings
                .Where(h => string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                            h.Date.Date == record.Date.Date)
                .ToList();
            var progress = DayHandlingDerivation.Derive(record.TopIssues, forDay, handling?.Status, unhandledSeverities);

            // 對外三態（#12）：日層級 fallback 出來的狀態可能是 wont_fix/false_positive/known_noise
            // （使用者把整天標成這些狀態、當天沒有問題層級標記時），改看 ExternalOf 前這三種狀態
            // 誰都不算，會讓 OpenCount+InProgressCount+ResolvedCount 加總小於 TotalCount，
            // 「未完成」（Total－Resolved）因此把已結案的日子誤算成未完成
            switch (HandlingStatuses.ExternalOf(progress.DayStatus))
            {
                case HandlingStatuses.Open: todo.OpenCount++; break;
                case HandlingStatuses.InProgress: todo.InProgressCount++; break;
                default: todo.ResolvedCount++; break;
            }

            // 逾期兩層都看：日層級 DueDate 過期且未結案，或任一問題層級「處理中」過期
            // （與問題查詢清單的 IsOverdue 同一套語意，來源同為 DayHandlingDerivation.HasOverdueIssue）
            var dayOverdue = handling?.DueDate.HasValue == true &&
                             handling.DueDate.Value.Date < DateTime.Today &&
                             progress.IsUnresolved;
            if (dayOverdue || DayHandlingDerivation.HasOverdueIssue(forDay, DateTime.Today))
            {
                todo.OverdueCount++;
            }
        }

        return todo;
    }

    /// <summary>
    /// 風險日產生時的預設處理人：**負責人恰好一人時自動帶入，多人或無人則留空**。
    /// 多人時不猜——猜錯會讓真正該處理的人以為有別人在處理。
    /// 由查詢端於首次讀取時觸發（批次不知道 Web 的負責人設定）。
    /// </summary>
    private RecordHandling NewHandling(string hostName, DateTime date)
    {
        var handling = new RecordHandling
        {
            HostName = hostName,
            Date = date.Date,
            Status = HandlingStatuses.Open,
            UpdatedAt = DateTime.Now
        };

        var host = _hosts.FindByName(hostName);
        var defaultHandlerId = host == null ? null : DefaultHandlerId(host);

        if (defaultHandlerId.HasValue)
        {
            handling.HandlerId = defaultHandlerId;

            _audit.RecordSystem(
                action: AuditActions.HandlingAssign,
                summary: $"{hostName} {date:yyyy-MM-dd} 的處理人自動帶入唯一負責人 " +
                         $"{_users.Get(defaultHandlerId.Value)?.DisplayName}",
                targetKind: "handling",
                targetId: $"{hostName}/{date:yyyy-MM-dd}");
        }

        return handling;
    }

    /// <summary>
    /// 預設處理人：**負責人恰好一人且未停用時**才帶入。
    /// 多人時不猜——猜錯會讓真正該處理的人以為有別人在處理，那比留空更糟。
    /// </summary>
    private long? DefaultHandlerId(WebHost host)
    {
        if (host.OwnerUserIds.Count != 1) return null;

        var owner = _users.Get(host.OwnerUserIds[0]);
        return owner is { Active: true } ? owner.UserId : null;
    }

    private void AppendLog(RecordHandling handling, string action)
    {
        _store.AppendLog(new RecordHandlingLog
        {
            HostName = handling.HostName,
            Date = handling.Date,
            Status = handling.Status,
            HandlerId = handling.HandlerId,
            Note = handling.Note,
            ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ActorAccount = _currentUser.Account,
            Action = action,
            CreatedAt = DateTime.Now
        });
    }

    /// <summary>
    /// 問題層級的歷程列（docs/HISTORY.md #6/D4）：與日層級的 <see cref="AppendLog"/>
    /// 分開——問題層級沒有「處理人」（那是日層級概念），且需要 IssueKey/IssueLabel 定位是哪個問題。
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

    private WebHost RequireVisibleHost(long hostId)
    {
        _visibility.EnsureVisible(hostId);
        return _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");
    }

    /// <summary>不允許對不存在的分析紀錄建立處理狀態——那會產生指向空白的待辦事項</summary>
    private void RequireRecordExists(long hostId, DateTime date)
    {
        if (_repository.GetOne(hostId, date) == null)
            throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");
    }

    private HandlingDto ToDto(WebHost host, DateTime date, RecordHandling? handling, DayHandlingDerivation.DayProgress? progress = null)
    {
        // 尚未有任何處理紀錄時，以「唯一負責人」作為顯示上的預設處理人。
        // **這裡只計算不寫入**——讀取不該產生副作用，也不該每次瀏覽都留下一筆稽核；
        // 實際的持久化與稽核發生在第一次真正寫入時（見 NewHandling），兩邊共用 DefaultHandlerId。
        //
        // 一旦有處理紀錄，其 HandlerId 就是唯一權威（含 null）：
        // 「從未指派」與「admin 明確取消指派」都是 null 但意義相反，
        // 對已存在的紀錄再套預設值會讓「取消指派」看起來沒有生效。
        var handlerId = handling != null ? handling.HandlerId : DefaultHandlerId(host);
        var handler = handlerId.HasValue ? _users.Get(handlerId.Value) : null;

        return new HandlingDto
        {
            HostId = host.HostId,
            HostName = host.HostName,
            Date = date.ToString("yyyy-MM-dd"),
            Status = handling?.Status ?? HandlingStatuses.Open,
            StatusText = StatusText(handling?.Status ?? HandlingStatuses.Open),
            // 由問題標記推導出的日狀態（#6）：處理面板應顯示這個，不是上面存的日層級快照——
            // 指派時日層級會被自動推進成 in_progress（見下方 Assign），之後問題全結案也不會
            // 回頭改寫那個存值，只有推導值能反映「現在真正的狀態」。progress 為 null（Update/
            // Assign 呼叫端不補算）時前端 fallback 用 Status/StatusText，行為與改版前一致。
            DerivedStatus = progress?.DayStatus,
            DerivedStatusText = progress != null ? StatusText(progress.Value.DayStatus) : null,
            TotalIssues = progress?.Total ?? 0,
            ClosedIssues = progress?.Closed ?? 0,
            HandlerId = handlerId,
            HandlerName = handler?.DisplayName,
            DueDate = handling?.DueDate?.ToString("yyyy-MM-dd"),
            IsOverdue = handling?.DueDate.HasValue == true &&
                        handling.DueDate.Value.Date < DateTime.Today &&
                        HandlingStatuses.Unresolved.Contains(handling.Status),
            Note = handling?.Note,
            UpdatedAt = handling?.UpdatedAt,

            // 負責人是主機的長期屬性，處理面板上唯讀顯示——改派不會動到它
            OwnerNames = host.OwnerUserIds
                .Select(id => _users.Get(id)?.DisplayName ?? "（已刪除）")
                .ToList(),

            CanAssign = _currentUser.Has(Capability.Assign),
            CanHandle = _currentUser.Has(Capability.Handle)
        };
    }

    private string OwnerNames(WebHost host)
    {
        var names = host.OwnerUserIds
            .Select(id => _users.Get(id)?.DisplayName ?? "（已刪除）")
            .ToList();

        return names.Count == 0 ? "未指定" : string.Join("、", names);
    }

    private static string StatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        HandlingStatuses.WontFix => "不處理",
        HandlingStatuses.FalsePositive => "誤報",
        HandlingStatuses.KnownNoise => "已知雜訊",
        _ => status
    };

    /// <summary>問題層級狀態文字（未標記＝未處理由呼叫端處理）</summary>
    private static string IssueStatusText(string status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        IssueHandlingStatuses.InProgress => "處理中",
        IssueHandlingStatuses.Open => "未處理",
        _ => status
    };

    private static string ActionText(string action) => action switch
    {
        HandlingActions.Assign => "指派處理人",
        HandlingActions.AutoAssign => "自動帶入處理人",
        HandlingActions.StatusChange => "變更狀態",
        HandlingActions.NoteUpdate => "更新說明",
        HandlingActions.IssueStatus => "標記問題",
        HandlingActions.IssueStatusCleared => "清除問題標記",
        HandlingActions.CaseAssign => "建立案件",
        HandlingActions.CaseSync => "案件同步",
        HandlingActions.CaseAttach => "排程掛接案件",
        _ => action
    };

    /// <summary>問題歷程列的顯示文字（「Source EventId」），反正規化存進 RecordHandlingLog.IssueLabel</summary>
    private static string IssueLabel(LogIssueSignature issue) => $"{issue.Source} {issue.EventId}";
}

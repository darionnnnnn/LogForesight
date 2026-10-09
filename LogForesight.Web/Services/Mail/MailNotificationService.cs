using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Data;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services.Mail;

/// <summary>
/// 郵件通知的組信與寄送（回饋十五輪批次D，回饋十七輪批次A/B 全面修正日期與權限範圍）。
/// 三路觸發共用：
/// 1. 排程/手動觸發執行結束後（<see cref="NotifyAfterRunAsync"/>）——摘要 + 緊急即時判定同一個掛載點。
/// 2. 每日／每週定時彙總（<see cref="CheckAndSendDailyWeeklyAsync"/>）。
/// 3. 高風險日即時通知內建在 <see cref="NotifyAfterRunAsync"/> 裡（見其文件說明「即時」的定義）。
///
/// **通知永遠不能弄掛分析**：所有對外方法內部自行 try/catch 到底，失敗只記 NLog WARN，
/// 不對外拋例外——呼叫端（SchedulerHostedService／排程輪詢）不需要、也不應該因為一封信寄失敗
/// 而讓整個執行流程報錯。
///
/// **回饋十七輪頭號發現的根修**：分析永遠只產出到「昨天」（Core 的 MissingDateFinder／
/// AnalysisOrchestrator 固定分析 yesterday），原本 <c>NotifyAfterRunAsync</c> 卻查「今天」，
/// 導致執行摘要與高風險即時通知兩路永遠零筆不寄，每日摘要更因窗口算到今天而天天寄一封
/// 「無事」的假信。修法：**改窗口查詢＋已通知狀態去重**（<see cref="MailNotifyState.SummarySentKeys"/>
/// 與既有 <see cref="MailNotifyState.UrgentSentKeys"/>），不再依賴呼叫端傳入的特定日期。
///
/// **回饋十七輪批次B-4：收件人只看得到自己權限範圍內的主機**——統計行（全站數字，不含主機名）
/// 所有收件人都收得到；主機明細只給解析得到帳號的收件人，且只列該帳號可見範圍內的主機。
/// 對應不到帳號的收件人（自由文字 email，如共用信箱，權限無從判定）只收統計行。
///
/// **回饋十八輪批次F：問題負責人優先於主機負責人**（見 <see cref="ResolvePerRecipient"/> 內的
/// <c>RecordOwnerIds</c>）——逐主機日判定，這天的問題命中問題負責人規則就通知問題負責人、
/// 不再通知主機負責人；沒有任何問題命中規則才落回主機負責人。
/// </summary>
public class MailNotificationService
{
    private readonly StorageBackend? _prtgBackend;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>執行摘要／每日週報信，明細行數上限（回饋十六輪批次A-5）：2000 台規模下
    /// 中風險以上可能有 600+ 筆，純文字信不該列到那個長度，超出改導向站台。</summary>
    private const int SummaryBodyLineLimit = 50;

    /// <summary>高風險即時通知單封信的主機明細行數上限（回饋十六輪批次A-1）。</summary>
    private const int UrgentBodyLineLimit = 20;

    /// <summary>單一寄送迴圈內連續失敗次數上限，達到即熔斷本輪剩餘寄送（回饋十六輪批次A-3）：
    /// SMTP 整台不通時，不把「30 秒逾時 × 剩餘收件人數」全部付掉。**與
    /// <see cref="RecipientFailureThreshold"/> 是兩件不同的事**：這個是「本輪」SMTP 整體異常的
    /// 保底熔斷；那個是「跨輪」單一收件人地址本身失效的長期排除。</summary>
    private const int CircuitBreakerThreshold = 3;

    /// <summary>單一收件人跨輪連續失敗達此次數即從寄送清單排除（回饋十七輪批次B-1）：
    /// 地址打錯是永久性失敗，不會因為重試而好轉，繼續嘗試只會讓全域收件人跟著同一個
    /// 壞地址每輪重複收信。熔斷（本輪未嘗試）不計入這個計數。</summary>
    private const int RecipientFailureThreshold = 3;

    /// <summary>執行摘要／高風險即時通知的查詢窗口（回饋十七輪批次A-1／A-2）：近 N 天、不含今天
    /// （分析永遠只產出到昨天）。14＝立即執行回補天數上限，回補多天產生的達門檻主機日
    /// 天然被涵蓋在窗口內，靠 UrgentSentKeys／SummarySentKeys 去重避免重複通知。</summary>
    private const int NotifyLookbackDays = 14;

    /// <summary>NotifyAfterRunAsync 的序列化 gate（本類別是 Singleton，見其內的說明）</summary>
    private readonly SemaphoreSlim _notifyGate = new(1, 1);

    private readonly ISystemSettingsStore _settingsStore;
    private readonly ISmtpMailSender _sender;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IGroupAccessStore _groupAccess;
    private readonly IAnalysisRecordQuery _records;
    private readonly IRecordHandlingStore _handlings;
    private readonly MailNotifyStateStore _state;
    private readonly ScheduleFreshnessService _freshness;

    /// <summary>問題負責人規則（回饋十八輪批次F）：郵件路由優先於主機負責人。
    /// 可為 null——測試組裝不注入時，路由靜默落回既有的「只通知主機負責人」行為。</summary>
    private readonly IIssueOwnerStore? _issueOwners;

    /// <summary>問題負責人授權路徑用（回饋十八輪體檢輪修正）：全域收件人若是問題負責人，
    /// 摘要信裡該看得到自己負責問題的主機明細——原本 GetVisibleHostIds(userId) 只聯集了
    /// 群組授權與主機負責人，漏了 VisibilityService.GetVisibleHostIdsFor 已經有的第三條路徑，
    /// 兩邊本該同步卻各自維護一份而漂移。可為 null，同 _issueOwners 的既有慣例。</summary>
    private readonly IIssueAggregateQuery? _issueAggregates;

    /// <summary>問題優先摘要（回饋十九輪批次H1）：三路信件的主要內容來源。可為 null——同
    /// _issueOwners／_issueAggregates 的既有慣例，測試不注入時問題優先區塊靜默留空，
    /// 不影響其餘欄位（統計行／熔斷／可見範圍過濾等既有行為完全不變）。</summary>
    private readonly MailIssueDigest? _issueDigest;
    private readonly PrtgMonitoringPolicyStore? _prtgMonitoring;
    private readonly HostDayWorkflowService? _workflow;

    public MailNotificationService(
        ISystemSettingsStore settingsStore,
        ISmtpMailSender sender,
        IHostStore hosts,
        IUserStore users,
        IUserGroupStore userGroups,
        IGroupAccessStore groupAccess,
        IAnalysisRecordQuery records,
        IRecordHandlingStore handlings,
        MailNotifyStateStore state,
        ScheduleFreshnessService freshness,
        IIssueOwnerStore? issueOwners = null,
        IIssueAggregateQuery? issueAggregates = null,
        MailIssueDigest? issueDigest = null, PrtgMonitoringPolicyStore? prtgMonitoring = null, StorageBackend? prtgBackend = null,
        HostDayWorkflowService? workflow = null)
    {
        _settingsStore = settingsStore;
        _sender = sender;
        _hosts = hosts;
        _users = users;
        _userGroups = userGroups;
        _groupAccess = groupAccess;
        _records = records;
        _handlings = handlings;
        _issueAggregates = issueAggregates;
        _state = state;
        _freshness = freshness;
        _issueOwners = issueOwners;
        _issueDigest = issueDigest;
        _prtgMonitoring = prtgMonitoring;
        _prtgBackend = prtgBackend;
        _workflow = workflow;
    }

    // ── 對外三路觸發 ──────────────────────────────────────────────────────

    /// <summary>
    /// 觸發來源執行結束後呼叫（<c>SchedulerHostedService.TriggerRunAsync</c> 的收尾處，
    /// 排程與手動觸發共用同一個掛載點）。兩件事在此一起判定：
    ///
    /// 1. <see cref="SystemSettings.MailOnRunCompleted"/>：近 <see cref="NotifyLookbackDays"/> 天內
    ///    尚未摘要過（<see cref="MailNotifyState.SummarySentKeys"/> 去重）的達門檻
    ///    （依 <see cref="SystemSettings.MailMinRiskLevel"/>）紀錄彙整成一封摘要信。
    /// 2. <see cref="SystemSettings.MailUrgentEnabled"/>：同一窗口內尚未通知過的 High 風險紀錄，
    ///    每個主機日只寄一次（<see cref="MailNotifyState.UrgentSentKeys"/> 去重），不受每日/每週
    ///    時刻限制——這就是「即時」的落地方式：不是在分析管線內部插入通知呼叫（Core 分析管線
    ///    不該依賴 Web 層的 SMTP 設定），而是緊接著「這一批執行剛完成」判定，時效上等同即時，
    ///    且不需要改動任何已充分測試的 Core 分析程式碼。
    ///
    /// **不再接受 targetDate 參數**（回饋十七輪頭號發現的根修）：分析永遠只產出到昨天，
    /// 傳「今天」查詢永遠零筆。改用窗口查詢＋已通知狀態去重，見類別文件的說明。
    /// </summary>
    public async Task NotifyAfterRunAsync(CancellationToken ct = default)
    {
        try
        {
            // 序列化 gate（回饋十六輪體檢）：通知移出執行鎖（批次A-4）後，前一輪的寄信還在
            // 進行時下一輪就可能開始並完成（統計模式秒級跑完），兩個 NotifyAfterRunAsync 並發
            // 會在「第一輪還沒標記已寄」的窗口各自算出同一批 pending、整批重複寄。
            // gate 必須包住 _state.Get()（在 SendUrgentNotificationsAsync／SendRunSummaryAsync 內）
            // ——後進的一輪等前一輪標記完才讀 state，天然排除已寄過的。只 gate 這個方法，不含每日/週彙總
            // （那邊有各自的「上次寄送日」防重複，且與這裡操作的是不同狀態欄位）。
            await _notifyGate.WaitAsync(ct);
            try
            {
                // 回饋十五輪體檢批G：settings 讀取原本在 try 外——ISystemSettingsStore.Get() 若拋例外
                // （blob 讀取失敗／並發衝突，見 DB-SPEC.md 的 ConcurrencyToken 說明）會直接穿透
                // NotifyAfterRunAsync，讓呼叫端（SchedulerHostedService）的外層 catch 誤把已成功的
                // 分析執行覆寫成失敗結果——「通知永遠不能弄掛分析」這句話必須連設定讀取都算在內。
                var settings = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(_settingsStore.Get()))!;
                var settingsFingerprint = HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(settings)]);
                var mailContextFingerprint = MailContextFingerprint(settings);
                var to = DateTime.Today.AddDays(-1);
                var from = to.AddDays(-(NotifyLookbackDays - 1));
                var notificationDays = new List<NotificationWorkflowRecord>();
                Dictionary<string, HostDayWorkflowVersion>? mailVersions = _workflow == null ? null : new(StringComparer.Ordinal);
                var recoveryWaitingRecordIds = new HashSet<long>();
                if (_workflow != null)
                {
                    long afterId = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = _records.QueryNotificationWorkflowPage(from, to, afterId, 500);
                        if (page.Count == 0) break;
                        foreach (var day in page)
                        {
                            notificationDays.Add(day);
                            var version = _workflow.CaptureDeliveryVersion(day.HostId, day.Date, day.RecordId,
                                out var recoveryWaiting);
                            if (version != null) mailVersions![$"{day.HostId}|{day.Date:yyyy-MM-dd}"] = version;
                            if (recoveryWaiting) recoveryWaitingRecordIds.Add(day.RecordId);
                        }
                        var nextId = page[^1].RecordId;
                        if (nextId <= afterId) throw new InvalidDataException("Notification workflow cursor did not advance.");
                        afterId = nextId;
                    }
                }
                void ClosePlans()
                {
                    var currentSettings = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(_settingsStore.Get()))!;
                    if (HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(currentSettings)]) != settingsFingerprint ||
                        MailContextFingerprint(currentSettings) != mailContextFingerprint) return;
                    var currentRecords = currentSettings.MailEnabled && (currentSettings.MailOnRunCompleted || currentSettings.MailUrgentEnabled)
                        ? _records.Query(new RecordQueryFilter
                        {
                            Hosts = null, From = from, To = to,
                            RiskLevels = currentSettings.MailOnRunCompleted
                                ? RiskLevels.AtOrAbove(currentSettings.MailMinRiskLevel) : new[] { RiskLevels.High }
                        })
                        : new List<DailyAnalysisRecord>();
                    currentRecords = currentRecords.Where(record => CurrentWorkflowVersion(record, mailVersions)).ToList();
                    var currentContext = BuildContext();
                    var summaryRecords = currentSettings.MailEnabled && currentSettings.MailOnRunCompleted
                        ? currentRecords.Where(record => RiskLevels.Rank(record.RiskLevel) >= RiskLevels.Rank(currentSettings.MailMinRiskLevel)).ToList()
                        : new List<DailyAnalysisRecord>();
                    var urgentRecords = currentSettings.MailEnabled && currentSettings.MailUrgentEnabled
                        ? currentRecords.Where(record => QualifiesForUrgentRisk(record) &&
                            (!record.TopIssues.Any(PrtgFindingMapper.IsPrtg) || record.CanSupplementWithPrtg()) &&
                            (!record.TopIssues.Any(PrtgFindingMapper.IsPrtg) || record.Date.Date == DateTime.Today.AddDays(-1))).ToList()
                        : new List<DailyAnalysisRecord>();
                    var (_, summaryViews) = ResolvePerRecipient(currentSettings, summaryRecords, currentContext);
                    var (_, urgentViews) = ResolvePerRecipient(currentSettings, urgentRecords, currentContext, includeSuspended: true);
                    PlanWorkflowMail("summary", summaryRecords, summaryViews, mailVersions, mailContextFingerprint);
                    PlanWorkflowMail("urgent", urgentRecords, urgentViews, mailVersions, mailContextFingerprint);
                    foreach (var day in notificationDays)
                    {
                        var key = $"{day.HostId}|{day.Date:yyyy-MM-dd}";
                        if (mailVersions == null || !mailVersions.TryGetValue(key, out var version)) continue;
                        if (_workflow!.CaptureDeliveryVersion(day.HostId, day.Date, version.ParentRecordId) != version) continue;
                        var requiredLanes = new List<string>();
                        if (summaryRecords.Any(record => RecordKey(record) == key)) requiredLanes.Add("summary");
                        if (urgentRecords.Any(record => RecordKey(record) == key)) requiredLanes.Add("urgent");
                        if (!requiredLanes.Contains("summary"))
                            _workflow.ReplaceMailPlan(day.HostId, day.Date, "summary", new Dictionary<string, IReadOnlyCollection<string>>(), version);
                        if (!requiredLanes.Contains("urgent"))
                            _workflow.ReplaceMailPlan(day.HostId, day.Date, "urgent", new Dictionary<string, IReadOnlyCollection<string>>(), version);
                        _workflow.CloseMailPlan(day.HostId, day.Date, version, requiredLanes,
                            requiredLanes.Count > 0 ? "recipient-plan-evaluated" : currentSettings.MailEnabled ? "below-threshold-or-not-required" : "disabled");
                    }
                }
                if (!settings.MailEnabled || !settings.MailOnRunCompleted && !settings.MailUrgentEnabled)
                {
                    ClosePlans();
                    return;
                }
                // RiskLevels 下推（回饋十八輪批次A）：兩路門檻不同——摘要用 MailMinRiskLevel（可能低到
                // 中），緊急只要 High。取聯集下推、記憶體判定不變（見 SendRunSummaryAsync／
                // SendUrgentNotificationsAsync 內仍各自 Rank／== High 一次），高選擇度預篩大幅減少
                // DB 端反序列化的列數，語意零風險（下推只是收窄查詢範圍，不是最終判定）。
                var riskFilter = settings.MailOnRunCompleted
                    ? RiskLevels.AtOrAbove(settings.MailMinRiskLevel)
                    : new[] { RiskLevels.High };
                var records = _records.Query(new RecordQueryFilter { Hosts = null, From = from, To = to, RiskLevels = riskFilter });
                if (recoveryWaitingRecordIds.Count > 0)
                    records = records.Where(record => !recoveryWaitingRecordIds.Contains(record.RecordId)).ToList();
                var ctx = BuildContext();
                // Summary and urgent mail share one full-window target lookup. Both lanes need
                // the same broader set to suppress stale pressure aggregates for each recipient.
                IReadOnlyList<DailyAnalysisRecord> formalMailScopeRecords = settings.MailOnRunCompleted || settings.MailUrgentEnabled
                    ? ReadFormalMailScopeRecords(from, to)
                    : Array.Empty<DailyAnalysisRecord>();

                if (settings.MailOnRunCompleted)
                {
                    await SendRunSummaryAsync(settings, records, ctx, from, to, ct, mailVersions, mailContextFingerprint,
                        settingsFingerprint, formalMailScopeRecords);
                }

                if (settings.MailUrgentEnabled)
                {
                    await SendUrgentNotificationsAsync(settings, records, ctx, from, to, ct, mailVersions, mailContextFingerprint,
                        settingsFingerprint, formalMailScopeRecords);
                    await RetryOldPendingUrgentPageAsync(settings, ctx, from, to, ct, mailContextFingerprint, settingsFingerprint);
                }
                ct.ThrowIfCancellationRequested();
                ClosePlans();

            }
            finally
            {
                _notifyGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 執行結束後的通知處理失敗（不影響分析結果本身）");
        }
    }

    /// <summary>
    /// 每日／每週定時彙總——由 <c>SchedulerHostedService</c> 的既有 60 秒輪詢一併呼叫。
    /// 用「上次寄送日」防同一天重複觸發（輪詢每分鐘跑一次，時刻比對命中的那一分鐘內可能被
    /// 呼叫多次）。時刻比對用「現在 &gt;= 設定時刻」而非精準相等——輪詢間隔與伺服器負載都可能
    /// 讓精準比對錯過那一分鐘，>= 加上「今天還沒寄過」的狀態防重複，兩者合起來才是可靠的每日觸發。
    /// </summary>
    public async Task RetryPendingUrgentAsync(CancellationToken ct = default)
    {
        if (!_state.Get().UrgentOutbox.Values.Any(i =>
                i.Status is not ("smtp-accepted" or "cancelled-formal-revoked" or "no-qualified-recipient"))) return;
        await NotifyAfterRunAsync(ct);
    }

    private async Task RetryOldPendingUrgentPageAsync(SystemSettings settings, MailContext context,
        DateTime currentFrom, DateTime currentTo, CancellationToken ct, string? mailContextFingerprint,
        string settingsFingerprint)
    {
        if (_prtgBackend == null || !settings.MailEnabled || !settings.MailUrgentEnabled) return;
        const int pageSize = 100;
        var state = _state.Get();
        var eligible = state.UrgentOutbox.Values.Where(intent => intent.RecordDate.Date < currentFrom.Date &&
                intent.Status is not ("smtp-accepted" or "cancelled-formal-revoked" or "no-qualified-recipient"))
            .OrderBy(intent => intent.Key, StringComparer.Ordinal).ToArray();
        var cursorStore = new MailUrgentRetryCursorStore(_prtgBackend.Blob(MailUrgentRetryCursorStore.BlobKey));
        var cursor = cursorStore.Get().LastIntentKey;
        var page = eligible.Where(intent => string.IsNullOrEmpty(cursor) ||
                StringComparer.Ordinal.Compare(intent.Key, cursor) > 0)
            .Take(pageSize).ToArray();
        if (page.Length == 0)
        {
            cursor = null;
            page = eligible.Take(pageSize).ToArray();
        }
        if (page.Length == 0)
        {
            if (cursorStore.Get().LastIntentKey != null)
                cursorStore.Update(value => { value.LastIntentKey = null; value.UpdatedAtUtc = DateTime.UtcNow; });
            return;
        }
        cursorStore.Update(value =>
        {
            value.LastIntentKey = page[^1].Key;
            value.UpdatedAtUtc = DateTime.UtcNow;
        });

        var records = new List<DailyAnalysisRecord>();
        var intentKeysByRecordId = new Dictionary<long, string>();
        Dictionary<string, HostDayWorkflowVersion>? versions = _workflow == null
            ? null : new Dictionary<string, HostDayWorkflowVersion>(StringComparer.Ordinal);
        foreach (var intent in page)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = intent.ParentRecordId > 0 ? intent.ParentRecordId : ParentRecordIdFromIntentKey(intent.Key);
            if (parentId <= 0)
            {
                MarkUrgentIntentWaiting(intent.Key, "parent-record-id-unavailable");
                continue;
            }

            ExactAnalysisRecordLookup lookup;
            try
            {
                lookup = _records.LookupByRecordId(parentId);
            }
            catch (NotSupportedException)
            {
                MarkUrgentIntentWaiting(intent.Key, "bounded-parent-lookup-unavailable");
                continue;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[Mail] urgent intent {0}: exact parent lookup failed; intent remains queued.", intent.Key);
                MarkUrgentIntentWaiting(intent.Key, "parent-record-read-failed");
                continue;
            }

            var reason = !lookup.Exists ? "parent-record-missing" :
                lookup.PayloadTooLarge ? "parent-record-over-128kib" :
                lookup.DetailPruned ? "parent-record-details-pruned" :
                lookup.Malformed ? "parent-record-malformed" :
                lookup.IdentityMismatch ? "parent-record-identity-mismatch" :
                lookup.Record is null ? "parent-record-unavailable" :
                lookup.HostId != intent.HostId || lookup.Date.Date != intent.RecordDate.Date ? "parent-record-identity-mismatch" :
                intent.ParentRecordId > 0 && intent.ParentRecordId != lookup.RecordId ? "parent-record-id-mismatch" :
                ParentRecordIdFromIntentKey(intent.Key) is var keyedParent && keyedParent > 0 && keyedParent != lookup.RecordId
                    ? "parent-record-key-mismatch" :
                intent.SettingsRevision != settings.Revision ? "settings-revision-changed" :
                lookup.Record.RiskLevel != RiskLevels.High ? "parent-no-longer-high-risk" :
                !lookup.Record.CanSupplementWithPrtg() ? "parent-no-longer-supplementable" :
                !lookup.Record.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)
                    ? "parent-formal-finding-missing" :
                intent.ProblemKeys.Any(key => !lookup.Record.TopIssues.Any(issue => issue.EventKey == key))
                    ? "parent-findings-changed" : null;
            if (reason != null)
            {
                MarkUrgentIntentWaiting(intent.Key, reason);
                continue;
            }

            var record = lookup.Record!;
            if (_workflow != null)
            {
                var version = _workflow.CaptureDeliveryVersion(record.HostId, record.Date, record.RecordId, out var recoveryWaiting);
                if (recoveryWaiting || version == null || version.ParentRecordId != record.RecordId)
                {
                    MarkUrgentIntentWaiting(intent.Key, "parent-workflow-not-current");
                    continue;
                }
                versions![RecordKey(record)] = version;
            }
            records.Add(record);
            intentKeysByRecordId[record.RecordId] = intent.Key;
        }

        if (records.Count == 0) return;
        ClearUrgentIntentWaiting(intentKeysByRecordId);
        await SendUrgentNotificationsAsync(settings, records, context, currentFrom, currentTo, ct, versions,
            mailContextFingerprint, settingsFingerprint, records, intentKeysByRecordId);
    }

    private static long ParentRecordIdFromIntentKey(string key)
    {
        const string marker = "|parent:";
        var start = key.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return 0;
        start += marker.Length;
        var end = key.IndexOf('|', start);
        var value = end < 0 ? key[start..] : key[start..end];
        return long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var recordId) ? recordId : 0;
    }

    private void MarkUrgentIntentWaiting(string key, string reason)
    {
        _state.Update(state =>
        {
            if (!state.UrgentOutbox.TryGetValue(key, out var intent)) return;
            if (intent.WaitingReasonCode != reason) intent.WaitingSinceUtc = DateTime.UtcNow;
            intent.WaitingReasonCode = reason;
            intent.UpdatedAtUtc = DateTime.UtcNow;
        });
    }

    private void ClearUrgentIntentWaiting(IReadOnlyDictionary<long, string> intentKeysByRecordId)
    {
        var parentByKey = intentKeysByRecordId.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        _state.Update(state =>
        {
            foreach (var (key, parentId) in parentByKey)
                if (state.UrgentOutbox.TryGetValue(key, out var intent))
                {
                    if (intent.ParentRecordId == 0) intent.ParentRecordId = parentId;
                    if (intent.CreatedAtUtc == default)
                        intent.CreatedAtUtc = intent.UpdatedAtUtc == default ? DateTime.UtcNow : intent.UpdatedAtUtc;
                    intent.WaitingReasonCode = null;
                    intent.WaitingSinceUtc = null;
                }
        });
    }

    public async Task CheckAndSendDailyWeeklyAsync(DateTime now, CancellationToken ct = default)
    {
        try
        {
            // 回饋十五輪體檢批G：settings 讀取原本在 try 外，未受保護——這支方法緊接著在
            // SchedulerHostedService.TickAsync 的排程窗口判斷之前呼叫且沒有自己的 try/catch
            // （文件註解宣稱「內部自行 try/catch 到底...不需要額外保護」），一旦 Get() 拋例外，
            // 整個 TickAsync 連同下方排程窗口判斷都會被跳過，等於通知路徑間接卡住了排程觸發。
            await RetryPendingUrgentAsync(ct);
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled) return;

            var today = now.ToString("yyyy-MM-dd");
            var to = now.Date.AddDays(-1);
            var dailyFrom = to;
            var weeklyFrom = to.AddDays(-6);
            var dailyDue = settings.MailDailyEnabled && TimeSpan.TryParse(settings.MailDailyTime, out var dailyTime) &&
                now.TimeOfDay >= dailyTime && _state.Get().LastDailySentDate != today;
            var weeklyDue = settings.MailWeeklyEnabled &&
                Enum.TryParse<DayOfWeek>(settings.MailWeeklyDayOfWeek, ignoreCase: true, out var weeklyDay) &&
                now.DayOfWeek == weeklyDay && TimeSpan.TryParse(settings.MailWeeklyTime, out var weeklyTime) &&
                now.TimeOfDay >= weeklyTime && _state.Get().LastWeeklySentDate != today;
            IReadOnlyList<DailyAnalysisRecord> formalScopeRecords = dailyDue || weeklyDue
                ? ReadFormalMailScopeRecords(weeklyDue ? weeklyFrom : dailyFrom, to)
                : Array.Empty<DailyAnalysisRecord>();

            if (dailyDue)
            {
                await SendDigestAsync(settings, now, windowDays: 1, isWeekly: false, ct,
                    formalScopeRecords.Where(record => record.Date.Date >= dailyFrom && record.Date.Date <= to).ToArray());
                _state.Update(s => s.LastDailySentDate = today);
            }

            if (weeklyDue)
            {
                await SendDigestAsync(settings, now, windowDays: 7, isWeekly: true, ct,
                    formalScopeRecords.Where(record => record.Date.Date >= weeklyFrom && record.Date.Date <= to).ToArray());
                _state.Update(s => s.LastWeeklySentDate = today);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 每日／每週定時彙總處理失敗");
        }
    }

    /// <summary>
    /// 問題上報通知（回饋十八輪批次G，第四路觸發——事件驅動即時單發）：處理狀態被標成
    /// 「無法處理」（<see cref="IssueHandlingStatuses.Escalated"/>）時，寄信給全部 admin 群組成員，
    /// 請他們決定結案或重新指派。與三路批次觸發不同：不去重、不落地 SentKeys（重複上報就
    /// 重複通知是正確行為——每次上報都是一次明確的人為求助）、不走收件人可見範圍過濾
    /// （admin 本來就看得到全站）。收件人由 <see cref="AdminMembersResolver"/> 即時解析
    /// （Role==Admin 判定，群組改名不受影響）。操作者身分由呼叫端傳入——本類別是 Singleton，
    /// 不能注入 Scoped 的 ICurrentUser（既有慣例，見 GetVisibleHostIds 的說明）。
    /// 內部 try/catch 到底：通知永遠不能弄掛狀態變更本身。
    /// </summary>
    public async Task NotifyEscalationAsync(EscalationNotice notice, CancellationToken ct = default)
    {
        try
        {
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled) return;

            var recipients = AdminMembersResolver.GetAdminMembers(_userGroups, _users)
                .Select(u => u.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                Log.Warn("[Mail] 問題上報通知略過：admin 群組沒有任何成員設定了 email（問題：{Issue}）。", notice.IssueLabel);
                return;
            }

            var dateText = DateTime.Today.ToString("yyyy-MM-dd");
            var statsLine = $"負責人回覆無法處理：{notice.IssueLabel}";
            var subject = ExpandTemplate(settings.MailSubjectTemplate, notice.HostLabel, dateText, "-", "問題上報", statsLine);

            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
            body.AppendLine($"問題「{notice.IssueLabel}」（{notice.HostLabel}）被回覆為「無法處理」，請決定結案或重新指派。");
            body.AppendLine();
            body.AppendLine($"  回覆人：{notice.ActorAccount}");
            if (!string.IsNullOrWhiteSpace(notice.Reason))
            {
                body.AppendLine($"  原因：{notice.Reason}");
            }
            body.AppendLine();
            body.AppendLine("請至站台的問題查詢頁（依問題視角）檢視並處理。");

            await SendSafeAsync(settings, recipients, subject, body.ToString(), ct);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 問題上報通知處理失敗（不影響狀態變更本身）");
        }
    }

    /// <summary>
    /// 交辦單通知：建立／改派／取消時通知處理人。
    /// 內部 try/catch 到底：通知永遠不能弄掛交辦單操作本身。
    /// </summary>
    public async Task NotifyWorkOrderAsync(WorkOrderNotice notice, CancellationToken ct = default)
    {
        try
        {
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled || !settings.MailNotifyWorkOrders) return;

            if (string.IsNullOrWhiteSpace(notice.RecipientEmail))
            {
                Log.Warn("[Mail] 交辦單通知略過：處理人 {Account} 沒有設定 email（單號 {Id}）。", notice.RecipientAccount, notice.WorkOrderId);
                return;
            }

            var email = notice.RecipientEmail.Trim();
            var dateText = DateTime.Today.ToString("yyyy-MM-dd");

            var (type, summary) = notice.Kind switch
            {
                WorkOrderNoticeKinds.Transferred => ("交辦移交", $"{notice.IssueLabel} 已移交"),
                WorkOrderNoticeKinds.Cancelled => ("交辦取消", $"{notice.IssueLabel} 已取消"),
                _ => ("交辦", $"{notice.IssueLabel}（{notice.HostCount} 台）"),
            };
            var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, "-", type, summary);

            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();

            var firstLine = notice.Kind switch
            {
                WorkOrderNoticeKinds.Transferred => $"交辦單 {notice.WorkOrderId}「{notice.IssueLabel}」已由 {notice.ActorAccount} 移交給其他處理人，你不需再處理這張單中移出的主機。",
                WorkOrderNoticeKinds.Cancelled => $"交辦單 {notice.WorkOrderId}「{notice.IssueLabel}」已由 {notice.ActorAccount} 取消，其中的主機已調回未處理。",
                _ => $"{notice.ActorAccount} 交辦了一張單給你：{notice.IssueLabel}",
            };
            body.AppendLine(firstLine);
            body.AppendLine();
            body.AppendLine($"  單號：{notice.WorkOrderId}");
            if (!string.IsNullOrWhiteSpace(notice.PlainExplanation))
            {
                body.AppendLine($"  說明：{notice.PlainExplanation}");
            }
            body.AppendLine($"  主機數：{notice.HostCount}");
            if (notice.HostNames is { Count: > 0 })
            {
                var hostList = string.Join("、", notice.HostNames.Take(20));
                // 以總台數判定：呼叫端可能只傳部分主機名，名單比總數少就要註明總數
                if (notice.HostCount > Math.Min(notice.HostNames.Count, 20))
                {
                    body.AppendLine($"  主機：{hostList}…等 {notice.HostCount} 台");
                }
                else
                {
                    body.AppendLine($"  主機：{hostList}");
                }
            }
            if (!string.IsNullOrWhiteSpace(notice.Note))
            {
                body.AppendLine($"  交辦說明：{notice.Note}");
            }
            if (notice.DueDate.HasValue)
            {
                body.AppendLine($"  期限：{notice.DueDate.Value:yyyy-MM-dd}");
            }
            if (!string.IsNullOrWhiteSpace(notice.Reason))
            {
                body.AppendLine($"  原因：{notice.Reason}");
            }
            body.AppendLine();
            body.AppendLine($"請至站台的「交辦單」頁檢視單號 {notice.WorkOrderId}。");

            await SendSafeAsync(settings, new List<string> { email }, subject, body.ToString(), ct);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 交辦單通知處理失敗（不影響交辦單本身）");
        }
    }

    /// <summary>
    /// 夜間交辦摘要信（回饋第 47 輪 E-1）：每位處理人一封，列出昨夜為其新建或續掛的交辦單。
    /// 內部 try/catch 到底：通知永遠不能弄掛分析流程。
    /// </summary>
    public async Task NotifyWorkOrderDigestAsync(NightlyDispatchSummary summary, CancellationToken ct = default)
    {
        try
        {
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled || !settings.MailNotifyWorkOrders || summary.PerHandler.Count == 0) return;

            var users = _users.GetAll().ToDictionary(u => u.UserId);
            var dateText = DateTime.Today.ToString("yyyy-MM-dd");

            foreach (var (userId, orders) in summary.PerHandler.OrderBy(kv => kv.Key))
            {
                if (!users.TryGetValue(userId, out var user) || !user.Active)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(user.Email))
                {
                    Log.Warn("[Mail] 交辦摘要略過：處理人 {Account} 沒有設定 email。", user.Account);
                    continue;
                }

                var email = user.Email.Trim();
                var createdCount = orders.Count(o => o.CreatedThisRun);
                var addedCount = orders.Sum(o => o.AddedMembers);
                var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, "-", "交辦摘要", $"新交辦 {createdCount} 張、新增 {addedCount} 台");

                var body = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();

                body.AppendLine("昨夜的分析替你派了以下交辦單：");
                body.AppendLine();
                foreach (var order in orders.OrderBy(o => o.WorkOrderId))
                {
                    var line = order.CreatedThisRun
                        ? $"  單號 {order.WorkOrderId}：{order.IssueLabel}（新建，{order.AddedMembers} 台）"
                        : $"  單號 {order.WorkOrderId}：{order.IssueLabel}（新增 {order.AddedMembers} 台）";
                    if (order.RecurrenceMembers > 0)
                    {
                        line += $"，其中 {order.RecurrenceMembers} 台是復發：你最近 30 天內修好過的主機又出現同一個問題";
                    }
                    body.AppendLine(line);
                }
                body.AppendLine();
                body.AppendLine("請至站台的「交辦單」頁檢視。");

                await SendSafeAsync(settings, new List<string> { email }, subject, body.ToString(), ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 交辦摘要通知處理失敗（不影響分析結果）");
        }
    }

    /// <summary>測試寄信（設定頁「測試寄信」鈕）：用表單目前值（可能還沒儲存），不落地任何狀態。</summary>
    public async Task SendTestAsync(SmtpConnectionSpec connection, string from, List<string> recipients,
        string subjectTemplate, string bodyIntro, CancellationToken ct = default)
    {
        var subject = ExpandTemplate(subjectTemplate, "測試", DateTime.Today.ToString("yyyy-MM-dd"), "-", "測試", "這是一封測試郵件");
        var bodyBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bodyIntro)) bodyBuilder.AppendLine(bodyIntro).AppendLine();
        bodyBuilder.AppendLine("這是 LogForesight 的測試郵件，收到即代表 SMTP 設定正確可用。");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        await _sender.SendAsync(connection, new MailMessageSpec(from, recipients, subject, bodyBuilder.ToString()), timeoutCts.Token);
    }

    /// <summary>設定頁儲存郵件設定時呼叫（回饋十七輪批次B-1）：收件人地址若已改正，
    /// 舊的連續失敗計數不該繼續卡著——整份清空，讓改正後的地址從零開始重新累計。</summary>
    public void ResetRecipientFailureStreaks() => _state.Update(s => s.RecipientFailureStreaks.Clear());

    /// <summary>
    /// 郵件通知由關轉開時呼叫（回饋十八輪批次C，設定頁儲存郵件設定的掛載點）：把
    /// <see cref="NotifyLookbackDays"/> 窗口內既有的分析紀錄一律標成已通知——語意是
    /// 「從啟用（含重新啟用）起算」，啟用前累積的歷史紀錄不補寄，第一封信只涵蓋啟用後新產出的。
    ///
    /// 呼叫端必須自行判定「由關轉開」才呼叫（見 SystemSettingsService.Update）：這個方法本身
    /// 不做判斷、無條件執行——若每次儲存設定都呼叫，會把當下真正 pending 的達門檻紀錄也標成
    /// 已通知，變成真的漏寄。<paramref name="summary"/>／<paramref name="urgent"/> 對應
    /// SummarySentKeys／UrgentSentKeys，分別由「執行摘要」「高風險即時通知」各自由關轉開的
    /// 那一路觸發，互不影響——只開一邊時不動另一邊的狀態。
    ///
    /// 兩個集合都填「窗口內全部紀錄」的 key，不只是達門檻的那些：這樣之後調整
    /// <see cref="SystemSettings.MailMinRiskLevel"/> 也不會讓「啟用前、當時未達門檻」的紀錄
    /// 突然變成 pending 積壓——集合本來就只是「已經看過、不用再通知」的標記，門檻高低與它無關。
    /// </summary>
    public void MarkExistingRecordsAsNotified(bool summary, bool urgent)
    {
        if (!summary && !urgent) return;

        // try/catch 到底（回饋十八輪終檢輪補）：這是設定儲存流程的附掛動作，預填失敗
        // （查詢逾時等）最壞就是啟用前的歷史紀錄被補寄一輪，不能反過來弄掛設定儲存本身。
        try
        {
            var to = DateTime.Today.AddDays(-1);
            var from = to.AddDays(-(NotifyLookbackDays - 1));
            var keys = _records.ListHostDates(from, to)
                .Select(h => $"{h.HostId}|{h.Date:yyyy-MM-dd}")
                .ToList();
            if (keys.Count == 0) return;

            _state.Update(s =>
            {
                if (summary) foreach (var key in keys) s.SummarySentKeys.Add(key);
                if (urgent)
                {
                    foreach (var key in keys) s.UrgentSentKeys.Add(key);
                    foreach (var record in _records.Query(new RecordQueryFilter { From = from, To = to }))
                    {
                        s.UrgentSentKeys.Add(UrgentRecordKey(record));
                        foreach (var fact in PrtgUrgentFacts(record))
                        { s.PrtgUrgentAcceptedFacts[fact.Key] = fact.Value; s.PrtgUrgentFactSeenAtUtc[fact.Key] = DateTime.UtcNow; }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 郵件啟用預填已通知狀態失敗（不影響設定儲存本身；啟用前的歷史紀錄可能被補寄）");
        }
    }

    /// <summary>目前已因連續失敗達門檻而被排除寄送的收件人（回饋十七輪批次B-1）：
    /// 供設定頁與 /api/health/detail 顯示，讓管理者看得到「為什麼這個人一直沒收到信」。</summary>
    public List<string> GetSuspendedRecipients() =>
        _state.Get().RecipientFailureStreaks
            .Where(kv => kv.Value >= RecipientFailureThreshold)
            .Select(kv => kv.Key)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── 內部：批次上下文（回饋十七輪批次B-2，N+1 修正） ─────────────────────

    /// <summary>一次通知批次共用的主機／使用者字典：避免逐筆紀錄各自呼叫 Store.Get()——
    /// 每次呼叫都整份 blob 反序列化（見 JsonBlobCollection.Read），高風險 pending 動輒數百筆時
    /// 這是明顯的 N+1（回饋十六輪體檢發現3／回饋十七輪體檢低3）。一次批次只建一次。
    /// <paramref name="IssueOwnersByKey"/>（回饋十八輪批次F）：同一個理由——問題負責人比對
    /// 若逐 record、逐 issue 各自呼叫 _issueOwners.GetAll()，會是比主機字典更嚴重的 N+1
    /// （2000 台規模下每台每天可能有數個問題）。key 為 (SourceUpper, EventId)。</summary>
    private sealed record MailContext(
        Dictionary<long, WebHost> HostsById, List<WebUser> AllUsers,
        Dictionary<(string SourceUpper, int EventId), List<long>> IssueOwnersByKey)
    {
        public WebHost? Host(long hostId) => HostsById.GetValueOrDefault(hostId);
    }

    private MailContext BuildContext() => new(
        _hosts.GetAll().ToDictionary(h => h.HostId),
        _users.GetAll(),
        IssueProfile.IndexByKey(_issueOwners?.GetAll() ?? new List<IssueProfile>()));

    // ── 內部：收件人解析與可見範圍 ────────────────────────────────────────

    /// <summary>
    /// email → 對應的 Active 使用者帳號（回饋十七輪批次B-4）：解析不到的收件人（共用信箱等
    /// 自由文字地址）回 null，呼叫端據此只寄統計行，不寄任何主機明細。停用帳號視為未對應
    /// （與 VisibilityService 一致：停用優先於一切授權路徑）。email 理應唯一，對到多個帳號
    /// 屬設定錯誤，取第一個並記 WARN。
    /// </summary>
    private static WebUser? ResolveAccount(string email, List<WebUser> allUsers)
    {
        var matches = allUsers.Where(u => u.Active && string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            Log.Warn("[Mail] 收件人 {Email} 對應到 {Count} 個啟用中的帳號，取第一個。", email, matches.Count);
        return matches.FirstOrDefault();
    }

    /// <summary>指定使用者可見的主機 ID（回饋十七輪批次B-4）：與
    /// LogForesight.Web.Services.VisibilityService.GetVisibleHostIdsFor 邏輯對稱但獨立實作——
    /// MailNotificationService 是 Singleton，VisibilityService 依賴 Scoped 的 ICurrentUser
    /// 無法被 Singleton 直接消費，見 <see cref="HostVisibilityResolver"/>。</summary>
    private IReadOnlySet<long> GetVisibleHostIds(long userId, int retentionDays) =>
        HostVisibilityResolver.GetVisibleHostIds(_hosts, _users, _userGroups, _groupAccess, userId,
            _issueOwners, _issueAggregates, retentionDays);

    private SmtpConnectionSpec ResolveConnection(SystemSettings settings) => new(
        settings.SmtpServer, settings.SmtpPort, settings.SmtpUseTls, settings.SmtpAccount,
        // TryDecrypt：非密文原樣回傳；解不開（金鑰不符）當成未設定，不讓整批每日／每週寄信失敗
        string.IsNullOrEmpty(settings.SmtpPasswordEnc) ? null
            : CryptoHelper.TryDecrypt(settings.SmtpPasswordEnc, out var smtpPassword) ? smtpPassword
            : null);

    private static string ExpandTemplate(string template, string host, string date, string risk, string type, string summary) =>
        template
            .Replace("{site}", WebBrandName)
            .Replace("{host}", host)
            .Replace("{date}", date)
            .Replace("{risk}", risk)
            .Replace("{type}", type)
            .Replace("{summary}", summary);

    /// <summary>品牌名稱走系統設定（外觀分頁），不是這個服務的職責範圍——郵件主旨的 {site} 直接
    /// 用固定產品名，品牌自訂的套用範圍留在畫面顯示層，避免這裡多依賴一份設定讀取</summary>
    private const string WebBrandName = "LogForesight";

    // ── 內部：收件人分組寄送共用骨架（回饋十七輪批次B） ─────────────────────

    /// <summary>
    /// 一位收件人要收到的內容：<see cref="Detail"/> 為 null＝該收件人（通常是對應不到帳號的
    /// 共用信箱）只收統計行，不含任何主機明細（回饋十七輪批次B-4 決策：權限無從判定時只給
    /// 全站數字）；否則為該收件人可見範圍內的紀錄清單（可能是空清單——代表全域收件人但看不到
    /// 任何一筆本輪內容，仍會收到純統計信）。
    ///
    /// <see cref="VisibleHostIds"/>（回饋十九輪批次H2）：該收件人的完整可見主機範圍，供
    /// <see cref="MailIssueDigest"/> 查問題優先清單用——與 <see cref="Detail"/> 是兩件不同的事：
    /// Detail 是「本輪窗口內、達門檻的主機日」子集（用於既有的 coverage／SentKeys 去重機制，
    /// 不能動），VisibleHostIds 是「這個人整體看得到哪些主機」，問題優先清單要看的是後者——
    /// 一個問題即使沒有主機日觸發本輪通知門檻，只要在可見範圍內持續逾期或擴散，收件人仍該
    /// 在信裡看到它。null＝這位收件人無法判定可見範圍（同 Detail 為 null 的情境），
    /// 問題優先區塊比照統計行以外的明細一樣整段留空。
    /// </summary>
    private sealed record RecipientView(List<DailyAnalysisRecord>? Detail, IReadOnlySet<long>? VisibleHostIds,
        List<DailyAnalysisRecord>? ApprovedRecords = null, HashSet<string>? SuppressedPressurePairs = null);

    /// <summary>
    /// 依可見範圍把「本次涵蓋的全部紀錄」拆給每位收件人（回饋十七輪批次B-4）：
    /// 全域收件人與（選填）負責人套用同一套規則——對應到帳號的人只看見自己可見範圍內的
    /// 主機明細，對應不到帳號的人只收統計行。負責人一律限縮到自己負責的主機（本來就是可見範圍
    /// 的子集，見 HostVisibilityResolver.GetOwnedHostIds）。
    ///
    /// **問題負責人優先於主機負責人**（回饋十八輪批次F，見 <see cref="RecordOwnerIds"/>）：
    /// 逐 record 判定——這筆主機日的問題中只要有任一個命中問題負責人規則，通知對象就是
    /// 命中規則的問題負責人聯集，**不再**通知主機負責人；record 內所有問題都未命中規則的
    /// 才落回主機負責人。這是 record 粒度的優先取代，不是全站開關，同一批通知裡不同主機日
    /// 可能各自路由到不同對象。
    ///
    /// 保序：全域收件人（依設定清單順序）在前、負責人（依 records 出現順序）在後——熔斷依這個
    /// 順序判斷「連續」失敗，順序必須是明確保證的行為而非依賴 Dictionary 迭代順序這種實作細節。
    /// **永久失效收件人排除**（回饋十七輪批次B-1）：連續失敗達門檻的收件人不進清單，不嘗試寄送。
    /// </summary>
    private (List<string> Order, Dictionary<string, RecipientView> Views) ResolvePerRecipient(
        SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx, bool includeSuspended = false)
    {
        var suspended = includeSuspended ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(GetSuspendedRecipients(), StringComparer.OrdinalIgnoreCase);

        var globalRecipients = settings.MailRecipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Where(r => !suspended.Contains(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var globalSet = new HashSet<string>(globalRecipients, StringComparer.OrdinalIgnoreCase);

        var order = new List<string>();
        var views = new Dictionary<string, RecipientView>(StringComparer.OrdinalIgnoreCase);

        foreach (var email in globalRecipients)
        {
            var account = ResolveAccount(email, ctx.AllUsers);
            List<DailyAnalysisRecord>? detail = null;
            IReadOnlySet<long>? visible = null;
            if (account != null)
            {
                // 體檢輪抓到：GetVisibleHostIds 若直接寫在 Where 的 predicate 裡，
                // 每筆 record 都會重新呼叫一次（LINQ 對每個來源元素求值一次 predicate），
                // 對每位收件人重跑一次完整的 store 全表掃描，正是 B-2 想修掉的同一種 N+1。
                visible = GetVisibleHostIds(account.UserId, settings.RetentionDays);
                detail = records.Where(r => visible.Contains(r.HostId)).ToList();
            }
            views[email] = new RecipientView(detail, visible);
            order.Add(email);
        }

        if (settings.MailNotifyHostOwners)
        {
            foreach (var record in records)
            {
                var host = ctx.Host(record.HostId);
                if (host == null) continue;

                foreach (var ownerId in RecordOwnerIds(record, host, ctx))
                {
                    var email = ctx.AllUsers.FirstOrDefault(u => u.UserId == ownerId)?.Email;
                    if (string.IsNullOrWhiteSpace(email) || globalSet.Contains(email) || suspended.Contains(email)) continue;

                    if (!views.TryGetValue(email, out var existing))
                    {
                        // 問題優先區塊要看負責人的**整體**可見範圍，不是只有這一筆觸發的主機——
                        // 一次批次觸發只補一位負責人一筆 record，但這個人可能同時負責好幾台
                        // 正在逾期／擴散的主機，那些理應一併出現在信裡（同全域收件人的既有原則）
                        var ownerVisible = GetVisibleHostIds(ownerId, settings.RetentionDays);
                        views[email] = new RecipientView(new List<DailyAnalysisRecord> { record }, ownerVisible);
                        order.Add(email);
                    }
                    else if (existing.Detail != null && !existing.Detail.Contains(record))
                    {
                        existing.Detail.Add(record);
                    }
                    // existing.Detail == null 不會發生：負責人路徑一律先建立含明細的 view
                }
            }
        }

        return (order, views);
    }

    /// <summary>
    /// 這筆主機日通知路由的收件人 ID（回饋十八輪批次F）：問題負責人優先於主機負責人。
    /// 逐一比對 <paramref name="record"/>.TopIssues 的 (Source,EventId) 是否命中
    /// <see cref="MailContext.IssueOwnersByKey"/>，命中的規則各自的負責人聯集去重；
    /// 有任何命中就回傳這個聯集（不落回主機負責人——「優先取代」不是「疊加」）；
    /// 全都沒命中才回傳 host.OwnerUserIds（既有行為）。
    /// 被抑制（含靜音）的問題不參與問題負責人比對。
    /// </summary>
    private static IReadOnlyList<long> RecordOwnerIds(DailyAnalysisRecord record, WebHost host, MailContext ctx)
    {
        var issueOwnerIds = record.TopIssues
            .Where(issue => !issue.Suppressed)
            .SelectMany(issue => ctx.IssueOwnersByKey.TryGetValue(
                IssueProfile.KeyOf(issue.Source, issue.EventId), out var owners) ? owners : Enumerable.Empty<long>())
            .Distinct()
            .ToList();

        return issueOwnerIds.Count > 0 ? issueOwnerIds : host.OwnerUserIds;
    }

    // ── 內部：組信 ────────────────────────────────────────────────────────

    private async Task SendRunSummaryAsync(
        SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx, DateTime from, DateTime to, CancellationToken ct,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? mailVersions = null, string? mailContextFingerprint = null,
        string? expectedSettingsFingerprint = null,
        IReadOnlyCollection<DailyAnalysisRecord>? formalScopeRecords = null)
    {
        var minRank = RiskLevels.Rank(settings.MailMinRiskLevel);
        var state = _state.Get();
        var candidates = records.Where(r => RiskLevels.Rank(r.RiskLevel) >= minRank).ToList();
        var (_, candidateViews) = ResolvePerRecipient(settings, candidates, ctx);
        var qualifying = candidates.Where(record =>
        {
            var key = WorkflowRecordKey("summary", record, mailVersions, candidateViews, mailContextFingerprint);
            return !state.SummarySentKeys.Contains(key) &&
                (mailVersions == null || !mailVersions.TryGetValue(RecordKey(record), out var version) || version.ParentRecordId != record.RecordId
                    ? !state.SummarySentKeys.Contains(RecordKey(record))
                    : true);
        }).ToList();
        if (qualifying.Count == 0) return;

        var (order, views) = ResolvePerRecipient(settings, qualifying, ctx);
        PlanWorkflowMail("summary", qualifying, views, mailVersions, mailContextFingerprint);
        if (order.Count == 0) return;

        var settingsFingerprint = expectedSettingsFingerprint ?? HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(settings)]);
        var policyRevision = _prtgMonitoring?.Get().Revision;
        var scopeReader = _prtgBackend == null ? null : new PrtgScopeRevisionReader(_prtgBackend, _hosts);
        var scopeRevision = scopeReader?.Read();

        var issueRowsCache = new Dictionary<string, List<MailIssueRow>>();
        var attemptedSummaryDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        var attemptedFormalSummaryDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        var summaryKeys = qualifying.Select(RecordKey).ToHashSet(StringComparer.Ordinal);
        var formalRecords = qualifying.Concat(formalScopeRecords ?? ReadFormalMailScopeRecords(from, to))
            .GroupBy(RecordKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        (string Subject, string Body) BuildMessage(RecipientView view)
        {
            var count = (view.ApprovedRecords ?? qualifying).Count(record => summaryKeys.Contains(RecordKey(record)) &&
                RiskLevels.Rank(record.RiskLevel) >= RiskLevels.Rank(settings.MailMinRiskLevel));
            var statsLine = $"執行摘要：{count} 台主機達 {settings.MailMinRiskLevel} 風險以上";
            var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", DateTime.Today.ToString("yyyy-MM-dd"),
                settings.MailMinRiskLevel, "執行摘要", statsLine);
            return (subject, BuildStatsAndDetailBody(settings, statsLine,
                BuildIssueRowsCached(issueRowsCache, from, to, view.VisibleHostIds, view.SuppressedPressurePairs)));
        }

        // 標記放在 SendPerRecipientAsync 的 finally 裡執行（見其文件說明）：取消例外會讓
        // await 直接拋出、跳過這裡以下的程式碼，中斷前已寄成的部分仍要落地標記，
        // 不能靠「await 正常回傳後才標記」這種寫法——那樣取消一發生就整批漏標。
        RecipientView? Recheck(string email, RecipientView original)
        {
            var currentSettings = _settingsStore.Get();
            if (HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(currentSettings)]) != settingsFingerprint ||
                MailContextFingerprint(currentSettings) != mailContextFingerprint ||
                !currentSettings.MailEnabled || !currentSettings.MailOnRunCompleted ||
                _prtgMonitoring?.Get().Revision != policyRevision || scopeReader?.Read() != scopeRevision) return null;
            var currentRisk = RiskLevels.AtOrAbove(currentSettings.MailMinRiskLevel);
            var currentRecords = _records.Query(new RecordQueryFilter { Hosts = null, From = from, To = to, RiskLevels = currentRisk })
                .Where(record => qualifying.Any(candidate => RecordKey(candidate) == RecordKey(record)) && CurrentWorkflowVersion(record, mailVersions))
                .ToList();
            var (currentOrder, currentViews) = ResolvePerRecipient(currentSettings, currentRecords, BuildContext());
            foreach (var oldRecipient in views.Keys.ToArray()) views.Remove(oldRecipient);
            foreach (var entry in currentViews) views[entry.Key] = entry.Value;
            if (!currentOrder.Contains(email, StringComparer.OrdinalIgnoreCase) || !currentViews.TryGetValue(email, out var live)) return null;
            if (live.Detail == null)
            {
                var unresolvedStats = qualifying.Where(record => CurrentWorkflowVersion(record, mailVersions) &&
                    !IsWorkflowMailPartAccepted("summary", email, record, mailVersions, mailContextFingerprint)).ToList();
                return original.Detail == null && unresolvedStats.Count > 0 ? live : null;
            }
            var candidatesNow = live.Detail ?? new List<DailyAnalysisRecord>();
            var unresolved = FilterUnresolvedWorkflowMail("summary", email, new RecipientView(candidatesNow, live.VisibleHostIds), mailVersions, mailContextFingerprint);
            if (original.Detail is { Count: > 0 } && unresolved.Count == 0) return null;
            return live.Detail == null ? live : new RecipientView(unresolved, live.VisibleHostIds);
        }

        await SendPerRecipientAsync(settings, order, views, BuildMessage,
            record => WorkflowRecordKey("summary", record, mailVersions, candidateViews, mailContextFingerprint), ct,
            onComplete: (success, coverage) => MarkSent(settings, qualifying, success, coverage, s => s.SummarySentKeys,
                record => WorkflowRecordKey("summary", record, mailVersions, candidateViews, mailContextFingerprint)),
            onRecipientStarting: (email, detail) =>
            {
                attemptedSummaryDetails[email] = detail ?? qualifying;
                attemptedFormalSummaryDetails[email] = detail ?? [];
            },
            onRecipientResult: (email, success) =>
            {
                var current = views.GetValueOrDefault(email);
                var checkedView = current == null ? null : Recheck(email, current);
                var attempted = attemptedSummaryDetails.GetValueOrDefault(email) ?? new List<DailyAnalysisRecord>();
                CompleteFormalStartClaims("summary", email,
                    attemptedFormalSummaryDetails.GetValueOrDefault(email) ?? [], success);
                if (checkedView != null && (checkedView.Detail == null
                        ? attempted.All(record => CurrentWorkflowVersion(record, mailVersions))
                        : attempted.All(record => checkedView.Detail.Any(live =>
                            RecordKey(live) == RecordKey(record) && live.RecordId == record.RecordId &&
                            SameWorkflowMailContent("summary", live, record, mailVersions, mailContextFingerprint)))) )
                    TrackWorkflowMail("summary", attempted, email, success, views, mailVersions, mailContextFingerprint);
            },
            recheck: Recheck,
            authorizeStart: (email, view) => AuthorizeFormalStart(email, view, "summary", formalRecords));
    }

    /// <summary>
    /// 高風險即時通知：**按收件人聚合**（回饋十六輪批次A-1）——一位收件人一次執行最多只收
    /// 一封信。回饋十七輪批次B-4 疊加可見範圍過濾：對應到帳號的收件人只看見自己可見範圍內的
    /// 主機，對應不到帳號的收件人只收統計行。
    /// </summary>
    private async Task SendUrgentNotificationsAsync(
        SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx, DateTime from, DateTime to, CancellationToken ct,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? mailVersions = null, string? mailContextFingerprint = null,
        string? expectedSettingsFingerprint = null,
        IReadOnlyCollection<DailyAnalysisRecord>? formalScopeRecords = null,
        IReadOnlyDictionary<long, string>? durableRetryIntentKeys = null)
    {
        // 固定本輪起點，避免 store 回傳共用物件時設定修改連帶改掉比較基準。
        settings = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(settings))!;
        var settingsFingerprint = expectedSettingsFingerprint ?? HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(settings)]);
        var state = _state.Get();
        var policyRevision = _prtgMonitoring?.Get().Revision;
        var scopeReader = _prtgBackend == null ? null : new PrtgScopeRevisionReader(_prtgBackend, _hosts);
        var scopeRevision = scopeReader?.Read();
        _state.Update(s =>
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(14, settings.RetentionDays));
            foreach (var fact in records.SelectMany(PrtgUrgentFacts)) s.PrtgUrgentFactSeenAtUtc[fact.Key] = DateTime.UtcNow;
            foreach (var key in s.UrgentOutbox.Where(p => p.Value.RecordDate < cutoff.Date &&
                         (p.Value.Status is "smtp-accepted" or "cancelled-formal-revoked" or "no-qualified-recipient"))
                     .Select(p => p.Key).ToArray())
                s.UrgentOutbox.Remove(key);
            foreach (var key in s.PrtgUrgentAcceptedFacts.Keys.Where(k =>
                !s.PrtgUrgentFactSeenAtUtc.TryGetValue(k, out var seen) || seen < cutoff).ToArray())
            { s.PrtgUrgentAcceptedFacts.Remove(key); s.PrtgUrgentFactSeenAtUtc.Remove(key); }
        });
        var candidates = records.Where(QualifiesForUrgentRisk)
            .Where(r => !r.TopIssues.Any(PrtgFindingMapper.IsPrtg) || r.CanSupplementWithPrtg())
            .ToList();
        var (_, candidateViews) = ResolvePerRecipient(settings, candidates, ctx);
        string IntentKey(DailyAnalysisRecord record) => durableRetryIntentKeys?.GetValueOrDefault(record.RecordId) ??
            UrgentRecordKey(record, mailVersions, candidateViews, mailContextFingerprint);
        candidates = candidates.Where(record => !record.TopIssues.Any(PrtgFindingMapper.IsPrtg) ||
            record.Date.Date == DateTime.Today.AddDays(-1) ||
            durableRetryIntentKeys?.ContainsKey(record.RecordId) == true ||
            state.UrgentOutbox.TryGetValue(IntentKey(record), out var existingIntent) &&
                existingIntent.Status is not ("smtp-accepted" or "cancelled-formal-revoked" or "no-qualified-recipient"))
            .ToList(); // Late PRTG delivery is eligible only as a retry of the exact durable parent/fence intent.
        var pending = candidates.Where(r => !state.UrgentSentKeys.Contains(IntentKey(r)))
            .Where(r => !state.UrgentOutbox.TryGetValue(IntentKey(r), out var priorIntent) ||
                priorIntent.Status != "cancelled-formal-revoked")
            .Where(r => mailVersions != null && mailVersions.TryGetValue(RecordKey(r), out var version) && version.ParentRecordId == r.RecordId ||
                PrtgUrgentFacts(r).Count == 0 ||
                PrtgUrgentFacts(r).Any(f => f.Value > state.PrtgUrgentAcceptedFacts.GetValueOrDefault(f.Key)) ||
                r.TopIssues.Any(i => !PrtgFindingMapper.IsPrtg(i) && !i.Suppressed && i.ElevatesDayRisk))
            .ToList();
        if (pending.Count == 0) return;
        var (order, views) = ResolvePerRecipient(settings, pending, ctx);
        if (durableRetryIntentKeys is { Count: > 0 })
        {
            foreach (var email in views.Keys.ToArray())
            {
                var view = views[email];
                var detail = view.Detail?.Where(record => durableRetryIntentKeys.TryGetValue(record.RecordId, out var key) &&
                    state.UrgentOutbox.TryGetValue(key, out var intent) && intent.Recipients.ContainsKey(email) &&
                    intent.Recipients.GetValueOrDefault(email) != "smtp-accepted").ToList();
                views[email] = view with { Detail = detail };
            }
            order.RemoveAll(email => views[email].Detail is not { Count: > 0 });
        }
        if (pending.All(r => r.TopIssues.Any(PrtgFindingMapper.IsPrtg)))
            order.RemoveAll(email => views[email].Detail is not { Count: > 0 });
        PlanWorkflowMail("urgent", pending, views, mailVersions, mailContextFingerprint);
        _state.Update(s =>
        {
            foreach (var record in pending)
            {
                var key = IntentKey(record);
                if (!s.UrgentOutbox.TryGetValue(key, out var intent))
                    s.UrgentOutbox[key] = intent = new MailUrgentIntent
                    {
                        Key = key, HostId = record.HostId, ParentRecordId = record.RecordId,
                        RecordDate = record.Date, CreatedAtUtc = DateTime.UtcNow,
                        SettingsRevision = settings.Revision, ProblemKeys = record.TopIssues.Select(i => i.EventKey).ToList()
                    };
                if (intent.ParentRecordId == 0 && intent.Key == key)
                    intent.ParentRecordId = record.RecordId;
                if (intent.CreatedAtUtc == default)
                    intent.CreatedAtUtc = intent.UpdatedAtUtc == default ? DateTime.UtcNow : intent.UpdatedAtUtc;
                intent.WaitingReasonCode = null;
                intent.WaitingSinceUtc = null;
                if (intent.Status is not ("smtp-accepted" or "cancelled-formal-revoked")) intent.Status = "pending";
                intent.UpdatedAtUtc = DateTime.UtcNow;
                foreach (var email in order.Where(e => views[e].Detail?.Any(r => r.HostId == record.HostId && r.Date == record.Date) == true))
                    if (intent.Recipients.GetValueOrDefault(email) != "smtp-accepted")
                        intent.Recipients[email] = "pending";
                if (intent.Recipients.Count == 0) intent.Status = "no-qualified-recipient";
            }
        });
        if (order.Count == 0) return;
        var attemptedKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var attemptedDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        var attemptedFormalDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        var issueRowsCache = new Dictionary<string, List<MailIssueRow>>();
        var urgentKeys = pending.Select(RecordKey).ToHashSet(StringComparer.Ordinal);
        var formalRecords = pending.Concat(formalScopeRecords ?? ReadFormalMailScopeRecords(from, to))
            .GroupBy(RecordKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        (string Subject, string Body) BuildMessage(RecipientView view)
        {
            var count = (view.ApprovedRecords ?? pending).Count(record => urgentKeys.Contains(RecordKey(record)) && QualifiesForUrgentRisk(record));
            var globalStatsLine = $"本次執行共 {count} 筆高風險主機日達門檻";
            return BuildUrgentMessage(settings, globalStatsLine, view.Detail,
                BuildIssueRowsCached(issueRowsCache, from, to, view.VisibleHostIds, view.SuppressedPressurePairs), BuildContext());
        }

        RecipientView? Recheck(string email, RecipientView original)
        {
            var currentSettings = _settingsStore.Get();
            var policy = _prtgMonitoring?.Get();
            if (!currentSettings.MailEnabled || !currentSettings.MailUrgentEnabled ||
                currentSettings.SmtpServer != settings.SmtpServer || currentSettings.SmtpPort != settings.SmtpPort ||
                currentSettings.SmtpUseTls != settings.SmtpUseTls || currentSettings.SmtpAccount != settings.SmtpAccount ||
                currentSettings.SmtpPasswordEnc != settings.SmtpPasswordEnc || currentSettings.MailFrom != settings.MailFrom ||
                GetSuspendedRecipients().Contains(email, StringComparer.OrdinalIgnoreCase)) return null;
            var liveContext = BuildContext();
            if (HostDayWorkflowFingerprint.HashParts([JsonSerializer.Serialize(currentSettings)]) != settingsFingerprint ||
                MailContextFingerprint(currentSettings) != mailContextFingerprint) return null;
            var currentRecords = _records.Query(new RecordQueryFilter { From = from, To = to, Hosts = null, RiskLevels = (currentSettings.MailOnRunCompleted ? RiskLevels.AtOrAbove(currentSettings.MailMinRiskLevel) : new[] { RiskLevels.High }) });
            if (durableRetryIntentKeys is { Count: > 0 })
                currentRecords = currentRecords.Concat(records.Where(record => durableRetryIntentKeys.ContainsKey(record.RecordId)))
                    .GroupBy(record => record.RecordId).Select(group => group.Last()).ToList();
            var allowedKeys = (original.Detail ?? new()).Select(IntentKey).ToHashSet(StringComparer.Ordinal);
            var (_, liveViews) = ResolvePerRecipient(currentSettings, currentRecords, liveContext);
            if (!liveViews.TryGetValue(email, out var liveView)) return null;
            if (liveView.Detail == null)
            {
                var unresolvedStats = pending.Where(record => CurrentWorkflowVersion(record, mailVersions) &&
                    !IsWorkflowMailPartAccepted("urgent", email, record, mailVersions, mailContextFingerprint)).ToList();
                return original.Detail == null && unresolvedStats.Count > 0 ? liveView : null;
            }
            var visible = liveView.VisibleHostIds;
            var detail = liveView.Detail.Where(r => CurrentWorkflowVersion(r, mailVersions) &&
                allowedKeys.Contains(IntentKey(r)))
                .Where(r => !r.TopIssues.Any(PrtgFindingMapper.IsPrtg) ||
                    (currentSettings.PrtgEnabled && PrtgOperationScope.SameSettings(settings, currentSettings) &&
                     (policy == null || policy.Revision == policyRevision && policy.Ready(currentSettings.PrtgUrl) &&
                        policy.HostIds.Contains(r.HostId) && r.TopIssues.Where(PrtgFindingMapper.IsPrtg).All(i =>
                            PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(i) ||
                            i.PrtgSourceGeneration == policy.SourceGeneration && i.EventKey.Split(':') is { Length: >= 3 } parts &&
                            long.TryParse(parts[2], out var targetId) &&
                            (i.EventKey.Split(':') is { Length: >= 2 } keyParts && keyParts[1] == PrtgRuleEvaluator.RuleSilent
                                ? CurrentResource(r.HostId, targetId, i, r.Date)
                                : policy.SensorIds.Contains(targetId) && CurrentResource(r.HostId, targetId, i, r.Date)))) &&
                     (scopeReader == null || scopeReader.Read() == scopeRevision) &&
                     r.CanSupplementWithPrtg() && liveContext.Host(r.HostId) is { Active: true, MergedInto: null }))
                .ToList();
            var liveFiltered = FilterUnresolvedWorkflowMail("urgent", email, new RecipientView(detail, visible), mailVersions, mailContextFingerprint);
            foreach (var oldRecipient in views.Keys.ToArray()) views.Remove(oldRecipient);
            foreach (var entry in liveViews) views[entry.Key] = entry.Value;
            return liveFiltered.Count == 0 && original.Detail?.Count > 0 ? null : new RecipientView(liveFiltered, visible);
        }
        bool CurrentResource(long hostId, long sensorId, LogIssueSignature issue, DateTime recordDate)
        {
            if (_prtgBackend == null) return true;
            var host = _hosts.GetAll().FirstOrDefault(h => h.HostId == hostId);
            if (host == null || host.Source != "netiq") return false;
            var rules = new KnownIssueRuleStore(_prtgBackend.Blob("rules"));
            if (!rules.Exists) return false;
            KnownIssueRule? capturedRule;
            {
                var current = rules.Load().Content?.Rules.FirstOrDefault(r => r.Id == issue.RuleId && r.Enabled && r.Platform == "prtg");
                if (current == null) return false;
                using var db = _prtgBackend.CreateContext();
                var json = db.PrtgObservations.Where(o => o.ActiveKey != null && o.HostId == hostId && o.EventKey == issue.EventKey)
                    .OrderByDescending(o => o.RecordDate).Select(o => o.ContentJson).FirstOrDefault();
                if (json == null) return false;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                capturedRule = doc.RootElement.GetProperty("Finding").GetProperty("Rule").Deserialize<KnownIssueRule>();
                if (System.Text.Json.JsonSerializer.Serialize(capturedRule) != System.Text.Json.JsonSerializer.Serialize(current)) return false;
            }
            var suppressionStore = new MuteAwareSuppressionStore(new SuppressionStore(_prtgBackend.Blob("suppressions")), _issueOwners ?? new IssueOwnerStore(_prtgBackend.Blob("issue_owners")));
            var suppressions = suppressionStore.LoadAll();
            var copy = System.Text.Json.JsonSerializer.Deserialize<LogIssueSignature>(System.Text.Json.JsonSerializer.Serialize(issue))!;
            SuppressionFilter.MarkSuppressed([copy], SuppressionFilter.ActiveForHost(suppressions, host.HostName, host.GroupIds, DateTime.Now),
                SuppressionFilter.MutesOf(suppressions), DateTime.Today.AddDays(-1));
            if (copy.Suppressed) return false;
            if (issue.EventKey.Split(':') is { Length: >= 2 } keyParts && keyParts[1] == PrtgRuleEvaluator.RuleSilent)
                return PrtgSilentDeviceProofRevalidator.IsCurrent(_prtgBackend, hostId,
                    recordDate, issue, capturedRule, _settingsStore.Get().PrtgUrl);
            var identity = _prtgBackend.PrtgStore().GetResourceIdentity(sensorId);
            if (PrtgResourceProfileQualification.IsCpuOrMemoryPressure(issue))
                return PrtgResourceProfileQualification.IsCurrentFormalPressure(_prtgBackend, hostId, sensorId,
                    issue, DateTime.UtcNow);
            if (issue.RuleId != "builtin-prtg-resource-disk-pressure")
            {
                var evidence = new PrtgSensorTimelineStore(_prtgBackend.Blob(PrtgSensorTimelineStore.Prefix + sensorId)).Get();
                if (evidence.HostId != hostId || evidence.SourceGeneration != issue.PrtgSourceGeneration ||
                    evidence.ResourceGeneration != issue.PrtgResourceGeneration ||
                    !PrtgResourceQualification.IsCurrent(evidence, identity, issue.PrtgSourceGeneration,
                        sensorId, identity.DeviceId, hostId)) return false;
            }
            if (issue.RuleId == "builtin-prtg-resource-disk-pressure")
            {
                var resourceRules = PrtgResourceCurrentRuleCatalog.Load(_prtgBackend);
                if (!PrtgResourceProfileQualification.IsCurrentDiskProfile(_prtgBackend, hostId, sensorId,
                        DateTime.UtcNow, issue.PrtgSourceGeneration, issue.PrtgResourceGeneration,
                        issue.PrtgChannelGeneration) ||
                    issue.PrtgRuleAdmissionFingerprint != resourceRules.AdmissionFingerprintFor(PrtgResourceFamily.Disk) ||
                    issue.PrtgResourceReasonCodes is not { Count: > 0 } reasons)
                    return false;
                if (reasons.Contains("disk-seven-day-low-water-trend") &&
                    (!resourceRules.DiskTrendEnabled || issue.PrtgTrendSourceRuleId != resourceRules.DiskTrendRuleId ||
                     issue.PrtgTrendSourceRuleFingerprint != resourceRules.DiskTrendRuleFingerprint ||
                     !PrtgResourceQualification.IsChannelCurrent(
                        new PrtgDiskSemanticEvidenceStore(_prtgBackend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Get(sensorId),
                        new PrtgDiskVerificationResultStore(_prtgBackend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Get(sensorId),
                        identity))) return false;
            }
            else if (issue.RuleId is "disk_free_trend" or "builtin-prtg-disk-free-trend")
            {
                var semantic = new PrtgDiskSemanticEvidenceStore(_prtgBackend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Get(sensorId);
                var probe = new PrtgDiskVerificationResultStore(_prtgBackend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Get(sensorId);
                return PrtgResourceQualification.IsChannelCurrent(semantic, probe, identity);
            }
            return true;
        }
        // Formal authorization may remove a revoked pressure finding while preserving
        // an independent NetIQ risk. Transport coverage belongs to the immutable intent
        // created for that exact parent, not to the narrowed rendering's content hash.
        // Recheck above still validates live content/version before authorization.
        string DeliveryIntentKey(DailyAnalysisRecord rendered)
        {
            var original = pending.FirstOrDefault(record => record.RecordId == rendered.RecordId &&
                RecordKey(record) == RecordKey(rendered));
            return IntentKey(original ?? rendered);
        }
        await SendPerRecipientAsync(settings, order, views, BuildMessage,
            DeliveryIntentKey, ct,
            onComplete: (success, coverage) =>
            {
                MarkSent(settings, pending.Where(r => r.TopIssues.Any(PrtgFindingMapper.IsPrtg)).ToList(), success, coverage,
                    s => s.UrgentSentKeys, IntentKey, allowZeroCoverage: false);
                MarkSent(settings, pending.Where(r => !r.TopIssues.Any(PrtgFindingMapper.IsPrtg)).ToList(), success, coverage,
                    s => s.UrgentSentKeys, IntentKey);
                _state.Update(s =>
                {
                    foreach (var record in pending)
                    {
                        var key = IntentKey(record);
                        var intent = s.UrgentOutbox[key];
                        var expected = coverage.GetValueOrDefault(key) ?? new();
                        foreach (var email in expected)
                        {
                            if (success.GetValueOrDefault(email) || intent.SmtpAcceptedAtUtc.ContainsKey(email))
                            {
                                intent.Recipients[email] = "smtp-accepted";
                                continue;
                            }

                            // A sender callback marks recipients before/after transport. Keep the
                            // attempted state: a missing final SMTP response can mean DATA was
                            // accepted remotely, while recipients that never started remain not-sent.
                            if (intent.Recipients.GetValueOrDefault(email) is not ("sending-result-unknown" or "failed-or-unknown" or "cancelled-formal-revoked"))
                                intent.Recipients[email] = "not-sent";
                        }
                        intent.Status = expected.Count == 0 ? "no-qualified-recipient" :
                            expected.All(e => success.GetValueOrDefault(e) ||
                                intent.Recipients.GetValueOrDefault(e) == "smtp-accepted" ||
                                intent.SmtpAcceptedAtUtc.ContainsKey(e)) ? "smtp-accepted" : "pending";
                        var targetIssues = record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue).ToArray();
                        if (expected.Count == 0 && targetIssues.Length > 0 && intent.Recipients.Count > 0 &&
                            intent.Recipients.Keys.All(email => targetIssues.All(issue =>
                                intent.FormalIssueStates.GetValueOrDefault(FormalRecipientIssueKey(email, issue)) is "revoked" or "smtp-accepted")) &&
                            intent.Recipients.Keys.Any(email => targetIssues.Any(issue =>
                                intent.FormalIssueStates.GetValueOrDefault(FormalRecipientIssueKey(email, issue)) == "revoked")))
                            intent.Status = "cancelled-formal-revoked";
                        intent.UpdatedAtUtc = DateTime.UtcNow;
                        if (intent.Status == "smtp-accepted")
                            foreach (var fact in PrtgUrgentFactsForAcceptedIntent(record, intent))
                                s.PrtgUrgentAcceptedFacts[fact.Key] = fact.Value;
                    }
                });
            }, recheck: Recheck,
            onRecipientStarting: (email, detail) =>
            {
                attemptedDetails[email] = detail ?? pending;
                attemptedFormalDetails[email] = detail ?? [];
                attemptedKeys[email] = (detail ?? pending).Select(record =>
                {
                    var original = pending.FirstOrDefault(candidate => RecordKey(candidate) == RecordKey(record)) ?? record;
                    return IntentKey(original);
                }).ToHashSet(StringComparer.Ordinal);
                _state.Update(state =>
                {
                    foreach (var key in attemptedKeys[email])
                        if (state.UrgentOutbox.TryGetValue(key, out var intent) && intent.Recipients.GetValueOrDefault(email) != "smtp-accepted")
                        {
                            intent.Recipients[email] = "sending-result-unknown";
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                        }
                });
                TrackWorkflowMail("urgent", detail, email, null, views, mailVersions, mailContextFingerprint);
            },
            onRecipientResult: (email, accepted) =>
            {
                var current = views.GetValueOrDefault(email);
                var checkedView = current == null ? null : Recheck(email, current);
                var attempted = attemptedDetails.GetValueOrDefault(email) ?? new List<DailyAnalysisRecord>();
                CompleteFormalStartClaims("urgent", email,
                    attemptedFormalDetails.GetValueOrDefault(email) ?? [], accepted);
                if (checkedView != null && (checkedView.Detail == null
                        ? attempted.All(record => CurrentWorkflowVersion(record, mailVersions))
                        : attempted.All(record => checkedView.Detail.Any(live =>
                            RecordKey(live) == RecordKey(record) && live.RecordId == record.RecordId &&
                            SameWorkflowMailContent("urgent", live, record, mailVersions, mailContextFingerprint)))) )
                    TrackWorkflowMail("urgent", attempted, email, accepted, views, mailVersions, mailContextFingerprint);
                _state.Update(s =>
                {
                    foreach (var intent in (attemptedKeys.GetValueOrDefault(email) ?? new()).Select(k => s.UrgentOutbox[k])
                        .Where(i => i.Recipients.ContainsKey(email) && i.Status == "pending"))
                    {
                        intent.Recipients[email] = accepted ? "smtp-accepted" : "failed-or-unknown";
                        if (accepted) intent.SmtpAcceptedAtUtc[email] = DateTime.UtcNow;
                        foreach (var record in attempted)
                            foreach (var issue in record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
                            {
                                var issueStateKey = FormalRecipientIssueKey(email, issue);
                                if (intent.FormalIssueStates.GetValueOrDefault(issueStateKey) == "revoked") continue;
                                intent.FormalIssueStates[issueStateKey] = accepted ? "smtp-accepted" : "failed-or-unknown";
                            }
                        intent.UpdatedAtUtc = DateTime.UtcNow;
                    }
                });
            },
            authorizeStart: (email, view) => AuthorizeFormalStart(email, view, "urgent", formalRecords,
                IntentKey));
    }

    private static bool QualifiesForUrgentRisk(DailyAnalysisRecord record) =>
        record.RiskLevel == RiskLevels.High && record.RiskReview?.Status != "pending";

    private static Dictionary<string, int> PrtgUrgentFacts(DailyAnalysisRecord record) => record.TopIssues
        .Where(i => PrtgFindingMapper.IsPrtg(i) && !i.Suppressed && i.ElevatesDayRisk)
        .GroupBy(i => $"{record.HostId}|{i.EventKey}|{i.PrtgIncidentStartedAt:O}")
        .ToDictionary(g => g.Key, g => g.Max(i => (int)i.Severity + 10));

    private static Dictionary<string, int> PrtgUrgentFactsForAcceptedIntent(DailyAnalysisRecord record, MailUrgentIntent intent)
    {
        var accepted = record.TopIssues.Where(issue => PrtgFindingMapper.IsPrtg(issue) && !issue.Suppressed && issue.ElevatesDayRisk)
            .Where(issue => !PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(issue) ||
                intent.Recipients.Count > 0 && intent.Recipients.Keys.All(email =>
                    (intent.FormalStartFenceRefs?.ContainsKey(FormalRecipientIssueKey(email, issue)) == true ||
                     intent.FormalStartFenceRefs?.ContainsKey(FormalIssueKey(issue)) == true) &&
                    intent.FormalIssueStates.GetValueOrDefault(FormalRecipientIssueKey(email, issue)) == "smtp-accepted") ||
                intent.FormalStartFences?.ContainsKey(FormalIssueKey(issue)) == true && intent.Recipients.Count > 0 &&
                    intent.Recipients.Keys.All(email => intent.Recipients.GetValueOrDefault(email) == "smtp-accepted" ||
                        intent.SmtpAcceptedAtUtc.ContainsKey(email)));
        return accepted.GroupBy(issue => $"{record.HostId}|{issue.EventKey}|{issue.PrtgIncidentStartedAt:O}")
            .ToDictionary(group => group.Key, group => group.Max(issue => (int)issue.Severity + 10));
    }

    private static string FormalIssueKey(LogIssueSignature issue) =>
        IssueSignatureKey.For(issue) + "|" + issue.EventKey + "|" + issue.PrtgChannelGeneration;

    private static string FormalRecipientIssueKey(string recipient, LogIssueSignature issue) =>
        recipient.Trim().ToLowerInvariant() + "|" + FormalIssueKey(issue);

    private static string LegacyFormalClaimKey(string lane, string recipient, DailyAnalysisRecord record, LogIssueSignature issue) =>
        $"{lane}|{recipient.Trim().ToLowerInvariant()}|{record.RecordId}|mode:{record.PrtgManifest?.ResourceModeBlobVersion ?? -1}|{FormalIssueKey(issue)}";

    private static string FormalFenceKey(DailyAnalysisRecord record, LogIssueSignature issue) =>
        $"{record.RecordId}|mode:{record.PrtgManifest?.ResourceModeBlobVersion ?? -1}|{FormalIssueKey(issue)}";

    private static string FormalClaimKey(string lane, string recipient, string fenceKey) =>
        $"{lane}|{recipient.Trim().ToLowerInvariant()}|{fenceKey}";

    private static string FormalFenceHash(PrtgResourceFormalDeliveryFence fence) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fence, LfJsonOptions.Pretty))));

    private void CompleteFormalStartClaims(string lane, string recipient,
        IEnumerable<DailyAnalysisRecord> records, bool smtpAccepted)
    {
        var targetRecords = records.Where(record => record.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)).ToArray();
        if (targetRecords.Length == 0) return;

        _state.MutateWithContext((db, state) =>
        {
            var shards = new PrtgFormalMailClaimStore.MutationSession(db);
            foreach (var record in targetRecords)
                foreach (var issue in record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
                {
                    var fenceKey = FormalFenceKey(record, issue);
                    var key = FormalClaimKey(lane, recipient, fenceKey);
                    var issueKey = FormalIssueKey(issue);
                    var intent = lane == "urgent" ? state.UrgentOutbox.Values.FirstOrDefault(candidate =>
                        candidate.HostId == record.HostId && candidate.RecordDate.Date == record.Date.Date &&
                        (candidate.FormalStartFenceRefs?.ContainsKey(FormalRecipientIssueKey(recipient, issue)) == true ||
                         candidate.FormalStartFenceRefs?.ContainsKey(issueKey) == true)) : null;
                    var recipientIssueKey = FormalRecipientIssueKey(recipient, issue);
                    var shardKey = intent?.FormalStartFenceRefs?.GetValueOrDefault(recipientIssueKey)?.ShardKey ??
                        intent?.FormalStartFenceRefs?.GetValueOrDefault(issueKey)?.ShardKey ??
                        PrtgFormalMailClaimStore.BlobKey(record.HostId, record.Date, lane, fenceKey, recipient);
                    var claim = shards.GetClaim(record.HostId, shardKey, key);
                    if (claim == null ||
                        claim.Status is "revoked" or "smtp-accepted") continue;
                    claim.Status = smtpAccepted ? "smtp-accepted" : "failed-or-unknown";
                    claim.UpdatedAtUtc = DateTime.UtcNow;
                    if (smtpAccepted) claim.SmtpAcceptedAtUtc = DateTime.UtcNow;
                    shards.SetClaim(record.HostId, shardKey, key, claim);
                }
            shards.Persist();
            return true;
        });
    }

    /// <summary>
    /// Creates the durable recipient start claim and an approved mail snapshot in one
    /// serializable transaction. Formal CPU/memory findings are copied into the message only
    /// when the persisted parent, mode revision, rule and grant all still match at claim time.
    /// </summary>
    private RecipientView? AuthorizeFormalStart(string recipient, RecipientView view, string lane,
        IReadOnlyCollection<DailyAnalysisRecord> sourceRecords,
        Func<DailyAnalysisRecord, string>? urgentIntentKey = null)
    {
        var records = sourceRecords.Concat(view.Detail ?? [])
            .GroupBy(RecordKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        if (!records.Any(record => record.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)))
            return view;

        // Statistics-only recipients have no detailed finding to deliver and therefore create no
        // durable start claim. Validate current formal contributions read-only so a full global
        // outbox blob cannot suppress otherwise deliverable NetIQ/other-pressure statistics.
        if (view.Detail == null)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_prtgBackend != null)
            {
                using var db = _prtgBackend.CreateContext();
                foreach (var record in records)
                foreach (var issue in record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
                {
                    if (record.RecordId > 0 && PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(
                            db, record.HostId, record.RecordId, issue, null, out _))
                        allowed.Add(RecordKey(record) + "|" + FormalIssueKey(issue));
                    else
                        suppressed.Add(PressureContributionKey(record, issue));
                }
            }
            else
            {
                foreach (var record in records)
                foreach (var issue in record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
                    suppressed.Add(PressureContributionKey(record, issue));
            }

            var approved = records.Select(record => FilterFormalIssues(record, allowed, suppressed)).ToList();
            return view with { ApprovedRecords = approved, SuppressedPressurePairs =
                suppressed.Select(key => key.Split('\0', 4)).Where(parts => parts.Length == 4)
                    .Select(parts => $"{parts[0]}\0{parts[2]}").ToHashSet(StringComparer.OrdinalIgnoreCase) };
        }

        FormalStartSnapshot authorization;
        try
        {
            authorization = _state.MutateWithContext((db, state) =>
        {
            var shards = new PrtgFormalMailClaimStore.MutationSession(db);
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var suppressedContributions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in records)
            {
                foreach (var issue in record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
                {
                    var issueKey = FormalIssueKey(issue);
                    var isRecipientFinding = view.Detail?.Any(candidate => RecordKey(candidate) == RecordKey(record) &&
                        candidate.TopIssues.Any(candidateIssue => FormalIssueKey(candidateIssue) == issueKey)) == true;
                    if (!isRecipientFinding)
                    {
                        // Scope-only records can suppress an aggregate row, but need no shard IO
                        // or recipient claim. Prove their current status directly in this context.
                        if (record.RecordId > 0 && PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(
                                db, record.HostId, record.RecordId, issue, null, out _))
                            allowed.Add(RecordKey(record) + "|" + issueKey);
                        else
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                        continue;
                    }

                    var fenceKey = FormalFenceKey(record, issue);
                    var claimKey = FormalClaimKey(lane, recipient, fenceKey);
                    var legacyClaimKey = LegacyFormalClaimKey(lane, recipient, record, issue);
                    state.FormalMailStartClaims.TryGetValue(legacyClaimKey, out var legacyClaim);
                    MailUrgentIntent? intent = null;
                    string? recipientIssueKey = null;
                    if (lane == "urgent" && urgentIntentKey != null)
                        state.UrgentOutbox.TryGetValue(urgentIntentKey(record), out intent);
                    var recipientReferenceKey = FormalRecipientIssueKey(recipient, issue);
                    var shardKey = intent?.FormalStartFenceRefs?.GetValueOrDefault(recipientReferenceKey)?.ShardKey ??
                        intent?.FormalStartFenceRefs?.GetValueOrDefault(issueKey)?.ShardKey ??
                        PrtgFormalMailClaimStore.BlobKey(record.HostId, record.Date, lane, fenceKey, recipient);
                    var priorClaim = shards.GetClaim(record.HostId, shardKey, claimKey);
                    var expected = shards.GetFence(record.HostId, shardKey, fenceKey);

                    if (legacyClaim != null)
                    {
                        // Older releases stored full fences in the singleton. Keep that evidence
                        // intact, but never reinterpret or recapture it into the new shard format.
                        if (intent != null && intent.Recipients.GetValueOrDefault(recipient) != "smtp-accepted" &&
                            !intent.SmtpAcceptedAtUtc.ContainsKey(recipient))
                        {
                            recipientIssueKey = FormalRecipientIssueKey(recipient, issue);
                            intent.FormalIssueStates[recipientIssueKey] = "revoked";
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                        }
                        suppressedContributions.Add(PressureContributionKey(record, issue));
                        continue;
                    }
                    if (priorClaim?.FenceKey != null && priorClaim.FenceKey != fenceKey)
                        throw new InvalidDataException("Formal mail claim key resolved to a different immutable fence key.");
                    if (priorClaim?.Status is "revoked" or "smtp-accepted")
                    {
                        if (priorClaim.Status == "smtp-accepted" && intent != null)
                        {
                            recipientIssueKey ??= FormalRecipientIssueKey(recipient, issue);
                            if (intent.FormalIssueStates.GetValueOrDefault(recipientIssueKey) != "revoked")
                                intent.FormalIssueStates[recipientIssueKey] = "smtp-accepted";
                            if (intent.Recipients.GetValueOrDefault(recipient) != "smtp-accepted")
                            {
                                intent.Recipients[recipient] = "smtp-accepted";
                                intent.SmtpAcceptedAtUtc[recipient] = priorClaim.SmtpAcceptedAtUtc ?? DateTime.UtcNow;
                            }
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                        }
                        suppressedContributions.Add(PressureContributionKey(record, issue));
                        continue;
                    }
                    if (priorClaim != null && expected == null)
                    {
                        // An active claim without its immutable shard fence is ambiguous. Keep
                        // the unknown state and fail closed instead of recapturing a newer proof.
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                        continue;
                    }
                    if (lane == "urgent")
                    {
                        if (intent == null)
                        {
                            // Aggregate rows may contain other current formal occurrences in the
                            // window. They have no urgent outbox intent, so prove their current
                            // parent authority afresh; never let them bypass the start fence.
                            if (record.RecordId > 0 && PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(
                                    db, record.HostId, record.RecordId, issue, expected, out _))
                                allowed.Add(RecordKey(record) + "|" + issueKey);
                            else
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                            continue;
                        }
                        intent.FormalStartFenceRefs ??= new Dictionary<string, PrtgFormalMailFenceReference>(StringComparer.Ordinal);
                        if (intent.FormalStartFences?.ContainsKey(issueKey) == true)
                        {
                            if (intent.Recipients.GetValueOrDefault(recipient) != "smtp-accepted" &&
                                !intent.SmtpAcceptedAtUtc.ContainsKey(recipient))
                            {
                                intent.FormalIssueStates[FormalRecipientIssueKey(recipient, issue)] = "revoked";
                                intent.UpdatedAtUtc = DateTime.UtcNow;
                            }
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                            continue;
                        }
                        recipientIssueKey = FormalRecipientIssueKey(recipient, issue);
                        var priorIssueState = intent.FormalIssueStates.GetValueOrDefault(recipientIssueKey);
                        if (priorIssueState is "revoked" or "smtp-accepted" ||
                            intent.Recipients.GetValueOrDefault(recipient) == "smtp-accepted")
                        {
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                            continue;
                        }
                        if ((intent.FormalStartFenceRefs.TryGetValue(recipientIssueKey, out var reference) ||
                             intent.FormalStartFenceRefs.TryGetValue(issueKey, out reference)) &&
                            (reference.HostId != record.HostId || reference.ShardKey != shardKey ||
                             reference.FenceKey != fenceKey || expected == null || reference.FenceHash != FormalFenceHash(expected)))
                        {
                            intent.FormalIssueStates[recipientIssueKey] = "revoked";
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                            continue;
                        }
                        if (expected == null && (priorIssueState is "sending-result-unknown" or "failed-or-unknown" or "smtp-accepted" ||
                            intent.Recipients.GetValueOrDefault(recipient) is "sending-result-unknown" or "failed-or-unknown" or "smtp-accepted"))
                        {
                            intent.FormalIssueStates[recipientIssueKey] = "revoked";
                            suppressedContributions.Add(PressureContributionKey(record, issue));
                            continue;
                        }
                    }

                    if (record.RecordId > 0 && PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(
                            db, record.HostId, record.RecordId, issue, expected, out var fence))
                    {
                        if (expected == null)
                            shards.SetFence(record.HostId, shardKey, fenceKey, fence, DateTime.UtcNow);
                        if (intent != null)
                        {
                            var reference = new PrtgFormalMailFenceReference(record.HostId, shardKey, fenceKey, FormalFenceHash(fence));
                            if (intent.FormalStartFenceRefs.TryGetValue(recipientIssueKey, out var existingReference) && existingReference != reference)
                                throw new InvalidDataException("Urgent mail retry fence reference changed inside its immutable intent.");
                            intent.FormalStartFenceRefs[recipientIssueKey] = reference;
                            if (recipientIssueKey != null) intent.FormalIssueStates[recipientIssueKey] = "sending-result-unknown";
                            if (intent.Recipients.GetValueOrDefault(recipient) != "smtp-accepted")
                                intent.Recipients[recipient] = "sending-result-unknown";
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                        }
                        if (priorClaim == null)
                            priorClaim = new MailFormalStartClaim { FenceKey = fenceKey };
                        priorClaim.FenceKey = fenceKey;
                        priorClaim.Status = "sending-result-unknown";
                        priorClaim.UpdatedAtUtc = DateTime.UtcNow;
                        shards.SetClaim(record.HostId, shardKey, claimKey, priorClaim);
                        allowed.Add(RecordKey(record) + "|" + issueKey);
                    }
                    else
                    {
                        suppressedContributions.Add(PressureContributionKey(record, issue));
                        if (priorClaim == null)
                            priorClaim = new MailFormalStartClaim { FenceKey = fenceKey };
                        priorClaim.Status = "revoked";
                        priorClaim.UpdatedAtUtc = DateTime.UtcNow;
                        shards.SetClaim(record.HostId, shardKey, claimKey, priorClaim);
                        if (intent != null && recipientIssueKey != null)
                        {
                            intent.FormalIssueStates[recipientIssueKey] = "revoked";
                            intent.UpdatedAtUtc = DateTime.UtcNow;
                        }
                    }
                }
            }

            shards.Persist();
            var approvedRecords = records.Select(record => FilterFormalIssues(record, allowed, suppressedContributions)).ToList();
            var contributionsByHostAndPair = records.SelectMany(record => record.TopIssues.Select(issue => new
                {
                    Key = PressureHostPairKey(record.HostId, issue),
                    Contribution = PressureContributionKey(record, issue)
                }))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Contribution).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var suppressedPairs = contributionsByHostAndPair
                .Where(group => group.Value.Length > 0 && group.Value.All(suppressedContributions.Contains))
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (lane == "urgent" && urgentIntentKey != null && view.Detail != null)
            {
                foreach (var original in view.Detail)
                {
                    var approved = approvedRecords.FirstOrDefault(record => RecordKey(record) == RecordKey(original));
                    if (approved == null || approved.TopIssues.Count != 0 ||
                        !original.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)) continue;
                    if (state.UrgentOutbox.TryGetValue(urgentIntentKey(original), out var intent) &&
                        intent.Recipients.GetValueOrDefault(recipient) != "smtp-accepted")
                    {
                        intent.Recipients[recipient] = "cancelled-formal-revoked";
                        intent.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
            }
            return new FormalStartSnapshot(approvedRecords, suppressedPairs);
            });
        }
        catch (Exception ex)
        {
            // A failed durable formal-claim transaction fails closed for the formal subset only.
            // Keep unrelated NetIQ/PRTG pressure content, and never report SMTP acceptance for a claim we did not commit.
            Log.Warn(ex, "[Mail] recipient {0}: formal claim transaction failed; suppressing formal pressure findings for this recipient.", recipient);
            var suppressed = records.SelectMany(record => record.TopIssues
                    .Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)
                    .Select(issue => PressureContributionKey(record, issue)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var approved = records.Select(record => FilterFormalIssues(record, allowed, suppressed)).ToList();
            var suppressedPairs = records.SelectMany(record => record.TopIssues.Select(issue => new
                {
                    Key = PressureHostPairKey(record.HostId, issue),
                    Contribution = PressureContributionKey(record, issue)
                }))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.All(item => suppressed.Contains(item.Contribution)))
                .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            authorization = new FormalStartSnapshot(approved, suppressedPairs);
        }

        var approvedByKey = authorization.Records.ToDictionary(RecordKey, StringComparer.Ordinal);
        List<DailyAnalysisRecord>? detail = null;
        if (view.Detail != null)
        {
            detail = view.Detail.Where(record => approvedByKey.ContainsKey(RecordKey(record)))
                .Select(record => approvedByKey[RecordKey(record)])
                .Where(record => lane != "urgent" || QualifiesForUrgentRisk(record))
                .Where(record => record.TopIssues.Count > 0 ||
                    !view.Detail.Any(original => RecordKey(original) == RecordKey(record) &&
                        original.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)))
                .ToList();
            if (view.Detail.Count > 0 && detail.Count == 0) return null;
        }
        return view with { Detail = detail, ApprovedRecords = authorization.Records,
            SuppressedPressurePairs = authorization.SuppressedPairs };
    }

    private static DailyAnalysisRecord FilterFormalIssues(DailyAnalysisRecord record,
        HashSet<string> allowed, HashSet<string> suppressedContributions)
    {
        var invalidPressure = record.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)
            .Where(issue => !allowed.Contains(RecordKey(record) + "|" + FormalIssueKey(issue))).ToArray();
        if (invalidPressure.Length == 0) return record;

        foreach (var issue in invalidPressure) suppressedContributions.Add(PressureContributionKey(record, issue));
        var copy = JsonSerializer.Deserialize<DailyAnalysisRecord>(JsonSerializer.Serialize(record))!;
        copy.RecordId = record.RecordId;
        copy.TopIssues = copy.TopIssues.Where(issue => !PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(issue) ||
            allowed.Contains(RecordKey(record) + "|" + FormalIssueKey(issue))).ToList();
        var baselineRisk = record.PrtgBaselineRiskLevel ?? RiskLevels.Low;
        copy.RiskLevel = RiskLevels.MoreSevere(baselineRisk, PrtgFindingMapper.RiskFromFindings(copy.TopIssues));
        copy.RiskBasis = copy.RiskLevel == baselineRisk
            ? record.PrtgBaselineRiskBasis
            : PrtgFindingMapper.RiskBasisFrom(copy.TopIssues);
        return copy;
    }

    private static string PressurePair(LogIssueSignature issue) =>
        $"{IssueProfile.KeyOf(issue.Source, issue.EventId).SourceUpper}|{issue.EventId}";

    private static string PressureContributionKey(DailyAnalysisRecord record, LogIssueSignature issue) =>
        $"{record.HostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{record.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{PressurePair(issue)}\0{FormalIssueKey(issue)}";

    private static string PressureHostPairKey(long hostId, LogIssueSignature issue) =>
        $"{hostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{PressurePair(issue)}";

    private List<DailyAnalysisRecord> ReadFormalMailScopeRecords(DateTime from, DateTime to) =>
        _records.Query(new RecordQueryFilter { Hosts = null, From = from, To = to })
            .Where(record => record.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue))
            .ToList();

    private sealed record FormalStartSnapshot(List<DailyAnalysisRecord> Records, HashSet<string> SuppressedPairs);

    private static string UrgentRecordKey(DailyAnalysisRecord record,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions = null,
        IReadOnlyDictionary<string, RecipientView>? views = null, string? mailContextFingerprint = null)
    {
        var facts = PrtgUrgentFacts(record);
        var parentVersion = WorkflowRecordKey("urgent", record, versions, views, mailContextFingerprint);
        if (facts.Count == 0) return parentVersion;
        var value = string.Join(";", facts.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}:{f.Value}"));
        var modeVersion = record.TopIssues.Any(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue)
            ? record.PrtgManifest?.ResourceModeBlobVersion ?? -1 : -1;
        return parentVersion + (modeVersion >= 0 ? $"|mode:{modeVersion}" : string.Empty) +
            "|prtg:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>涵蓋此 record 的收件人全部成功才標記；coverage 為空（沒有任何收件人可見這台
    /// 主機的明細）的 record 隨「本輪至少一封信寄成功」一併標記，否則會永遠卡在 pending
    /// 每輪重算白做工——統計行已如實反映它的存在（回饋十七輪批次B-4）。</summary>
    private void MarkSent(SystemSettings settings, List<DailyAnalysisRecord> records,
        Dictionary<string, bool> success, Dictionary<string, List<string>> coverage,
        Func<MailNotifyState, HashSet<string>> keys, Func<DailyAnalysisRecord, string>? recordKey = null, bool allowZeroCoverage = true)
    {
        var anySuccess = success.Values.Any(ok => ok);
        _state.Update(s =>
        {
            var set = keys(s);
            foreach (var record in records)
            {
                var key = (recordKey ?? RecordKey)(record);
                var coveredBy = coverage.TryGetValue(key, out var emails) ? emails : new List<string>();
                var allCoveredSucceeded = coveredBy.Count > 0 && coveredBy.All(e => success.TryGetValue(e, out var ok) && ok);
                if (allCoveredSucceeded || (allowZeroCoverage && coveredBy.Count == 0 && anySuccess))
                {
                    set.Add(key);
                }
            }
            var cutoff = DateTime.Today.AddDays(-settings.RetentionDays);
            set.RemoveWhere(key => IsBeforeCutoff(key, cutoff));
        });
    }

    /// <summary>
    /// 逐收件人寄送的共用骨架（執行摘要／高風險即時／每日週報三路共用）：per-record coverage
    /// 計算、本輪連續失敗熔斷、跨輪失敗 streak 累計（回饋十七輪批次B-1）、取消例外處理，
    /// 全部集中在這裡，避免三處各自維護一份而漂移。
    ///
    /// <paramref name="onComplete"/> 在 <c>finally</c> 裡執行（回饋十六輪體檢發現2a 的根修，
    /// 十七輪重構時的迴歸修復）：取消例外會讓迴圈中的 await 直接拋出，若標記邏輯寫在
    /// 「await 這個方法後才執行」，取消一發生就會整批漏標——已寄成的部分也白算。
    /// 呼叫端把 MarkSent 包成這個 callback，確保無論正常回傳或例外中斷都會執行到。
    /// </summary>
    private async Task<(Dictionary<string, bool> Success, Dictionary<string, List<string>> Coverage)> SendPerRecipientAsync(
        SystemSettings settings, List<string> order, Dictionary<string, RecipientView> views,
        Func<RecipientView, (string Subject, string Body)> buildMessage,
        Func<DailyAnalysisRecord, string> recordKey, CancellationToken ct,
        Action<Dictionary<string, bool>, Dictionary<string, List<string>>>? onComplete = null,
        Func<string, RecipientView, RecipientView?>? recheck = null, Action<string, bool>? onRecipientResult = null,
        Action<string, List<DailyAnalysisRecord>?>? onRecipientStarting = null,
        Func<string, RecipientView, RecipientView?>? authorizeStart = null)
    {
        // recordKey → 涵蓋到它明細的收件人清單（只有看得到明細的收件人才算涵蓋；純統計信
        // 不涵蓋任何一筆特定 record，見呼叫端對 coverage 為空的處理）
        var coverage = new Dictionary<string, List<string>>();
        foreach (var (email, view) in views)
        {
            if (view.Detail == null) continue;
            foreach (var record in view.Detail)
            {
                var key = recordKey(record);
                if (!coverage.TryGetValue(key, out var emails))
                {
                    emails = new List<string>();
                    coverage[key] = emails;
                }
                emails.Add(email);
            }
        }

        // Keep the evaluated authorization/content view for each recipient even if a prior recipient recheck updates the live plan.
        var evaluatedViews = views.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var recipientSuccess = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var consecutiveFailures = 0;
        var circuitBroken = false;
        var streakDeltas = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // true=成功(歸零) false=失敗(+1)

        try
        {
            foreach (var email in order)
            {
                ct.ThrowIfCancellationRequested();
                if (circuitBroken)
                {
                    recipientSuccess[email] = false;
                    continue;
                }

                var checkedView = recheck == null ? evaluatedViews[email] : recheck(email, evaluatedViews[email]);
                if (checkedView == null)
                {
                    recipientSuccess[email] = false;
                    continue;
                }
                // Recovery can quarantine an oversized or malformed authoritative row after the
                // recipient snapshot/recheck. Do not send stale success while recovery waits.
                if (_workflow != null && checkedView.Detail?.Any(record =>
                        _workflow.IsRecoveryWaiting(record.HostId, record.Date, record.RecordId)) == true)
                    continue;
                checkedView = authorizeStart == null ? checkedView : authorizeStart(email, checkedView);
                if (checkedView == null)
                {
                    recipientSuccess[email] = false;
                    continue;
                }
                // Recheck and formal-start authorization both narrow what this recipient may see.
                if (recheck != null || authorizeStart != null)
                {
                    var keysNow = (checkedView.Detail ?? new()).Select(recordKey).ToHashSet(StringComparer.Ordinal);
                    foreach (var pair in coverage.Where(p => p.Value.Contains(email) && !keysNow.Contains(p.Key)))
                        pair.Value.Remove(email);
                }
                var (mailSubject, body) = buildMessage(checkedView);
                onRecipientStarting?.Invoke(email, checkedView.Detail);
                var success = await SendSafeAsync(settings, new List<string> { email }, mailSubject, body, ct);
                recipientSuccess[email] = success;
                onRecipientResult?.Invoke(email, success);
                streakDeltas[email] = success;

                if (success)
                {
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= CircuitBreakerThreshold)
                {
                    circuitBroken = true;
                    Log.Warn("[Mail] 連續 {N} 封通知寄送失敗，本輪停止剩餘寄送（尚有 {Remaining} 位收件人未寄）。",
                        CircuitBreakerThreshold, order.Count - recipientSuccess.Count);
                }
            }
        }
        finally
        {
            // 跨輪失敗 streak（回饋十七輪批次B-1）：熔斷跳過的收件人不在 streakDeltas 裡，
            // 不計入——熔斷是「本輪沒嘗試」，不是這個收件人本身的問題
            if (streakDeltas.Count > 0)
            {
                _state.Update(s =>
                {
                    foreach (var (email, succeeded) in streakDeltas)
                    {
                        var key = email.ToLowerInvariant();
                        if (succeeded)
                        {
                            s.RecipientFailureStreaks.Remove(key);
                        }
                        else
                        {
                            s.RecipientFailureStreaks[key] = s.RecipientFailureStreaks.GetValueOrDefault(key) + 1;
                        }
                    }
                });
            }

            // 無論正常回傳或例外中斷（取消）都要執行——中斷前已寄成的部分仍要落地標記
            onComplete?.Invoke(recipientSuccess, coverage);
        }

        return (recipientSuccess, coverage);
    }

    /// <summary>
    /// 統計行永遠開頭，問題優先區塊（若可判定可見範圍）接在後面（回饋十九輪批次H2，取代舊版
    /// 逐主機日一行的明細）：對應不到帳號的收件人只看得到統計行，<paramref name="issueRows"/>
    /// 為 null（同 <see cref="RecipientView.VisibleHostIds"/> 為 null 的情境）時不附任何明細
    /// （沿用回饋十七輪批次B-4 的既有原則）。主機日逐日明細不再列出，改成一行站台連結——
    /// 問題優先區塊已經回答「該看哪個問題」，逐日列表留給站台的問題查詢頁展開。
    /// </summary>
    private string BuildStatsAndDetailBody(SystemSettings settings, string statsLine, List<MailIssueRow>? issueRows)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(statsLine);

        if (issueRows == null) return body.ToString();

        body.AppendLine();
        AppendIssueRows(body, issueRows, SummaryBodyLineLimit);
        body.AppendLine();
        body.AppendLine("如需查看逐主機逐日明細，請至站台的問題查詢頁。");
        return body.ToString();
    }

    /// <summary>每日／週報彙總（回饋十九輪批次H2，取代舊版逐主機一行的「高風險 N 天、中風險 M 天」
    /// 彙整）：內容整段改為問題優先區塊——窗口動輒涵蓋 7 天，「這個問題新出現／擴散中／逾期」
    /// 比「哪些主機累計了幾天」更能回答「這週該先處理哪個」。<paramref name="issueRows"/> 為 null
    /// 時（同 <see cref="BuildStatsAndDetailBody"/>）不附任何明細。</summary>
    private string BuildDigestBody(SystemSettings settings, string windowText, List<MailIssueRow>? issueRows)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(windowText);

        if (issueRows == null) return body.ToString();

        body.AppendLine();
        AppendIssueRows(body, issueRows, SummaryBodyLineLimit);
        return body.ToString();
    }

    /// <summary>
    /// 高風險即時通知（回饋十九輪批次H2）：主要內容改為問題優先區塊（<paramref name="issueRows"/>），
    /// 逐主機日一行的既有明細降級為附錄——兩者互補不是重複：問題優先答「哪些問題」，可能把同一
    /// 問題跨多台合併成一行；附錄仍逐一點名觸發的主機日，答「哪台、哪一天」。
    /// <paramref name="globalStatsLine"/> 是未經可見範圍過濾的全站聚合統計行，每位收件人共用
    /// 同一句（見呼叫端的說明）；<paramref name="detail"/> 才是該收件人自己可見範圍內的主機明細，
    /// null 或空清單都只附全站統計行、不附任何明細或問題優先區塊。
    /// </summary>
    private (string Subject, string Body) BuildUrgentMessage(
        SystemSettings settings, string globalStatsLine, List<DailyAnalysisRecord>? detail,
        List<MailIssueRow>? issueRows, MailContext ctx)
    {
        var dateText = DateTime.Today.ToString("yyyy-MM-dd");
        var records = detail ?? new List<DailyAnalysisRecord>();

        if (records.Count == 0)
        {
            // 對應不到帳號、或可見範圍不含本輪任何一筆的收件人：只給全站統計行，不含主機明細
            var subj = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, RiskLevels.High, "高風險即時通知", globalStatsLine);
            var b = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) b.AppendLine(settings.MailBodyIntro).AppendLine();
            b.AppendLine($"{globalStatsLine}，詳情請至站台檢視。");
            return (subj, b.ToString());
        }

        var ordered = records.OrderByDescending(r => r.Date).ThenBy(r => ResolveHostDisplayName(r, ctx)).ToList();
        var hostLabel = ordered.Count == 1 ? ResolveHostDisplayName(ordered[0], ctx) : $"{ordered.Count} 台主機";
        var subject = ExpandTemplate(settings.MailSubjectTemplate, hostLabel, dateText, RiskLevels.High, "高風險即時通知", globalStatsLine);

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(globalStatsLine);
        body.AppendLine();

        if (issueRows != null)
        {
            AppendIssueRows(body, issueRows, UrgentBodyLineLimit);
            body.AppendLine();
        }

        // 主機日附錄（規劃原文「逐問題行＋主機日附錄」，上限沿用既有的 UrgentBodyLineLimit）
        body.AppendLine(ordered.Count == 1
            ? $"主機日附錄：{ResolveHostDisplayName(ordered[0], ctx)} 於 {ordered[0].Date:yyyy-MM-dd} 判定為高風險日。"
            : "主機日附錄（本次執行新增以下您可見範圍內的高風險主機日）：");
        body.AppendLine();

        foreach (var record in ordered.Take(UrgentBodyLineLimit))
        {
            body.AppendLine($"  - {ResolveHostDisplayName(record, ctx)}　{record.Date:yyyy-MM-dd}");
        }
        if (ordered.Count > UrgentBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {ordered.Count - UrgentBodyLineLimit} 筆請至站台的問題查詢頁檢視。");
        }

        // 判定依據（RiskBasis）不再附上——內容廣泛化（回饋十七輪批次B-3）

        return (subject, body.ToString());
    }

    /// <summary>
    /// 問題優先區塊的共用格式（回饋十九輪批次H2，三種信共用同一份措辭與排序規則）：
    /// 逾期 &gt; 新出現 &gt; 擴散中 &gt; 其他高風險（同 <see cref="MailIssueDigest.Build"/> 的分區
    /// 優先序），同區內依影響主機數由大到小——這是使用者判斷「這個問題有多急」的第一手訊號。
    /// 空清單時明講「目前沒有」，不留空白讓人誤以為漏印或信件壞掉。<paramref name="limit"/>
    /// 語意由舊版「主機日行數上限」改為「問題行數上限」，數值沿用（規劃原文明定）。
    /// </summary>
    private static void AppendIssueRows(StringBuilder body, List<MailIssueRow> rows, int limit)
    {
        if (rows.Count == 0)
        {
            body.AppendLine("目前沒有需要優先處理的問題。");
            return;
        }

        var ordered = rows.OrderBy(r => IssueBucketRank(r.Bucket)).ThenByDescending(r => r.HostCount).ToList();
        foreach (var row in ordered.Take(limit))
        {
            body.AppendLine($"  - {row.FormatLine()}");
        }
        if (ordered.Count > limit)
        {
            body.AppendLine($"  ……其餘 {ordered.Count - limit} 個問題請至站台的問題查詢頁檢視。");
        }
    }

    private static int IssueBucketRank(string bucket) => bucket switch
    {
        MailIssueBucket.Overdue => 0,
        MailIssueBucket.New => 1,
        MailIssueBucket.Spreading => 2,
        _ => 3
    };

    /// <summary>
    /// H3：同一批次內，相同可見範圍集合的收件人共用同一次 <see cref="MailIssueDigest.Build"/>
    /// 查詢結果——不是常駐快取，<paramref name="cache"/> 由每個 SendXxxAsync 各自在方法開頭
    /// 新建、隨方法結束捨棄，只避免同一輪批次內對同一個 visibleHostIds 集合重複跑 GROUP BY
    /// （2000 台環境下，多位收件人共用同一組部門群組授權時完全同一個集合是常見情況）。
    /// 鍵用排序後的 id 序列組字串——集合雜湊的簡化版，批次內收件人數量級（十幾到數十人）
    /// 用字串鍵的字典查找已經足夠快，不需要真正的雜湊函式。
    /// null（無法判定可見範圍）與 _issueDigest 未注入（測試／功能未啟用）都回 null，
    /// 呼叫端據此整段略過問題優先區塊，不是顯示「查無問題」。
    /// </summary>
    private List<MailIssueRow>? BuildIssueRowsCached(
        Dictionary<string, List<MailIssueRow>> cache, DateTime from, DateTime to, IReadOnlySet<long>? visibleHostIds,
        HashSet<string>? suppressedPressurePairs = null)
    {
        if (_issueDigest == null || visibleHostIds == null) return null;

        var key = (visibleHostIds.Count == 0 ? "∅" : string.Join(",", visibleHostIds.OrderBy(x => x))) + "|" +
            string.Join(",", (suppressedPressurePairs ?? new HashSet<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (cache.TryGetValue(key, out var cached)) return cached;

        var rows = _issueDigest.Build(from, to, visibleHostIds);
        if (suppressedPressurePairs is { Count: > 0 })
        {
            var suppressedByPair = suppressedPressurePairs.Select(key => key.Split('\0', 2))
                .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
                .GroupBy(parts => parts[1], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(parts => long.Parse(parts[0],
                    System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(), StringComparer.OrdinalIgnoreCase);
            rows = rows.Select(row =>
                {
                    var pair = $"{IssueProfile.KeyOf(row.Source, row.EventId).SourceUpper}|{row.EventId}";
                    if (!suppressedByPair.TryGetValue(pair, out var hiddenHosts)) return row;
                    var visibleCount = Math.Max(0, row.HostCount - hiddenHosts.Count);
                    var bucket = row.Bucket;
                    if (bucket == MailIssueBucket.Spreading && visibleCount <= row.PreviousHostCount)
                        bucket = row.MaxSeverityRank >= (int)IssueSeverity.High
                            ? MailIssueBucket.OtherHighRisk : string.Empty;
                    return row with { HostCount = visibleCount, Bucket = bucket };
                })
                .Where(row => row.HostCount > 0 && row.Bucket.Length > 0)
                .ToList();
        }
        cache[key] = rows;
        return rows;
    }

    private async Task SendDigestAsync(SystemSettings settings, DateTime now, int windowDays, bool isWeekly, CancellationToken ct,
        IReadOnlyCollection<DailyAnalysisRecord>? formalScopeRecords = null)
    {
        // 窗口右移一天（回饋十七輪批次A-3）：分析永遠只產出到昨天，每日摘要的語意本來就是
        // 「昨天發生了什麼」；週報＝近七個完整日（昨天往回 7 天）。
        var to = now.Date.AddDays(-1);
        var from = to.AddDays(-(windowDays - 1));
        // RiskLevels 下推（回饋十八輪批次A-3，與 NotifyAfterRunAsync 同一手法）：記憶體 Rank 過濾
        // 保留（雙保險，語意不變），下推只是收窄 DB 端回傳的列數。
        var records = _records.Query(new RecordQueryFilter
        {
            Hosts = null, From = from, To = to, RiskLevels = RiskLevels.AtOrAbove(settings.MailMinRiskLevel)
        });
        var minRank = RiskLevels.Rank(settings.MailMinRiskLevel);
        var qualifying = records.Where(r => RiskLevels.Rank(r.RiskLevel) >= minRank).ToList();

        // 無事時不寄（回饋十七輪批次A-4）：預設 false（照寄）——期間內無事同時是系統存活訊號
        if (qualifying.Count == 0 && settings.MailDigestSkipEmpty) return;

        var ctx = BuildContext();
        var (order, views) = ResolvePerRecipient(settings, qualifying, ctx);
        if (order.Count == 0) return;

        var type = isWeekly ? "週報" : "每日摘要";
        var dateText = now.ToString("yyyy-MM-dd");

        // 週報附未處理數（docs/archive/FEEDBACK-15-PLAN.md D-3）：不限窗口——「還沒處理完」是當下狀態，
        // 一個上上週就掛著沒人動的風險日正是週報最該提醒的東西，只看近 7 天反而幫忙藏爛帳。
        // 這是全站統計，不受可見範圍過濾（同 statsLine 的粒度），所有收件人一律看得到。
        string? unresolvedLine = null;
        if (isWeekly)
        {
            var unresolved = _handlings.GetUnresolved();
            unresolvedLine = unresolved.Count == 0
                ? "目前沒有未處理（含處理中）的風險日。"
                : $"目前未處理（含處理中）的風險日共 {unresolved.Count} 筆，請至站台的問題查詢頁檢視。";
        }

        // 週報附目前靜音中的問題（回饋第 47 輪 E-1）：不限窗口——「目前靜音中」是當下狀態。
        // 這是全站資訊，不受可見範圍過濾（同 unresolvedLine 的既有取捨，所有收件人一律看得到）。
        // _issueOwners 為既有可選相依，null 表示測試未注入或未啟用。
        string? mutedSection = null;
        if (isWeekly && _issueOwners != null)
        {
            var muted = _issueOwners.GetAll()
                .Select(p => (Profile: p, Mute: IssueProfile.CurrentMute(p, now.Date)))
                .Where(x => x.Mute != null)
                .OrderBy(x => x.Profile.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Profile.EventId)
                .ToList();

            if (muted.Count > 0)
            {
                var lines = new List<string> { $"目前靜音中的問題（共 {muted.Count} 個，到期後恢復告警）：" };
                lines.AddRange(muted.Select(x =>
                    $"  {x.Profile.SourceName}/{x.Profile.EventId}：靜音至 {x.Mute!.To:yyyy-MM-dd}｜原因：{x.Mute.Reason}｜設定者：{x.Mute.ByAccount}"));
                mutedSection = string.Join(Environment.NewLine, lines);
            }
        }

        // 排程資料過期警示（任務 A-3）
        var freshness = _freshness.GetScheduleFreshness(now);
        string? freshnessAlert = null;
        if (freshness.Stale && !freshness.Acked)
        {
            var state = _state.Get();
            var today = now.ToString("yyyy-MM-dd");
            var shouldAlert = false;

            if (!state.FreshnessAlertActive)
            {
                shouldAlert = true;
                _state.Update(s =>
                {
                    s.FreshnessAlertActive = true;
                    s.LastFreshnessAlertDate = today;
                });
            }
            else
            {
                var daysDiff = DateTime.TryParse(state.LastFreshnessAlertDate, out var lastDate)
                    ? (now.Date - lastDate.Date).TotalDays
                    : 7;
                if (daysDiff >= 7)
                {
                    shouldAlert = true;
                    _state.Update(s =>
                    {
                        s.LastFreshnessAlertDate = today;
                    });
                }
            }

            if (shouldAlert)
            {
                var lastSuccessText = freshness.LastSuccessAt.HasValue
                    ? freshness.LastSuccessAt.Value.ToString("yyyy-MM-dd HH:mm")
                    : "近 14 天沒有紀錄";
                freshnessAlert = $"⚠ 排程資料已超過 48 小時沒有成功更新（最近一次成功：{lastSuccessText}）。以下摘要可能不完整，請系統管理員至站台「排程作業」頁檢查。";
            }
        }
        else
        {
            var state = _state.Get();
            if (state.FreshnessAlertActive)
            {
                _state.Update(s => s.FreshnessAlertActive = false);
            }
        }

        var issueRowsCache = new Dictionary<string, List<MailIssueRow>>();
        var digestKeys = qualifying.Select(RecordKey).ToHashSet(StringComparer.Ordinal);
        var formalRecords = qualifying.Concat(formalScopeRecords ?? ReadFormalMailScopeRecords(from, to))
            .GroupBy(RecordKey, StringComparer.Ordinal).Select(group => group.Last()).ToList();
        var digestLane = (isWeekly ? "weekly-digest:" : "daily-digest:") + now.ToString("yyyyMMdd");
        var attemptedDigestDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        var attemptedFormalDigestDetails = new Dictionary<string, List<DailyAnalysisRecord>>(StringComparer.OrdinalIgnoreCase);
        (string Subject, string Body) BuildMessage(RecipientView view)
        {
            var count = (view.ApprovedRecords ?? qualifying).Count(record => digestKeys.Contains(RecordKey(record)) &&
                RiskLevels.Rank(record.RiskLevel) >= RiskLevels.Rank(settings.MailMinRiskLevel));
            var statsLine = count == 0
                ? "期間內無達門檻的風險日"
                : $"{count} 個主機日達 {settings.MailMinRiskLevel} 風險以上";
            var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, settings.MailMinRiskLevel, type, statsLine);
            var windowText = $"{type}（{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}）：{statsLine}";
            var issueRows = BuildIssueRowsCached(issueRowsCache, from, to, view.VisibleHostIds, view.SuppressedPressurePairs);
            var body = new StringBuilder();
            if (freshnessAlert != null) body.AppendLine(freshnessAlert).AppendLine();
            body.Append(BuildDigestBody(settings, windowText, issueRows));
            if (unresolvedLine != null) body.AppendLine().AppendLine(unresolvedLine);
            if (mutedSection != null) body.AppendLine().AppendLine(mutedSection);
            return (subject, body.ToString());
        }

        // 每日／週報沒有 UrgentSentKeys 一類的逐筆去重狀態（同一天只寄一次靠
        // LastDailySentDate／LastWeeklySentDate），所以不需要 coverage 標記——只借用
        // SendPerRecipientAsync 的熔斷與跨輪失敗 streak 追蹤（回饋十七輪批次B-1 對三路一視同仁）。
        await SendPerRecipientAsync(settings, order, views, BuildMessage, RecordKey, ct,
            onRecipientStarting: (email, detail) =>
            {
                attemptedDigestDetails[email] = detail ?? qualifying;
                attemptedFormalDigestDetails[email] = detail ?? [];
            },
            onRecipientResult: (email, accepted) => CompleteFormalStartClaims(digestLane, email,
                attemptedFormalDigestDetails.GetValueOrDefault(email) ?? [], accepted),
            authorizeStart: (email, view) => AuthorizeFormalStart(email, view, digestLane, formalRecords));
    }

    // ── 內部：共用工具 ────────────────────────────────────────────────────

    private static string ResolveHostDisplayName(DailyAnalysisRecord record, MailContext ctx) =>
        ctx.Host(record.HostId)?.DisplayName is { Length: > 0 } display ? $"{record.Host}（{display}）" : record.Host;

    private static string RecordKey(DailyAnalysisRecord record) => $"{record.HostId}|{record.Date:yyyy-MM-dd}";

    private string MailContextFingerprint(SystemSettings settings)
    {
        var policyRevision = _prtgMonitoring?.Get().Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        var scopeRevision = _prtgBackend == null ? "none" : new PrtgScopeRevisionReader(_prtgBackend, _hosts).Read();
        var messageSettings = JsonSerializer.Serialize(new
        {
            settings.MailSubjectTemplate, settings.MailBodyIntro, settings.MailMinRiskLevel, settings.MailNotifyHostOwners
        });
        return HostDayWorkflowFingerprint.HashParts([messageSettings, policyRevision, scopeRevision]);
    }

    private static string WorkflowRecordKey(string lane, DailyAnalysisRecord record,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions, IReadOnlyDictionary<string, RecipientView>? views = null,
        string? mailContextFingerprint = null)
    {
        var key = RecordKey(record);
        if (versions == null || !versions.TryGetValue(key, out var version) || version.ParentRecordId != record.RecordId) return key;
        var content = WorkflowMailIntent(lane, record, version, mailContextFingerprint);
        var recipients = views == null ? string.Empty : string.Join("|", views
            .Where(view => view.Value.Detail == null || view.Value.Detail.Any(item => RecordKey(item) == key))
            .Select(view => view.Key.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        var route = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipients)));
        return $"{key}|parent:{version.ParentRecordId}|decision:{version.DecisionVersion}|content:{content}|route:{route}|{lane}";
    }

    private static string WorkflowMailIntent(string kind, DailyAnalysisRecord record, HostDayWorkflowVersion version,
        string? mailContextFingerprint)
    {
        var fingerprint = HostDayWorkflowFingerprint.HashParts([
            kind, record.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version.ParentRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version.DecisionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HostDayWorkflowFingerprint.ForRecord(record), PrtgFindingMapper.Fingerprint(record.TopIssues), mailContextFingerprint ?? "legacy-context"]);
        return $"{kind}:{fingerprint}";
    }

    private static string WorkflowMailPart(string intent, string normalizedRecipient) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(intent + "|recipient|" + normalizedRecipient)));

    private bool IsWorkflowMailPartAccepted(string kind, string recipient, DailyAnalysisRecord record,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions, string? mailContextFingerprint)
    {
        var key = RecordKey(record);
        if (_workflow == null || versions == null || !versions.TryGetValue(key, out var version) || version.ParentRecordId != record.RecordId)
            return false;
        var recipientHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipient.Trim().ToLowerInvariant())));
        var intent = WorkflowMailIntent(kind, record, version, mailContextFingerprint);
        return _workflow.HasAcceptedMailPart(record.HostId, record.Date, version, intent,
            WorkflowMailPart(intent, recipientHash));
    }

    private bool CurrentWorkflowVersion(DailyAnalysisRecord record,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions)
    {
        if (versions == null) return true;
        var key = RecordKey(record);
        if (_workflow == null || !versions.TryGetValue(key, out var expected) || expected.ParentRecordId != record.RecordId) return false;
        return _workflow.CaptureDeliveryVersion(record.HostId, record.Date, record.RecordId) == expected;
    }

    private static bool SameWorkflowMailContent(string lane, DailyAnalysisRecord left, DailyAnalysisRecord right,
        IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions, string? mailContextFingerprint)
    {
        if (versions == null) return true;
        var key = RecordKey(left);
        return RecordKey(right) == key && versions.TryGetValue(key, out var version) &&
            version.ParentRecordId == left.RecordId && version.ParentRecordId == right.RecordId &&
            WorkflowMailIntent(lane, left, version, mailContextFingerprint) == WorkflowMailIntent(lane, right, version, mailContextFingerprint);
    }

    private List<DailyAnalysisRecord> FilterUnresolvedWorkflowMail(string kind, string recipient,
        RecipientView view, IReadOnlyDictionary<string, HostDayWorkflowVersion>? versions, string? mailContextFingerprint)
    {
        var candidates = view.Detail ?? new List<DailyAnalysisRecord>();
        return candidates.Where(record => !IsWorkflowMailPartAccepted(kind, recipient, record, versions, mailContextFingerprint)).ToList();
    }

    private void PlanWorkflowMail(string kind, List<DailyAnalysisRecord> records,
        IReadOnlyDictionary<string, RecipientView> views, IReadOnlyDictionary<string, HostDayWorkflowVersion>? mailVersions,
        string? mailContextFingerprint)
    {
        if (_workflow == null || mailVersions == null) return;
        foreach (var record in records)
        {
            var key = RecordKey(record);
            if (!mailVersions.TryGetValue(key, out var version) || version.ParentRecordId != record.RecordId) continue;
            var intent = WorkflowMailIntent(kind, record, version, mailContextFingerprint);
            var expectedParts = views.Where(view => view.Value.Detail != null && view.Value.Detail.Any(item => RecordKey(item) == key))
                .Select(view => WorkflowMailPart(intent, Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(view.Key.Trim().ToLowerInvariant())))))
                .Distinct(StringComparer.Ordinal).ToArray();
            var expected = expectedParts.Length == 0
                ? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { [intent] = expectedParts };
            _workflow.ReplaceMailPlan(record.HostId, record.Date, kind, expected, version);
        }
    }

    private void TrackWorkflowMail(string kind, List<DailyAnalysisRecord>? records, string recipient, bool? accepted,
        IReadOnlyDictionary<string, RecipientView> views, IReadOnlyDictionary<string, HostDayWorkflowVersion>? mailVersions,
        string? mailContextFingerprint)
    {
        if (_workflow == null || records == null || records.Count == 0 || accepted == null) return;
        var recipientKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipient.Trim().ToLowerInvariant())));
        foreach (var record in records)
        {
            try
            {
                var key = RecordKey(record);
                if (mailVersions == null || !mailVersions.TryGetValue(key, out var version) || version.ParentRecordId != record.RecordId) continue;
                var intent = WorkflowMailIntent(kind, record, version, mailContextFingerprint);
                var part = WorkflowMailPart(intent, recipientKey);
                var expected = views.Where(view => view.Value.Detail != null && view.Value.Detail.Any(item => RecordKey(item) == key))
                    .Select(view => WorkflowMailPart(intent, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(view.Key.Trim().ToLowerInvariant())))))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (!expected.Contains(part, StringComparer.Ordinal)) continue;
                var expectedByIntent = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal) { [intent] = expected };
                _workflow.RecordMail(record.HostId, record.Date, [intent], expectedByIntent,
                    accepted == true ? [part] : [], accepted == false ? [part] : [], version);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "郵件 workflow tracking failed for {RecordKey}; notification outcome remains governed by mail sender", RecordKey(record));
            }
        }
    }

    private static bool IsBeforeCutoff(string key, DateTime cutoff)
    {
        var parts = key.Split('|');
        return parts.Length >= 2 && DateTime.TryParse(parts[1], out var date) && date < cutoff;
    }

    /// <summary>
    /// 寄送失敗只記 WARN、回 false，不往外拋——這是三路觸發共用的最後一道防線，呼叫端（排程輪詢／
    /// 執行收尾）不該因為一封信寄失敗而受影響。SmtpClient 逾時無獨立設定，共用 30 秒合理上限，
    /// relay 通常是內網，30 秒逾時仍未回應代表連線本身有問題，繼續等沒有意義。
    ///
    /// **回傳成敗**：<see cref="SendPerRecipientAsync"/> 需要依成敗決定要不要標記已寄與是否
    /// 累計失敗 streak。
    ///
    /// **外層真正的取消要求會重新拋出**（<c>ct.IsCancellationRequested</c>）：呼叫端（服務停止／
    /// 執行被取消）需要迴圈立即中斷，不能讓尚未寄出的收件人也被判定「處理過」。30 秒逾時觸發的
    /// 取消（外層 ct 本身未取消）視為單純寄送失敗，回 false 讓迴圈繼續處理下一位收件人。
    /// </summary>
    private async Task<bool> SendSafeAsync(SystemSettings settings, List<string> recipients, string subject, string body, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _sender.SendAsync(ResolveConnection(settings),
                new MailMessageSpec(settings.MailFrom, recipients, subject, body), timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 寄送失敗：{Subject}（收件人 {Count} 位）", subject, recipients.Count);
            return false;
        }
    }
}

/// <summary>
/// 問題上報通知的內容素材（回饋十八輪批次G，見 <see cref="MailNotificationService.NotifyEscalationAsync"/>）。
/// <paramref name="IssueLabel"/>＝問題顯示文字（「Source EventId」，與案件的反正規化快照同源）；
/// <paramref name="HostLabel"/>＝主機標籤（單台為主機名，多台為「N 台主機」）；
/// <paramref name="ActorAccount"/>＝回覆無法處理的人（由呼叫端自 ICurrentUser 取出傳入）；
/// <paramref name="Reason"/>＝上報原因（狀態變更時必填的說明）。
/// </summary>
public sealed record EscalationNotice(string IssueLabel, string HostLabel, string ActorAccount, string? Reason);

public static class WorkOrderNoticeKinds
{
    public const string Created = "created";
    public const string Transferred = "transferred";
    public const string Cancelled = "cancelled";
}

public sealed record WorkOrderNotice(
    string Kind, long WorkOrderId, string IssueLabel, string? PlainExplanation,
    int HostCount, IReadOnlyList<string> HostNames, string? Note, DateTime? DueDate,
    string RecipientAccount, string? RecipientEmail, string ActorAccount, string? Reason);

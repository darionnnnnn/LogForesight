using System.Text;
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

    /// <summary>問題負責人規則（回饋十八輪批次F）：郵件路由優先於主機負責人。
    /// 可為 null——測試組裝不注入時，路由靜默落回既有的「只通知主機負責人」行為。</summary>
    private readonly IIssueOwnerStore? _issueOwners;

    /// <summary>問題負責人授權路徑用（回饋十八輪體檢輪修正）：全域收件人若是問題負責人，
    /// 摘要信裡該看得到自己負責問題的主機明細——原本 GetVisibleHostIds(userId) 只聯集了
    /// 群組授權與主機負責人，漏了 VisibilityService.GetVisibleHostIdsFor 已經有的第三條路徑，
    /// 兩邊本該同步卻各自維護一份而漂移。可為 null，同 _issueOwners 的既有慣例。</summary>
    private readonly IIssueAggregateQuery? _issueAggregates;

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
        IIssueOwnerStore? issueOwners = null,
        IIssueAggregateQuery? issueAggregates = null)
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
        _issueOwners = issueOwners;
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
                var settings = _settingsStore.Get();
                if (!settings.MailEnabled) return;
                if (!settings.MailOnRunCompleted && !settings.MailUrgentEnabled) return;

                var to = DateTime.Today.AddDays(-1);
                var from = to.AddDays(-(NotifyLookbackDays - 1));
                // RiskLevels 下推（回饋十八輪批次A）：兩路門檻不同——摘要用 MailMinRiskLevel（可能低到
                // 中），緊急只要 High。取聯集下推、記憶體判定不變（見 SendRunSummaryAsync／
                // SendUrgentNotificationsAsync 內仍各自 Rank／== High 一次），高選擇度預篩大幅減少
                // DB 端反序列化的列數，語意零風險（下推只是收窄查詢範圍，不是最終判定）。
                var riskFilter = settings.MailOnRunCompleted
                    ? RiskLevels.AtOrAbove(settings.MailMinRiskLevel)
                    : new[] { RiskLevels.High };
                var records = _records.Query(new RecordQueryFilter { Hosts = null, From = from, To = to, RiskLevels = riskFilter });
                var ctx = BuildContext();

                if (settings.MailOnRunCompleted)
                {
                    await SendRunSummaryAsync(settings, records, ctx, ct);
                }

                if (settings.MailUrgentEnabled)
                {
                    await SendUrgentNotificationsAsync(settings, records, ctx, ct);
                }
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
    public async Task CheckAndSendDailyWeeklyAsync(DateTime now, CancellationToken ct = default)
    {
        try
        {
            // 回饋十五輪體檢批G：settings 讀取原本在 try 外，未受保護——這支方法緊接著在
            // SchedulerHostedService.TickAsync 的排程窗口判斷之前呼叫且沒有自己的 try/catch
            // （文件註解宣稱「內部自行 try/catch 到底...不需要額外保護」），一旦 Get() 拋例外，
            // 整個 TickAsync 連同下方排程窗口判斷都會被跳過，等於通知路徑間接卡住了排程觸發。
            var settings = _settingsStore.Get();
            if (!settings.MailEnabled) return;

            var today = now.ToString("yyyy-MM-dd");

            if (settings.MailDailyEnabled &&
                TimeSpan.TryParse(settings.MailDailyTime, out var dailyTime) &&
                now.TimeOfDay >= dailyTime &&
                _state.Get().LastDailySentDate != today)
            {
                await SendDigestAsync(settings, now, windowDays: 1, isWeekly: false, ct);
                _state.Update(s => s.LastDailySentDate = today);
            }

            if (settings.MailWeeklyEnabled &&
                Enum.TryParse<DayOfWeek>(settings.MailWeeklyDayOfWeek, ignoreCase: true, out var weeklyDay) &&
                now.DayOfWeek == weeklyDay &&
                TimeSpan.TryParse(settings.MailWeeklyTime, out var weeklyTime) &&
                now.TimeOfDay >= weeklyTime &&
                _state.Get().LastWeeklySentDate != today)
            {
                await SendDigestAsync(settings, now, windowDays: 7, isWeekly: true, ct);
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

    /// <summary>測試寄信（設定頁「測試寄信」鈕）：用表單目前值（可能還沒儲存），不落地任何狀態。</summary>
    public async Task SendTestAsync(SmtpConnectionSpec connection, string from, List<string> recipients,
        string subjectTemplate, string bodyIntro, CancellationToken ct = default)
    {
        var subject = ExpandTemplate(subjectTemplate, "測試", DateTime.Today.ToString("yyyy-MM-dd"), "-", "測試", "這是一封測試郵件");
        var bodyBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bodyIntro)) bodyBuilder.AppendLine(bodyIntro).AppendLine();
        bodyBuilder.AppendLine("這是 LogForesight 的測試郵件，收到即代表 SMTP 設定正確可用。");
        await _sender.SendAsync(connection, new MailMessageSpec(from, recipients, subject, bodyBuilder.ToString()), ct);
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

        var to = DateTime.Today.AddDays(-1);
        var from = to.AddDays(-(NotifyLookbackDays - 1));
        var keys = _records.ListHostDates(from, to)
            .Select(h => $"{h.HostId}|{h.Date:yyyy-MM-dd}")
            .ToList();
        if (keys.Count == 0) return;

        _state.Update(s =>
        {
            if (summary) foreach (var key in keys) s.SummarySentKeys.Add(key);
            if (urgent) foreach (var key in keys) s.UrgentSentKeys.Add(key);
        });
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
        IssueOwnerRule.IndexByKey(_issueOwners?.GetAll() ?? new List<IssueOwnerRule>()));

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
        string.IsNullOrEmpty(settings.SmtpPasswordEnc) ? null : CryptoHelper.Decrypt(settings.SmtpPasswordEnc));

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
    /// </summary>
    private sealed record RecipientView(List<DailyAnalysisRecord>? Detail);

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
        SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx)
    {
        var suspended = new HashSet<string>(GetSuspendedRecipients(), StringComparer.OrdinalIgnoreCase);

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
            if (account != null)
            {
                // 體檢輪抓到：GetVisibleHostIds 若直接寫在 Where 的 predicate 裡，
                // 每筆 record 都會重新呼叫一次（LINQ 對每個來源元素求值一次 predicate），
                // 對每位收件人重跑一次完整的 store 全表掃描，正是 B-2 想修掉的同一種 N+1。
                var visible = GetVisibleHostIds(account.UserId, settings.RetentionDays);
                detail = records.Where(r => visible.Contains(r.HostId)).ToList();
            }
            views[email] = new RecipientView(detail);
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
                        views[email] = new RecipientView(new List<DailyAnalysisRecord> { record });
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
    /// </summary>
    private static IReadOnlyList<long> RecordOwnerIds(DailyAnalysisRecord record, WebHost host, MailContext ctx)
    {
        var issueOwnerIds = record.TopIssues
            .SelectMany(issue => ctx.IssueOwnersByKey.TryGetValue(
                IssueOwnerRule.KeyOf(issue.Source, issue.EventId), out var owners) ? owners : Enumerable.Empty<long>())
            .Distinct()
            .ToList();

        return issueOwnerIds.Count > 0 ? issueOwnerIds : host.OwnerUserIds;
    }

    // ── 內部：組信 ────────────────────────────────────────────────────────

    private async Task SendRunSummaryAsync(SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx, CancellationToken ct)
    {
        var minRank = RiskLevels.Rank(settings.MailMinRiskLevel);
        var state = _state.Get();
        var qualifying = records
            .Where(r => RiskLevels.Rank(r.RiskLevel) >= minRank)
            .Where(r => !state.SummarySentKeys.Contains(RecordKey(r)))
            .ToList();
        if (qualifying.Count == 0) return;

        var (order, views) = ResolvePerRecipient(settings, qualifying, ctx);
        if (order.Count == 0) return;

        var statsLine = $"執行摘要：{qualifying.Count} 台主機達 {settings.MailMinRiskLevel} 風險以上";
        var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", DateTime.Today.ToString("yyyy-MM-dd"),
            settings.MailMinRiskLevel, "執行摘要", statsLine);

        (string Subject, string Body) BuildMessage(RecipientView view) =>
            (subject, BuildStatsAndDetailBody(settings, statsLine, view.Detail, ctx));

        // 標記放在 SendPerRecipientAsync 的 finally 裡執行（見其文件說明）：取消例外會讓
        // await 直接拋出、跳過這裡以下的程式碼，中斷前已寄成的部分仍要落地標記，
        // 不能靠「await 正常回傳後才標記」這種寫法——那樣取消一發生就整批漏標。
        await SendPerRecipientAsync(settings, order, views, BuildMessage, RecordKey, ct,
            onComplete: (success, coverage) => MarkSent(settings, qualifying, success, coverage, s => s.SummarySentKeys));
    }

    /// <summary>
    /// 高風險即時通知：**按收件人聚合**（回饋十六輪批次A-1）——一位收件人一次執行最多只收
    /// 一封信。回饋十七輪批次B-4 疊加可見範圍過濾：對應到帳號的收件人只看見自己可見範圍內的
    /// 主機，對應不到帳號的收件人只收統計行。
    /// </summary>
    private async Task SendUrgentNotificationsAsync(SystemSettings settings, List<DailyAnalysisRecord> records, MailContext ctx, CancellationToken ct)
    {
        var highRisk = records.Where(r => r.RiskLevel == RiskLevels.High).ToList();
        if (highRisk.Count == 0) return;

        var state = _state.Get();
        var pending = highRisk.Where(r => !state.UrgentSentKeys.Contains(RecordKey(r))).ToList();
        if (pending.Count == 0) return;

        var (order, views) = ResolvePerRecipient(settings, pending, ctx);

        // 沒人可收（全域清單空且無負責人 email，或全數因連續失敗被排除）：這批 pending 完全
        // 不標記，設定補齊後下次執行自動補寄——修掉「先啟用通知、後填收件人」那幾天永久漏寄的
        // 路徑（回饋十六輪體檢發現2b）
        if (order.Count == 0) return;

        // 全站聚合統計行（回饋十七輪體檢輪修正）：與 SendRunSummaryAsync 的 statsLine 同樣用
        // 未經可見範圍過濾的 pending.Count，每位收件人共用同一句，不因各自 Detail 的子集而縮水。
        // MarkSent 的 zero-coverage fallback（見其文件說明）前提是「coverage 為空的 record 仍會
        // 被統計行如實反映」——這個前提只有在統計行是全站聚合時才成立；先前這裡的統計行是用
        // 已過濾後的 view.Detail 現算，收件人看得到部分主機、看不到的那些主機就連統計數字都
        // 沒被提及，卻仍被標記成已通知，是真實的靜默漏寄。
        var globalStatsLine = $"本次執行共 {pending.Count} 筆高風險主機日達門檻";
        (string Subject, string Body) BuildMessage(RecipientView view) => BuildUrgentMessage(settings, globalStatsLine, view.Detail, ctx);

        // 標記語意：涵蓋此 record 的信全部寄成功才標記已寄（回饋十六輪體檢發現2a 的根修）——
        // 寧可讓已收到的人下次重複收到，也不漏寄。放在 SendPerRecipientAsync 的 finally 裡
        // 執行（見其文件說明）：取消例外中斷迴圈時，中斷前已寄成的部分仍要落地標記。
        await SendPerRecipientAsync(settings, order, views, BuildMessage, RecordKey, ct,
            onComplete: (success, coverage) => MarkSent(settings, pending, success, coverage, s => s.UrgentSentKeys));
    }

    /// <summary>涵蓋此 record 的收件人全部成功才標記；coverage 為空（沒有任何收件人可見這台
    /// 主機的明細）的 record 隨「本輪至少一封信寄成功」一併標記，否則會永遠卡在 pending
    /// 每輪重算白做工——統計行已如實反映它的存在（回饋十七輪批次B-4）。</summary>
    private void MarkSent(SystemSettings settings, List<DailyAnalysisRecord> records,
        Dictionary<string, bool> success, Dictionary<string, List<string>> coverage,
        Func<MailNotifyState, HashSet<string>> keys)
    {
        var anySuccess = success.Values.Any(ok => ok);
        _state.Update(s =>
        {
            var set = keys(s);
            foreach (var record in records)
            {
                var key = RecordKey(record);
                var coveredBy = coverage.TryGetValue(key, out var emails) ? emails : new List<string>();
                var allCoveredSucceeded = coveredBy.Count > 0 && coveredBy.All(e => success.TryGetValue(e, out var ok) && ok);
                if (allCoveredSucceeded || (coveredBy.Count == 0 && anySuccess))
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
        Action<Dictionary<string, bool>, Dictionary<string, List<string>>>? onComplete = null)
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

        var recipientSuccess = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var consecutiveFailures = 0;
        var circuitBroken = false;
        var streakDeltas = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // true=成功(歸零) false=失敗(+1)

        try
        {
            foreach (var email in order)
            {
                if (circuitBroken)
                {
                    recipientSuccess[email] = false;
                    continue;
                }

                var (mailSubject, body) = buildMessage(views[email]);
                var success = await SendSafeAsync(settings, new List<string> { email }, mailSubject, body, ct);
                recipientSuccess[email] = success;
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

    /// <summary>統計行永遠開頭，明細行（若有）接在後面——對應不到帳號的收件人只看得到統計行，
    /// view.Detail 為 null 時不附任何明細（回饋十七輪批次B-4）。</summary>
    private string BuildStatsAndDetailBody(SystemSettings settings, string statsLine, List<DailyAnalysisRecord>? detail, MailContext ctx)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(statsLine);

        if (detail == null) return body.ToString();

        body.AppendLine();
        var ordered = detail.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel)).ThenBy(r => r.Date).ToList();
        foreach (var record in ordered.Take(SummaryBodyLineLimit))
        {
            var hostName = ResolveHostDisplayName(record, ctx);
            // 內容廣泛化（回饋十七輪批次B-3，使用者回饋「其他 8」）：不含 Headline／RiskBasis，
            // 只留主機、日期、風險等級、錯誤／警告數量
            body.AppendLine($"  - [{record.RiskLevel}風險] {hostName}　{record.Date:yyyy-MM-dd}　錯誤 {record.ErrorCount}／警告 {record.WarningCount}");
        }
        if (ordered.Count > SummaryBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {ordered.Count - SummaryBodyLineLimit} 台請至站台的問題查詢頁檢視。");
        }
        return body.ToString();
    }

    /// <summary>每日／週報彙總的明細格式——與 <see cref="BuildStatsAndDetailBody"/>（逐主機日
    /// 一行）不同：這裡是**逐主機一行**，彙整窗口內的高／中風險天數（「host1：高風險 1 天、
    /// 中風險 1 天」），窗口動輒涵蓋 7 天、同一主機多天都達門檻時逐日列會太長。</summary>
    private string BuildDigestBody(SystemSettings settings, string windowText, List<DailyAnalysisRecord>? detail, MailContext ctx)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.MailBodyIntro)) body.AppendLine(settings.MailBodyIntro).AppendLine();
        body.AppendLine(windowText);

        if (detail == null) return body.ToString();

        body.AppendLine();
        var hostGroups = detail.GroupBy(r => r.HostId).ToList();
        foreach (var group in hostGroups.Take(SummaryBodyLineLimit))
        {
            var hostName = ResolveHostDisplayName(group.First(), ctx);
            var highCount = group.Count(r => r.RiskLevel == RiskLevels.High);
            var mediumCount = group.Count(r => r.RiskLevel == RiskLevels.Medium);
            body.AppendLine($"  - {hostName}：高風險 {highCount} 天、中風險 {mediumCount} 天");
        }
        if (hostGroups.Count > SummaryBodyLineLimit)
        {
            body.AppendLine($"  ……其餘 {hostGroups.Count - SummaryBodyLineLimit} 台請至站台的問題查詢頁檢視。");
        }
        return body.ToString();
    }

    /// <summary><paramref name="globalStatsLine"/> 是未經可見範圍過濾的全站聚合統計行，每位
    /// 收件人共用同一句（見呼叫端的說明）；<paramref name="detail"/> 才是該收件人自己可見範圍
    /// 內的主機明細，null 或空清單都只附全站統計行、不附明細清單。</summary>
    private (string Subject, string Body) BuildUrgentMessage(SystemSettings settings, string globalStatsLine, List<DailyAnalysisRecord>? detail, MailContext ctx)
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
        body.AppendLine(ordered.Count == 1
            ? $"主機 {ResolveHostDisplayName(ordered[0], ctx)} 於 {ordered[0].Date:yyyy-MM-dd} 判定為高風險日。"
            : "本次執行新增以下您可見範圍內的高風險主機日：");
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

    private async Task SendDigestAsync(SystemSettings settings, DateTime now, int windowDays, bool isWeekly, CancellationToken ct)
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
        var statsLine = qualifying.Count == 0
            ? "期間內無達門檻的風險日"
            : $"{qualifying.Count} 個主機日達 {settings.MailMinRiskLevel} 風險以上";
        var subject = ExpandTemplate(settings.MailSubjectTemplate, "全站", dateText, settings.MailMinRiskLevel, type, statsLine);
        var windowText = $"{type}（{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}）：{statsLine}";

        // 週報附未處理數（docs/FEEDBACK-15-PLAN.md D-3）：不限窗口——「還沒處理完」是當下狀態，
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

        (string Subject, string Body) BuildMessage(RecipientView view)
        {
            var body = new StringBuilder(BuildDigestBody(settings, windowText, view.Detail, ctx));
            if (unresolvedLine != null) body.AppendLine().AppendLine(unresolvedLine);
            return (subject, body.ToString());
        }

        // 每日／週報沒有 UrgentSentKeys 一類的逐筆去重狀態（同一天只寄一次靠
        // LastDailySentDate／LastWeeklySentDate），所以不需要 coverage 標記——只借用
        // SendPerRecipientAsync 的熔斷與跨輪失敗 streak 追蹤（回饋十七輪批次B-1 對三路一視同仁）。
        await SendPerRecipientAsync(settings, order, views, BuildMessage, RecordKey, ct);
    }

    // ── 內部：共用工具 ────────────────────────────────────────────────────

    private static string ResolveHostDisplayName(DailyAnalysisRecord record, MailContext ctx) =>
        ctx.Host(record.HostId)?.DisplayName is { Length: > 0 } display ? $"{record.Host}（{display}）" : record.Host;

    private static string RecordKey(DailyAnalysisRecord record) => $"{record.HostId}|{record.Date:yyyy-MM-dd}";

    private static bool IsBeforeCutoff(string key, DateTime cutoff)
    {
        var parts = key.Split('|');
        return parts.Length == 2 && DateTime.TryParse(parts[1], out var date) && date < cutoff;
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

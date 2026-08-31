using LogForesight.Core.Configuration;
using System.Runtime.Versioning;
using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 全站系統設定維護（「系統管理 > 設定」頁）。取代原本分散在批次 appsettings.json
/// （AI 位址）與程式碼寫死常數（未處理等級門檻、補充／留存天數）的可調整項目。
/// </summary>
public interface ISystemSettingsService
{
    SystemSettingsDto Get();

    SystemSettingsDto Update(UpdateSystemSettingsRequest request);

    /// <summary>
    /// 模式為 SiteHidden 時回傳應顯示的嚴重度集合（RecordRepository 據此過濾問題聚合，
    /// 這是全站唯一的過濾點，見 docs/archive/HISTORY.md S1）；
    /// DefaultHidden 回傳 null（表示不過濾，維持顯示層各自決定）。
    /// </summary>
    HashSet<string>? GetVisibleSeverities();

    /// <summary>
    /// 顯示中的日風險等級集合（docs/archive/FEEDBACK-3-PLAN.md #8，RecordRepository 據此過濾）。
    /// 全勾（高/中/低皆顯示，等同未設定過）回傳 null——與 <see cref="GetVisibleSeverities"/>
    /// 同慣例，讓呼叫端可以跳過不必要的交集運算。
    /// </summary>
    IReadOnlySet<string>? GetVisibleDayRiskLevels();

    /// <summary>
    /// AD 測試連線（docs/archive/HISTORY.md #9）：用管理者當場輸入的帳密，對表單目前填的
    /// 伺服器清單試 bind——未儲存的值也能測。密碼不落盤、不進稽核 detail，稽核只記執行過測試
    /// 與對象伺服器。這裡是管理者對自己測試，失敗原因可以顯示細節（與一般登入的規則不同）。
    /// </summary>
    TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request);

    /// <summary>
    /// 測試寄信（回饋十五輪批次D）：用表單目前值（可能還沒儲存）試寄一封信。密碼欄留空＝沿用
    /// 已儲存的密碼（表單不會把已儲存的密碼明碼帶回前端，留空是唯一「不變更密碼」的表示方式，
    /// 與 <see cref="Update"/> 的 ClearSmtpPassword／SmtpPassword 寫入慣例對稱）。
    /// </summary>
    Task<TestMailResultDto> TestMail(TestMailRequest request);

    /// <summary>
    /// PRTG 測試連線（PRTG 第 1 輪批次B）：用表單目前填的值試連線。token 留空＝沿用已儲存的 token。
    /// 成功回傳耗時，失敗回傳錯誤訊息，不擲例外。
    /// </summary>
    Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) =>
        throw new NotSupportedException("測試未使用此方法");
}

public class SystemSettingsService : ISystemSettingsService
{
    /// <summary>合法嚴重度名稱，順序即畫面勾選順序（由重到輕）。docs/archive/HISTORY.md #1
    /// （B1 三級化）：Critical 不再是可選層級，舊設定殘留的 "Critical" 由 NormalizeLegacySeverities
    /// 讀取時正規化為 "High"。</summary>
    public static readonly string[] ValidSeverities = { "High", "Medium", "Low" };

    /// <summary>舊資料相容：既有設定 blob 裡的 "Critical" 一律視同 "High"（三級化前後語意相同，
    /// 見 docs/archive/HISTORY.md #1）。只在讀取/過濾判斷時正規化，不改寫 blob 本身。</summary>
    private static List<string> NormalizeLegacySeverities(IEnumerable<string> values) =>
        values.Select(v => v == "Critical" ? "High" : v).Distinct().ToList();

    /// <summary>
    /// 合法層級顯示模式（見 SystemSettings.SeverityDisplayMode）。
    /// docs/archive/HISTORY.md #5：原本三個模式（DefaultHidden／Locked／GlobalFilter）
    /// 簡化為兩個——Locked 與 GlobalFilter 的差異只在於「詳情頁是否顯示已隱藏層級的按鈕」，
    /// 而過濾機制已收斂到 RecordRepository 單一咽喉點（S1），沒有理由再分兩種嚴格程度不同的隱藏。
    /// </summary>
    public static readonly string[] ValidSeverityDisplayModes = { "DefaultHidden", "SiteHidden" };

    /// <summary>合法 AI 提供者名稱</summary>
    public static IReadOnlyList<string> ValidAiProviders => AiProviders.All;

    /// <summary>舊值遷移：既存 blob 裡的 Locked／GlobalFilter 一律視同新的 SiteHidden——
    /// 兩者語意都被新模式涵蓋（全站查詢層排除）且更嚴格一致，不需要区分。
    /// 只在讀取／過濾判斷時正規化，不改寫 blob 本身，下次使用者存檔會自然寫入新值。</summary>
    private static string NormalizeDisplayMode(string raw) =>
        raw is "Locked" or "GlobalFilter" ? "SiteHidden" : raw;

    private readonly ISystemSettingsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserStore _users;
    private readonly MailNotificationService _mail;
    private readonly IReportUsageQuery _reportUsage;

    public SystemSettingsService(ISystemSettingsStore store, ICurrentUser currentUser, IAuditService audit,
        IUserStore users, MailNotificationService mail, IReportUsageQuery reportUsage)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
        _users = users;
        _mail = mail;
        _reportUsage = reportUsage;
    }

    public SystemSettingsDto Get() => ToDto(_store.Get());

    public HashSet<string>? GetVisibleSeverities() => ResolveVisibleSeverities(_store.Get());

    public IReadOnlySet<string>? GetVisibleDayRiskLevels() => ResolveVisibleDayRiskLevels(_store.Get());

    /// <summary>
    /// 模式為 SiteHidden 時回傳應顯示的嚴重度集合；DefaultHidden 回傳 null。
    /// 供非 Scoped 呼叫端（如 Singleton 的 MailIssueDigest）直接依 settings 運算，
    /// 與實例方法共用同一份判定邏輯。
    /// </summary>
    public static HashSet<string>? ResolveVisibleSeverities(SystemSettings settings) =>
        NormalizeDisplayMode(settings.SeverityDisplayMode) == "SiteHidden"
            ? NormalizeLegacySeverities(settings.UnhandledSeverities).ToHashSet()
            : null;

    /// <summary>
    /// 顯示中的日風險等級集合。全勾（高/中/低皆顯示）回傳 null。
    /// 供非 Scoped 呼叫端直接依 settings 運算，與實例方法共用同一份判定邏輯。
    /// </summary>
    public static IReadOnlySet<string>? ResolveVisibleDayRiskLevels(SystemSettings settings)
    {
        var visible = NormalizeDayRiskLevels(settings.VisibleDayRiskLevels);
        return visible.Count == RiskLevels.All.Length ? null : visible.ToHashSet();
    }

    /// <summary>過濾掉非法值＋去重；不在這裡強制補回「高」——那是 Update 的寫入時驗證職責
    /// （拒絕不合法的請求，比靜默改寫使用者的選擇更誠實），讀取路徑只需要防禦壞資料。</summary>
    private static List<string> NormalizeDayRiskLevels(List<string>? values) =>
        (values ?? new List<string>())
            .Where(v => RiskLevels.All.Contains(v))
            .Distinct()
            .ToList();

    public SystemSettingsDto Update(UpdateSystemSettingsRequest request)
    {
        var severities = NormalizeSeverities(request.UnhandledSeverities);
        if (severities.Count == 0)
            throw DomainException.Validation("請至少勾選一個未處理等級。");

        if (!ValidSeverityDisplayModes.Contains(request.SeverityDisplayMode))
            throw DomainException.Validation("層級顯示模式不合法。");

        var dayRiskLevels = NormalizeDayRiskLevels(request.VisibleDayRiskLevels);
        if (!dayRiskLevels.Contains(RiskLevels.High))
            throw DomainException.Validation("「高風險日」為必要顯示項目，無法取消勾選。");

        if (request.RetentionDays < request.InitialHistoryDays)
            throw DomainException.Validation("歷史資料保留天數不可小於首次回補天數。");

        if (request.RawEventRetentionDays > request.RetentionDays)
            throw DomainException.Validation("原始事件內容保留天數不可大於歷史資料保留天數。");

        // 報告全文存在資料庫：超過歷史資料保留天數之後對應的分析紀錄已被清除，
        // 那些報告在站上不再有任何入口可以點開，留著只是佔資料庫空間
        if (request.ReportRetentionDays > request.RetentionDays)
            throw DomainException.Validation("報告保留天數不可大於歷史資料保留天數。");

        if (request.PrtgRetentionDays > request.RetentionDays)
            throw DomainException.Validation("PRTG 資料保留天數不可大於歷史資料保留天數。");

        if (request.PrtgEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.PrtgUrl))
                throw DomainException.Validation("啟用 PRTG 時，PRTG 位址不可為空。");

            if (!IsValidHttpUrl(request.PrtgUrl))
                throw DomainException.Validation("PRTG 位址格式不合法，必須以 http:// 或 https:// 開頭。");
        }

        var adServers = NormalizeAdServers(request.AdServers);
        if (request.AdAuthEnabled && adServers.Count == 0)
            throw DomainException.Validation("啟用 AD 驗證時，請至少輸入一台 AD 伺服器。");

        // §12：AI 額外請求欄位——必須是 JSON **物件**（要合併進請求本體）。空字串＝不附加。
        var extraFieldsJson = (request.AiExtraRequestFieldsJson ?? "").Trim();
        if (extraFieldsJson.Length > 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(extraFieldsJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw DomainException.Validation("「AI 額外請求欄位」必須是 JSON 物件（例如 {\"rep_pen\": 1.3}）。");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw DomainException.Validation($"「AI 額外請求欄位」不是合法的 JSON：{ex.Message}");
            }
        }

        // §12：掃描頻道只做正規化，**不驗證是否為已知頻道**——ChannelCatalog.Resolve 對未知頻道
        // 保守以 ErrorWarningOnly 處理，是「使用者可自行加自訂頻道」的既有設計；拼錯的頻道
        // 在分析時會誠實申報「頻道不存在／不適用」，不會靜默假裝掃過。
        var channels = NormalizeLines(request.AnalysisChannels);
        var watchedFolders = NormalizeLines(request.WatchedFolders);
        var permOperatorFields = NormalizeLines(request.PermissionOperatorFields);
        var permMemberFields = NormalizeLines(request.PermissionMemberFields);
        var permGroupFields = NormalizeLines(request.PermissionGroupFields);
        var permObjectFields = NormalizeLines(request.PermissionObjectFields);

        // §1（回饋第十輪）：品牌三欄。名稱空白＝回退出廠名，副標允許空（刻意只留產品名）
        var brandName = string.IsNullOrWhiteSpace(request.BrandName) ? DefaultBrandName : request.BrandName.Trim();
        if (brandName.Length > BrandTextMaxLength)
            throw DomainException.Validation($"主標題不可超過 {BrandTextMaxLength} 個字。");

        var brandSubtitle = (request.BrandSubtitle ?? "").Trim();
        if (brandSubtitle.Length > BrandTextMaxLength)
            throw DomainException.Validation($"副標題不可超過 {BrandTextMaxLength} 個字。");

        var brandIcon = ValidateBrandIcon(request.BrandIconDataUri);

        // 郵件通知（回饋十五輪批次D）：任一路觸發啟用時，收件人與寄件人是硬性前提——
        // 沒有收件人的通知設定等於沒設定，儲存當下就該擋，而不是等到排程觸發時才在 log 裡默默失敗
        var mailRecipients = NormalizeLines(request.MailRecipients);
        var anyMailTriggerEnabled = request.MailOnRunCompleted || request.MailDailyEnabled ||
                                     request.MailWeeklyEnabled || request.MailUrgentEnabled;
        if (request.MailEnabled && anyMailTriggerEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.SmtpServer))
                throw DomainException.Validation("已啟用郵件通知的觸發項目，請輸入 SMTP 伺服器。");
            if (string.IsNullOrWhiteSpace(request.MailFrom))
                throw DomainException.Validation("已啟用郵件通知的觸發項目，請輸入寄件人。");
            if (mailRecipients.Count == 0)
                throw DomainException.Validation("已啟用郵件通知的觸發項目，請至少輸入一位收件人。");
        }
        // 位址格式在儲存當下就驗（含未啟用時填的值）：格式錯誤的位址若放行落盤，排程觸發時
        // 建 MailAddress 才炸、又被 SendSafeAsync 靜默吞掉只記 log——使用者會以為通知設好了
        // 卻永遠收不到信，這正是「儲存當下就該擋」最有價值的一類錯誤
        if (!string.IsNullOrWhiteSpace(request.MailFrom) &&
            !System.Net.Mail.MailAddress.TryCreate(request.MailFrom.Trim(), out _))
            throw DomainException.Validation($"寄件人「{request.MailFrom.Trim()}」不是合法的電子郵件位址。");
        foreach (var recipient in mailRecipients)
        {
            if (!System.Net.Mail.MailAddress.TryCreate(recipient, out _))
                throw DomainException.Validation($"收件人「{recipient}」不是合法的電子郵件位址。");
        }
        if (!RiskLevels.All.Contains(request.MailMinRiskLevel))
            throw DomainException.Validation("郵件摘要門檻不合法。");
        if (request.MailDailyEnabled && !TimeSpan.TryParse(request.MailDailyTime, out _))
            throw DomainException.Validation("每日摘要時刻格式不合法，請用 HH:mm。");
        if (request.MailWeeklyEnabled)
        {
            if (!TimeSpan.TryParse(request.MailWeeklyTime, out _))
                throw DomainException.Validation("每週摘要時刻格式不合法，請用 HH:mm。");
            if (!Enum.TryParse<DayOfWeek>(request.MailWeeklyDayOfWeek, ignoreCase: true, out _))
                throw DomainException.Validation("每週摘要星期不合法。");
        }

        var aiProvider = AiProviders.Normalize(request.AiProvider);
        var matchedProvider = ValidAiProviders.FirstOrDefault(p => string.Equals(p, aiProvider, StringComparison.OrdinalIgnoreCase));
        if (matchedProvider == null)
            throw DomainException.Validation("AI 服務提供者不合法。");
        aiProvider = matchedProvider;

        var before = _store.Get();

        var hasApiKey = !string.IsNullOrWhiteSpace(request.AiApiKey) ||
                        (!string.IsNullOrEmpty(before.AiApiKeyEnc) && !request.ClearAiApiKey);

        if (aiProvider == AiProviders.OpenAi)
        {
            if (!hasApiKey)
                throw DomainException.Validation("使用 OpenAI 官方 API 時，請輸入 API 金鑰。");
            if (string.IsNullOrWhiteSpace(request.AiModel))
                throw DomainException.Validation("使用 OpenAI 官方 API 時，請輸入模型名稱。");
        }
        else if (aiProvider == AiProviders.AzureOpenAi)
        {
            if (string.IsNullOrWhiteSpace(request.AiBaseUrl))
                throw DomainException.Validation("使用 Azure OpenAI 時，請輸入端點位址。");
            if (string.IsNullOrWhiteSpace(request.AiAzureDeployment))
                throw DomainException.Validation("使用 Azure OpenAI 時，請輸入部署名稱。");
            if (string.IsNullOrWhiteSpace(request.AiAzureApiVersion))
                throw DomainException.Validation("使用 Azure OpenAI 時，請輸入 API 版本。");
            if (!hasApiKey)
                throw DomainException.Validation("使用 Azure OpenAI 時，請輸入 API 金鑰。");
        }

        // 由關轉開判定要用的三個舊值先讀成區域變數（回饋十八輪批次C）：不能依賴 before 這個
        // 物件在 _store.Update() 之後仍保有「更新前」的內容——ISystemSettingsStore 的介面契約
        // 只保證 Get() 回傳的是讀取當下的快照，並未保證與後續 Update() 使用的是不同執行個體
        // （正式的 blob 實作每次 Get() 都會重新反序列化、天然是不同物件；但介面本身沒有明文
        // 這條保證，捕捉當下值比依賴這個隱含細節更穩妥）。
        var wasMailEnabled = before.MailEnabled;
        var wasSummaryOn = before.MailOnRunCompleted;
        var wasUrgentOn = before.MailUrgentEnabled;

        var saved = _store.Update(s =>
        {
            s.BrandName = brandName;
            s.BrandSubtitle = brandSubtitle;
            s.BrandIconDataUri = brandIcon;
            s.UnhandledSeverities = severities;
            s.SeverityDisplayMode = request.SeverityDisplayMode;
            s.VisibleDayRiskLevels = dayRiskLevels;
            s.AiProvider = aiProvider;
            s.AiBaseUrl = request.AiBaseUrl.Trim();
            if (request.ClearAiApiKey)
                s.AiApiKeyEnc = "";
            else if (!string.IsNullOrEmpty(request.AiApiKey))
                s.AiApiKeyEnc = CryptoHelper.Encrypt(request.AiApiKey);
            s.AiModel = string.IsNullOrWhiteSpace(request.AiModel)
                ? AiProviders.DefaultModel(aiProvider)
                : request.AiModel.Trim();
            s.AiAzureDeployment = request.AiAzureDeployment?.Trim() ?? "";
            s.AiAzureApiVersion = string.IsNullOrWhiteSpace(request.AiAzureApiVersion)
                ? "2024-10-21"
                : request.AiAzureApiVersion.Trim();
            s.InitialHistoryDays = request.InitialHistoryDays;
            s.RetentionDays = request.RetentionDays;
            s.RunLogRetentionDays = request.RunLogRetentionDays;
            s.AuditRetentionDays = request.AuditRetentionDays;
            s.RawEventRetentionDays = request.RawEventRetentionDays;
            s.ReportRetentionDays = request.ReportRetentionDays;
            s.AdAuthEnabled = request.AdAuthEnabled;
            s.AdServers = adServers;
            s.AdSearchBase = request.AdSearchBase?.Trim() ?? "";
            s.AdSearchFilter = string.IsNullOrWhiteSpace(request.AdSearchFilter)
                ? "(sAMAccountName={0})"
                : request.AdSearchFilter.Trim();
            s.AccountDisplayRules = request.AccountDisplayRules ?? "";

            // §12：自 appsettings 遷入的參數
            s.AiTimeoutSeconds = request.AiTimeoutSeconds;
            s.AiRetryCount = request.AiRetryCount;
            s.AiRetryDelaySeconds = request.AiRetryDelaySeconds;
            s.AiJsonRetryCount = request.AiJsonRetryCount;
            s.AiMaxTokens = request.AiMaxTokens;
            s.AiDeepDiveMaxTokens = request.AiDeepDiveMaxTokens;
            s.AiFrequencyPenalty = request.AiFrequencyPenalty;
            s.AiPresencePenalty = request.AiPresencePenalty;
            // 估費單價：負數沒有意義（會算出負金額），一律夾回 0＝不估費
            s.AiInputPricePerMillion = Math.Max(0, request.AiInputPricePerMillion);
            s.AiOutputPricePerMillion = Math.Max(0, request.AiOutputPricePerMillion);
            s.AiExtraRequestFieldsJson = extraFieldsJson;
            s.WatchedFolders = watchedFolders;
            s.ServerDescription = request.ServerDescription?.Trim() ?? "";
            s.CheckupIntervalDays = request.CheckupIntervalDays;
            s.AnalysisChannels = channels;
            s.PermissionOperatorFields = permOperatorFields;
            s.PermissionMemberFields = permMemberFields;
            s.PermissionGroupFields = permGroupFields;
            s.PermissionObjectFields = permObjectFields;
            s.ImportMaxFileSizeKb = request.ImportMaxFileSizeKb;
            s.ImportMaxRows = request.ImportMaxRows;

            // 郵件通知（回饋十五輪批次D）
            s.MailEnabled = request.MailEnabled;
            s.SmtpServer = request.SmtpServer?.Trim() ?? "";
            s.SmtpPort = request.SmtpPort;
            s.SmtpUseTls = request.SmtpUseTls;
            s.SmtpAccount = request.SmtpAccount?.Trim() ?? "";
            if (request.ClearSmtpPassword)
                s.SmtpPasswordEnc = "";
            else if (!string.IsNullOrEmpty(request.SmtpPassword))
                s.SmtpPasswordEnc = CryptoHelper.Encrypt(request.SmtpPassword);
            s.MailFrom = request.MailFrom?.Trim() ?? "";
            s.MailRecipients = mailRecipients;
            s.MailNotifyHostOwners = request.MailNotifyHostOwners;
            s.MailMinRiskLevel = request.MailMinRiskLevel;
            s.MailOnRunCompleted = request.MailOnRunCompleted;
            s.MailDailyEnabled = request.MailDailyEnabled;
            s.MailDailyTime = request.MailDailyTime;
            s.MailWeeklyEnabled = request.MailWeeklyEnabled;
            s.MailWeeklyDayOfWeek = request.MailWeeklyDayOfWeek;
            s.MailWeeklyTime = request.MailWeeklyTime;
            s.MailUrgentEnabled = request.MailUrgentEnabled;
            s.MailSubjectTemplate = string.IsNullOrWhiteSpace(request.MailSubjectTemplate)
                ? new SystemSettings().MailSubjectTemplate
                : request.MailSubjectTemplate.Trim();
            s.MailBodyIntro = request.MailBodyIntro?.Trim() ?? "";
            s.MailDigestSkipEmpty = request.MailDigestSkipEmpty;

            // PRTG 監控系統設定（PRTG 第 1 輪批次B）
            s.PrtgEnabled = request.PrtgEnabled;
            s.PrtgUrl = request.PrtgUrl?.Trim() ?? "";
            if (request.ClearPrtgApiToken)
                s.PrtgApiTokenEnc = "";
            else if (!string.IsNullOrEmpty(request.PrtgApiToken))
                s.PrtgApiTokenEnc = CryptoHelper.Encrypt(request.PrtgApiToken);
            s.PrtgIgnoreSslErrors = request.PrtgIgnoreSslErrors;
            s.PrtgTimeoutSeconds = request.PrtgTimeoutSeconds;
            s.PrtgFetchConcurrency = request.PrtgFetchConcurrency;
            s.PrtgBackfillDays = request.PrtgBackfillDays;
            s.PrtgRetentionDays = request.PrtgRetentionDays;

            s.UpdatedByAccount = _currentUser.Account;
        });

        // 收件人清單可能剛被改正（回饋十七輪批次B-1）：舊的連續失敗計數不該繼續卡著，
        // 讓改正後的地址從零開始重新累計，而不是沿用改動前的失敗歷史繼續排除它。
        _mail.ResetRecipientFailureStreaks();

        // 郵件通知由關轉開時預填已通知狀態（回饋十八輪批次C）：語意「從啟用（含重新啟用）
        // 起算」——不預填的話，第一次啟用會把窗口內累積的歷史紀錄當成全部未通知，寄出一封
        // 「近 14 天總帳」（可能上千筆，且是使用者對這個功能的第一印象）。逐路判定（摘要／緊急
        // 各自的開關）：只有「這一路確實由關轉開」才預填對應集合，已經開著時重複儲存設定
        // （哪怕只是改了無關欄位）不能觸發預填——否則會把當下真正 pending 的紀錄也標成已讀，
        // 變成真的漏寄。MailEnabled 本身也是一道總開關，同樣要看關轉開。
        var summaryTurnedOn = saved.MailEnabled && saved.MailOnRunCompleted && !(wasMailEnabled && wasSummaryOn);
        var urgentTurnedOn = saved.MailEnabled && saved.MailUrgentEnabled && !(wasMailEnabled && wasUrgentOn);
        if (summaryTurnedOn || urgentTurnedOn)
        {
            _mail.MarkExistingRecordsAsNotified(summaryTurnedOn, urgentTurnedOn);
        }

        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: "更新系統設定",
            targetKind: "system_settings",
            targetId: "system_settings",
            // API 金鑰是否變動只留布林，不留明碼/密文，比照 Sentinel 密碼的稽核原則；
            // AD 設定不含任何機密（bind 用登入者自己的帳密），伺服器清單可以整份留稽核
            detail: new
            {
                Before = new
                {
                    before.UnhandledSeverities, before.SeverityDisplayMode, before.VisibleDayRiskLevels,
                    before.AiProvider, before.AiBaseUrl, before.AiModel, before.AiAzureDeployment, before.AiAzureApiVersion,
                    before.InitialHistoryDays, before.RetentionDays, before.RunLogRetentionDays, before.AuditRetentionDays,
                    before.RawEventRetentionDays, before.ReportRetentionDays,
                    before.WatchedFolders, before.ServerDescription, before.CheckupIntervalDays, before.AnalysisChannels,
                    before.PermissionOperatorFields, before.PermissionMemberFields, before.PermissionGroupFields, before.PermissionObjectFields,
                    before.AdAuthEnabled, before.AdServers, before.AdSearchBase, before.AdSearchFilter,
                    before.MailEnabled, before.SmtpServer, before.SmtpPort, before.SmtpUseTls, before.SmtpAccount,
                    before.MailFrom, before.MailRecipients, before.MailNotifyHostOwners, before.MailMinRiskLevel,
                    before.MailOnRunCompleted, before.MailDailyEnabled, before.MailDailyTime,
                    before.MailWeeklyEnabled, before.MailWeeklyDayOfWeek, before.MailWeeklyTime, before.MailUrgentEnabled,
                    before.PrtgEnabled, before.PrtgUrl, before.PrtgIgnoreSslErrors, before.PrtgTimeoutSeconds,
                    before.PrtgFetchConcurrency, before.PrtgBackfillDays, before.PrtgRetentionDays,
                    PrtgHasApiToken = !string.IsNullOrEmpty(before.PrtgApiTokenEnc)
                },
                After = new
                {
                    saved.UnhandledSeverities, saved.SeverityDisplayMode, saved.VisibleDayRiskLevels,
                    saved.AiProvider, saved.AiBaseUrl, saved.AiModel, saved.AiAzureDeployment, saved.AiAzureApiVersion,
                    saved.InitialHistoryDays, saved.RetentionDays, saved.RunLogRetentionDays, saved.AuditRetentionDays,
                    saved.RawEventRetentionDays, saved.ReportRetentionDays,
                    saved.WatchedFolders, saved.ServerDescription, saved.CheckupIntervalDays, saved.AnalysisChannels,
                    saved.PermissionOperatorFields, saved.PermissionMemberFields, saved.PermissionGroupFields, saved.PermissionObjectFields,
                    saved.AdAuthEnabled, saved.AdServers, saved.AdSearchBase, saved.AdSearchFilter,
                    saved.MailEnabled, saved.SmtpServer, saved.SmtpPort, saved.SmtpUseTls, saved.SmtpAccount,
                    saved.MailFrom, saved.MailRecipients, saved.MailNotifyHostOwners, saved.MailMinRiskLevel,
                    saved.MailOnRunCompleted, saved.MailDailyEnabled, saved.MailDailyTime,
                    saved.MailWeeklyEnabled, saved.MailWeeklyDayOfWeek, saved.MailWeeklyTime, saved.MailUrgentEnabled,
                    saved.PrtgEnabled, saved.PrtgUrl, saved.PrtgIgnoreSslErrors, saved.PrtgTimeoutSeconds,
                    saved.PrtgFetchConcurrency, saved.PrtgBackfillDays, saved.PrtgRetentionDays,
                    PrtgHasApiToken = !string.IsNullOrEmpty(saved.PrtgApiTokenEnc)
                },
                AiApiKeyChanged = request.ClearAiApiKey || !string.IsNullOrEmpty(request.AiApiKey),
                SmtpPasswordChanged = request.ClearSmtpPassword || !string.IsNullOrEmpty(request.SmtpPassword),
                PrtgApiTokenChanged = request.ClearPrtgApiToken || !string.IsNullOrEmpty(request.PrtgApiToken)
            });

        return ToDto(saved);
    }

    // ── 品牌自訂的驗證常數與規則（docs/archive/FEEDBACK-10-PLAN.md §1）──────────────────

    /// <summary>名稱空白時回退的出廠名（與 <see cref="SystemSettings.BrandName"/> 初始值同源）</summary>
    private static readonly string DefaultBrandName = new SystemSettings().BrandName;

    /// <summary>主標題／副標題的長度上限：側欄寬度固定，再長也只會被 text-truncate 切掉</summary>
    private const int BrandTextMaxLength = 40;

    /// <summary>自訂圖示解碼後的大小上限（KB）。側欄顯示尺寸約 30px，64KB 綽綽有餘；
    /// 設定是整包 blob，塞大圖會拖慢每一次設定讀寫</summary>
    private const int BrandIconMaxKb = 64;

    /// <summary>
    /// 允許的圖示 data URI 前綴。**刻意不含 SVG**：SVG 可內嵌 script，即使以 img 載入不會執行，
    /// 也不值得為單一裝飾性功能開這個驗證面（見 SystemSettings.BrandIconDataUri）。
    /// </summary>
    private static readonly string[] BrandIconPrefixes =
    {
        "data:image/png;base64,",
        "data:image/jpeg;base64,"
    };

    /// <summary>
    /// 品牌圖示驗證：空＝沿用內建向量圖示；否則必須是允許的 data URI 前綴、base64 可解碼、
    /// 且解碼後不超過上限。**驗證解碼後的真實大小**而非字串長度——base64 會膨脹約 1/3，
    /// 用字串長度當門檻等於實際只放行 3/4 的量。
    /// </summary>
    private static string ValidateBrandIcon(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return "";

        var prefix = BrandIconPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (prefix == null)
            throw DomainException.Validation("品牌圖示只接受 PNG 或 JPG 圖片。");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value[prefix.Length..]);
        }
        catch (FormatException)
        {
            throw DomainException.Validation("品牌圖示的內容無法解讀，請重新選擇圖片。");
        }

        if (bytes.Length > BrandIconMaxKb * 1024)
            throw DomainException.Validation($"品牌圖示不可超過 {BrandIconMaxKb} KB（目前 {bytes.Length / 1024} KB）。");

        return value;
    }

    /// <summary>trim、去除空白行、去重——與 UserAdminService.NormalizeBatchAccounts 同樣的寬鬆解析慣例</summary>
    private static List<string> NormalizeAdServers(List<string>? servers) => NormalizeLines(servers);

    /// <summary>多行文字輸入的共用正規化（§12 的監控資料夾／掃描頻道與 AD 伺服器同一套慣例）</summary>
    private static List<string> NormalizeLines(List<string>? lines) =>
        (lines ?? new List<string>())
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [SupportedOSPlatform("windows")]
    public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request)
    {
        var servers = NormalizeAdServers(request.Servers);
        if (servers.Count == 0)
            throw DomainException.Validation("請至少輸入一台 AD 伺服器。");

        // 稽核只記「執行過測試」與對象伺服器，密碼不落盤、不進稽核 detail
        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: $"執行 AD 測試連線（帳號 {request.Account}）",
            targetKind: "system_settings",
            targetId: "ad_test",
            detail: new { Servers = servers, request.SearchBase, request.SearchFilter });

        try
        {
            var ldap = new LdapService(new LdapOptions
            {
                Servers = servers.ToArray(),
                SearchBase = string.IsNullOrWhiteSpace(request.SearchBase) ? null : request.SearchBase,
                SearchFilter = string.IsNullOrWhiteSpace(request.SearchFilter) ? "(sAMAccountName={0})" : request.SearchFilter
            });

            var status = ldap.Authenticate(request.Account, request.Password);
            return new TestAdConnectionResultDto
            {
                Success = status == LdapAuthStatus.Success,
                Message = DescribeAdStatus(status)
            };
        }
        catch (Exception ex)
        {
            return new TestAdConnectionResultDto { Success = false, Message = $"測試連線失敗：{ex.Message}" };
        }
    }

    public async Task<TestMailResultDto> TestMail(TestMailRequest request)
    {
        // 密碼留空＝沿用已儲存的密碼——表單不會把已存密碼明碼帶回前端，這是使用者「沒改密碼」
        // 的唯一表示方式，與 Update 的 ClearSmtpPassword／SmtpPassword 寫入慣例對稱。
        var password = string.IsNullOrEmpty(request.SmtpPassword)
            ? DecryptSavedSmtpPassword()
            : request.SmtpPassword;

        var connection = new SmtpConnectionSpec(request.SmtpServer.Trim(), request.SmtpPort, request.SmtpUseTls,
            request.SmtpAccount.Trim(), password);
        var recipients = request.Recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();

        // 稽核只記「執行過測試」與對象伺服器／收件人，密碼不落盤、不進稽核 detail
        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: "執行郵件測試寄信",
            targetKind: "system_settings",
            targetId: "mail_test",
            detail: new { request.SmtpServer, request.SmtpPort, request.SmtpUseTls, Recipients = recipients });

        // 模板空白時回退出廠模板——與 Update 的儲存路徑同一個 fallback 規則，測試信的主旨
        // 才不會因為表單還沒填模板就寄出空白主旨（測出來的行為要跟存檔後的實際行為一致）
        var subjectTemplate = string.IsNullOrWhiteSpace(request.SubjectTemplate)
            ? new SystemSettings().MailSubjectTemplate
            : request.SubjectTemplate;

        try
        {
            await _mail.SendTestAsync(connection, request.MailFrom.Trim(), recipients,
                subjectTemplate, request.BodyIntro);
            return new TestMailResultDto { Success = true, Message = "測試郵件已送出，請確認收件匣。" };
        }
        catch (Exception ex)
        {
            return new TestMailResultDto { Success = false, Message = $"測試寄信失敗：{ex.Message}" };
        }
    }

    public async Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct)
    {
        var url = (request.Url ?? "").Trim();
        if (url.Length == 0)
            throw DomainException.Validation("請輸入 PRTG 連線位址。");

        var token = string.IsNullOrEmpty(request.ApiToken)
            ? DecryptSavedPrtgApiToken()
            : request.ApiToken;

        if (string.IsNullOrWhiteSpace(token))
            throw DomainException.Validation("尚未設定 PRTG API token，無法測試。");

        // 稽核只記「執行過測試」與對象 URL，token 不落盤、不進稽核 detail
        _audit.Record(
            action: AuditActions.PrtgConnectionTest,
            summary: "執行 PRTG 測試連線",
            targetKind: "system_settings",
            targetId: "prtg_test",
            detail: new { Url = url, request.IgnoreSslErrors, request.TimeoutSeconds });

        try
        {
            using var client = new PrtgClient(url, token, request.TimeoutSeconds, request.IgnoreSslErrors);
            var elapsed = await client.TestConnectionAsync(ct);
            return new TestPrtgConnectionResultDto
            {
                Success = true,
                Message = "連線成功。",
                ElapsedMs = (long)elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new TestPrtgConnectionResultDto
            {
                Success = false,
                Message = $"測試連線失敗：{ex.Message}"
            };
        }
    }

    private string? DecryptSavedPrtgApiToken()
    {
        var enc = _store.Get().PrtgApiTokenEnc;
        if (string.IsNullOrEmpty(enc)) return null;
        // 先判斷才解密：CryptoHelper.Decrypt 對非本格式的值會擲例外，而這個欄位在
        // 匯入或手動編輯 blob 的路徑上有可能是明文（同 SentinelConnectionFactory 的相容寫法）
        return CryptoHelper.IsEncrypted(enc) ? CryptoHelper.Decrypt(enc) : enc;
    }

    private string? DecryptSavedSmtpPassword()
    {
        var enc = _store.Get().SmtpPasswordEnc;
        return string.IsNullOrEmpty(enc) ? null : CryptoHelper.Decrypt(enc);
    }

    /// <summary>
    /// 這裡是管理者對自己測試，細節可以顯示（與一般登入一律「帳號或密碼錯誤」不同，
    /// 見 LdapCredentialVerifier 與定案 2026-07-27）。
    /// </summary>
    private static string DescribeAdStatus(LdapAuthStatus status) => status switch
    {
        LdapAuthStatus.Success => "驗證成功。",
        LdapAuthStatus.InvalidCredentials => "帳號或密碼錯誤。",
        LdapAuthStatus.UserNotFound => "找不到此帳號。",
        LdapAuthStatus.AccountDisabled => "帳號已停用。",
        LdapAuthStatus.AccountLocked => "帳號已被鎖定。",
        LdapAuthStatus.AccountExpired => "帳號已到期。",
        LdapAuthStatus.PasswordExpired => "密碼已過期。",
        LdapAuthStatus.MustChangePassword => "需於下次登入時變更密碼。",
        LdapAuthStatus.LogonNotAllowed => "不允許於此時間或此工作站登入。",
        LdapAuthStatus.ServerUnavailable => "無法連線至任何 AD 伺服器，請確認伺服器位址是否正確。",
        _ => "驗證失敗（未分類的錯誤）。"
    };

    private static List<string> NormalizeSeverities(List<string>? values) =>
        (values ?? new List<string>())
            .Select(v => ValidSeverities.FirstOrDefault(valid => string.Equals(valid, v, StringComparison.OrdinalIgnoreCase)))
            .Where(v => v != null)
            .Select(v => v!)
            .Distinct()
            .ToList();

    private static bool IsValidHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// AI 進階參數的出廠值（docs/archive/FEEDBACK-10-PLAN.md §3）：直接讀 <see cref="SystemSettings"/> 的
    /// 屬性初始器，不在這裡抄第二份數字——改 Core 的預設值，設定頁的「還原預設值」自動跟上。
    /// static readonly 而非每次 new：這是不變的常數集合。
    /// </summary>
    private static readonly AiAdvancedDefaultsDto AiAdvancedDefaults = BuildAiAdvancedDefaults();

    private static AiAdvancedDefaultsDto BuildAiAdvancedDefaults()
    {
        var factory = new SystemSettings();
        return new AiAdvancedDefaultsDto
        {
            TimeoutSeconds = factory.AiTimeoutSeconds,
            RetryCount = factory.AiRetryCount,
            RetryDelaySeconds = factory.AiRetryDelaySeconds,
            JsonRetryCount = factory.AiJsonRetryCount,
            MaxTokens = factory.AiMaxTokens,
            DeepDiveMaxTokens = factory.AiDeepDiveMaxTokens,
            FrequencyPenalty = factory.AiFrequencyPenalty,
            PresencePenalty = factory.AiPresencePenalty,
            ExtraRequestFieldsJson = factory.AiExtraRequestFieldsJson
        };
    }

    private SystemSettingsDto ToDto(SystemSettings s) => ToDto(s, ReadReportUsage());

    /// <summary>
    /// 報告全文的實際佔用。查詢失敗不得讓整個設定頁掛掉——這只是一則參考資訊，
    /// 拿不到就顯示 0，其餘設定照常運作。
    /// </summary>
    private (int Count, double SizeMb) ReadReportUsage()
    {
        try
        {
            var (count, totalChars) = _reportUsage.Usage();
            // 以 UTF-8 概估：報告以中文敘事為主但夾雜大量 ASCII 的原始 log，取 2 bytes/字元
            return (count, Math.Round(totalChars * 2 / (1024.0 * 1024.0), 2));
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private SystemSettingsDto ToDto(SystemSettings s, (int Count, double SizeMb) reportUsage) => new()
    {
        BrandName = s.BrandName,
        BrandSubtitle = s.BrandSubtitle,
        BrandIconDataUri = s.BrandIconDataUri,
        UnhandledSeverities = NormalizeLegacySeverities(s.UnhandledSeverities),
        SeverityDisplayMode = NormalizeDisplayMode(s.SeverityDisplayMode),
        VisibleDayRiskLevels = NormalizeDayRiskLevels(s.VisibleDayRiskLevels),
        AiProvider = AiProviders.Normalize(s.AiProvider),
        AiBaseUrl = s.AiBaseUrl,
        AiHasApiKey = !string.IsNullOrEmpty(s.AiApiKeyEnc),
        AiModel = string.IsNullOrWhiteSpace(s.AiModel) ? AiProviders.DefaultModel(s.AiProvider) : s.AiModel,
        AiAzureDeployment = s.AiAzureDeployment,
        AiAzureApiVersion = string.IsNullOrWhiteSpace(s.AiAzureApiVersion) ? "2024-10-21" : s.AiAzureApiVersion,
        InitialHistoryDays = s.InitialHistoryDays,
        RetentionDays = s.RetentionDays,
        RunLogRetentionDays = s.RunLogRetentionDays,
        AuditRetentionDays = s.AuditRetentionDays,
        RawEventRetentionDays = s.RawEventRetentionDays,
        ReportRetentionDays = s.ReportRetentionDays,
        // 空間告知用實測值而非估算公式：份數與內容長度是查得到的事實，
        // 一條使用者無法驗證的估算公式只會讓人不知道該不該相信
        ReportCount = reportUsage.Count,
        ReportSizeMb = reportUsage.SizeMb,
        AdAuthEnabled = s.AdAuthEnabled,
        AdServers = s.AdServers,
        AdSearchBase = s.AdSearchBase,
        AdSearchFilter = s.AdSearchFilter,
        AccountDisplayRules = s.AccountDisplayRules,
        // §12：自 appsettings 遷入的參數
        AiTimeoutSeconds = s.AiTimeoutSeconds,
        AiRetryCount = s.AiRetryCount,
        AiRetryDelaySeconds = s.AiRetryDelaySeconds,
        AiJsonRetryCount = s.AiJsonRetryCount,
        AiMaxTokens = s.AiMaxTokens,
        AiDeepDiveMaxTokens = s.AiDeepDiveMaxTokens,
        AiFrequencyPenalty = s.AiFrequencyPenalty,
        AiPresencePenalty = s.AiPresencePenalty,
        AiInputPricePerMillion = s.AiInputPricePerMillion,
        AiOutputPricePerMillion = s.AiOutputPricePerMillion,
        AiExtraRequestFieldsJson = s.AiExtraRequestFieldsJson,
        AiAdvancedDefaults = AiAdvancedDefaults,
        WatchedFolders = s.WatchedFolders,
        ServerDescription = s.ServerDescription,
        CheckupIntervalDays = s.CheckupIntervalDays,
        AnalysisChannels = s.AnalysisChannels,
        PermissionOperatorFields = s.PermissionOperatorFields,
        PermissionMemberFields = s.PermissionMemberFields,
        PermissionGroupFields = s.PermissionGroupFields,
        PermissionObjectFields = s.PermissionObjectFields,
        ImportMaxFileSizeKb = s.ImportMaxFileSizeKb,
        ImportMaxRows = s.ImportMaxRows,
        // 郵件通知（回饋十五輪批次D）
        MailEnabled = s.MailEnabled,
        SmtpServer = s.SmtpServer,
        SmtpPort = s.SmtpPort,
        SmtpUseTls = s.SmtpUseTls,
        SmtpAccount = s.SmtpAccount,
        SmtpHasPassword = !string.IsNullOrEmpty(s.SmtpPasswordEnc),
        MailFrom = s.MailFrom,
        MailRecipients = s.MailRecipients,
        MailNotifyHostOwners = s.MailNotifyHostOwners,
        MailMinRiskLevel = s.MailMinRiskLevel,
        MailOnRunCompleted = s.MailOnRunCompleted,
        MailDailyEnabled = s.MailDailyEnabled,
        MailDailyTime = s.MailDailyTime,
        MailWeeklyEnabled = s.MailWeeklyEnabled,
        MailWeeklyDayOfWeek = s.MailWeeklyDayOfWeek,
        MailWeeklyTime = s.MailWeeklyTime,
        MailUrgentEnabled = s.MailUrgentEnabled,
        MailSubjectTemplate = s.MailSubjectTemplate,
        MailBodyIntro = s.MailBodyIntro,
        MailDigestSkipEmpty = s.MailDigestSkipEmpty,
        SuspendedMailRecipients = _mail.GetSuspendedRecipients(),
        // PRTG 監控系統設定
        PrtgEnabled = s.PrtgEnabled,
        PrtgUrl = s.PrtgUrl,
        PrtgHasApiToken = !string.IsNullOrEmpty(s.PrtgApiTokenEnc),
        PrtgIgnoreSslErrors = s.PrtgIgnoreSslErrors,
        PrtgTimeoutSeconds = s.PrtgTimeoutSeconds,
        PrtgFetchConcurrency = s.PrtgFetchConcurrency,
        PrtgBackfillDays = s.PrtgBackfillDays,
        PrtgRetentionDays = s.PrtgRetentionDays,
        UpdatedAt = s.UpdatedAt,
        UpdatedByAccount = s.UpdatedByAccount,
        UpdatedByDisplayName = string.IsNullOrEmpty(s.UpdatedByAccount)
            ? null
            : _users.FindByAccount(s.UpdatedByAccount)?.DisplayName
    };
}

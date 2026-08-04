using System.Runtime.Versioning;
using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

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
    /// 這是全站唯一的過濾點，見 docs/HISTORY.md S1）；
    /// DefaultHidden 回傳 null（表示不過濾，維持顯示層各自決定）。
    /// </summary>
    HashSet<string>? GetVisibleSeverities();

    /// <summary>
    /// 顯示中的日風險等級集合（docs/FEEDBACK-3-PLAN.md #8，RecordRepository 據此過濾）。
    /// 全勾（高/中/低皆顯示，等同未設定過）回傳 null——與 <see cref="GetVisibleSeverities"/>
    /// 同慣例，讓呼叫端可以跳過不必要的交集運算。
    /// </summary>
    IReadOnlySet<string>? GetVisibleDayRiskLevels();

    /// <summary>
    /// AD 測試連線（docs/HISTORY.md #9）：用管理者當場輸入的帳密，對表單目前填的
    /// 伺服器清單試 bind——未儲存的值也能測。密碼不落盤、不進稽核 detail，稽核只記執行過測試
    /// 與對象伺服器。這裡是管理者對自己測試，失敗原因可以顯示細節（與一般登入的規則不同）。
    /// </summary>
    TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request);
}

public class SystemSettingsService : ISystemSettingsService
{
    /// <summary>合法嚴重度名稱，順序即畫面勾選順序（由重到輕）。docs/HISTORY.md #1
    /// （B1 三級化）：Critical 不再是可選層級，舊設定殘留的 "Critical" 由 NormalizeLegacySeverities
    /// 讀取時正規化為 "High"。</summary>
    public static readonly string[] ValidSeverities = { "High", "Medium", "Low" };

    /// <summary>舊資料相容：既有設定 blob 裡的 "Critical" 一律視同 "High"（三級化前後語意相同，
    /// 見 docs/HISTORY.md #1）。只在讀取/過濾判斷時正規化，不改寫 blob 本身。</summary>
    private static List<string> NormalizeLegacySeverities(IEnumerable<string> values) =>
        values.Select(v => v == "Critical" ? "High" : v).Distinct().ToList();

    /// <summary>
    /// 合法層級顯示模式（見 SystemSettings.SeverityDisplayMode）。
    /// docs/HISTORY.md #5：原本三個模式（DefaultHidden／Locked／GlobalFilter）
    /// 簡化為兩個——Locked 與 GlobalFilter 的差異只在於「詳情頁是否顯示已隱藏層級的按鈕」，
    /// 而過濾機制已收斂到 RecordRepository 單一咽喉點（S1），沒有理由再分兩種嚴格程度不同的隱藏。
    /// </summary>
    public static readonly string[] ValidSeverityDisplayModes = { "DefaultHidden", "SiteHidden" };

    /// <summary>舊值遷移：既存 blob 裡的 Locked／GlobalFilter 一律視同新的 SiteHidden——
    /// 兩者語意都被新模式涵蓋（全站查詢層排除）且更嚴格一致，不需要区分。
    /// 只在讀取／過濾判斷時正規化，不改寫 blob 本身，下次使用者存檔會自然寫入新值。</summary>
    private static string NormalizeDisplayMode(string raw) =>
        raw is "Locked" or "GlobalFilter" ? "SiteHidden" : raw;

    private readonly ISystemSettingsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserStore _users;

    public SystemSettingsService(ISystemSettingsStore store, ICurrentUser currentUser, IAuditService audit, IUserStore users)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
        _users = users;
    }

    public SystemSettingsDto Get() => ToDto(_store.Get());

    public HashSet<string>? GetVisibleSeverities()
    {
        var settings = _store.Get();
        return NormalizeDisplayMode(settings.SeverityDisplayMode) == "SiteHidden"
            ? NormalizeLegacySeverities(settings.UnhandledSeverities).ToHashSet()
            : null;
    }

    public IReadOnlySet<string>? GetVisibleDayRiskLevels()
    {
        var visible = NormalizeDayRiskLevels(_store.Get().VisibleDayRiskLevels);
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

        if (request.RiskyEventRetentionDays > request.RetentionDays)
            throw DomainException.Validation("風險 log 暫存保留天數不可大於歷史資料保留天數。");

        var adServers = NormalizeAdServers(request.AdServers);
        if (request.AdAuthEnabled && adServers.Count == 0)
            throw DomainException.Validation("啟用 AD 驗證時，請至少輸入一台 AD 伺服器。");

        var before = _store.Get();

        var saved = _store.Update(s =>
        {
            s.UnhandledSeverities = severities;
            s.SeverityDisplayMode = request.SeverityDisplayMode;
            s.VisibleDayRiskLevels = dayRiskLevels;
            s.AiBaseUrl = request.AiBaseUrl.Trim();
            if (request.ClearAiApiKey)
                s.AiApiKeyEnc = "";
            else if (!string.IsNullOrEmpty(request.AiApiKey))
                s.AiApiKeyEnc = CryptoHelper.Encrypt(request.AiApiKey);
            s.InitialHistoryDays = request.InitialHistoryDays;
            s.RetentionDays = request.RetentionDays;
            s.RunLogRetentionDays = request.RunLogRetentionDays;
            s.AuditRetentionDays = request.AuditRetentionDays;
            s.RiskyEventRetentionDays = request.RiskyEventRetentionDays;
            s.AdAuthEnabled = request.AdAuthEnabled;
            s.AdServers = adServers;
            s.AdSearchBase = request.AdSearchBase?.Trim() ?? "";
            s.AdSearchFilter = string.IsNullOrWhiteSpace(request.AdSearchFilter)
                ? "(sAMAccountName={0})"
                : request.AdSearchFilter.Trim();
            s.UpdatedByAccount = _currentUser.Account;
        });

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
                    before.UnhandledSeverities, before.SeverityDisplayMode, before.VisibleDayRiskLevels, before.AiBaseUrl,
                    before.InitialHistoryDays, before.RetentionDays, before.RunLogRetentionDays, before.AuditRetentionDays,
                    before.RiskyEventRetentionDays,
                    before.AdAuthEnabled, before.AdServers, before.AdSearchBase, before.AdSearchFilter
                },
                After = new
                {
                    saved.UnhandledSeverities, saved.SeverityDisplayMode, saved.VisibleDayRiskLevels, saved.AiBaseUrl,
                    saved.InitialHistoryDays, saved.RetentionDays, saved.RunLogRetentionDays, saved.AuditRetentionDays,
                    saved.RiskyEventRetentionDays,
                    saved.AdAuthEnabled, saved.AdServers, saved.AdSearchBase, saved.AdSearchFilter
                },
                AiApiKeyChanged = request.ClearAiApiKey || !string.IsNullOrEmpty(request.AiApiKey)
            });

        return ToDto(saved);
    }

    /// <summary>trim、去除空白行、去重——與 UserAdminService.NormalizeBatchAccounts 同樣的寬鬆解析慣例</summary>
    private static List<string> NormalizeAdServers(List<string>? servers) =>
        (servers ?? new List<string>())
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

    private SystemSettingsDto ToDto(SystemSettings s) => new()
    {
        UnhandledSeverities = NormalizeLegacySeverities(s.UnhandledSeverities),
        SeverityDisplayMode = NormalizeDisplayMode(s.SeverityDisplayMode),
        VisibleDayRiskLevels = NormalizeDayRiskLevels(s.VisibleDayRiskLevels),
        AiBaseUrl = s.AiBaseUrl,
        AiHasApiKey = !string.IsNullOrEmpty(s.AiApiKeyEnc),
        InitialHistoryDays = s.InitialHistoryDays,
        RetentionDays = s.RetentionDays,
        RunLogRetentionDays = s.RunLogRetentionDays,
        AuditRetentionDays = s.AuditRetentionDays,
        RiskyEventRetentionDays = s.RiskyEventRetentionDays,
        AdAuthEnabled = s.AdAuthEnabled,
        AdServers = s.AdServers,
        AdSearchBase = s.AdSearchBase,
        AdSearchFilter = s.AdSearchFilter,
        UpdatedAt = s.UpdatedAt,
        UpdatedByAccount = s.UpdatedByAccount,
        UpdatedByDisplayName = string.IsNullOrEmpty(s.UpdatedByAccount)
            ? null
            : _users.FindByAccount(s.UpdatedByAccount)?.DisplayName
    };
}

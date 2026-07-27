using LogForesight.Web.Auth;
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
    /// 模式為 GlobalFilter 時回傳應顯示的嚴重度集合（查詢層據此過濾問題聚合）；
    /// 其他模式回傳 null（表示不過濾，維持顯示層各自決定）。
    /// </summary>
    HashSet<string>? GetVisibleSeverities();
}

public class SystemSettingsService : ISystemSettingsService
{
    /// <summary>合法嚴重度名稱，順序即畫面勾選順序（由重到輕）</summary>
    public static readonly string[] ValidSeverities = { "Critical", "High", "Medium", "Low" };

    /// <summary>合法層級顯示模式（見 SystemSettings.SeverityDisplayMode）</summary>
    public static readonly string[] ValidSeverityDisplayModes = { "DefaultHidden", "Locked", "GlobalFilter" };

    private readonly ISystemSettingsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public SystemSettingsService(ISystemSettingsStore store, ICurrentUser currentUser, IAuditService audit)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
    }

    public SystemSettingsDto Get() => ToDto(_store.Get());

    public HashSet<string>? GetVisibleSeverities()
    {
        var settings = _store.Get();
        return settings.SeverityDisplayMode == "GlobalFilter"
            ? settings.UnhandledSeverities.ToHashSet()
            : null;
    }

    public SystemSettingsDto Update(UpdateSystemSettingsRequest request)
    {
        var severities = NormalizeSeverities(request.UnhandledSeverities);
        if (severities.Count == 0)
            throw DomainException.Validation("請至少勾選一個未處理等級。");

        if (!ValidSeverityDisplayModes.Contains(request.SeverityDisplayMode))
            throw DomainException.Validation("層級顯示模式不合法。");

        if (request.RetentionDays < request.InitialHistoryDays)
            throw DomainException.Validation("歷史資料保留天數不可小於首次回補天數。");

        var before = _store.Get();

        var saved = _store.Update(s =>
        {
            s.UnhandledSeverities = severities;
            s.SeverityDisplayMode = request.SeverityDisplayMode;
            s.AiBaseUrl = request.AiBaseUrl.Trim();
            if (request.ClearAiApiKey)
                s.AiApiKeyEnc = "";
            else if (!string.IsNullOrEmpty(request.AiApiKey))
                s.AiApiKeyEnc = CryptoHelper.Encrypt(request.AiApiKey);
            s.InitialHistoryDays = request.InitialHistoryDays;
            s.RetentionDays = request.RetentionDays;
            s.RunLogRetentionDays = request.RunLogRetentionDays;
            s.AuditRetentionDays = request.AuditRetentionDays;
            s.UpdatedByAccount = _currentUser.Account;
        });

        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: "更新系統設定",
            targetKind: "system_settings",
            targetId: "system_settings",
            // API 金鑰是否變動只留布林，不留明碼/密文，比照 Sentinel 密碼的稽核原則
            detail: new
            {
                Before = new { before.UnhandledSeverities, before.SeverityDisplayMode, before.AiBaseUrl, before.InitialHistoryDays, before.RetentionDays, before.RunLogRetentionDays, before.AuditRetentionDays },
                After = new { saved.UnhandledSeverities, saved.SeverityDisplayMode, saved.AiBaseUrl, saved.InitialHistoryDays, saved.RetentionDays, saved.RunLogRetentionDays, saved.AuditRetentionDays },
                AiApiKeyChanged = request.ClearAiApiKey || !string.IsNullOrEmpty(request.AiApiKey)
            });

        return ToDto(saved);
    }

    private static List<string> NormalizeSeverities(List<string>? values) =>
        (values ?? new List<string>())
            .Select(v => ValidSeverities.FirstOrDefault(valid => string.Equals(valid, v, StringComparison.OrdinalIgnoreCase)))
            .Where(v => v != null)
            .Select(v => v!)
            .Distinct()
            .ToList();

    private static SystemSettingsDto ToDto(SystemSettings s) => new()
    {
        UnhandledSeverities = s.UnhandledSeverities,
        SeverityDisplayMode = s.SeverityDisplayMode,
        AiBaseUrl = s.AiBaseUrl,
        AiHasApiKey = !string.IsNullOrEmpty(s.AiApiKeyEnc),
        InitialHistoryDays = s.InitialHistoryDays,
        RetentionDays = s.RetentionDays,
        RunLogRetentionDays = s.RunLogRetentionDays,
        AuditRetentionDays = s.AuditRetentionDays,
        UpdatedAt = s.UpdatedAt,
        UpdatedByAccount = s.UpdatedByAccount
    };
}

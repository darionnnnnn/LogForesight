using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 首次啟動精靈的就緒度判定（回饋十八輪批次H，「/setup」頁；五十輪擴充為十步）：把 /api/health/detail 已有的
/// 部分狀態（儲存體）與其餘散落各處的設定狀態彙整成一份 checklist，一眼看出「還缺什麼」。
///
/// **與 /api/health/detail 刻意分開**：那支 API 回答「現在健康嗎」（六大塊，含慢操作／
/// 遷移進度等維運訊號），這支回答「設好了嗎」（十個一次性設定步驟），關注點不同，硬併會讓
/// health/detail 更難用（那支已經 90% 內容沒有 UI 消費）。
///
/// 混合制：「完成」全部自動判定（讀現成 store，不新增探測邏輯），「跳過」由使用者手動決定
/// （見 <see cref="SetupWizardStateStore"/>）。步驟清單本身**不含規則版本**——規則庫由
/// RuleBootstrapper 啟動時自動就緒，使用者無事可做，列了只會困惑。
/// </summary>
public class SetupReadinessService
{
    private readonly HealthService _health;
    private readonly IdentityService _identity;
    private readonly ISystemSettingsStore _settings;
    private readonly ISentinelStore _sentinels;
    private readonly IHostStore _hosts;
    private readonly IGroupAccessStore _groupAccess;
    private readonly IUserGroupStore _userGroups;
    private readonly ScheduleOptionsStore _scheduleOptions;
    private readonly SetupWizardStateStore _state;
    private readonly WebAppSettings _webSettings;
    private readonly PrtgStructureSyncStatusStore _prtgSyncStatus;

    public SetupReadinessService(
        HealthService health,
        IdentityService identity,
        ISystemSettingsStore settings,
        ISentinelStore sentinels,
        IHostStore hosts,
        IGroupAccessStore groupAccess,
        IUserGroupStore userGroups,
        ScheduleOptionsStore scheduleOptions,
        SetupWizardStateStore state,
        WebAppSettings webSettings,
        PrtgStructureSyncStatusStore prtgSyncStatus)
    {
        _health = health;
        _identity = identity;
        _settings = settings;
        _sentinels = sentinels;
        _hosts = hosts;
        _groupAccess = groupAccess;
        _userGroups = userGroups;
        _scheduleOptions = scheduleOptions;
        _state = state;
        _webSettings = webSettings;
        _prtgSyncStatus = prtgSyncStatus;
    }

    private sealed record StepMetadata(
        string Id,
        string Title,
        string? TargetUrl,
        Func<WebAppSettings, bool> CanSkip,
        Func<SetupEvaluationContext, bool> IsDone,
        string DoneDetail,
        string NotDoneDetail);

    private sealed class SetupEvaluationContext
    {
        public required bool StorageOk { get; init; }
        public required bool AdConfigured { get; init; }
        public required bool HasAdmins { get; init; }
        public required bool HasDeptGroups { get; init; }
        public required bool HasNetiqHosts { get; init; }
        public required bool HasAuthorization { get; init; }
        public required bool PrtgConfigured { get; init; }
        public required bool MailConfigured { get; init; }
        public required bool AiConfigured { get; init; }
        public required bool ScheduleEnabled { get; init; }
        public required bool ScheduleConfigured { get; init; }
    }

    private static readonly IReadOnlyList<StepMetadata> StepDefinitions = new[]
    {
        new StepMetadata("storage", "儲存體", null,
            _ => false,
            ctx => ctx.StorageOk,
            "資料庫連線正常。", "資料庫目前無法連線，請檢查連線設定。"),

        new StepMetadata("ad", "AD 驗證", "/admin/settings#ad",
            s => string.Equals(s.Auth?.Provider, "Stub", StringComparison.OrdinalIgnoreCase),
            ctx => ctx.AdConfigured,
            "AD 驗證已啟用並設定伺服器。", "尚未啟用 AD 驗證或未設定伺服器位址。"),

        new StepMetadata("admin-account", "管理員帳號", "/admin/users",
            _ => false,
            ctx => ctx.HasAdmins,
            "已有啟用中的管理員帳號。", "尚未指派任何管理員，請至「使用者」頁建立。"),

        new StepMetadata("dept-groups", "部門群組", "/admin/groups",
            _ => true,
            ctx => ctx.HasDeptGroups,
            "已有啟用中的一般使用者部門群組。", "尚未建立任何一般使用者部門群組，請至「群組與授權」建立。"),

        new StepMetadata("netiq", "NetIQ Sentinel 與主機", "/admin/netiq",
            _ => true,
            ctx => ctx.HasNetiqHosts,
            "已有啟用中的 Sentinel 與可輪巡的 NetIQ 主機。", "尚未設定 Sentinel 或沒有可輪巡的 NetIQ 主機。"),

        new StepMetadata("groups", "主機群組與授權", "/admin/groups",
            _ => true,
            ctx => ctx.HasAuthorization,
            "已有部門群組授權或主機負責人設定。", "尚未設定任何部門群組授權或主機負責人——一般使用者將看不到任何主機。"),

        new StepMetadata("prtg", "PRTG 整合", "/admin/prtg",
            _ => true,
            ctx => ctx.PrtgConfigured,
            "PRTG 監控已啟用且結構同步正常。", "PRTG 監控尚未啟用、未完成同步或尚無主機對應成功。"),

        new StepMetadata("mail", "郵件通知", "/admin/settings#mail",
            _ => true,
            ctx => ctx.MailConfigured,
            "郵件通知已啟用並設定 SMTP 伺服器。", "尚未啟用郵件通知或未設定 SMTP 伺服器。"),

        new StepMetadata("ai", "AI 服務", "/admin/settings#ai",
            _ => true,
            ctx => ctx.AiConfigured,
            "AI 服務位址已設定。", "尚未設定 AI 服務位址，分析將以統計模式執行（可正常運作，僅缺白話摘要）。"),

        new StepMetadata("schedule", "排程啟用", "/runs#settings",
            _ => true,
            ctx => ctx.ScheduleConfigured,
            "排程已啟用且設有執行窗口，將自動定時執行分析。", "排程尚未啟用或未設定執行窗口，僅能於「排程作業」頁手動立即執行。")
    };

    private static readonly Dictionary<string, StepMetadata> StepDefinitionsById =
        StepDefinitions.ToDictionary(s => s.Id);

    private static readonly HashSet<string> OldSevenStepIds = new()
    {
        "storage", "admin-account", "mail", "ai", "netiq", "groups", "schedule"
    };

    private static readonly string[] NewThreeStepIds = new[]
    {
        "ad", "dept-groups", "prtg"
    };

    public SetupStatusDto GetStatus()
    {
        var state = _state.Get();
        var evalCtx = BuildEvaluationContext();

        if (state.StepsVersion < 2)
        {
            var oldSevenDoneOrSkipped = OldSevenStepIds.All(id =>
            {
                var def = StepDefinitionsById[id];
                // 升級判斷必須沿用舊七步當時的完成口徑。舊版排程只看 Enabled；
                // 若改用新版「至少一個窗口」，原本已全部完成的站台會被誤判成未完成，
                // 進而在升級後突然顯示三個新增步驟。
                var legacyDone = id == "schedule" ? evalCtx.ScheduleEnabled : def.IsDone(evalCtx);
                return legacyDone || (def.CanSkip(_webSettings) && state.SkippedSteps.Contains(id));
            });

            bool shouldMigrate = state.Hidden || oldSevenDoneOrSkipped;

            _state.Update(s =>
            {
                s.StepsVersion = 2;
                if (shouldMigrate)
                {
                    foreach (var id in NewThreeStepIds)
                    {
                        var def = StepDefinitionsById[id];
                        if (!def.IsDone(evalCtx) && def.CanSkip(_webSettings))
                        {
                            s.SkippedSteps.Add(id);
                        }
                    }
                }
            });

            state = _state.Get();
        }

        var steps = StepDefinitions.Select(def =>
        {
            bool canSkip = def.CanSkip(_webSettings);
            bool done = def.IsDone(evalCtx);
            bool skipped = canSkip && state.SkippedSteps.Contains(def.Id);
            return new SetupStepDto
            {
                Id = def.Id,
                Title = def.Title,
                Done = done,
                Skipped = skipped,
                CanSkip = canSkip,
                Detail = done ? def.DoneDetail : def.NotDoneDetail,
                TargetUrl = def.TargetUrl
            };
        }).ToList();

        return new SetupStatusDto
        {
            Steps = steps,
            AllSettled = steps.All(s => s.Done || s.Skipped),
            Hidden = state.Hidden
        };
    }

    public void SetSkipped(string stepId, bool skipped)
    {
        if (!StepDefinitionsById.TryGetValue(stepId, out var def)) return;
        if (skipped && !def.CanSkip(_webSettings)) return;

        _state.Update(s =>
        {
            if (skipped) s.SkippedSteps.Add(stepId);
            else s.SkippedSteps.Remove(stepId);
        });
    }

    public void SetHidden(bool hidden) => _state.Update(s => s.Hidden = hidden);

    private SetupEvaluationContext BuildEvaluationContext()
    {
        var settings = _settings.Get();
        var allHosts = _hosts.GetAll();
        var scheduleOptions = _scheduleOptions.Get();
        var prtgSync = _prtgSyncStatus.GetOrNull();

        return new SetupEvaluationContext
        {
            StorageOk = _health.GetLiveness().StorageOk,
            AdConfigured = settings.AdAuthEnabled && settings.AdServers.Any(s => !string.IsNullOrWhiteSpace(s)),
            HasAdmins = !_identity.HasNoAdmins(),
            HasDeptGroups = _userGroups.GetAll().Any(g => g.Active && g.Role == UserRole.User),
            HasNetiqHosts = HasPollableNetiqHosts(allHosts),
            HasAuthorization = HasAnyAuthorization(allHosts),
            PrtgConfigured = settings.PrtgEnabled && prtgSync != null && prtgSync.Success && prtgSync.MapOk > 0,
            MailConfigured = settings.MailEnabled && !string.IsNullOrWhiteSpace(settings.SmtpServer),
            AiConfigured = !string.IsNullOrWhiteSpace(settings.AiBaseUrl),
            ScheduleEnabled = scheduleOptions.Enabled,
            ScheduleConfigured = scheduleOptions.Enabled && (scheduleOptions.Windows?.Count ?? 0) > 0
        };
    }

    private bool HasPollableNetiqHosts(List<WebHost> allHosts)
    {
        var sentinels = _sentinels.GetAll();
        if (!sentinels.Any(s => s.Active)) return false;

        var sentinelsById = sentinels.ToDictionary(s => s.SentinelId);
        return NetiqHostList.Pollable(allHosts, id => sentinelsById.TryGetValue(id, out var s) && s.Active).Count > 0;
    }

    /// <summary>有部門群組授權**或**任一啟用中主機有負責人——兩條路徑（群組授權／主機負責人）
    /// 任一成立就算「有人看得到東西」，不強制兩者都設。</summary>
    private bool HasAnyAuthorization(List<WebHost> allHosts) =>
        _groupAccess.GetAll().Count > 0 || allHosts.Any(h => h.Active && h.OwnerUserIds.Count > 0);
}

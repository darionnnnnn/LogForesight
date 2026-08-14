using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 首次啟動精靈的就緒度判定（回饋十八輪批次H，「/setup」頁）：把 /api/health/detail 已有的
/// 部分狀態（儲存體）與其餘散落各處的設定狀態彙整成一份 checklist，一眼看出「還缺什麼」。
///
/// **與 /api/health/detail 刻意分開**：那支 API 回答「現在健康嗎」（六大塊，含慢操作／
/// 遷移進度等維運訊號），這支回答「設好了嗎」（七個一次性設定步驟），關注點不同，硬併會讓
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
    private readonly ScheduleOptionsStore _scheduleOptions;
    private readonly SetupWizardStateStore _state;

    public SetupReadinessService(
        HealthService health,
        IdentityService identity,
        ISystemSettingsStore settings,
        ISentinelStore sentinels,
        IHostStore hosts,
        IGroupAccessStore groupAccess,
        ScheduleOptionsStore scheduleOptions,
        SetupWizardStateStore state)
    {
        _health = health;
        _identity = identity;
        _settings = settings;
        _sentinels = sentinels;
        _hosts = hosts;
        _groupAccess = groupAccess;
        _scheduleOptions = scheduleOptions;
        _state = state;
    }

    public SetupStatusDto GetStatus()
    {
        var state = _state.Get();
        var settings = _settings.Get();
        // 一次批次載入一次（回饋十八輪體檢輪修正）：HasPollableNetiqHosts／HasAnyAuthorization
        // 原本各自呼叫一次 _hosts.GetAll()，這支 API 呼叫頻率低（僅精靈頁），但同一次請求內
        // 兩次全表讀取仍是明顯可避免的重複——見 SearchByIssue 等處已經在遵守的同一個原則。
        var allHosts = _hosts.GetAll();

        var steps = new List<SetupStepDto>
        {
            BuildStep("storage", "儲存體", canSkip: false, targetUrl: null,
                done: _health.GetLiveness().StorageOk,
                doneDetail: "資料庫連線正常。", notDoneDetail: "資料庫目前無法連線，請檢查連線設定。",
                skipped: state.SkippedSteps),

            BuildStep("admin-account", "管理員帳號", canSkip: false, targetUrl: "/admin/users",
                done: !_identity.HasNoAdmins(),
                doneDetail: "已有啟用中的管理員帳號。", notDoneDetail: "尚未指派任何管理員，請至「使用者」頁建立。",
                skipped: state.SkippedSteps),

            BuildStep("mail", "郵件通知", canSkip: true, targetUrl: "/admin/settings",
                done: settings.MailEnabled && !string.IsNullOrWhiteSpace(settings.SmtpServer),
                doneDetail: "郵件通知已啟用並設定 SMTP 伺服器。", notDoneDetail: "尚未啟用郵件通知或未設定 SMTP 伺服器。",
                skipped: state.SkippedSteps),

            BuildStep("ai", "AI 服務", canSkip: true, targetUrl: "/admin/settings",
                done: !string.IsNullOrWhiteSpace(settings.AiBaseUrl),
                doneDetail: "AI 服務位址已設定。", notDoneDetail: "尚未設定 AI 服務位址，分析將以統計模式執行（可正常運作，僅缺白話摘要）。",
                skipped: state.SkippedSteps),

            BuildStep("netiq", "NetIQ Sentinel 與主機", canSkip: true, targetUrl: "/admin/netiq",
                done: HasPollableNetiqHosts(allHosts),
                doneDetail: "已有啟用中的 Sentinel 與可輪巡的 NetIQ 主機。", notDoneDetail: "尚未設定 Sentinel 或沒有可輪巡的 NetIQ 主機。",
                skipped: state.SkippedSteps),

            BuildStep("groups", "群組與授權", canSkip: true, targetUrl: "/admin/groups",
                done: HasAnyAuthorization(allHosts),
                doneDetail: "已有部門群組授權或主機負責人設定。", notDoneDetail: "尚未設定任何部門群組授權或主機負責人——一般使用者將看不到任何主機。",
                skipped: state.SkippedSteps),

            BuildStep("schedule", "排程啟用", canSkip: true, targetUrl: "/runs",
                done: _scheduleOptions.Get().Enabled,
                doneDetail: "排程已啟用，將自動定時執行分析。", notDoneDetail: "排程尚未啟用，僅能於「排程作業」頁手動立即執行。",
                skipped: state.SkippedSteps)
        };

        return new SetupStatusDto
        {
            Steps = steps,
            AllSettled = steps.All(s => s.Done || s.Skipped),
            Hidden = state.Hidden
        };
    }

    /// <summary>不可跳過的步驟（儲存體／管理員帳號）id——後端是最後防線，不能只靠前端
    /// 不顯示跳過按鈕：前端被繞過（直接打 API）時，這裡仍要擋下。</summary>
    private static readonly HashSet<string> NonSkippableStepIds = new() { "storage", "admin-account" };

    /// <summary>全部合法步驟 id（終檢輪補）：SetSkipped 只接受這份清單——未知 id 不落地，
    /// 避免直接打 API 的任意字串靜默累積進 SkippedSteps blob 變成垃圾。</summary>
    private static readonly HashSet<string> AllStepIds = new()
    {
        "storage", "admin-account", "mail", "ai", "netiq", "groups", "schedule"
    };

    public void SetSkipped(string stepId, bool skipped)
    {
        if (!AllStepIds.Contains(stepId)) return;
        if (skipped && NonSkippableStepIds.Contains(stepId)) return;

        _state.Update(s =>
        {
            if (skipped) s.SkippedSteps.Add(stepId);
            else s.SkippedSteps.Remove(stepId);
        });
    }

    public void SetHidden(bool hidden) => _state.Update(s => s.Hidden = hidden);

    private static SetupStepDto BuildStep(
        string id, string title, bool canSkip, string? targetUrl, bool done,
        string doneDetail, string notDoneDetail, HashSet<string> skipped) => new()
    {
        Id = id,
        Title = title,
        Done = done,
        Skipped = canSkip && skipped.Contains(id),
        CanSkip = canSkip,
        Detail = done ? doneDetail : notDoneDetail,
        TargetUrl = targetUrl
    };

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

using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>操作紀錄查閱（docs/WEB-SPEC.md §9.11）</summary>
public class AuditQueryService
{
    private readonly AuditLogStore _store;
    private readonly IUserStore _users;
    private readonly IUserDisplayNameService _displayNameService;

    public AuditQueryService(AuditLogStore store, IUserStore users, IUserDisplayNameService displayNameService)
    {
        _store = store;
        _users = users;
        _displayNameService = displayNameService;
    }

    public PagedResult<AuditEntryDto> Query(AuditQuery query)
    {
        var result = _store.Query(query);

        // 一次載入做字典（docs/archive/FEEDBACK-8-PLAN.md #6）：單頁筆數有限，不必逐筆查
        var byAccount = _users.GetAll().ToDictionary(u => u.Account, u => _displayNameService.Of(u.DisplayName), StringComparer.OrdinalIgnoreCase);

        return new PagedResult<AuditEntryDto>
        {
            Items = result.Items.Select(e => ToDto(e, byAccount)).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total
        };
    }

    public Dictionary<string, string> GetActionNames() => new(ActionNames);

    /// <summary>
    /// 動作代碼 → 中文。放在後端而不是前端：動作代碼由後端定義，
    /// 對照表跟著定義走才不會在新增動作時漏掉一邊。
    /// </summary>
    private static readonly Dictionary<string, string> ActionNames = new()
    {
        [AuditActions.Login] = "登入",
        [AuditActions.Logout] = "登出",
        [AuditActions.LoginFailed] = "登入失敗",
        [AuditActions.SessionExpired] = "工作階段逾期",
        ["access_denied"] = "權限不足被拒",

        [AuditActions.HandlingAssign] = "指派處理人",
        [AuditActions.HandlingStatus] = "變更處理狀態",
        [AuditActions.HandlingNote] = "更新處理說明",
        [AuditActions.IssueBulkClose] = "統一標記問題",

        [AuditActions.PermConfirmAuthorized] = "確認權限異動為授權",
        [AuditActions.PermConfirmSuspicious] = "標記權限異動可疑",
        [AuditActions.PermConfirmBatch] = "批次確認權限異動",

        [AuditActions.RuleCreate] = "新增規則",
        [AuditActions.RuleUpdate] = "修改規則",
        [AuditActions.RuleEnable] = "啟用規則",
        [AuditActions.RuleDisable] = "停用規則",
        [AuditActions.RuleRestoreSeed] = "回復規則預設",
        [AuditActions.RuleDelete] = "刪除規則",
        [AuditActions.SuppressAdd] = "新增抑制",
        [AuditActions.SuppressRemove] = "移除抑制",

        [AuditActions.UserCreate] = "新增使用者",
        [AuditActions.UserUpdate] = "更新使用者",
        [AuditActions.HostUpdate] = "更新主機",
        [AuditActions.HostMerge] = "合併主機",
        [AuditActions.HostUnmerge] = "解除主機合併",
        [AuditActions.GroupCreate] = "新增群組",
        [AuditActions.GroupUpdate] = "更新群組",
        [AuditActions.GroupDelete] = "刪除群組",
        [AuditActions.AccessGrant] = "授予存取權",
        [AuditActions.AccessRevoke] = "收回存取權",

        [AuditActions.ImportApply] = "套用 CSV 匯入",

        [AuditActions.NetiqImportApplied] = "NetIQ 掃描匯入",

        [AuditActions.SentinelCreate] = "新增 Sentinel",
        [AuditActions.SentinelUpdate] = "更新 Sentinel",
        [AuditActions.SentinelDelete] = "刪除 Sentinel",
        [AuditActions.SentinelSetActive] = "變更 Sentinel 啟用狀態",

        [AuditActions.SettingsUpdate] = "更新系統設定",
        [AuditActions.NetiqOptionsUpdate] = "更新 NetIQ 連線與節流參數",

        [AuditActions.RuleSeedImport] = "套用規則升級",

        [AuditActions.ScheduleOptionsUpdate] = "更新排程設定",
        [AuditActions.ScheduleManualRun] = "手動觸發分析",
        [AuditActions.ScheduleManualCancel] = "取消執行中的分析",

        [AuditActions.NetiqProbeRun] = "執行 NetIQ API 診斷"
    };

    private static AuditEntryDto ToDto(AuditEntry entry, IReadOnlyDictionary<string, string> displayNameByAccount) => new()
    {
        AuditId = entry.AuditId,
        OccurredAt = entry.OccurredAt,
        Account = entry.Account,
        // 查無對應使用者（登入失敗的帳號打錯、外部帳號等）時為 null，前端退回只顯示帳號——
        // 這正是登入失敗稽核最常見的情境，不能因為查不到就整列出錯（docs/archive/FEEDBACK-8-PLAN.md #6）
        AccountDisplayName = displayNameByAccount.TryGetValue(entry.Account, out var displayName) ? displayName : null,
        Action = entry.Action,
        ActionText = ActionNames.TryGetValue(entry.Action, out var name) ? name : entry.Action,
        TargetKind = entry.TargetKind,
        TargetId = entry.TargetId,
        Summary = entry.Summary,
        DetailJson = entry.DetailJson,
        IpAddress = entry.IpAddress,
        Result = entry.Result.ToString().ToLowerInvariant()
    };
}

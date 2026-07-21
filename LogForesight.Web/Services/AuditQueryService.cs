using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>操作紀錄查閱（docs/WEB-SPEC.md §9.11）</summary>
public interface IAuditQueryService
{
    PagedResult<AuditEntryDto> Query(AuditQuery query);

    /// <summary>可用的動作代碼與中文名稱（篩選下拉的來源，前端不自行維護對照表）</summary>
    Dictionary<string, string> GetActionNames();
}

public class AuditQueryService : IAuditQueryService
{
    private readonly IAuditLogStore _store;

    public AuditQueryService(IAuditLogStore store)
    {
        _store = store;
    }

    public PagedResult<AuditEntryDto> Query(AuditQuery query)
    {
        var result = _store.Query(query);

        return new PagedResult<AuditEntryDto>
        {
            Items = result.Items.Select(ToDto).ToList(),
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

        [AuditActions.PermConfirmAuthorized] = "確認權限異動為授權",
        [AuditActions.PermConfirmSuspicious] = "標記權限異動可疑",

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
        [AuditActions.GroupCreate] = "新增群組",
        [AuditActions.GroupUpdate] = "更新群組",
        [AuditActions.GroupDelete] = "刪除群組",
        [AuditActions.AccessGrant] = "授予存取權",
        [AuditActions.AccessRevoke] = "收回存取權",

        [AuditActions.ImportApply] = "套用 CSV 匯入"
    };

    private static AuditEntryDto ToDto(AuditEntry entry) => new()
    {
        AuditId = entry.AuditId,
        OccurredAt = entry.OccurredAt,
        Account = entry.Account,
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

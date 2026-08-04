using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 名稱清單顯示的共用小工具（原本各 Service 各自寫一份逐字相同的版本）。
///
/// <see cref="DayHandlingCommandService"/> 的「未指定」與匯入器（UserCsvImporter／HostCsvImporter）
/// 的「(未知:{id})」是刻意不同的文字，不在此統一——那是各自情境的既有措辭，不是疏漏。
/// </summary>
internal static class NameFormat
{
    /// <summary>清單轉顯示字串：空清單顯示「（無）」，否則以「、」串接</summary>
    public static string Join(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);

    /// <summary>單一 id 查無對應名稱時的顯示回退</summary>
    public static string OrDeleted(string? name, long id) => name ?? $"(已刪除:{id})";

    /// <summary>
    /// 使用者名稱的唯一顯示格式：顯示名稱(帳號)（docs/archive/FEEDBACK-8-PLAN.md #6）——半形括號，
    /// 前端 format.js 的 formatUserName() 是同一個規則的瀏覽器端版本，後端組字串的出口
    /// （TriggerText 之類「誰做的」敘述句）統一走這裡。查無對應使用者時退回只顯示帳號。
    /// </summary>
    public static string FormatAccount(IUserStore users, string account)
    {
        var displayName = users.FindByAccount(account)?.DisplayName;
        return string.IsNullOrEmpty(displayName) ? account : $"{displayName}({account})";
    }

    /// <summary>id 清單逐一解析為名稱清單，查無對應項目時以「(已刪除:{id})」回退</summary>
    public static List<string> ResolveNames<T>(
        IEnumerable<long> ids, IReadOnlyDictionary<long, T> byId, Func<T, string> nameSelector) =>
        ids.Select(id => byId.TryGetValue(id, out var entity) ? nameSelector(entity) : $"(已刪除:{id})").ToList();

    /// <summary>
    /// 驗證 id 清單全部存在於 known 中，否則丟出驗證例外列出不存在的 id
    /// （4 處「SetXxx」寫入方法逐字相同的前置檢查：UserAdminService.SetUserGroups／
    /// HostAdminService.SetHostGroups／SetHostOwners／GroupAdminService.SetAccess）。
    /// </summary>
    public static void EnsureAllKnown<T>(List<long> requestedIds, IReadOnlyDictionary<long, T> known, string label)
    {
        var unknown = requestedIds.Where(id => !known.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw DomainException.Validation($"指定的{label}不存在（ID：{string.Join("、", unknown)}）。");
    }
}

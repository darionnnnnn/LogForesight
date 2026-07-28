namespace LogForesight.Web.Services;

/// <summary>
/// 名稱清單顯示的共用小工具（原本各 Service 各自寫一份逐字相同的版本）。
///
/// <see cref="HandlingService"/> 的「未指定」與匯入器（UserCsvImporter／HostCsvImporter）
/// 的「(未知:{id})」是刻意不同的文字，不在此統一——那是各自情境的既有措辭，不是疏漏。
/// </summary>
internal static class NameFormat
{
    /// <summary>清單轉顯示字串：空清單顯示「（無）」，否則以「、」串接</summary>
    public static string Join(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);

    /// <summary>單一 id 查無對應名稱時的顯示回退</summary>
    public static string OrDeleted(string? name, long id) => name ?? $"(已刪除:{id})";

    /// <summary>id 清單逐一解析為名稱清單，查無對應項目時以「(已刪除:{id})」回退</summary>
    public static List<string> ResolveNames<T>(
        IEnumerable<long> ids, IReadOnlyDictionary<long, T> byId, Func<T, string> nameSelector) =>
        ids.Select(id => byId.TryGetValue(id, out var entity) ? nameSelector(entity) : $"(已刪除:{id})").ToList();
}

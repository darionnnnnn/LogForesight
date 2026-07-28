namespace LogForesight.Web.Services.Import;

/// <summary>
/// CSV 匯入異動比對的共用小工具。原本 HostCsvImporter 有一份區域函式版本、
/// UserCsvImporter 的 Email 欄位重寫了一份逐字相同的版本。
/// </summary>
internal static class ImportChangeHelpers
{
    /// <summary>
    /// 單一文字欄位的異動比對：欄位未出現在檔案中就跳過；值相同（把 null 當空字串比）也跳過；
    /// 否則記一筆異動，Before 為空/null 時顯示「（無）」。
    /// </summary>
    public static void CompareText(CsvRow row, List<ImportFieldChange> changes, string header, string field, string? before)
    {
        if (!row.HasValue(header)) return;
        var after = row.Get(header);
        if (after == (before ?? "")) return;
        changes.Add(new ImportFieldChange { Field = field, Before = string.IsNullOrEmpty(before) ? "（無）" : before, After = after });
    }
}

namespace LogForesight;

/// <summary>
/// 清理超過保留天數的風險報告檔（docs/HISTORY.md P1-4）。
/// 檔名固定以 <c>yyyy-MM-dd</c> 開頭（<see cref="RiskReportService"/> 的 BuildFileName），
/// 比對檔名比 LastWriteTime 更準——檔案被搬移/複製會改變寫入時間，但檔名記的是報告所屬的那一天。
/// 遞迴掃描：報告可能落在 export\ 直接底下（單機批次），也可能在 export\{host}\ 子目錄下
/// （NetIQ 多主機情境，見 FileReportSink）。
/// </summary>
public static class ExportReportPruner
{
    public static int Prune(string exportDir, int retentionDays)
    {
        if (!Directory.Exists(exportDir)) return 0;

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(exportDir, "*.txt", SearchOption.AllDirectories))
        {
            if (!TryParseReportDate(Path.GetFileName(file), out var reportDate)) continue; // 非本系統產生的報告：保守略過，不猜測性刪除
            if (reportDate >= cutoff) continue;

            File.Delete(file);
            removed++;
        }
        return removed;
    }

    internal static bool TryParseReportDate(string fileName, out DateTime date)
    {
        if (fileName.Length < 10)
        {
            date = default;
            return false;
        }
        return DateTime.TryParseExact(fileName[..10], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out date);
    }
}

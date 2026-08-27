namespace LogForesight.Core.Service;

/// <summary>
/// 清理超過保留天數的風險報告檔（docs/archive/HISTORY.md P1-4）。
/// 檔名固定以 <c>yyyy-MM-dd</c> 開頭（<see cref="RiskReportService"/> 的 BuildFileName），
/// 比對檔名比 LastWriteTime 更準——檔案被搬移/複製會改變寫入時間，但檔名記的是報告所屬的那一天。
/// 遞迴掃描：報告可能落在 export\ 直接底下（單機批次），也可能在 export\{host}\ 子目錄下
/// （NetIQ 多主機情境，見 FileReportSink）。
/// </summary>
internal static class ExportReportPruner
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

        DeleteEmptySubdirectories(exportDir);

        return removed;
    }

    /// <summary>
    /// 由深至淺移除已完全空掉的子目錄（不含 exportDir 本身）。
    /// </summary>
    private static void DeleteEmptySubdirectories(string dir, bool isRoot = true)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                DeleteEmptySubdirectories(subDir, isRoot: false);
            }

            if (!isRoot && IsDirectoryEmpty(dir))
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
            // 清理為盡力而為，吞掉例外並繼續處理其餘目錄
        }
    }

    /// <summary>
    /// 判定目錄是否完全為空（無任何檔案且無任何子目錄）。
    /// </summary>
    private static bool IsDirectoryEmpty(string dir)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析檔名開頭的 yyyy-MM-dd 日期（FileReportSink 與 ExportReportPruner 共同複用）。
    /// </summary>
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

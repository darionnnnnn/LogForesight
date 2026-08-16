using System.Text;

namespace LogForesight.Core.Persistence;

/// <summary>預設的報告輸出實作：寫到執行檔目錄下的 export（單機/多機共用一個實作，host 有值時分子目錄）</summary>
internal class FileReportSink : IReportSink
{
    private readonly string _exportDir;

    public FileReportSink(string? exportDir = null)
    {
        // 輸出到執行檔所在目錄下的 export（排程執行時 CurrentDirectory 可能是 system32，不可靠）
        _exportDir = exportDir ?? Path.Combine(AppContext.BaseDirectory, "export");
    }

    public async Task<string> WriteAsync(ReportKind kind, string host, string fileName, string content)
    {
        var safeHost = string.IsNullOrEmpty(host) ? string.Empty : SanitizePathComponent(host);
        var safeFileName = SanitizePathComponent(fileName);

        var dir = string.IsNullOrEmpty(safeHost) ? _exportDir : Path.Combine(_exportDir, safeHost);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, safeFileName);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// 淨化路徑片段：避免外部輸入（如 host）含有 .. 或路徑分隔字元，導致寫出 export 目錄之外。
    /// </summary>
    private static string SanitizePathComponent(string component)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(component.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(sanitized) || sanitized == "." || sanitized == "..")
        {
            return "_";
        }
        return sanitized;
    }
}

using System.Text;

namespace LogForesight;

/// <summary>預設的報告輸出實作：寫到執行檔目錄下的 export（單機/多機共用一個實作，host 有值時分子目錄）</summary>
public class FileReportSink : IReportSink
{
    private readonly string _exportDir;

    public FileReportSink(string? exportDir = null)
    {
        // 輸出到執行檔所在目錄下的 export（排程執行時 CurrentDirectory 可能是 system32，不可靠）
        _exportDir = exportDir ?? Path.Combine(AppContext.BaseDirectory, "export");
    }

    public async Task<ReportRef> WriteAsync(ReportKind kind, string host, string fileName, string content)
    {
        var dir = string.IsNullOrEmpty(host) ? _exportDir : Path.Combine(_exportDir, host);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return new ReportRef(path);
    }
}

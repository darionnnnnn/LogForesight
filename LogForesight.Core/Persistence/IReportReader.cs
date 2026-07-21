using System.Text;

namespace LogForesight;

/// <summary>
/// 報告全文的讀取（<see cref="IReportSink"/> 的對應讀取端）。
///
/// 為什麼要獨立一個介面而不是加在 IReportSink 上：批次只寫、Web 只讀，
/// 合成一個介面會讓批次被迫依賴它用不到的讀取方法（ISP）。
/// </summary>
public interface IReportReader
{
    /// <summary>
    /// 依報告參照讀取全文（`DailyAnalysisRecord.ReportFile` 的值）。
    /// 找不到時回 null——報告檔可能已被清理或搬移，那不是錯誤，畫面顯示「報告已不存在」即可。
    /// </summary>
    string? Read(string reportRef);
}

/// <summary>
/// 檔案後端的報告讀取：`export\` 目錄下的 txt。
///
/// 安全性：報告參照來自歷史紀錄檔，理論上是可信的，但它終究是**資料**而不是程式常數——
/// 若歷史檔被竄改成 `..\..\Windows\System32\config\SAM` 這類路徑，
/// 沒有防護的讀取就變成任意檔案讀取。因此一律驗證解析後的路徑仍在資料根目錄內。
/// </summary>
public class FileReportReader : IReportReader
{
    private readonly string _dataRoot;

    public FileReportReader(string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? AppContext.BaseDirectory);
    }

    public string? Read(string reportRef)
    {
        if (string.IsNullOrWhiteSpace(reportRef)) return null;

        string fullPath;
        try
        {
            // 報告參照可能是完整路徑（批次寫入時的形式）或相對於資料根目錄的路徑
            fullPath = Path.GetFullPath(Path.IsPathRooted(reportRef)
                ? reportRef
                : Path.Combine(_dataRoot, reportRef));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsWithinDataRoot(fullPath)) return null;
        if (!File.Exists(fullPath)) return null;

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    /// <summary>
    /// 純前綴比對是不夠的：資料根目錄 C:\data 會誤放行 C:\databad\x.txt
    /// （字串前綴相符，但不是子目錄）。比對前補上目錄分隔符號才是「在目錄之內」的正確語意。
    /// </summary>
    private bool IsWithinDataRoot(string fullPath)
    {
        var root = _dataRoot.EndsWith(Path.DirectorySeparatorChar) || _dataRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? _dataRoot
            : _dataRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

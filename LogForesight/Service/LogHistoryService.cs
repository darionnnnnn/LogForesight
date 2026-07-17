using System.Text.Json;

namespace LogForesight;

public class DailyAnalysisRecord
{
    public DateTime Date { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }
    public List<LogIssueSignature> TopIssues { get; set; } = new();

    /// <summary>程式比對歷史後偵測到的頻率異常（首次出現、頻率上升、整體錯誤量突增）</summary>
    public List<string> TrendAlerts { get; set; } = new();

    /// <summary>跨 log 關聯訊號：多個獨立事件的已知攻擊鏈/故障鏈組合（CorrelationAnalyzer 確定性比對）</summary>
    public List<string> CorrelationAlerts { get; set; } = new();

    public string RiskLevel { get; set; } = string.Empty;

    // AI 回傳的結構化結果（JSON 契約解析後的欄位），後續接 mail/webhook 可直接取用
    public string Summary { get; set; } = string.Empty;
    public string TrendAssessment { get; set; } = string.Empty;
    public List<string> Recommendations { get; set; } = new();

    /// <summary>false = 統計模式紀錄（AI 未呼叫或呼叫失敗時的降級紀錄）</summary>
    public bool AiAnalyzed { get; set; } = true;

    /// <summary>前置掃描檢視的低嚴重度項目數（0 = 當日事件種類未超過主 prompt 上限，未觸發掃描）</summary>
    public int ScreenedTailCount { get; set; }

    /// <summary>前置掃描判定值得注意的項目（事件 + AI 掃描意見），已回流主分析一併判讀</summary>
    public List<string> ScreeningNotes { get; set; } = new();

    /// <summary>風險「中」以上時輸出的報告檔完整路徑（export/{日期}.txt），無風險為 null</summary>
    public string? ReportFile { get; set; }
}

/// <summary>
/// 以 JSON Lines 格式儲存每日分析結果的歷史紀錄（單行一筆，可安全地逐日 append，不需整檔重寫）
/// </summary>
public class LogHistoryService
{
    private readonly string _filePath;

    public LogHistoryService(string? filePath = null)
    {
        // 預設放執行檔同目錄（與 export 一致），方便部署時整個資料夾搬移；
        // 用 AppContext.BaseDirectory 而非 CurrentDirectory，排程執行時後者可能是 system32
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "history.txt");

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string FilePath => _filePath;

    public void Append(DailyAnalysisRecord record)
    {
        var json = JsonSerializer.Serialize(record);
        File.AppendAllText(_filePath, json + Environment.NewLine);
    }

    /// <summary>該日期已分析過就跳過，重跑不會產生重複紀錄</summary>
    public bool HasRecord(DateTime date)
    {
        return ReadRecent(int.MaxValue).Any(r => r.Date.Date == date.Date);
    }

    /// <summary>
    /// 清除超過保留天數的舊紀錄，避免歷史檔無限增長。回傳清除的筆數。
    /// 保留的行原樣寫回（不重新序列化），無法解析的行一併清除。
    /// </summary>
    public int Prune(int retentionDays)
    {
        if (!File.Exists(_filePath))
        {
            return 0;
        }

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var allLines = File.ReadAllLines(_filePath);
        var keptLines = allLines
            .Where(line => TryParse(line)?.Date.Date >= cutoff)
            .ToArray();

        if (keptLines.Length == allLines.Length)
        {
            return 0;
        }

        File.WriteAllLines(_filePath, keptLines);
        return allLines.Length - keptLines.Length;
    }

    public List<DailyAnalysisRecord> ReadRecent(int days)
    {
        if (!File.Exists(_filePath))
        {
            return new List<DailyAnalysisRecord>();
        }

        return File.ReadLines(_filePath)
            .Select(TryParse)
            .Where(r => r != null)
            .Cast<DailyAnalysisRecord>()
            .OrderByDescending(r => r.Date)
            .Take(days)
            .OrderBy(r => r.Date)
            .ToList();
    }

    private static DailyAnalysisRecord? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<DailyAnalysisRecord>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

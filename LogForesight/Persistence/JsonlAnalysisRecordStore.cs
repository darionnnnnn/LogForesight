using System.Text.Json;
using NLog;

namespace LogForesight;

/// <summary>
/// 以 JSON Lines 格式儲存每日分析結果的歷史紀錄（單行一筆，可安全地逐日 append，不需整檔重寫）。
/// 這是 <see cref="IAnalysisRecordStore"/> 的預設實作；換成 DB 後端時只需新增另一個實作類別，
/// 呼叫端（LogAnalysisService 等）完全不用修改，因為都只依賴介面（DIP/OCP）。
/// </summary>
public class JsonlAnalysisRecordStore : IAnalysisRecordStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _filePath;

    public JsonlAnalysisRecordStore(string? filePath = null)
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

    public string Location => _filePath;

    /// <summary>相容舊稱呼——README 與既有文件都提到「history.txt」的完整路徑</summary>
    public string FilePath => _filePath;

    public void Append(DailyAnalysisRecord record)
    {
        var json = JsonSerializer.Serialize(RecordStorageShaper.ForStorage(record));
        File.AppendAllText(_filePath, json + Environment.NewLine);
    }

    /// <summary>該日期已分析過就跳過，重跑不會產生重複紀錄</summary>
    public bool HasRecord(DateTime date)
    {
        return ReadAll().Any(r => r.Date.Date == date.Date);
    }

    /// <summary>
    /// 重寫對應日期那一行，附掛週體檢結果。單機/單週一次的低頻操作，重寫整檔的成本可接受
    /// （與現有 Prune 的做法一致）。找不到對應日期時安靜略過並記 WARN。
    /// </summary>
    public void AttachWeeklyCheckup(DateTime date, WeeklyCheckupResult checkup)
    {
        if (!File.Exists(_filePath))
        {
            Log.Warn("AttachWeeklyCheckup：歷史檔不存在，無法附掛 {Date:yyyy-MM-dd} 的週體檢結果", date);
            return;
        }

        var lines = File.ReadAllLines(_filePath);
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var record = TryParse(lines[i]);
            if (record == null || record.Date.Date != date.Date)
            {
                continue;
            }

            record.WeeklyCheckup = checkup;
            lines[i] = JsonSerializer.Serialize(record);
            changed = true;
            break;
        }

        if (changed)
        {
            File.WriteAllLines(_filePath, lines);
        }
        else
        {
            Log.Warn("AttachWeeklyCheckup：找不到 {Date:yyyy-MM-dd} 的既有紀錄，週體檢結果未附掛", date);
        }
    }

    public DateTime? LastWeeklyCheckupDate()
    {
        return ReadAll()
            .Where(r => r.WeeklyCheckup != null)
            .Select(r => (DateTime?)r.WeeklyCheckup!.CheckupDate.Date)
            .OrderByDescending(d => d)
            .FirstOrDefault();
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
        return ReadAll()
            .OrderByDescending(r => r.Date)
            .Take(days)
            .OrderBy(r => r.Date)
            .ToList();
    }

    private List<DailyAnalysisRecord> ReadAll()
    {
        if (!File.Exists(_filePath))
        {
            return new List<DailyAnalysisRecord>();
        }

        return File.ReadLines(_filePath)
            .Select(TryParse)
            .Where(r => r != null)
            .Cast<DailyAnalysisRecord>()
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

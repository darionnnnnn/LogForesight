using System.Text;
using System.Text.Json;
using NLog;

namespace LogForesight;

/// <summary>
/// 以 JSON Lines 格式儲存每日分析結果的歷史紀錄（單行一筆，可安全地逐日 append，不需整檔重寫）。
/// 這是 <see cref="IAnalysisRecordStore"/> 的預設實作；換成 DB 後端時只需新增另一個實作類別，
/// 呼叫端（LogAnalysisService 等）完全不用修改，因為都只依賴介面（DIP/OCP）。
/// </summary>
public class JsonlAnalysisRecordStore : IAnalysisRecordStore, IAnalysisRecordQuery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int RewriteRetryCount = 10;
    private const int RewriteRetryDelayMs = 50;

    /// <summary>「檔案暫時不見」的重試上限，只在檔案曾經存在時適用（見 OpenForRead）</summary>
    private const int MissingFileRetryCount = 5;

    private readonly string _filePath;

    /// <summary>上次讀取時的壞行數，用來只在數字變化時記 log（見 ReportBadLines）</summary>
    private int _lastBadLineCount;

    /// <summary>是否曾成功開啟過此檔——用來區分「首次執行還沒有檔」與「正在被替換」</summary>
    private volatile bool _fileSeen;

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

        var lines = ReadAllLinesTolerant();
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
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
            RewriteAtomic(lines);
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
        var allLines = ReadAllLinesTolerant();
        var keptLines = allLines
            .Where(line => TryParse(line)?.Date.Date >= cutoff)
            .ToList();

        if (keptLines.Count == allLines.Count)
        {
            return 0;
        }

        RewriteAtomic(keptLines);
        return allLines.Count - keptLines.Count;
    }

    public List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days)
    {
        // 窗長含錨定日本身，所以往回 days-1 天
        var from = anchorDate.Date.AddDays(-(days - 1));
        var to = anchorDate.Date;

        return ReadAll()
            .Where(r => r.Date.Date >= from && r.Date.Date <= to)
            .OrderBy(r => r.Date)
            .ToList();
    }

    public bool HasAnyRecord() => ReadAll().Count > 0;

    // ── IAnalysisRecordQuery（Web 查詢，見 IAnalysisRecordQuery 的介面註解）──────────
    // JSONL 後端就是把整份 history.txt 讀出來後在記憶體篩選。前期單機資料量（90 天 × 1 台）
    // 完全負擔得起；SQL 後端會把同一組條件轉成真正的查詢，語意由合約測試保證一致。

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        // Hosts 為 null = 不限；空集合 = 查不到任何資料（授權範圍為空）。
        // 這個區別是授權正確性的關鍵，不能簡化成「空的就當不限」——
        // 空集合建出來的 matcher 兩個索引都是空的，天然就是「什麼都不命中」
        var matcher = filter.Hosts == null ? null : new HostMatcher(filter.Hosts);

        return ReadAll()
            .Where(r => (matcher == null || matcher.Matches(r)) && Matches(r, filter))
            .OrderByDescending(r => r.Date)
            .ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DailyAnalysisRecord? GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date)
    {
        if (hosts.Count == 0) return null;

        var sameDay = ReadAll().Where(r => r.Date.Date == date.Date).ToList();
        if (sameDay.Count == 0) return null;

        // 依傳入順序擇一：合併後同一天可能存活主機與墓碑各有一筆（合併當天兩邊都分析過），
        // 存活主機排在前面，畫面呈現的就會是現行識別下的那筆
        foreach (var host in hosts)
        {
            var matcher = new HostMatcher(new[] { host });
            var match = sameDay.FirstOrDefault(matcher.Matches);
            if (match != null) return match;
        }

        return null;
    }

    private static bool Matches(DailyAnalysisRecord record, RecordQueryFilter filter)
    {
        if (filter.From.HasValue && record.Date.Date < filter.From.Value.Date) return false;
        if (filter.To.HasValue && record.Date.Date > filter.To.Value.Date) return false;

        if (filter.RiskLevels is { Count: > 0 } && !filter.RiskLevels.Contains(record.RiskLevel)) return false;

        if (filter.Categories is { Count: > 0 } &&
            !record.TopIssues.Any(i => filter.Categories.Contains(i.Category)))
        {
            return false;
        }

        if (filter.MinSeverity.HasValue &&
            !record.TopIssues.Any(i => i.Severity >= filter.MinSeverity.Value))
        {
            return false;
        }

        if (filter.EventId.HasValue &&
            !record.TopIssues.Any(i => i.EventId == filter.EventId.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Source) &&
            !record.TopIssues.Any(i => string.Equals(i.Source, filter.Source, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private List<DailyAnalysisRecord> ReadAll()
    {
        var records = new List<DailyAnalysisRecord>();
        int badLines = 0;

        foreach (var line in ReadAllLinesTolerant())
        {
            var record = TryParse(line);
            if (record != null) records.Add(record);
            else if (line.Trim().Length > 0) badLines++;
        }

        ReportBadLines(badLines);
        return records;
    }

    /// <summary>
    /// 逐行讀取，**允許其他人同時寫入與刪除本檔**（<see cref="FileShare.ReadWrite"/> ＋
    /// <see cref="FileShare.Delete"/>）。
    ///
    /// 這個共用模式是 <see cref="RewriteAtomic"/> 能成立的另一半：少了
    /// <c>FileShare.Delete</c>，Web 的每次查詢都會讓批次的 <see cref="File.Replace(string,string,string)"/>
    /// 撞上共用違規；少了 <c>FileShare.ReadWrite</c>，讀取又會擋住批次的 Append。
    /// 兩邊要一起做，單獨改一邊沒有意義。
    /// </summary>
    private List<string> ReadAllLinesTolerant()
    {
        var lines = new List<string>();

        using var stream = OpenForRead();
        if (stream == null) return lines;

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) lines.Add(line);

        return lines;
    }

    /// <summary>
    /// 開檔讀取，並對「整檔替換的瞬間」短暫重試。
    ///
    /// <see cref="File.Replace(string,string,string)"/> 執行期間，OS 會短暫獨占目的檔——
    /// 讀者恰好在那幾毫秒開檔就會撞上共用違規。**讀取端也要重試**，否則 Web 的查詢
    /// 會在批次重寫的瞬間直接擲例外（使用者看到的是錯誤頁，不是資料）。
    /// 寫入端的重試只解決反方向的碰撞，兩邊都要做。
    ///
    /// 重試到底仍失敗才讓例外外拋：那代表有長時間持檔者（例如有人用記事本開著
    /// history.txt），是真的該讓人知道的狀況，不該假裝查無資料。
    ///
    /// **「檔案不存在」也要重試**，但只在這個檔案曾經被成功開啟過的情況下——
    /// <see cref="RewriteAtomic"/> 用的 File.Replace 在替換過程中會讓目的檔短暫消失，
    /// 此時直接回空清單就是「查詢突然查無資料」。用曾否見過檔案來區分：
    /// 首次執行（真的還沒有 history.txt）不重試、直接回空，不讓空資料庫的每次查詢都空等。
    /// </summary>
    private FileStream? OpenForRead()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(
                    _filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _fileSeen = true;
                return stream;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!_fileSeen || attempt >= MissingFileRetryCount) return null;
                Thread.Sleep(RewriteRetryDelayMs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && attempt < RewriteRetryCount)
            {
                Thread.Sleep(RewriteRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// 整檔重寫：寫暫存檔 → <see cref="File.Replace(string,string,string)"/> 原子替換。
    ///
    /// 直接覆寫（<c>File.WriteAllLines</c>）會讓正在讀取的 Web **看到截斷到一半的檔案**，
    /// 而解析失敗的行是被略過的——結果是查詢少了幾天資料卻沒有任何跡象。原子替換之下，
    /// 讀者要嘛看到舊的完整檔案、要嘛看到新的完整檔案，不存在中間狀態。
    ///
    /// 寫入者只有批次一個（Web 對 history.txt 唯讀）且批次本身有單一執行個體互斥鎖，
    /// 所以這裡**不需要** hosts.json 那套跨程序鎖檔——寫入者對寫入者的競態已在結構上排除，
    /// 剩下的只有「讀者看到半截檔案」，原子替換就能解，加鎖只會讓查詢與寫入互相排隊。
    /// </summary>
    private void RewriteAtomic(IEnumerable<string> lines)
    {
        var tempPath = _filePath + ".tmp";
        File.WriteAllLines(tempPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // **為什麼是 File.Replace 而不是 File.Move(overwrite)**（兩者都試過，各有一個缺陷）：
        //   - File.Move(overwrite)：沒有空窗，但目的檔只要有人開著就直接失敗——
        //     Web 持續查詢時，寫入端會反覆重試到放棄。
        //   - File.Replace：容忍持有 FileShare.Delete 的讀者，代價是替換過程中目的檔
        //     有一段**短暫不存在**的空窗（實測密集重寫下約四分之一的併發讀取會撞到）。
        // 選 Replace，因為「寫入端因讀者而失敗」無法在讀取端補救，
        // 而空窗可以——讀取端對此重試即可（見 OpenForRead）。兩邊要一起看才成立。
        //
        // 讀者持檔的瞬間仍可能撞上共用違規：單寫入者環境下衝突只可能來自秒級的讀取，
        // 短退避重試必然成功；重試到底仍失敗代表有未知的長時間持檔者，
        // 此時讓例外外拋——靜默放棄 Prune／體檢附掛比顯性失敗更糟。
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
                return;
            }
            // UnauthorizedAccessException 也要重試：目的檔正被開啟時，替換可能以這個型別失敗，
            // 與 IOException 是同一種暫時性碰撞（JsonCollectionFile 的鎖檔取得也是這樣處理）
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && attempt < RewriteRetryCount)
            {
                Thread.Sleep(RewriteRetryDelayMs);
            }
            catch
            {
                // 失敗時不留下暫存檔，避免下次重寫誤用殘檔
                TryDeleteTemp(tempPath);
                throw;
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (IOException)
        {
            // 清不掉就算了，下次重寫會直接覆寫它
        }
    }

    /// <summary>
    /// 壞行顯性化。**數字有變化才記**：Web 每次查詢都全檔重讀，同一批壞行若每次都記，
    /// 診斷 log 會被洗掉——訊號淹沒在重複裡等於沒有訊號。
    /// </summary>
    private void ReportBadLines(int badLines)
    {
        var previous = Interlocked.Exchange(ref _lastBadLineCount, badLines);
        if (badLines == previous) return;

        if (badLines > 0)
        {
            Log.Warn("{File} 有 {Count} 行無法解析，已略過（該日資料在查詢與趨勢基準中都不會出現）",
                _filePath, badLines);
        }
        else
        {
            Log.Info("{File} 先前的無法解析行已消失", _filePath);
        }
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

namespace LogForesight;

/// <summary>唯讀存取每日分析紀錄。未來查詢/UI 專案只依賴這個介面（ISP），不會被迫依賴寫入或清理能力</summary>
public interface IAnalysisRecordReader
{
    /// <summary>
    /// <paramref name="anchorDate"/> 往回 <paramref name="days"/> 天的紀錄，依日期升冪排序。
    /// 窗口為日期區間 <c>[anchor-(days-1), anchor]</c>，**含兩端**。
    /// DB 實作對應：<c>WHERE date BETWEEN @anchor-(days-1) AND @anchor ORDER BY date</c>。
    ///
    /// **錨定日之後的紀錄一律不回傳**——呼叫端分析哪一天，基準就只能是那一天之前的世界。
    /// 這不是防禦性的多此一舉：回補流程會分析「已經有後續紀錄」的日子（某天執行中斷、
    /// 之後幾天照常執行），而 <c>TrendAnalyzer</c> 拿到什麼就當基準、不自行過濾日期，
    /// 所以未來的紀錄一旦混進來，就是拿後來發生的事去判斷過去那一天。
    ///
    /// 窗長刻意由呼叫端連同錨定日一起指定，不提供「以今天為錨」的便利多載：
    /// 少傳一個參數就編譯得過、行為卻悄悄錯掉，正是這個設計要關掉的失誤路徑。
    /// </summary>
    List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days);

    /// <summary>
    /// 是否存在任何紀錄（不限日期）。用於「首次執行、還沒有任何基準」的判定。
    /// 與 <see cref="ReadRecent"/> 分開的理由：錨定窗下「近 1 天有沒有紀錄」問的是**今天**，
    /// 和「有沒有歷史」是兩件事，不該讓後者搭前者的便車。
    /// DB 實作對應：<c>EXISTS</c>。
    /// </summary>
    bool HasAnyRecord();

    /// <summary>該日期是否已有紀錄——回補流程用來判斷是否跳過，重複執行不會產生重複資料</summary>
    bool HasRecord(DateTime date);

    /// <summary>最近一次已執行週體檢的日期，無紀錄為 null（用於判斷是否該補跑週體檢）</summary>
    DateTime? LastWeeklyCheckupDate();
}

/// <summary>寫入每日分析紀錄。契約：append-only，且不保證同日去重——去重由呼叫端以 <see cref="IAnalysisRecordReader.HasRecord"/> 防護</summary>
public interface IAnalysisRecordWriter
{
    void Append(DailyAnalysisRecord record);

    /// <summary>清除超過保留天數的舊紀錄，回傳清除筆數</summary>
    int Prune(int retentionDays);

    /// <summary>
    /// 將週體檢結果附掛到已存在的當日紀錄。週體檢在每日分析＋Append 之後才執行（需要讀到當天剛寫入的統計），
    /// 所以是對既有紀錄的一次更新，不是新增一筆——JSONL 實作會重寫該行；DB 後端會是一次 UPDATE。
    /// 找不到對應日期的紀錄時安靜略過（理論上不應發生，因為呼叫前一定先做過當日分析）。
    /// </summary>
    void AttachWeeklyCheckup(DateTime date, WeeklyCheckupResult checkup);
}

/// <summary>
/// 分析 pipeline 實際使用的完整存取介面（讀+寫合一）。查詢用途的消費端應改依賴上面兩個較窄的介面。
/// </summary>
public interface IAnalysisRecordStore : IAnalysisRecordReader, IAnalysisRecordWriter
{
    /// <summary>供 console/log 顯示的位置描述（今日為檔案完整路徑，未來 DB 後端可能是連線字串摘要）</summary>
    string Location { get; }
}

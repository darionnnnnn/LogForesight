namespace LogForesight;

/// <summary>唯讀存取每日分析紀錄。未來查詢/UI 專案只依賴這個介面（ISP），不會被迫依賴寫入或清理能力</summary>
public interface IAnalysisRecordReader
{
    /// <summary>近 N 天的紀錄，依日期升冪排序</summary>
    List<DailyAnalysisRecord> ReadRecent(int days);

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

namespace LogForesight;

/// <summary>
/// 風險日處理狀態的讀寫（↔ lf_record_handling ＋ lf_record_handling_log）。
///
/// 快照與歷程分開的語意寫在介面上：<see cref="Save"/> 更新當前狀態、
/// <see cref="AppendLog"/> 追加一筆敘事，**兩者必須成對呼叫**（由 Service 層負責），
/// 否則會出現「狀態變了但歷程沒有記錄」的斷點。
/// </summary>
public interface IRecordHandlingStore
{
    /// <summary>單筆處理狀態；從未處理過回 null</summary>
    RecordHandling? Get(string hostName, DateTime date);

    /// <summary>批次取得多筆（清單頁避免 N 次查詢）</summary>
    List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to);

    /// <summary>所有未結案的處理狀態（儀表板待辦與逾期清單）</summary>
    List<RecordHandling> GetUnresolved();

    void Save(RecordHandling handling);

    void AppendLog(RecordHandlingLog log);

    /// <summary>單一風險日的完整處理歷程，依時間先後排序</summary>
    List<RecordHandlingLog> GetLogs(string hostName, DateTime date);
}

/// <summary>
/// 問題層級處理狀態的讀寫（↔ 未來 lf_issue_handling）。
/// 與 <see cref="IRecordHandlingStore"/> 分開：後者是日層級的案件（處理人／期限／說明），
/// 這裡是同一天內每個問題各自的結案狀態。日層級的結案與否由這裡的資料推導。
/// </summary>
public interface IIssueHandlingStore
{
    /// <summary>單一風險日內所有已標記的問題狀態（未標記的問題不會有列＝未處理）</summary>
    List<IssueHandling> GetForDay(string hostName, DateTime date);

    /// <summary>批次取得多筆（清單／儀表板彙總避免 N 次查詢）</summary>
    List<IssueHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to);

    /// <summary>寫入／更新單一問題的狀態；status 為 null／空字串代表清除該問題的標記（回到未處理）</summary>
    void Save(IssueHandling handling);

    /// <summary>清除某問題的標記（回到未處理）</summary>
    void Clear(string hostName, DateTime date, string issueKey);
}


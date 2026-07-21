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
/// 權限異動的讀寫（↔ lf_permission_changes）。
///
/// **批次與 Web 的寫入職責分離**：批次呼叫 <see cref="AppendChanges"/> 寫入偵測到的異動，
/// Web 呼叫 <see cref="SaveConfirmation"/> 寫入人工確認結果。JSONL 後端下這是兩個檔案
/// （單一寫入者規則），SQL 後端是同一張表的不同欄位——介面隱藏這個差異。
/// </summary>
public interface IPermissionChangeStore
{
    /// <summary>批次寫入本次偵測到的異動（append-only）</summary>
    void AppendChanges(IEnumerable<PermissionChangeRecord> changes);

    /// <summary>依主機與確認狀態查詢；hostNames 為空集合時回空結果（授權範圍為空）</summary>
    List<PermissionChangeRecord> Query(IReadOnlyCollection<string>? hostNames, string? status, int maxCount);

    PermissionChangeRecord? Get(string changeId);

    /// <summary>確認狀態；未確認過的異動回 null（呼叫端視為 pending）</summary>
    PermissionChangeConfirmation? GetConfirmation(string changeId);

    List<PermissionChangeConfirmation> GetConfirmations(IEnumerable<string> changeIds);

    void SaveConfirmation(PermissionChangeConfirmation confirmation);

    /// <summary>待確認筆數（儀表板待辦區）</summary>
    int CountPending(IReadOnlyCollection<string>? hostNames);
}

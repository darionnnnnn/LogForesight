namespace LogForesight.Core.Persistence;

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

    /// <summary>指定處理人名下的全部日層級指派（不論結案與否；「近 N 天已結案」等篩選由呼叫端
    /// 在記憶體處理——docs/archive/FEEDBACK-4-PLAN.md §6 處理人員工作頁）</summary>
    List<RecordHandling> GetByHandler(long userId);

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

    /// <summary>某案件展開寫入的全部逐日列（案件同步展開時定位既有列用，docs/archive/FEEDBACK-4-PLAN.md §0.5）</summary>
    List<IssueHandling> GetByCase(string caseId);

    /// <summary>寫入／更新單一問題的狀態；status 為 null／空字串代表清除該問題的標記（回到未處理）</summary>
    void Save(IssueHandling handling);

    /// <summary>
    /// 批次寫入／更新多筆（案件回溯關聯／狀態同步一次可能涉及上百天，docs/archive/FEEDBACK-4-PLAN.md §0.4）：
    /// 走一次 Mutate，避免逐日呼叫 <see cref="Save"/> 造成 N 次整份 blob 讀改寫。
    /// 與 Save 同語意：單筆 Status 為 null／空字串代表清除。
    /// </summary>
    void SaveMany(IEnumerable<IssueHandling> handlings);

    /// <summary>清除某問題的標記（回到未處理）</summary>
    void Clear(string hostName, DateTime date, string issueKey);
}

/// <summary>
/// 問題案件的讀寫（↔ 未來 lf_issue_cases，docs/archive/FEEDBACK-4-PLAN.md §0）。
/// 案件是（主機、問題簽章）跨日的處理協調紀錄；逐日結案狀態仍在 <see cref="IIssueHandlingStore"/>，
/// 兩者的關係與職責邊界見 <see cref="IssueCase"/> 類別註解。
/// </summary>
public interface IIssueCaseStore
{
    /// <summary>單一（主機, 問題簽章）目前的進行中案件；沒有進行中案件回 null</summary>
    IssueCase? GetOpen(string hostName, string issueKey);

    /// <summary>單一主機全部進行中案件（批次逐日掛接一次撈，docs/archive/FEEDBACK-4-PLAN.md §0.4-C）</summary>
    List<IssueCase> GetOpenForHost(string hostName);

    /// <summary>多台主機的全部案件（含已結案，供依問題視角彙總／處理人工作頁）</summary>
    List<IssueCase> GetMany(IEnumerable<string> hostNames);

    /// <summary>指定處理人名下的全部進行中案件（處理人員工作頁，docs/archive/FEEDBACK-4-PLAN.md §6）</summary>
    List<IssueCase> GetOpenByHandler(long userId);

    /// <summary>
    /// 指定處理人名下的全部案件**含已結案**（docs/archive/FEEDBACK-10-PLAN.md §7）：
    /// 案件授與的可見性以「現在或曾經是處理人」為準——處理過的問題結案後仍要看得到，
    /// 否則使用者剛結案就再也打不開自己寫的處理紀錄。
    /// </summary>
    List<IssueCase> GetByHandler(long userId);

    IssueCase? Get(string caseId);

    void Save(IssueCase issueCase);

    /// <summary>
    /// 批次寫入／更新（docs/SCALE-ISSUE-FIRST-PLAN.md P3，對應體檢 S4）：
    /// 夜間掛接 <c>AttachNewDay</c> 過去在迴圈內逐案 <see cref="Save"/>，
    /// 2000 台每晚約 4000 次寫入、blob 時代每次都是整份讀改寫。
    /// 與 <see cref="IIssueHandlingStore.SaveMany"/> 同一個理由存在。
    /// </summary>
    void SaveMany(IEnumerable<IssueCase> cases);
}


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

    /// <summary>批次附加歷程：續號規則同 AppendLog，整批只讀一次尾端續號</summary>
    void AppendLogs(IReadOnlyList<RecordHandlingLog> logs);

    /// <summary>單一風險日的完整處理歷程，依時間先後排序</summary>
    List<RecordHandlingLog> GetLogs(string hostName, DateTime date);
    /// <summary>有明確原處理人證據的改派歷程，僅讀指定時間以後。</summary>
    List<RecordHandlingLog> GetReassignments(long previousHandlerId, DateTime from);
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

    /// <summary>
    /// 多個案件展開寫入的全部逐日列（批次取消用，依 case_id 精確查、store 內分批）。
    /// </summary>
    List<IssueHandling> GetByCases(IReadOnlyCollection<string> caseIds);

    /// <summary>
    /// 案件目前這一輪「觀察中」的開始時間：取該案件展開寫入的逐日列裡、狀態為 observing 者最早的 UpdatedAt（實作就是這個最小值）。
    /// 它等於「最近一次被回覆為觀察中的時間」，靠一個不變式：每次回覆為觀察中，案件同步都會把可覆寫的逐日列整批改寫成同一個 UpdatedAt（回覆時間），
    /// 夜間掛接只在後面追加較晚的新日子，所以最小值恆為最近一次回覆的時間；中間改成別的狀態再回覆回觀察中，同樣整批重寫、時間更新。
    /// 沒有任何觀察中逐日列的案件不在結果裡（呼叫端退回案件本身的最後更新時間）。單一 GROUP BY、依 case_id 分批。
    /// </summary>
    Dictionary<string, DateTime> GetObservationStarts(IReadOnlyCollection<string> caseIds) =>
        GetByCases(caseIds)
            .Where(h => h.CaseId != null && h.Status == IssueHandlingStatuses.Observing)
            .GroupBy(h => h.CaseId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(h => h.UpdatedAt), StringComparer.Ordinal);

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
/// 「沿用此問題上次的說明」的查詢（回饋第 50 輪 C-4）。由 <see cref="Sql.EfIssueHandlingStore"/> 實作；
/// 獨立成窄介面是為了不讓 <see cref="IIssueHandlingStore"/> 的每個替身都得跟著實作一個用不到的方法。
/// </summary>
public interface IIssueNoteQuery
{
    /// <summary>
    /// 此問題簽章最新一筆非空白說明（依 UpdatedAt 降冪）。
    /// <paramref name="visibleHostNameKeys"/> 為 host_name_key 集合：null＝不限（全域可見者），
    /// 非 null 時只在這些主機內找——空集合一律回 null。
    /// </summary>
    (string HostName, DateTime RecordDate, string Note, DateTime UpdatedAt)? GetLatestNote(
        string issueKey, IReadOnlyCollection<string>? visibleHostNameKeys);
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

    /// <summary>
    /// 多台主機的進行中案件。已結案案件在保留期內不清除，3000 台時遠多於進行中的；只要進行中的呼叫端用這個，
    /// 不要取回全部再自己濾（正式實作在 SQL 端過濾）。
    /// </summary>
    List<IssueCase> GetOpenMany(IEnumerable<string> hostNames) => GetMany(hostNames).Where(c => c.ClosedAt == null).ToList();

    /// <summary>
    /// 「不再打擾」的候選案件（未計 30 天解除）：指定主機上，每個（主機, 問題鍵）建立最晚的未取消案件，
    /// 且它是不處理類狀態（<see cref="IssueHandlingStatuses.Dismissals"/>）、<see cref="IssueCase.BlocksRedispatch"/> 不是 false。
    /// 定義等同 <c>DispatchContext.DismissalCandidates</c>（待派頁恢復清單與計數用）；正式實作在資料庫端取，
    /// 候選集合只有被擋的案件而不是主機全部案件。呼叫端再以 <c>DispatchContext.DismissedCases</c> 套用解除條件。
    /// 預設實作（測試替身）逐主機取全部案件後在記憶體套同一個判定。
    /// </summary>
    List<IssueCase> GetDismissalCandidates(IEnumerable<string> hostNames) =>
        GetMany(hostNames)
            .GroupBy(c => HostNameKey.Of(c.HostName), StringComparer.Ordinal)
            .SelectMany(g => DispatchContext.DismissalCandidates(g))
            .ToList();

    /// <summary>指定處理人名下的全部進行中案件（處理人員工作頁，docs/archive/FEEDBACK-4-PLAN.md §6）</summary>
    List<IssueCase> GetOpenByHandler(long userId);

    /// <summary>
    /// 指定處理人名下的全部案件**含已結案**（docs/archive/FEEDBACK-10-PLAN.md §7）：
    /// 案件授與的可見性以「現在或曾經是處理人」為準——處理過的問題結案後仍要看得到，
    /// 否則使用者剛結案就再也打不開自己寫的處理紀錄。
    /// </summary>
    List<IssueCase> GetByHandler(long userId);

    /// <summary>指定處理人在指定主機上是否有案件，含已結案、排除因取消交辦而關閉的案件（案件授與語意）</summary>
    bool HasCaseOnHost(long handlerId, string hostName);

    /// <summary>指定處理人在指定主機上的問題鍵集合，含已結案、排除取消的案件（案件授與語意）</summary>
    HashSet<string> IssueKeysOnHost(long handlerId, string hostName);

    /// <summary>指定處理人名下有案件的主機名清單，含已結案、排除取消的案件（案件授與語意）</summary>
    List<string> HostNamesWithCases(long handlerId);

    /// <summary>結案時間 &gt;= <paramref name="since"/> 且狀態為 resolved 的案件（派工延續性用）</summary>
    List<IssueCase> GetResolvedSince(DateTime since);

    IssueCase? Get(string caseId);

    void Save(IssueCase issueCase);

    /// <summary>
    /// 批次寫入／更新（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3，對應體檢 S4）：
    /// 夜間掛接 <c>AttachNewDay</c> 過去在迴圈內逐案 <see cref="Save"/>，
    /// 2000 台每晚約 4000 次寫入、blob 時代每次都是整份讀改寫。
    /// 與 <see cref="IIssueHandlingStore.SaveMany"/> 同一個理由存在。
    /// </summary>
    void SaveMany(IEnumerable<IssueCase> cases);

    /// <summary>全部進行中案件的（host_name_key, issue_key）：單一查詢、只選這兩欄（待派清單扣除已有人處理的缺口用）</summary>
    List<(string HostNameKey, string IssueKey)> GetOpenKeys();

    /// <summary>某問題的全部進行中案件（source 不分大小寫，比對正規化的 source_key）</summary>
    List<IssueCase> GetOpenByIssue(string source, int eventId);

    /// <summary>指定主機集合中某問題的進行中案件（主機以 host_name_key 比對，分批查）</summary>
    List<IssueCase> GetOpenMany(IEnumerable<string> hostNames, string source, int eventId);

    /// <summary>某交辦單的成員案件（分頁，含已結案）</summary>
    List<IssueCase> GetByWorkOrder(long workOrderId, int skip, int take);

    int CountByWorkOrder(long workOrderId);

    /// <summary>
    /// 交辦單的**進行中**成員（精簡欄位版，不含 PRTG 證據與逐日意圖）：單一查詢取多張單，
    /// 復發、主機數與觀察統計用（<see cref="IssueRecurrence"/>）。<paramref name="workOrderIds"/> 為 null＝全部交辦單的進行中成員
    /// （管理者提醒掃全站用）；空集合回空清單。未連結交辦單的案件不回。
    /// </summary>
    List<IssueCase> GetActiveMemberSummaries(IReadOnlyCollection<long>? workOrderIds) =>
        (workOrderIds ?? GetAllOrderIds()).SelectMany(id => GetByWorkOrder(id, 0, int.MaxValue)).Where(c => c.ClosedAt == null).ToList();

    /// <summary>
    /// 指定交辦單的**全部**成員（含已結案，精簡欄位版）：「單內沒有進行中成員時取全部成員最後已知嚴重度」用，
    /// 單一查詢取多張單。空集合回空清單。預設實作逐張 <see cref="GetByWorkOrder"/>；EF 實作依 500 張一批。
    /// </summary>
    List<IssueCase> GetMemberSummaries(IReadOnlyCollection<long> workOrderIds) =>
        workOrderIds.Distinct().SelectMany(id => GetByWorkOrder(id, 0, int.MaxValue)).ToList();

    /// <summary>預設實作的全站交辦單枚舉：只有替身沒覆寫 <see cref="GetActiveMemberSummaries"/> 時才會走到，EF 實作不使用</summary>
    private IReadOnlyCollection<long> GetAllOrderIds() =>
        throw new NotSupportedException("這個案件 store 沒有實作全站進行中成員查詢。");

    /// <summary>
    /// 已結案（ClosedAt 非 null）且未取消的案件，限定指定主機與問題（Source＋EventId，來源不分大小寫）：
    /// 復發判定的前案候選，一次取回、不逐成員查。<paramref name="closedSince"/> 非 null 時只取結案時間不早於它的案件。
    /// 主機以 host_name_key 比對、分批查；空主機或空問題回空清單。
    /// </summary>
    List<IssueCase> GetClosedHistory(IEnumerable<string> hostNames, IReadOnlyCollection<(string Source, int EventId)> issues, DateTime? closedSince)
    {
        var names = hostNames.Select(HostNameKey.Of).ToHashSet(StringComparer.Ordinal);
        var wanted = issues.Select(i => (WorkOrderIssueKey.SourceKeyOf(i.Source), i.EventId)).ToHashSet();
        return GetMany(hostNames)
            .Where(c => c.ClosedAt != null && !c.Cancelled && c.SourceName != null && c.EventId != null
                        && (closedSince == null || c.ClosedAt >= closedSince)
                        && names.Contains(HostNameKey.Of(c.HostName))
                        && wanted.Contains((WorkOrderIssueKey.SourceKeyOf(c.SourceName), c.EventId.Value)))
            .ToList();
    }

    /// <summary>
    /// 每張單不重複的主機數（host_name_key 去重，含已結案成員與已停用主機）；零成員的單不出現在結果裡。
    /// 單一 GROUP BY，查詢次數不隨成員數增長。
    /// </summary>
    Dictionary<long, int> CountDistinctHosts(IReadOnlyCollection<long> workOrderIds) =>
        workOrderIds.Distinct()
            .Select(id => (Id: id, Hosts: GetByWorkOrder(id, 0, int.MaxValue).Select(c => HostNameKey.Of(c.HostName)).Distinct().Count()))
            .Where(x => x.Hosts > 0)
            .ToDictionary(x => x.Id, x => x.Hosts);

    /// <summary>
    /// 某交辦單的成員（狀態與主機名篩選、分頁），依 host_name_key、case_id 排序；
    /// 狀態為「全部」時同主機的新舊案件相鄰、建立日新到舊（host_name_key、created_at 降冪、case_id）。
    /// 查詢次數不隨成員數增長；主機名清單依既有 500 一批分批比對。條件不合法擲 ArgumentException。
    /// </summary>
    (List<IssueCase> Items, int Total) QueryMembers(WorkOrderMemberQuery q);

    /// <summary>待背景逐日同步的案件（day_sync_pending = 1），依 updated_at、case_id 升冪取前 take 筆</summary>
    List<IssueCase> GetDaySyncPending(int take);

    int CountDaySyncPending();

    /// <summary>
    /// 背景寫完逐日列後清旗標：只在案件上存的意圖仍等於 <paramref name="intent"/>（序列化字串相同）時
    /// 清除旗標與意圖，回傳是否清到。期間使用者送了新意圖就不清，留給下一批依新意圖重寫。
    /// </summary>
    bool ClearDaySyncPendingIfUnchanged(string caseId, CaseDayIntent intent);
}


namespace LogForesight.Core.Persistence;

/// <summary>
/// 交辦單的讀寫（↔ lf_work_orders ＋ lf_work_order_events）。
/// store 只管持久化：建單、改派、結案推導等協調邏輯不在這裡。
/// 「進行中」一律指 <see cref="WorkOrder.ClosedAt"/> == null；問題來源比對不分大小寫（source_key 正規化大寫）。
/// </summary>
public interface IWorkOrderStore
{
    WorkOrder? Get(long workOrderId);

    /// <summary>某處理人對某問題的進行中交辦單（資料庫保證至多一張）</summary>
    WorkOrder? GetActiveFor(long handlerId, string source, int eventId);

    List<WorkOrder> GetActiveByIssue(string source, int eventId);

    List<WorkOrder> GetActiveByHandler(long handlerId);

    /// <summary>全部進行中（closed_at IS NULL）的單；派工脈絡一趟執行建一次索引用</summary>
    List<WorkOrder> GetAllActive();

    /// <summary>新增並回傳新 id（同時回填到 <paramref name="order"/>）</summary>
    long Insert(WorkOrder order);

    /// <summary>
    /// 以 <see cref="WorkOrder.UpdatedAt"/> 做併發檢查後更新；衝突時擲
    /// <c>DbUpdateConcurrencyException</c>（不吞、不重試——重試是協調層的事）。
    /// 成功後 UpdatedAt 更新為現在並回填物件。
    /// </summary>
    void Save(WorkOrder order);

    void AppendEvent(WorkOrderEvent evt);

    /// <summary>依 CreatedAt 升冪</summary>
    List<WorkOrderEvent> ListEvents(long workOrderId);

    /// <summary>一次查詢（GROUP BY work_order_id）取得多張單的成員計數；ids 為空回空字典</summary>
    Dictionary<long, WorkOrderMemberCounts> CountMembers(IReadOnlyCollection<long> workOrderIds);

    /// <summary>
    /// 交辦單清單（篩選／排序／分頁）：狀態條件以 EXISTS 子查詢表達、不把成員拉回記憶體；
    /// 查詢次數固定（總數一次、本頁一次），不隨單數或成員數增長。條件不合法擲 ArgumentException。
    /// </summary>
    WorkOrderPage QueryOrders(WorkOrderQuery q);

    /// <summary>一次查詢取得每位「有進行中交辦單或近 7 日有結案單」的處理人負載</summary>
    List<HandlerLoad> LoadBoard();

    /// <summary>
    /// 單一處理人的進行中摘要（我的交辦清單與側欄徽章）：單一查詢；沒有進行中單回全 0。
    /// 逾期判準同 <see cref="WorkOrderQueries.IsOverdue"/>。
    /// </summary>
    WorkOrderHandlerSummary HandlerSummary(long handlerId);

    /// <summary>
    /// 進行中、但底下已沒有任何進行中案件的單（含零成員），依 work_order_id 升冪取前 <paramref name="take"/> 筆。
    /// 單句 SQL；供背景結案掃描補上「成員在交辦單以外的路徑全結案」的單。
    /// </summary>
    List<long> FindActiveWithoutActiveMembers(int take);

    /// <summary>刪除結案早於保留期的交辦單與其事件，回傳刪除的單數</summary>
    int PruneClosed(int retentionDays);
}

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

    /// <summary>一次查詢取得每位有進行中交辦單的處理人負載</summary>
    List<HandlerLoad> LoadBoard();

    /// <summary>刪除結案早於保留期的交辦單與其事件，回傳刪除的單數</summary>
    int PruneClosed(int retentionDays);
}

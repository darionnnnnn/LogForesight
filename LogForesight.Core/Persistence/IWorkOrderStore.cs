namespace LogForesight.Core.Persistence;

/// <summary>
/// 交辦單的讀寫（↔ lf_work_orders ＋ lf_work_order_events）。
/// store 只管持久化：建單、改派、結案推導等協調邏輯不在這裡。
/// 「進行中」一律指 <see cref="WorkOrder.ClosedAt"/> == null；問題來源比對不分大小寫（source_key 正規化大寫）。
/// </summary>
/// <summary>續掛停止欄位的一筆更新（<see cref="IWorkOrderStore.SetContinuationStops"/>）</summary>
public sealed record ContinuationStopUpdate(long WorkOrderId, string? Reason, DateTime At);

public interface IWorkOrderStore
{
    WorkOrder? Get(long workOrderId);

    /// <summary>
    /// 一次取多張單（含已結案；不存在的編號不回）：批次改派預覽與套用用，查詢次數不隨張數增長。
    /// 預設實作逐張 <see cref="Get"/>（測試替身）；EF 實作單句 IN 查詢。
    /// </summary>
    List<WorkOrder> GetMany(IReadOnlyCollection<long> workOrderIds) =>
        workOrderIds.Distinct().Select(Get).OfType<WorkOrder>().ToList();

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

    /// <summary>
    /// 只寫 <see cref="WorkOrder.AdminSeenAt"/>（單句 UPDATE）：不動 UpdatedAt，不與處理人回覆等寫入互搶併發權杖。
    /// 單不存在時什麼都不做。
    /// </summary>
    void MarkAdminSeen(long workOrderId, DateTime seenAt);

    /// <summary>
    /// 批次寫入續掛停止欄位（每張單一句 UPDATE）：<see cref="ContinuationStopUpdate.Reason"/> 為 null 即清除（停止時間一併清掉）。
    /// 不動 UpdatedAt，不與其他寫入互搶併發權杖；單不存在時略過。
    /// </summary>
    void SetContinuationStops(IReadOnlyCollection<ContinuationStopUpdate> updates);

    /// <summary>有未讀回覆的單數（SQL 端 COUNT，判定同 <see cref="WorkOrder.HasUnreadReply"/>）</summary>
    int CountUnreadReplies();

    /// <summary>
    /// 有未讀回覆、且含至少一個上報中進行中成員的單數（<see cref="CountUnreadReplies"/> 的子集；單一 SQL，不逐張查）。
    /// 上報中進行中＝成員未結案且狀態為 escalated；不排除停用主機，口徑與 <see cref="CountUnreadReplies"/> 相同。
    /// </summary>
    int CountUnreadRepliesWithEscalated();

    void AppendEvent(WorkOrderEvent evt);

    /// <summary>依 CreatedAt 升冪</summary>
    List<WorkOrderEvent> ListEvents(long workOrderId);

    /// <summary>
    /// 一次查詢（GROUP BY work_order_id）取得多張單的成員計數；ids 為空回空字典。
    /// <paramref name="excludedHostKeys"/> 為已停用主機的 host_name_key（大寫）：其進行中成員不計入進行中與各狀態數，改計於 ActiveOnDisabledHosts；null＝不排除。
    /// </summary>
    Dictionary<long, WorkOrderMemberCounts> CountMembers(IReadOnlyCollection<long> workOrderIds, IReadOnlyCollection<string>? excludedHostKeys = null);

    /// <summary>
    /// 交辦單清單（篩選／排序／分頁）：狀態條件以 EXISTS 子查詢表達、不把成員拉回記憶體；
    /// 查詢次數固定（總數一次、本頁一次），不隨單數或成員數增長。條件不合法擲 ArgumentException。
    /// </summary>
    WorkOrderPage QueryOrders(WorkOrderQuery q);

    /// <summary>
    /// 符合條件的**全部**交辦單（不分頁，依建立時間新到舊、單號降冪）：Web 服務對「讀取時推導」的排序與標記篩選
    /// （處理優先序、期限最近、嚴重度、被退回、復發…）要先取得全體、算好鍵、排序後再取頁，不能只排當頁。
    /// <paramref name="q"/> 的 Sort／Page／PageSize 被忽略。預設實作以 <see cref="QueryOrders"/> 逐頁取；EF 實作單句 SQL。
    /// </summary>
    List<WorkOrder> FilterOrders(WorkOrderQuery q)
    {
        var all = new List<WorkOrder>();
        for (var page = 1; ; page++)
        {
            var part = QueryOrders(new WorkOrderQuery
            {
                HandlerIds = q.HandlerIds, Source = q.Source, EventId = q.EventId, Status = q.Status,
                Sort = WorkOrderQueries.SortCreatedDesc, Page = page, PageSize = WorkOrderQueries.MaxOrderPageSize,
                PausedKeys = q.PausedKeys, PausedMode = q.PausedMode, OnlyKeys = q.OnlyKeys, UnreadOnly = q.UnreadOnly,
                ExcludedHostKeys = q.ExcludedHostKeys
            });
            all.AddRange(part.Items);
            if (part.Items.Count < WorkOrderQueries.MaxOrderPageSize || all.Count >= part.Total) return all;
        }
    }

    /// <summary>
    /// 多張單指定動作的事件（依 CreatedAt、EventId 升冪），一次查詢取回；沒有事件的單不出現在結果裡。
    /// 「被退回」的判定（最近一筆退回之後有沒有處理人回覆）用。預設實作逐張 <see cref="ListEvents"/>；EF 實作依 500 張一批。
    /// </summary>
    Dictionary<long, List<WorkOrderEvent>> ListEventsOf(IReadOnlyCollection<long> workOrderIds, IReadOnlyCollection<string> actions)
    {
        var result = new Dictionary<long, List<WorkOrderEvent>>();
        foreach (var id in workOrderIds.Distinct())
        {
            var events = ListEvents(id)
                .Where(e => actions.Contains(e.Action))
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId)
                .ToList();
            if (events.Count > 0) result[id] = events;
        }
        return result;
    }

    /// <summary>
    /// 一次查詢取得每位「有進行中交辦單或近 7 日有結案單」的處理人負載。
    /// <paramref name="excludedHostKeys"/>（已停用主機的 host_name_key）上的成員不計入成員數與逾期成員數；null＝不排除。
    /// </summary>
    List<HandlerLoad> LoadBoard(IReadOnlyCollection<string>? excludedHostKeys = null);

    /// <summary>
    /// 單一處理人的進行中摘要（我的交辦清單與側欄徽章）：單一查詢；沒有進行中單回全 0。
    /// 逾期判準同 <see cref="WorkOrderQueries.IsOverdue"/>。
    /// 四個既有數字（含成員子查詢）一律排除暫停單（組合鍵在 <paramref name="pausedKeys"/> 內的進行中單），
    /// 暫停單另計於 <see cref="WorkOrderHandlerSummary.PausedWorkOrders"/>。
    /// </summary>
    /// <remarks><paramref name="excludedHostKeys"/>（已停用主機的 host_name_key）上的成員不計入成員數與逾期成員數；null＝不排除。</remarks>
    WorkOrderHandlerSummary HandlerSummary(long handlerId, IReadOnlyCollection<string> pausedKeys, IReadOnlyCollection<string>? excludedHostKeys = null);

    /// <summary>
    /// 進行中、但底下已沒有任何進行中案件的單（含零成員），依 work_order_id 升冪取前 <paramref name="take"/> 筆。
    /// 單句 SQL；供背景結案掃描補上「成員在交辦單以外的路徑全結案」的單。
    /// </summary>
    List<long> FindActiveWithoutActiveMembers(int take);

    /// <summary>刪除結案早於保留期的交辦單與其事件，回傳刪除的單數</summary>
    int PruneClosed(int retentionDays);
}

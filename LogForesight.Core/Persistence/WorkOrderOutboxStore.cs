namespace LogForesight.Core.Persistence;

using System.Linq;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

public class WorkOrderOutboxStore : JsonBlobSingleton<WorkOrderOutbox>
{
    public const string BlobKey = "work_order_outbox";
    public const int MaxItems = 20_000;

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly TimeProvider _clock;
    private readonly WorkOrderOutboxDropStore? _drops;

    /// <param name="drops">丟棄紀錄（健康整體狀態的來源）；正式站台一律傳入，丟棄時由這裡記一筆，三個呼叫端不必各自記。</param>
    public WorkOrderOutboxStore(EfJsonBlobStore blob, TimeProvider? clock = null, WorkOrderOutboxDropStore? drops = null) : base(blob)
    {
        _clock = clock ?? TimeProvider.System;
        _drops = drops;
    }

    /// <summary>加入單一待寄項目，見 <see cref="Enqueue(IReadOnlyCollection{WorkOrderOutboxItem})"/>。</summary>
    public int Enqueue(WorkOrderOutboxItem item) => Enqueue(new[] { item });

    /// <summary>
    /// 在**一次**原子更新內加入多筆待寄項目（夜間派工一趟可能上百筆，逐筆更新等於整份 blob 重寫上百次）；
    /// 若超過上限則依 EnqueuedAt 由舊到新移除多出項目，回傳被丟棄的筆數。
    /// EnqueuedAt 一律是**本地時間**：寄送時刻（08:00／12:00）是本地時刻，混用 UTC 會讓項目落到錯的時段。
    /// </summary>
    public int Enqueue(IReadOnlyCollection<WorkOrderOutboxItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;
        foreach (var item in items)
        {
            if (item.EnqueuedAt == default) item.EnqueuedAt = _clock.LocalNow();
        }

        int dropped = 0;
        Update(outbox =>
        {
            foreach (var item in items)
            {
                var key = $"{item.Kind}|{item.WorkOrderId}|{item.RecipientUserId}|{item.EnqueuedAt.Ticks}";
                while (outbox.Items.ContainsKey(key))
                {
                    item.EnqueuedAt = item.EnqueuedAt.AddTicks(1);
                    key = $"{item.Kind}|{item.WorkOrderId}|{item.RecipientUserId}|{item.EnqueuedAt.Ticks}";
                }
                outbox.Items[key] = item;
            }

            if (outbox.Items.Count > MaxItems)
            {
                dropped = outbox.Items.Count - MaxItems;
                var keysToRemove = outbox.Items
                    .OrderBy(kv => kv.Value.EnqueuedAt)
                    .Take(dropped)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in keysToRemove)
                {
                    outbox.Items.Remove(k);
                }
            }
        });

        // 丟棄紀錄寫失敗不能讓入列失敗（項目已經入列）；記錄檔留痕，健康狀態少一次提示
        if (dropped > 0)
        {
            Log.Warn("[Mail] 交辦 outbox 超過上限 {Max} 筆，已移除 {Dropped} 筆最舊的待寄項目", MaxItems, dropped);
            try { _drops?.Record(dropped, _clock.LocalNow()); }
            catch (Exception ex) { Log.Warn(ex, "[Mail] 交辦 outbox 丟棄紀錄寫入失敗（已丟棄 {Dropped} 筆）", dropped); }
        }

        return dropped;
    }
}

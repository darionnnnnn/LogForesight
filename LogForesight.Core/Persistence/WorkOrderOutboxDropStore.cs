namespace LogForesight.Core.Persistence;

using LogForesight.Core.Persistence.Sql;

/// <summary>交辦 outbox 超過上限而丟棄項目的紀錄（獨立的小 blob，健康頁讀它不必反序列化整份 outbox）。</summary>
public sealed class WorkOrderOutboxDropState
{
    /// <summary>目前這段「近期丟棄」累計丟棄的筆數；超過 <see cref="WorkOrderOutboxDropStore.RecentWindow"/> 沒有新丟棄後，下一次丟棄會重新起算。</summary>
    public int RecentDropped { get; set; }

    /// <summary>最近一次丟棄的本地時間；null＝從未丟棄</summary>
    public DateTime? LastDroppedAt { get; set; }
}

/// <summary>
/// 交辦 outbox 丟棄紀錄。消失條件是時間：最近 <see cref="RecentWindow"/> 內沒有新的丟棄，健康狀態就不再提示。
/// 不另做「確認／重置」——丟棄代表待寄通知已經消失、補不回來，沒有可處理的待辦；
/// 只要持續塞滿就會持續被新的丟棄刷新，真正需要的是讓管理者知道有通知遺失並去查為何 outbox 堆積。
/// </summary>
public sealed class WorkOrderOutboxDropStore : JsonBlobSingleton<WorkOrderOutboxDropState>
{
    public const string BlobKey = "work_order_outbox_drops";

    /// <summary>丟棄後多久內仍列入健康整體狀態</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    public WorkOrderOutboxDropStore(EfJsonBlobStore blob) : base(blob) { }

    /// <summary>記一次丟棄（<paramref name="dropped"/> 大於 0 才會寫入）。</summary>
    public void Record(int dropped, DateTime nowLocal)
    {
        if (dropped <= 0) return;
        Update(state =>
        {
            var stillRecent = state.LastDroppedAt is { } last && nowLocal - last <= RecentWindow;
            state.RecentDropped = checked((stillRecent ? state.RecentDropped : 0) + dropped);
            state.LastDroppedAt = nowLocal;
        });
    }
}

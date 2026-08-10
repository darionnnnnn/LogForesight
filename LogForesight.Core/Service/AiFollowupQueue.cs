using System.Threading.Channels;

namespace LogForesight.Core.Service;

/// <summary>
/// NetIQ pipeline 的 AI 待處理佇列（docs/FEEDBACK-12-PLAN.md §3.2）：搜尋＋統計主線把需要 AI
/// 的主機日丟進來，單一背景消費者依序（FIFO）取出跑 AI，讓 NetIQ 搜尋不再被 AI 拖住
/// （現況：<c>NetiqPipelineService</c> 批次內逐台 <c>await</c> AI，本批沒跑完下一天的搜尋
/// 就不會發出）。FIFO 保序是刻意的：消費者處理同一台主機的日期時能保證前一天已經定案，
/// 讓隔日 prompt 引用前一天 AI 摘要的既有語意不因兩階段化而降級。
///
/// **有容量上限、不是無限佇列**：工作項會帶著該主機日的 mapped events（深析報告需要原始 log
/// 摘錄，而原始 log 不落地，只能隨件攜帶），無界佇列在 AI 大幅落後時就是 OOM 候選——本專案
/// 才在規模化輪被 OOM 咬過（docs/SCALE-ISSUE-FIRST-PLAN.md S2），這裡不再開第二個口子。
/// 滿載時 <see cref="EnqueueAsync"/> 背壓讓搜尋主線暫停等待，屬「AI 落後
/// <see cref="Capacity"/> 個主機日」的極端情況，記憶體保護優先於吞吐量。
/// </summary>
public sealed class AiFollowupQueue<T>
{
    /// <summary>預設佇列容量（docs/FEEDBACK-12-PLAN.md §3.2）。</summary>
    public const int Capacity = 200;

    private readonly Channel<T> _channel;

    public AiFollowupQueue(int? capacity = null) =>
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity ?? Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    /// <summary>把工作項排進佇列；佇列滿時等待有空位（背壓讓生產端自然暫停），取消時原樣拋出。</summary>
    public ValueTask EnqueueAsync(T item, CancellationToken ct = default) => _channel.Writer.WriteAsync(item, ct);

    /// <summary>
    /// 非阻塞嘗試入列；佇列已滿時立即回傳 false，不等待（回饋十三輪 A7）。
    /// 供呼叫端在真的要進入 <see cref="EnqueueAsync"/> 的阻塞等待前，先探測一次是否會背壓——
    /// 探測到會背壓時可以先切換進度顯示成「搜尋暫停中」，讓畫面誠實反映現況，
    /// 而不是讓使用者看著進度條卡住、以為程式當掉了。失敗後仍要呼叫 <see cref="EnqueueAsync"/>
    /// 完成真正的入列，這裡只探測不消耗。
    /// </summary>
    public bool TryEnqueue(T item) => _channel.Writer.TryWrite(item);

    /// <summary>宣告不會再有新項目——消費者的 <see cref="ReadAllAsync"/> 讀完既有項目後自然結束，
    /// 不需要額外的哨兵值或逾時判斷。</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>單一消費者依序（FIFO）讀取，直到 <see cref="Complete"/> 後清空為止。</summary>
    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken ct = default) => _channel.Reader.ReadAllAsync(ct);
}

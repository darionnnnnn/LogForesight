namespace LogForesight;

/// <summary>
/// Web AI 加值輸出的快取項（blob key=ai_cache）。鍵＝功能＋日期＋輸入雜湊，
/// 值＝AI 產出。同一份輸入不重算（同日多人瀏覽只有第一人觸發 AI 呼叫），
/// 啟動時清 7 天前舊項（docs/SCALE-2000-PLAN.md §6.1）。
/// </summary>
public class AiCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

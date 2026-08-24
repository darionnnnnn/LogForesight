using LogForesight.Core.Analysis;
using LogForesight.Core.Models;

namespace LogForesight.Core.Persistence;

/// <summary>
/// AI token 用量統計的儲存（回饋二十七輪作業 B），單一物件型 blob，見 <see cref="JsonBlobSingleton{T}"/>。
/// 同時是 <see cref="IAiUsageMeter"/> 的正式實作——AI 呼叫本身已被請求佇列序列化，
/// 每次呼叫多一次小 blob 的讀改寫，相對於一次動輒數十秒的推論可以忽略。
/// </summary>
public class AiUsageStore : JsonBlobSingleton<AiUsageStats>, IAiUsageMeter
{
    /// <summary>每日列保留天數；超過的裁掉，累計值不受影響。</summary>
    public const int RetainDays = 90;

    public const string BlobKey = "ai_usage";

    private readonly Func<DateTime> _now;

    public AiUsageStore(EfJsonBlobStore blob, Func<DateTime>? now = null) : base(blob) =>
        _now = now ?? (() => DateTime.Now);

    public void Record(int promptTokens, int completionTokens, int totalTokens, bool hasUsage)
    {
        // 負值只可能來自壞掉的回應，讓它汙染累計不如當成 0
        if (promptTokens < 0) promptTokens = 0;
        if (completionTokens < 0) completionTokens = 0;
        if (totalTokens < 0) totalTokens = 0;

        var today = _now().ToString("yyyy-MM-dd");

        Update(stats =>
        {
            stats.CountingSince ??= today;

            stats.Total.Calls++;
            stats.Total.PromptTokens += promptTokens;
            stats.Total.CompletionTokens += completionTokens;
            stats.Total.TotalTokens += totalTokens;
            if (!hasUsage) stats.Total.CallsWithoutUsage++;

            var day = stats.Days.FirstOrDefault(d => d.Date == today);
            if (day == null)
            {
                day = new AiUsageDay { Date = today };
                stats.Days.Add(day);
            }

            day.Calls++;
            day.PromptTokens += promptTokens;
            day.CompletionTokens += completionTokens;
            day.TotalTokens += totalTokens;
            if (!hasUsage) day.CallsWithoutUsage++;

            Trim(stats);
        });
    }

    /// <summary>清空重新計算：每日與累計全部歸零，起算日重設為今天。</summary>
    public AiUsageStats Reset() =>
        Update(stats =>
        {
            stats.Days.Clear();
            stats.Total = new AiUsageTotals();
            stats.CountingSince = _now().ToString("yyyy-MM-dd");
        });

    /// <summary>近 <paramref name="days"/> 天的每日用量，新到舊；沒有用量的日子不補空列。</summary>
    public List<AiUsageDay> RecentDays(int days)
    {
        var since = _now().Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
        return Get().Days
            .Where(d => string.CompareOrdinal(d.Date, since) >= 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .ToList();
    }

    protected override void Touch(AiUsageStats value) => value.UpdatedAt = _now();

    private void Trim(AiUsageStats stats)
    {
        if (stats.Days.Count <= RetainDays) return;

        var cutoff = _now().Date.AddDays(-(RetainDays - 1)).ToString("yyyy-MM-dd");
        stats.Days.RemoveAll(d => string.CompareOrdinal(d.Date, cutoff) < 0);
    }
}

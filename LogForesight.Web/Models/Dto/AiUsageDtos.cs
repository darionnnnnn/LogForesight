using LogForesight.Core.Models;
using LogForesight.Core.Persistence;

namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 設定頁「AI 服務」頁籤的 token 用量統計（回饋二十七輪作業 B）。
/// 估費由前端用使用者填的單價即時算，這裡只給量——單價一改就要重算，
/// 後端算好送過來的話每改一次數字都得往返一次。
/// </summary>
public class AiUsageDto
{
    /// <summary>展開表格顯示的天數</summary>
    public const int TableDays = 30;

    /// <summary>累計起算日（yyyy-MM-dd）；從未呼叫過為 null</summary>
    public string? CountingSince { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public AiUsageTotalsDto Today { get; set; } = new();
    public AiUsageTotalsDto Total { get; set; } = new();

    /// <summary>近 <see cref="TableDays"/> 天每日用量，新到舊；沒有用量的日子不列</summary>
    public List<AiUsageDayDto> Days { get; set; } = new();

    public static AiUsageDto From(AiUsageStore store, int tableDays)
    {
        var stats = store.Get();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var todayRow = stats.Days.FirstOrDefault(d => d.Date == today);

        return new AiUsageDto
        {
            CountingSince = stats.CountingSince,
            UpdatedAt = stats.UpdatedAt,
            Today = AiUsageTotalsDto.From(todayRow),
            Total = AiUsageTotalsDto.From(stats.Total),
            Days = store.RecentDays(tableDays)
                .Select(d => new AiUsageDayDto
                {
                    Date = d.Date,
                    Calls = d.Calls,
                    PromptTokens = d.PromptTokens,
                    CompletionTokens = d.CompletionTokens,
                    TotalTokens = d.TotalTokens,
                    CallsWithoutUsage = d.CallsWithoutUsage
                })
                .ToList()
        };
    }
}

public class AiUsageTotalsDto
{
    public long Calls { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }

    /// <summary>回應未帶 usage 的呼叫次數（這些呼叫的 token 記 0）</summary>
    public long CallsWithoutUsage { get; set; }

    public static AiUsageTotalsDto From(AiUsageTotals? source) => source == null
        ? new AiUsageTotalsDto()
        : new AiUsageTotalsDto
        {
            Calls = source.Calls,
            PromptTokens = source.PromptTokens,
            CompletionTokens = source.CompletionTokens,
            TotalTokens = source.TotalTokens,
            CallsWithoutUsage = source.CallsWithoutUsage
        };
}

public class AiUsageDayDto : AiUsageTotalsDto
{
    public string Date { get; set; } = string.Empty;
}

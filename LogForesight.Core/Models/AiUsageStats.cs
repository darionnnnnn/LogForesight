namespace LogForesight.Core.Models;

/// <summary>
/// AI token 用量統計（回饋二十七輪作業 B）。存放走 blob 單例（見 <c>AiUsageStore</c>），
/// 跟著資料庫一起備份／搬遷，不另開資料表也不落地成獨立檔案。
///
/// 「累計」與「每日」是兩套數字：每日只留近 <c>AiUsageStore.RetainDays</c> 天供趨勢檢視，
/// 累計自起算日起不隨每日列被裁掉而減少——使用者問的「目前累計用了多少」要的是後者。
/// </summary>
public class AiUsageStats
{
    /// <summary>累計起算日（yyyy-MM-dd）。首次記錄時寫入，「清空重新計算」時重設為當天。</summary>
    public string? CountingSince { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>自起算日以來的累計值（不受每日列裁切影響）</summary>
    public AiUsageTotals Total { get; set; } = new();

    /// <summary>每日用量，新到舊不保證排序；查詢端自行排序。</summary>
    public List<AiUsageDay> Days { get; set; } = new();
}

public class AiUsageTotals
{
    /// <summary>實際發出的 AI HTTP 呼叫次數（含重試的每一次；快取命中不計）</summary>
    public long Calls { get; set; }

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }

    /// <summary>回應未帶 usage 欄位的呼叫次數——部分地端模型不回報，
    /// 這些呼叫的 token 記 0，畫面要能說明「為什麼次數有、token 卻偏低」。</summary>
    public long CallsWithoutUsage { get; set; }
}

public class AiUsageDay : AiUsageTotals
{
    /// <summary>yyyy-MM-dd</summary>
    public string Date { get; set; } = string.Empty;
}

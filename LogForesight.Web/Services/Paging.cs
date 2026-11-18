namespace LogForesight.Web.Services;

/// <summary>分頁參數正規化：原本每個查詢方法各自寫一份逐字相同的 clamp/max</summary>
internal static class Paging
{
    /// <summary>
    /// PageSize 限制在 [1, maxPageSize]（預設 200）；Page 最小為 1，最大到「(Page - 1) × PageSize 不溢位」為止——
    /// 呼叫端都以這個乘積當 Skip 的位移，極大的頁碼溢位成負數時 Skip 會當成 0、回第 1 頁而不是空頁。
    /// </summary>
    public static (int Page, int PageSize) Normalize(int page, int pageSize, int maxPageSize = 200)
    {
        var size = Math.Clamp(pageSize, 1, maxPageSize);
        return (Math.Clamp(page, 1, int.MaxValue / size), size);
    }
}

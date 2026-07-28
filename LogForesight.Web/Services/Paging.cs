namespace LogForesight.Web.Services;

/// <summary>分頁參數正規化：原本每個查詢方法各自寫一份逐字相同的 clamp/max</summary>
internal static class Paging
{
    /// <summary>PageSize 限制在 [1, maxPageSize]（預設 200）；Page 最小為 1</summary>
    public static (int Page, int PageSize) Normalize(int page, int pageSize, int maxPageSize = 200) =>
        (Math.Max(page, 1), Math.Clamp(pageSize, 1, maxPageSize));
}

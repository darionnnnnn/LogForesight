using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 類別彙總（docs/WEB-SPEC.md §10.3）。
///
/// 這是**兩個儲存後端共用的同一份規則**：SQL 後端寫入時用它填 lf_record_categories，
/// JSONL 後端在查詢時即時計算。測試釘住的是「兩邊必然算出相同數字」的那份定義。
/// </summary>
public class CategoryAggregatorTests
{
    private static LogIssueSignature Issue(IssueCategory category, IssueSeverity severity, int count = 1) =>
        new() { Category = category, Severity = severity, Count = count, Source = "test", EventId = 1 };

    [Fact]
    public void 依類別分組並計算簽章數與事件總數()
    {
        var issues = new[]
        {
            Issue(IssueCategory.Storage, IssueSeverity.Critical, 10),
            Issue(IssueCategory.Storage, IssueSeverity.High, 5),
            Issue(IssueCategory.Security, IssueSeverity.High, 47)
        };

        var result = CategoryAggregator.Aggregate(issues);

        var storage = result.Single(c => c.Category == IssueCategory.Storage);
        Assert.Equal(2, storage.IssueCount);
        Assert.Equal(15, storage.TotalEvents);

        var security = result.Single(c => c.Category == IssueCategory.Security);
        Assert.Equal(1, security.IssueCount);
        Assert.Equal(47, security.TotalEvents);
    }

    /// <summary>嚴重度分解是 2026-07-21 為「類別×嚴重度」堆疊圖新增的欄位（§10.3 的唯一 schema 異動）</summary>
    [Fact]
    public void 各嚴重度分別計數()
    {
        var issues = new[]
        {
            Issue(IssueCategory.Storage, IssueSeverity.Critical),
            Issue(IssueCategory.Storage, IssueSeverity.Critical),
            Issue(IssueCategory.Storage, IssueSeverity.High),
            Issue(IssueCategory.Storage, IssueSeverity.Medium),
            Issue(IssueCategory.Storage, IssueSeverity.Low)
        };

        var storage = CategoryAggregator.Aggregate(issues).Single();

        Assert.Equal(2, storage.CriticalCount);
        Assert.Equal(1, storage.HighCount);
        Assert.Equal(1, storage.MediumCount);
        Assert.Equal(1, storage.LowCount);
        Assert.Equal(5, storage.IssueCount);
    }

    [Fact]
    public void MaxSeverity_取類別內最高()
    {
        var issues = new[]
        {
            Issue(IssueCategory.Service, IssueSeverity.Low),
            Issue(IssueCategory.Service, IssueSeverity.Medium),
            Issue(IssueCategory.Service, IssueSeverity.High)
        };

        Assert.Equal(IssueSeverity.High, CategoryAggregator.Aggregate(issues).Single().MaxSeverity);
    }

    /// <summary>最嚴重的類別要排前面——儀表板與報表的「嚴重度驅動顯著性」靠這個排序</summary>
    [Fact]
    public void 排序_最嚴重的類別在前()
    {
        var issues = new[]
        {
            Issue(IssueCategory.Service, IssueSeverity.Medium),
            Issue(IssueCategory.Storage, IssueSeverity.Critical),
            Issue(IssueCategory.Config, IssueSeverity.Low)
        };

        var result = CategoryAggregator.Aggregate(issues);

        Assert.Equal(IssueCategory.Storage, result[0].Category);
        Assert.Equal(IssueCategory.Config, result[^1].Category);
    }

    [Fact]
    public void 相同嚴重度時_問題數多的排前面()
    {
        var issues = new[]
        {
            Issue(IssueCategory.Service, IssueSeverity.High),
            Issue(IssueCategory.Storage, IssueSeverity.High),
            Issue(IssueCategory.Storage, IssueSeverity.High)
        };

        Assert.Equal(IssueCategory.Storage, CategoryAggregator.Aggregate(issues)[0].Category);
    }

    [Fact]
    public void 空清單_回傳空結果()
    {
        Assert.Empty(CategoryAggregator.Aggregate(Array.Empty<LogIssueSignature>()));
    }

    // Merge（跨多日彙總）已於回饋十九輪批次I 隨方法一併移除——批次D1 把報表的期間統計
    // 改走 AggregateByCategory 的 SQL 聚合後，Merge 只剩測試在呼叫（E7 退場普查的漏網死碼）。
}

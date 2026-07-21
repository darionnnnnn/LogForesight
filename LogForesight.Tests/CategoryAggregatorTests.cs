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

    [Fact]
    public void Merge_跨多日彙總相加()
    {
        var day1 = CategoryAggregator.Aggregate(new[]
        {
            Issue(IssueCategory.Storage, IssueSeverity.Critical, 10),
            Issue(IssueCategory.Storage, IssueSeverity.High, 3)
        });
        var day2 = CategoryAggregator.Aggregate(new[]
        {
            Issue(IssueCategory.Storage, IssueSeverity.Medium, 5)
        });

        var merged = CategoryAggregator.Merge(day1.Concat(day2)).Single();

        Assert.Equal(3, merged.IssueCount);
        Assert.Equal(18, merged.TotalEvents);
        Assert.Equal(1, merged.CriticalCount);
        Assert.Equal(1, merged.HighCount);
        Assert.Equal(1, merged.MediumCount);
        Assert.Equal(IssueSeverity.Critical, merged.MaxSeverity);
    }

    [Fact]
    public void Merge_不同類別各自保留()
    {
        var summaries = CategoryAggregator.Aggregate(new[]
        {
            Issue(IssueCategory.Storage, IssueSeverity.Critical),
            Issue(IssueCategory.Security, IssueSeverity.High)
        });

        var merged = CategoryAggregator.Merge(summaries);

        Assert.Equal(2, merged.Count);
    }
}

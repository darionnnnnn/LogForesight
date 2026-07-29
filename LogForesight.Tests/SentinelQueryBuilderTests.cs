using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelQueryBuilder：規則表→Lucene filter 的純函數產生器（docs/NETIQ-API-PLAN.md §4）。
/// 全部用建構出來的規則清單測試，不碰 KnownIssueCatalog 共用靜態狀態
/// （--selftest 的 RunSentinelQueryChecks 另外對真實種子規則表做結構性驗證）。
/// </summary>
public class SentinelQueryBuilderTests
{
    private static KnownIssueRule WindowsRule(string id, int[] eventIds, bool enabled = true, bool matchAll = false) => new()
    {
        Id = id,
        Platform = "windows",
        Enabled = enabled,
        MatchAllEventIds = matchAll,
        EventIds = eventIds,
        SourcePattern = "Test-Provider",
        Category = IssueCategory.Security
    };

    private static KnownIssueRule LinuxRule(string id) => new()
    {
        Id = id,
        Platform = "linux",
        Enabled = true,
        ProgramPattern = "sshd",
        Category = IssueCategory.Security
    };

    [Fact]
    public void BuildWindowsFilter_含IP批次子句與AND連接內容子句()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(
            new[] { "10.1.2.11", "10.1.2.12" }, new[] { WindowsRule("r1", new[] { 4625 }) });

        Assert.StartsWith("(repip:10.1.2.11 OR repip:10.1.2.12)", filter);
        Assert.Contains(" AND ", filter);
    }

    [Fact]
    public void BuildWindowsFilter_空IP清單擲例外()
    {
        Assert.Throws<ArgumentException>(() =>
            SentinelQueryBuilder.BuildWindowsFilter(Array.Empty<string>(), Enumerable.Empty<KnownIssueRule>()));
    }

    [Fact]
    public void BuildWindowsFilter_無規則時只有generic分支()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(new[] { "10.1.2.11" }, Enumerable.Empty<KnownIssueRule>());

        Assert.DoesNotContain("rv40:(", filter);
        Assert.Contains("rv150:System OR rv150:Application", filter);
        Assert.Contains($"sev:[{SentinelQueryBuilder.GenericErrorSeverityMin} TO 5]", filter);
    }

    [Fact]
    public void BuildWindowsFilter_有規則時rv40子句與generic分支皆存在_以OR連接()
    {
        var filter = SentinelQueryBuilder.BuildWindowsFilter(
            new[] { "10.1.2.11" }, new[] { WindowsRule("r1", new[] { 4625, 4720 }) });

        Assert.Contains("rv40:(4625 OR 4720)", filter);
        Assert.Contains(" OR (", filter);   // rv40 子句與 generic 子句以 OR 連接
    }

    [Fact]
    public void WindowsRuleEventIds_排除停用規則()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("enabled", new[] { 100 }),
            WindowsRule("disabled", new[] { 200 }, enabled: false)
        });

        Assert.Contains(100, ids);
        Assert.DoesNotContain(200, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_排除MatchAllEventIds規則()
    {
        // WHEA-Logger／Resource-Exhaustion／VSS 這類規則沒有具體 EventId 可下推，
        // 就算欄位上填了 EventIds 也不該混進聯集（防禦性測試：現行種子這類規則 EventIds 恆空，
        // 但函數本身的契約不該依賴這個巧合）
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("matchall", new[] { 999 }, matchAll: true)
        });

        Assert.DoesNotContain(999, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_排除Linux規則()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new KnownIssueRule[]
        {
            WindowsRule("win", new[] { 100 }),
            LinuxRule("linux")
        });

        Assert.Single(ids);
        Assert.Contains(100, ids);
    }

    [Fact]
    public void WindowsRuleEventIds_跨規則聯集去重且升冪排序()
    {
        var ids = SentinelQueryBuilder.WindowsRuleEventIds(new[]
        {
            WindowsRule("r1", new[] { 300, 100 }),
            WindowsRule("r2", new[] { 100, 200 })   // 100 與 r1 重複
        });

        Assert.Equal(new[] { 100, 200, 300 }, ids);
    }

    [Fact]
    public void BuildIpClause_單一IP不含多餘OR()
    {
        var clause = SentinelQueryBuilder.BuildIpClause(new[] { "10.1.2.11" });

        Assert.Equal("(repip:10.1.2.11)", clause);
    }
}

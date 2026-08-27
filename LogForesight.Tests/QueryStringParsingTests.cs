using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ParseDateRange 的單元測試（含頭尾天數、顛倒交換、顛倒丟錯、
/// 以及 ReportsController 端點整合驗收）。
/// 對應任務：task-A 驗收標準第 3 條。
/// </summary>
public class QueryStringParsingTests
{
    // ─── ParseDateRange：顛倒自動交換 ────────────────────────────────────────

    [Fact]
    public void ParseDateRange_顛倒時自動交換_結果正確()
    {
        var later   = new DateTime(2026, 8, 15);
        var earlier = new DateTime(2026, 1,  1);

        // from > to → 應自動交換（throwOnReversed = false 預設）
        var (from, to) = QueryStringParsing.ParseDateRange(
            later.ToString("yyyy-MM-dd"),
            earlier.ToString("yyyy-MM-dd"));

        Assert.Equal(earlier, from);
        Assert.Equal(later,   to);
    }

    // ─── ParseDateRange：顛倒時拋出驗證錯誤 ─────────────────────────────────

    [Fact]
    public void ParseDateRange_顛倒時啟用拋出模式_丟驗證例外()
    {
        var later   = new DateTime(2026, 8, 15);
        var earlier = new DateTime(2026, 1,  1);

        var ex = Assert.Throws<DomainException>(() =>
            QueryStringParsing.ParseDateRange(
                later.ToString("yyyy-MM-dd"),
                earlier.ToString("yyyy-MM-dd"),
                throwOnReversed: true));

        Assert.NotNull(ex.Message);
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    // ─── ParseDateRange：超過最大天數上限 ────────────────────────────────────

    [Fact]
    public void ParseDateRange_超過最大天數上限_丟驗證例外()
    {
        var from = new DateTime(2025, 1, 1);
        var to   = from.AddDays(366); // 367 天含頭尾

        var ex = Assert.Throws<DomainException>(() =>
            QueryStringParsing.ParseDateRange(
                from.ToString("yyyy-MM-dd"),
                to.ToString("yyyy-MM-dd"),
                maxDays: 366));

        Assert.Contains("366", ex.Message);
    }

    // ─── ParseDateRange：剛好最大天數邊界 ────────────────────────────────────

    [Fact]
    public void ParseDateRange_剛好最大天數邊界_通過不拋例外()
    {
        var from = new DateTime(2025, 1, 1);
        var to   = from.AddDays(365); // 366 天含頭尾

        // 不應拋出例外
        var (parsedFrom, parsedTo) = QueryStringParsing.ParseDateRange(
            from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"),
            maxDays: 366);

        Assert.Equal(from, parsedFrom);
        Assert.Equal(to,   parsedTo);
    }

    // ─── ReportsController：from > to → 回應驗證錯誤，不交換放行 ─────────────

    [Fact]
    public void ReportsController_Summary_from大於to_回應驗證錯誤不交換()
    {
        // 日期驗證發生在 controller 層的 ParseDateRange 呼叫，
        // 不會進入 service，因此用 null! 即可測出此行為。
        // 故意把 from 設成比 to 晚，驗證「不會被靜默交換」。
        var controller = new ReportsController(null!);
        var from = "2026-08-15";
        var to   = "2026-01-01";  // from > to

        var ex = Assert.Throws<DomainException>(() =>
            controller.Summary(from, to, null));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }
}

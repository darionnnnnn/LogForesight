using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-8-PLAN.md #4：觀察中狀態的到期判定——沿用 IssueHandling.DueDate 當「觀察至」，
/// 讀取時推導，不跑背景作業。
/// </summary>
public class IssueHandlingStatusesObservationTests
{
    private static readonly DateTime Today = DateTime.Today;

    [Fact]
    public void 觀察中且觀察至為未來日期時_視為有效()
    {
        Assert.True(IssueHandlingStatuses.IsObservationActive(IssueHandlingStatuses.Observing, Today.AddDays(7), Today));
        Assert.False(IssueHandlingStatuses.IsObservationExpired(IssueHandlingStatuses.Observing, Today.AddDays(7), Today));
    }

    [Fact]
    public void 觀察至恰為今天時_仍視為有效不算到期()
    {
        Assert.True(IssueHandlingStatuses.IsObservationActive(IssueHandlingStatuses.Observing, Today, Today));
        Assert.False(IssueHandlingStatuses.IsObservationExpired(IssueHandlingStatuses.Observing, Today, Today));
    }

    [Fact]
    public void 觀察至為過去日期時_視為到期()
    {
        Assert.False(IssueHandlingStatuses.IsObservationActive(IssueHandlingStatuses.Observing, Today.AddDays(-1), Today));
        Assert.True(IssueHandlingStatuses.IsObservationExpired(IssueHandlingStatuses.Observing, Today.AddDays(-1), Today));
    }

    [Fact]
    public void 非觀察中狀態一律不算觀察有效或到期()
    {
        Assert.False(IssueHandlingStatuses.IsObservationActive(IssueHandlingStatuses.InProgress, Today.AddDays(7), Today));
        Assert.False(IssueHandlingStatuses.IsObservationExpired(IssueHandlingStatuses.InProgress, Today.AddDays(-1), Today));
    }

    [Fact]
    public void 沒有觀察至日期時_一律不算有效或到期()
    {
        Assert.False(IssueHandlingStatuses.IsObservationActive(IssueHandlingStatuses.Observing, null, Today));
        Assert.False(IssueHandlingStatuses.IsObservationExpired(IssueHandlingStatuses.Observing, null, Today));
    }

    [Fact]
    public void Observing是合法狀態且非結案類()
    {
        Assert.True(IssueHandlingStatuses.IsValid(IssueHandlingStatuses.Observing));
        Assert.False(IssueHandlingStatuses.IsClosed(IssueHandlingStatuses.Observing));
        Assert.Contains(IssueHandlingStatuses.Observing, IssueHandlingStatuses.All);
    }

    /// <summary>對外三態把 observing 收斂為處理中——防禦性映射：沒有它的話 observing 掉進
    /// default 被誤歸「已處理」，觀察中的日子就從待辦統計裡消失了</summary>
    [Fact]
    public void ExternalOf_觀察中收斂為處理中而非已處理()
    {
        Assert.Equal(HandlingStatuses.InProgress, HandlingStatuses.ExternalOf(IssueHandlingStatuses.Observing));
    }

    // ── 歷程說明的觀察至後綴（ComposeLogNote）───────────────────────────────

    [Fact]
    public void ComposeLogNote_觀察中時補觀察至後綴()
    {
        var result = IssueHandlingStatuses.ComposeLogNote(
            IssueHandlingStatuses.Observing, "先看幾天", new DateTime(2026, 8, 11));

        Assert.Equal("先看幾天（觀察至 2026-08-11）", result);
    }

    [Fact]
    public void ComposeLogNote_觀察中且無說明時只留觀察至()
    {
        var result = IssueHandlingStatuses.ComposeLogNote(
            IssueHandlingStatuses.Observing, null, new DateTime(2026, 8, 11));

        Assert.Equal("（觀察至 2026-08-11）", result);
    }

    [Fact]
    public void ComposeLogNote_非觀察中狀態原樣回傳不加後綴()
    {
        Assert.Equal("已聯絡機房", IssueHandlingStatuses.ComposeLogNote(
            IssueHandlingStatuses.InProgress, "已聯絡機房", new DateTime(2026, 8, 11)));
        Assert.Null(IssueHandlingStatuses.ComposeLogNote(
            IssueHandlingStatuses.Resolved, null, null));
    }
}

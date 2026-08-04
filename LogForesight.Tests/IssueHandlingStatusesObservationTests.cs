using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/FEEDBACK-8-PLAN.md #4：觀察中狀態的到期判定——沿用 IssueHandling.DueDate 當「觀察至」，
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
}

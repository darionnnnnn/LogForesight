using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 「無法處理」狀態（回饋十八輪批次G，docs/FEEDBACK-18-PLAN.md）：處理人上報「我處理不了」，
/// 等 admin 決定結案或重新指派。三套值域（日層級 HandlingStatuses／問題層級
/// IssueHandlingStatuses／對外三態 ExternalOf）必須同步——值域漂移正是先前多輪體檢
/// 反覆抓到的 bug 家族，這組測試就是同步的鎖。
/// </summary>
public class EscalatedStatusTests
{
    [Fact]
    public void Escalated兩層值域同值()
    {
        Assert.Equal(HandlingStatuses.Escalated, IssueHandlingStatuses.Escalated);
    }

    [Fact]
    public void 日層級_Escalated是合法狀態且列入未結案()
    {
        Assert.True(HandlingStatuses.IsValid(HandlingStatuses.Escalated));
        Assert.Contains(HandlingStatuses.Escalated, HandlingStatuses.Unresolved);
    }

    [Fact]
    public void 問題層級_Escalated是合法狀態且非結案類()
    {
        Assert.True(IssueHandlingStatuses.IsValid(IssueHandlingStatuses.Escalated));
        Assert.False(IssueHandlingStatuses.IsClosed(IssueHandlingStatuses.Escalated));
    }

    /// <summary>對外三態把 escalated 收斂為處理中——掉進 default 被歸「已處理」的話，
    /// 上報等 admin 決定的日子會從待辦統計裡消失（同 observing 的防禦理由）。</summary>
    [Fact]
    public void ExternalOf_無法處理收斂為處理中而非已處理()
    {
        Assert.Equal(HandlingStatuses.InProgress, HandlingStatuses.ExternalOf(HandlingStatuses.Escalated));
    }

    /// <summary>日層級推導：問題被標成 escalated 時，這一天不再算 open——有人在管（上報中）。</summary>
    [Fact]
    public void 日層級推導_有問題標成無法處理時當日推導為處理中()
    {
        var issue = new LogIssueSignature { LogName = "System", Source = "disk", EventId = 7, Severity = IssueSeverity.High };
        var handling = new IssueHandling
        {
            HostName = "host1",
            Date = DateTime.Today,
            IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Escalated
        };

        var progress = LogForesight.Web.Services.DayHandlingDerivation.Derive(
            new[] { issue }, new[] { handling }, dayLevelStatus: null,
            unhandledSeverities: new HashSet<IssueSeverity> { IssueSeverity.High });

        Assert.Equal(HandlingStatuses.InProgress, progress.DayStatus);
        Assert.True(progress.IsUnresolved);
    }
}

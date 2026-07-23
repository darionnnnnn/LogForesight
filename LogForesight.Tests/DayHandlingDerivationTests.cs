using System.Diagnostics;
using LogForesight;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 由問題層級結案狀態推導日層級狀態（方案 B 核心規則）。
/// 這條規則被詳情頁、問題清單、儀表板待辦共用，逐位一致是「清單說未處理、待辦卻 0」那類 bug 的防線。
/// </summary>
public class DayHandlingDerivationTests
{
    private static LogIssueSignature Issue(string source, int eventId) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = EventLogEntryType.Error
    };

    private static IssueHandling Mark(LogIssueSignature issue, string status) => new()
    {
        IssueKey = IssueSignatureKey.For(issue),
        Status = status
    };

    [Fact]
    public void 全部問題結案_日狀態為已處理()
    {
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        var handlings = new[] { Mark(a, IssueHandlingStatuses.Resolved), Mark(b, IssueHandlingStatuses.FalsePositive) };

        var result = DayHandlingDerivation.Derive(new[] { a, b }, handlings, HandlingStatuses.Open);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Closed);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
        Assert.False(result.IsUnresolved);
    }

    [Fact]
    public void 部分問題結案_日狀態為處理中()
    {
        var a = Issue("disk", 153);
        var b = Issue("app", 1000);
        var handlings = new[] { Mark(a, IssueHandlingStatuses.Resolved) };

        var result = DayHandlingDerivation.Derive(new[] { a, b }, handlings, HandlingStatuses.Open);

        Assert.Equal(1, result.Closed);
        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
        Assert.True(result.IsUnresolved);
    }

    [Fact]
    public void 無問題被標記_退回日層級狀態()
    {
        var a = Issue("disk", 153);

        var open = DayHandlingDerivation.Derive(new[] { a }, System.Array.Empty<IssueHandling>(), HandlingStatuses.Open);
        Assert.Equal(HandlingStatuses.Open, open.DayStatus);
        Assert.Equal(0, open.Closed);
        Assert.True(open.IsUnresolved);

        // 日層級被標成處理中（有人在看整天）但個別問題還沒標——沿用日層級
        var inProgress = DayHandlingDerivation.Derive(new[] { a }, System.Array.Empty<IssueHandling>(), HandlingStatuses.InProgress);
        Assert.Equal(HandlingStatuses.InProgress, inProgress.DayStatus);
    }

    [Fact]
    public void 沒有任何問題_退回日層級狀態()
    {
        var result = DayHandlingDerivation.Derive(
            System.Array.Empty<LogIssueSignature>(), System.Array.Empty<IssueHandling>(), HandlingStatuses.Resolved);

        Assert.Equal(0, result.Total);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
    }
}

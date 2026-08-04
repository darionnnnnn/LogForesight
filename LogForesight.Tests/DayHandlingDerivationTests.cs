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
    /// <summary>與 SystemSettings.UnhandledSeverities 的預設值一致（Critical/High/Medium）</summary>
    private static readonly IReadOnlySet<IssueSeverity> DefaultSeverities =
        new HashSet<IssueSeverity> { IssueSeverity.Critical, IssueSeverity.High, IssueSeverity.Medium };

    private static readonly IReadOnlySet<IssueSeverity> AllSeverities =
        new HashSet<IssueSeverity> { IssueSeverity.Critical, IssueSeverity.High, IssueSeverity.Medium, IssueSeverity.Low };

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = EventLogEntryType.Error,
        Severity = severity
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

        var result = DayHandlingDerivation.Derive(new[] { a, b }, handlings, HandlingStatuses.Open, DefaultSeverities);

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

        var result = DayHandlingDerivation.Derive(new[] { a, b }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(1, result.Closed);
        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
        Assert.True(result.IsUnresolved);
    }

    [Fact]
    public void 無問題被標記_退回日層級狀態()
    {
        var a = Issue("disk", 153);

        var open = DayHandlingDerivation.Derive(new[] { a }, System.Array.Empty<IssueHandling>(), HandlingStatuses.Open, DefaultSeverities);
        Assert.Equal(HandlingStatuses.Open, open.DayStatus);
        Assert.Equal(0, open.Closed);
        Assert.True(open.IsUnresolved);

        // 日層級被標成處理中（有人在看整天）但個別問題還沒標——沿用日層級
        var inProgress = DayHandlingDerivation.Derive(new[] { a }, System.Array.Empty<IssueHandling>(), HandlingStatuses.InProgress, DefaultSeverities);
        Assert.Equal(HandlingStatuses.InProgress, inProgress.DayStatus);
    }

    [Fact]
    public void 沒有任何問題_退回日層級狀態()
    {
        var result = DayHandlingDerivation.Derive(
            System.Array.Empty<LogIssueSignature>(), System.Array.Empty<IssueHandling>(), HandlingStatuses.Resolved, DefaultSeverities);

        Assert.Equal(0, result.Total);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
    }

    [Fact]
    public void 未列入未處理等級且未標記_不計入總數_不阻擋已處理()
    {
        var high = Issue("disk", 153, IssueSeverity.High);
        var low = Issue("app", 1000, IssueSeverity.Low);   // 未列在 DefaultSeverities，且從未被標記
        var handlings = new[] { Mark(high, IssueHandlingStatuses.Resolved) };

        var result = DayHandlingDerivation.Derive(new[] { high, low }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(1, result.Total);   // low 被排除，不計入母體
        Assert.Equal(1, result.Closed);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
    }

    [Fact]
    public void 未列入未處理等級但曾被明確標記_仍計入總數()
    {
        var low = Issue("app", 1000, IssueSeverity.Low);
        // 使用者手動把一個本可預設略過的 Low 問題標成「不處理」——明確標記優先於等級篩選
        var handlings = new[] { Mark(low, IssueHandlingStatuses.WontFix) };

        var result = DayHandlingDerivation.Derive(new[] { low }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Closed);
        Assert.Equal(HandlingStatuses.Resolved, result.DayStatus);
    }

    [Fact]
    public void 未列入未處理等級但被明確調回未處理_不會被自動排除()
    {
        var low = Issue("app", 1000, IssueSeverity.Low);
        // 使用者把自動推導的「已知雜訊」調回未處理，明確持久化一筆 open——不能被等級篩選悄悄蓋掉
        var handlings = new[] { Mark(low, IssueHandlingStatuses.Open) };

        var result = DayHandlingDerivation.Derive(new[] { low }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Closed);
        Assert.Equal(HandlingStatuses.Open, result.DayStatus);
    }

    [Fact]
    public void 全部等級都納入時_低嚴重度未標記也計入總數()
    {
        var low = Issue("app", 1000, IssueSeverity.Low);

        var result = DayHandlingDerivation.Derive(new[] { low }, System.Array.Empty<IssueHandling>(), HandlingStatuses.Open, AllSeverities);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Closed);
    }

    // ── 觀察中（docs/FEEDBACK-8-PLAN.md #4）────────────────────────────────────

    [Fact]
    public void 問題標為觀察中_日狀態為處理中而非未處理()
    {
        var a = Issue("disk", 153);
        var handlings = new[] { new IssueHandling
        {
            IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(7)
        } };

        var result = DayHandlingDerivation.Derive(new[] { a }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
        Assert.True(result.IsUnresolved);   // 處理中仍算未結案，只是不再是 open
    }

    /// <summary>觀察到期後日狀態不倒退回 open——「有人在管」這件事不因到期而消失，
    /// 到期只透過 HasOverdueIssue 疊加逾期提示（見下方測試）</summary>
    [Fact]
    public void 觀察到期_日狀態仍為處理中不倒退回未處理()
    {
        var a = Issue("disk", 153);
        var handlings = new[] { new IssueHandling
        {
            IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(-1)   // 昨天到期
        } };

        var result = DayHandlingDerivation.Derive(new[] { a }, handlings, HandlingStatuses.Open, DefaultSeverities);

        Assert.Equal(HandlingStatuses.InProgress, result.DayStatus);
    }

    [Fact]
    public void HasOverdueIssue_觀察到期視為逾期()
    {
        var a = Issue("disk", 153);
        var expired = new IssueHandling
        {
            IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(-1)
        };

        Assert.True(DayHandlingDerivation.HasOverdueIssue(new[] { expired }, DateTime.Today));
    }

    [Fact]
    public void HasOverdueIssue_觀察中未到期不算逾期()
    {
        var a = Issue("disk", 153);
        var active = new IssueHandling
        {
            IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today.AddDays(7)
        };

        Assert.False(DayHandlingDerivation.HasOverdueIssue(new[] { active }, DateTime.Today));
    }

    [Fact]
    public void HasOverdueIssue_觀察至今天當天不算逾期()
    {
        var a = Issue("disk", 153);
        var dueToday = new IssueHandling
        {
            IssueKey = IssueSignatureKey.For(a), Status = IssueHandlingStatuses.Observing,
            DueDate = DateTime.Today
        };

        Assert.False(DayHandlingDerivation.HasOverdueIssue(new[] { dueToday }, DateTime.Today));
    }
}

using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 復發判定（<see cref="IssueRecurrence"/>）：同主機同問題、回看 30 天（日曆日差）、上一件必須是「已處理」、
/// 不處理類不算、取消的案件不參與。每條邊界兩側各一例。
/// </summary>
public sealed class IssueRecurrenceTests
{
    /// <summary>本件（被判定的案件）的建立日</summary>
    private static readonly DateTime Created = new(2026, 11, 20, 9, 0, 0);

    private static int _seq;

    private static IssueCase Case(string host, DateTime createdAt, string status, DateTime? closedAt, string source = "disk", int eventId = 153,
        bool cancelled = false, string? note = null) => new()
    {
        CaseId = $"c{++_seq:D4}", HostName = host, IssueKey = $"System|{source}|{eventId}", IssueLabel = $"{source} {eventId}",
        SourceName = source, EventId = eventId, Status = status, CreatedAt = createdAt, ClosedAt = closedAt, Cancelled = cancelled, Note = note
    };

    private static IssueCase Target(string host = "H1", string source = "disk", int eventId = 153) =>
        Case(host, Created, IssueHandlingStatuses.InProgress, null, source, eventId);

    private static RecurrenceInfo Eval(IssueCase target, params IssueCase[] history) =>
        IssueRecurrence.Evaluate(new[] { target }, history)[target.CaseId];

    private static IssueCase Resolved(DateTime closedAt, DateTime? createdAt = null, string host = "H1") =>
        Case(host, createdAt ?? closedAt.AddDays(-2), IssueHandlingStatuses.Resolved, closedAt);

    [Fact]
    public void 回看天數與派工延續性共用同一個常數()
    {
        Assert.Equal(DispatchContext.ContinuityDays, IssueRecurrence.WindowDays);
        Assert.Equal(30, IssueRecurrence.WindowDays);
    }

    // ── 回看窗邊界（日曆日差）─────────────────────────────────────────────────

    [Fact]
    public void 前案結案日是第30天_算復發()
    {
        var info = Eval(Target(), Resolved(Created.Date.AddDays(-30).AddHours(23)));

        Assert.True(info.IsRecurrence);
        Assert.Equal(1, info.Count);
    }

    [Fact]
    public void 前案結案日是第31天_不算復發_次數也不計()
    {
        var info = Eval(Target(), Resolved(Created.Date.AddDays(-31).AddHours(23)));

        Assert.False(info.IsRecurrence);
        Assert.Equal(0, info.Count);
    }

    [Fact]
    public void 第30天以日曆日計_不看時分_前案晚間結案本件清晨建立也算()
    {
        // 前案 10/21 23:59 結案、本件 11/20 00:01 建立：日曆日差 30，實際時間差不到 30 天整
        var target = Case("H1", new DateTime(2026, 11, 20, 0, 1, 0), IssueHandlingStatuses.InProgress, null);
        Assert.True(Eval(target, Resolved(new DateTime(2026, 10, 21, 23, 59, 0))).IsRecurrence);

        // 前案 10/20 23:59 結案：日曆日差 31，不算
        var target2 = Case("H1", new DateTime(2026, 11, 20, 0, 1, 0), IssueHandlingStatuses.InProgress, null);
        Assert.False(Eval(target2, Resolved(new DateTime(2026, 10, 20, 23, 59, 0))).IsRecurrence);
    }

    [Fact]
    public void 前案當天結案_算復發_前案結案日晚於本件建立日_不算()
    {
        Assert.True(Eval(Target(), Resolved(Created.Date.AddHours(1), createdAt: Created.AddDays(-1))).IsRecurrence);

        // 結案日在本件建立日之後（不應發生的資料）：不在 [建立日−30, 建立日] 內
        var info = Eval(Target(), Resolved(Created.Date.AddDays(1), createdAt: Created.AddDays(-1)));
        Assert.False(info.IsRecurrence);
        Assert.Equal(0, info.Count);
    }

    // ── 上一件的種類 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(IssueHandlingStatuses.WontFix)]
    [InlineData(IssueHandlingStatuses.FalsePositive)]
    [InlineData(IssueHandlingStatuses.KnownNoise)]
    public void 上一件是不處理類_不算復發_即使更早有已處理(string dismissal)
    {
        var earlierResolved = Resolved(Created.Date.AddDays(-20), createdAt: Created.AddDays(-25));
        var lastDismissed = Case("H1", Created.AddDays(-10), dismissal, Created.AddDays(-8));

        var info = Eval(Target(), earlierResolved, lastDismissed);

        Assert.False(info.IsRecurrence);          // 突變參考：改成「任何上一件都算」或「看更早的已處理」這列要紅
        Assert.Equal(1, info.Count);              // 更早那件已處理仍計入次數；不處理類不計
        Assert.Equal(lastDismissed.CaseId, info.Previous!.CaseId);
    }

    [Fact]
    public void 上一件是已處理_更早是不處理類_算復發_次數只計已處理()
    {
        var earlierDismissed = Case("H1", Created.AddDays(-25), IssueHandlingStatuses.WontFix, Created.AddDays(-22));
        var lastResolved = Resolved(Created.Date.AddDays(-5), createdAt: Created.AddDays(-9));

        var info = Eval(Target(), earlierDismissed, lastResolved);

        Assert.True(info.IsRecurrence);
        Assert.Equal(1, info.Count);
    }

    [Fact]
    public void 沒有前案_不是復發_第一次()
    {
        var info = Eval(Target());

        Assert.False(info.IsRecurrence);
        Assert.Equal(0, info.Count);
        Assert.Equal(1, info.Occurrence);
        Assert.Null(info.Previous);
    }

    [Fact]
    public void 只有未結案或取消的前案_等於沒有前案()
    {
        var open = Case("H1", Created.AddDays(-3), IssueHandlingStatuses.InProgress, null);
        var cancelled = Case("H1", Created.AddDays(-5), IssueHandlingStatuses.Resolved, Created.AddDays(-4), cancelled: true);

        var info = Eval(Target(), open, cancelled);

        Assert.False(info.IsRecurrence);
        Assert.Equal(0, info.Count);
        Assert.Null(info.Previous);
    }

    [Fact]
    public void 取消的案件不當上一件_更早的已處理仍算復發()
    {
        var resolved = Resolved(Created.Date.AddDays(-10), createdAt: Created.AddDays(-14));
        var cancelledLater = Case("H1", Created.AddDays(-4), IssueHandlingStatuses.Open, Created.AddDays(-3), cancelled: true);

        var info = Eval(Target(), resolved, cancelledLater);

        Assert.True(info.IsRecurrence);
        Assert.Equal(resolved.CaseId, info.Previous!.CaseId);
    }

    // ── 次數 N ───────────────────────────────────────────────────────────────

    [Fact]
    public void 三件已處理都在三十天內_次數是3_兩件是2()
    {
        var a = Resolved(Created.Date.AddDays(-25), createdAt: Created.AddDays(-28));
        var b = Resolved(Created.Date.AddDays(-15), createdAt: Created.AddDays(-18));
        var c = Resolved(Created.Date.AddDays(-5), createdAt: Created.AddDays(-8));

        Assert.Equal(3, Eval(Target(), a, b, c).Count);
        Assert.Equal(2, Eval(Target(), b, c).Count);
    }

    [Fact]
    public void 次數只計回看窗內_窗外的已處理不計()
    {
        var outside = Resolved(Created.Date.AddDays(-31), createdAt: Created.AddDays(-35));
        var inside1 = Resolved(Created.Date.AddDays(-30), createdAt: Created.AddDays(-33));
        var inside2 = Resolved(Created.Date.AddDays(-3), createdAt: Created.AddDays(-6));

        var info = Eval(Target(), outside, inside1, inside2);

        Assert.Equal(2, info.Count);
        Assert.Equal(4, info.Occurrence);   // 窗外的也算「第幾次」：前面共 3 件未取消案件
    }

    // ── 同一個問題的界線 ─────────────────────────────────────────────────────

    [Fact]
    public void 不同主機_不同事件編號_不同來源_都不是同一個問題()
    {
        var otherHost = Resolved(Created.Date.AddDays(-3), host: "H2");
        var otherEvent = Case("H1", Created.AddDays(-5), IssueHandlingStatuses.Resolved, Created.AddDays(-3), eventId: 154);
        var otherSource = Case("H1", Created.AddDays(-5), IssueHandlingStatuses.Resolved, Created.AddDays(-3), source: "ntfs");

        var info = Eval(Target(), otherHost, otherEvent, otherSource);

        Assert.False(info.IsRecurrence);
        Assert.Equal(0, info.Count);
    }

    [Fact]
    public void 主機與來源不分大小寫_同一個問題()
    {
        var resolved = Case("h1", Created.AddDays(-5), IssueHandlingStatuses.Resolved, Created.AddDays(-3), source: "DISK");

        Assert.True(Eval(Target("H1", "disk"), resolved).IsRecurrence);
    }

    [Fact]
    public void 問題欄解析不出的案件_不分組_回沒有前案()
    {
        var target = Target();
        target.SourceName = null;
        target.EventId = null;

        var info = Eval(target, Resolved(Created.Date.AddDays(-3)));

        Assert.Equal(RecurrenceInfo.None, info);
    }

    // ── 第 N 次與批次 ────────────────────────────────────────────────────────

    [Fact]
    public void 第N次_依建立先後編號_本件之後建立的不算()
    {
        var first = Case("H1", Created.AddDays(-40), IssueHandlingStatuses.Resolved, Created.AddDays(-38));
        var second = Case("H1", Created.AddDays(-20), IssueHandlingStatuses.WontFix, Created.AddDays(-18));
        var third = Case("H1", Created, IssueHandlingStatuses.Resolved, Created.AddDays(1));
        var later = Case("H1", Created.AddDays(5), IssueHandlingStatuses.Resolved, Created.AddDays(6));

        var all = new[] { first, second, third, later };
        var result = IssueRecurrence.Evaluate(all, all);

        Assert.Equal(1, result[first.CaseId].Occurrence);
        Assert.Equal(2, result[second.CaseId].Occurrence);
        Assert.Equal(3, result[third.CaseId].Occurrence);
        Assert.Equal(4, result[later.CaseId].Occurrence);
    }

    [Fact]
    public void 一次判定多件_各件各自對應自己的前案_結果以案件編號為鍵()
    {
        var t1 = Target("H1");
        var t2 = Target("H2");
        var h1Prior = Resolved(Created.Date.AddDays(-2), host: "H1");

        var result = IssueRecurrence.Evaluate(new[] { t1, t2 }, new[] { h1Prior });

        Assert.True(result[t1.CaseId].IsRecurrence);
        Assert.False(result[t2.CaseId].IsRecurrence);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 前案候選含目標自己_不把自己當前案()
    {
        var target = Case("H1", Created, IssueHandlingStatuses.Resolved, Created.AddDays(1));

        var info = Eval(target, target);

        Assert.Equal(RecurrenceInfo.None, info);
    }
}

using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 76-2 輪批次 C-2：管理者提醒的摘要樣板與長度保護（彙整與去重的逐日行為在 HandoffScenarioTests 的 S3／S11／S14）。</summary>
public sealed class AdminReminderTests
{
    private static WorkOrderOutboxItem Reminder(int escalated, int unassignable, int noRecipient, string? detail) => new()
    {
        Kind = WorkOrderOutboxKinds.AdminReminder,
        RecipientUserId = 1,
        EnqueuedAt = new DateTime(2026, 11, 17, 9, 0, 0),
        ActorAccount = "system",
        ReminderEscalated = escalated,
        ReminderUnassignable = unassignable,
        ReminderNoRecipient = noRecipient,
        Detail = detail
    };

    [Theory]
    [InlineData(RunScope.Full, false, false, true)]
    [InlineData(RunScope.LocalOnly, false, false, false)]
    [InlineData(RunScope.NetiqHosts, false, false, false)]
    [InlineData(RunScope.Full, true, false, false)]   // NetIQ 帳本複查趟
    [InlineData(RunScope.Full, false, true, false)]   // 同窗口續跑
    public void 派不出去清單只有涵蓋全部主機日的那一趟才被取代(RunScope scope, bool ledgerRetry, bool continues, bool replaces)
    {
        var summary = new NightlyDispatchSummary
        {
            Unassignable = new[] { new NightlyUnassignableIssue("磁碟 154", "無候選人", 1) }
        };
        var request = new RunRequest { Scope = scope, NetiqLedgerRetryOnly = ledgerRetry, ContinuesWindow = continues };

        var passed = AdminReminderService.SummaryForUnassignableList(request, summary);

        if (replaces) Assert.Same(summary, passed);
        else Assert.Null(passed);
        Assert.Null(AdminReminderService.SummaryForUnassignableList(request, null));
    }

    [Fact]
    public void 排程組出來的續跑與複查請求都被判成非完整趟_首趟是完整趟()
    {
        var start = new DateTime(2026, 11, 17, 22, 0, 0);
        var first = SchedulerHostedService.ComposeScheduledRequest(null, "note", (start, start.AddHours(2)));
        Assert.NotNull(AdminReminderService.SummaryForUnassignableList(first, new NightlyDispatchSummary()));

        var ledger = SchedulerHostedService.ComposeScheduledRequest(null, "note", (start, start.AddHours(2)), netiqLedgerRetryOnly: true);
        Assert.Null(AdminReminderService.SummaryForUnassignableList(ledger, new NightlyDispatchSummary()));

        // ComposeEffectiveRequest 逐欄重建，續跑旗標漏抄會讓判定靜默失效
        var cont = SchedulerHostedService.ComposeScheduledRequest(null, "note", (start, start.AddHours(2)), continuesWindow: true);
        var effective = SchedulerHostedService.ComposeEffectiveRequest(cont, new ScheduleOptions());
        Assert.True(effective.ContinuesWindow);
        Assert.Null(AdminReminderService.SummaryForUnassignableList(effective, new NightlyDispatchSummary()));
    }

    [Fact]
    public void 排程的管理者提醒呼叫點經範圍判定_不直接傳本趟結果()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Services", "SchedulerHostedService.cs"));
        Assert.Contains("RunAfterNightly(AdminReminderService.SummaryForUnassignableList(", source);
    }

    [Fact]
    public void 管理者摘要含三類筆數與明細_標題標明管理者提醒_不附處理人的我的交辦句()
    {
        var detail = "【上報後逾 3 天沒人處理】共 2 個成員\n  單號 5：disk 153（HOST-A），已上報 4 天\n"
                     + "【本趟派不出去的問題】共 1 項\n  disk 7：派工池沒有人（3 台）\n"
                     + "【上報時沒有任何收件人的單】共 1 張\n  單號 9：ntfs 55（上報信寄不出去，請確認管理者的 email）";
        var (subject, body) = WorkOrderDigestBuilder.Build(new[] { (Reminder(2, 1, 1, detail), (WorkOrderDigestOrder?)null) });

        Assert.Equal("管理者提醒", subject);
        Assert.Contains("上報逾期未處理 2 個成員、本趟派不出去 1 項、上報時沒有收件人 1 張單", body);
        Assert.Contains("單號 5：disk 153（HOST-A），已上報 4 天", body);
        Assert.Contains("disk 7：派工池沒有人（3 台）", body);
        Assert.Contains("單號 9：ntfs 55", body);
        Assert.Contains("交辦總覽", body);
        Assert.DoesNotContain("我的交辦", body);
    }

    [Fact]
    public void 管理者提醒與一般項目同封_標題附加提醒_兩段都在()
    {
        var created = new WorkOrderOutboxItem
        {
            Kind = WorkOrderOutboxKinds.Created, WorkOrderId = 7, RecipientUserId = 1, EnqueuedAt = new DateTime(2026, 11, 17, 9, 0, 0)
        };
        var order = new WorkOrderDigestOrder(7, "disk 153", null, 1, new[] { "HOST-A" }, null, null);
        var (subject, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (created, (WorkOrderDigestOrder?)order),
            (Reminder(1, 0, 0, "【上報後逾 3 天沒人處理】共 1 個成員"), (WorkOrderDigestOrder?)null)
        });

        Assert.EndsWith("、管理者提醒", subject);
        Assert.Contains("【新交辦】", body);
        Assert.Contains("【管理者提醒】", body);
        Assert.Contains("我的交辦", body);
    }

    [Fact]
    public void 同一位管理者同時段有多筆提醒_合併成一段_計數加總_兩組明細都在()
    {
        var first = Reminder(1, 0, 0, "【上報後逾 3 天沒人處理】共 1 個成員\n  單號 5：disk 153（HOST-A），已上報 4 天");
        first.EnqueuedAt = first.EnqueuedAt.AddHours(-1);
        var second = Reminder(2, 0, 1, "【上報後逾 3 天沒人處理】共 2 個成員\n  單號 6：disk 7（HOST-B），已上報 5 天\n  單號 8：disk 9（HOST-C），已上報 6 天\n"
                                       + "【上報時沒有任何收件人的單】共 1 張\n  單號 9：ntfs 55（上報信寄不出去，請確認管理者的 email）");
        var (subject, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (first, (WorkOrderDigestOrder?)null), (second, (WorkOrderDigestOrder?)null)
        });

        Assert.Equal("管理者提醒", subject);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "【管理者提醒】").Count);
        Assert.Contains("上報逾期未處理 3 個成員、本趟派不出去 0 項、上報時沒有收件人 1 張單", body);
        Assert.Contains("單號 5：disk 153", body);
        Assert.Contains("單號 6：disk 7", body);
        Assert.Contains("單號 8：disk 9", body);
        Assert.Contains("單號 9：ntfs 55", body);
        // 同類明細併在同一個標題下，標題計數是加總
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "【上報後逾 3 天沒人處理】").Count);
        Assert.Contains("【上報後逾 3 天沒人處理】共 3 個成員", body);
    }

    [Fact]
    public void 兩趟提醒的同一行明細只列一次()
    {
        var a = Reminder(0, 1, 0, "【本趟派不出去的問題】共 1 項\n  disk 7：派工池沒有人（3 台）");
        var b = Reminder(0, 1, 0, "【本趟派不出去的問題】共 1 項\n  disk 7：派工池沒有人（3 台）");
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (a, (WorkOrderDigestOrder?)null), (b, (WorkOrderDigestOrder?)null) });

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "disk 7：派工池沒有人").Count);
        Assert.Contains("【本趟派不出去的問題】共 1 項", body);
    }

    [Fact]
    public void 明細超過上限被截斷_不擲例外()
    {
        var clamped = WorkOrderOutboxItem.ClampDetail(new string('字', WorkOrderOutboxItem.DetailMaxLength + 500));
        Assert.Equal(WorkOrderOutboxItem.DetailMaxLength, clamped!.Length);
        Assert.Null(WorkOrderOutboxItem.ClampDetail(null));
    }
}

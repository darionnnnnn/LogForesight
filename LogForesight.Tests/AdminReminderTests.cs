using LogForesight.Core.Models;
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
    public void 同一位管理者同時段有多筆提醒只列最新一筆()
    {
        var older = Reminder(1, 0, 0, "舊的明細");
        older.EnqueuedAt = older.EnqueuedAt.AddDays(-1);
        var (_, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (older, (WorkOrderDigestOrder?)null), (Reminder(3, 0, 0, "新的明細"), (WorkOrderDigestOrder?)null)
        });

        Assert.Contains("新的明細", body);
        Assert.DoesNotContain("舊的明細", body);
    }

    [Fact]
    public void 明細超過上限被截斷_不擲例外()
    {
        var clamped = WorkOrderOutboxItem.ClampDetail(new string('字', WorkOrderOutboxItem.DetailMaxLength + 500));
        Assert.Equal(WorkOrderOutboxItem.DetailMaxLength, clamped!.Length);
        Assert.Null(WorkOrderOutboxItem.ClampDetail(null));
    }
}

using Microsoft.Extensions.Time.Testing;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
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

    private static FakeTimeProvider LocalClock(DateTime localNow)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        return clock;
    }

    private static (AdminReminderService Service, MailNotifyStateStore State, FakeTimeProvider Clock, EfSqliteFixture Fx) NewUnassignableService(DateTime now)
    {
        var fx = new EfSqliteFixture();
        var settings = new FakeSystemSettingsStore();
        var hosts = new FakeHostStore();
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var cases = new FakeIssueCaseStore();
        var orders = new FakeWorkOrderStore(cases);
        var state = new MailNotifyStateStore(fx.Blob("mail_notify_state"));
        var mail = new MailNotificationService(
            settings, new FakeSmtpMailSender(), hosts, users, groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), state,
            new ScheduleFreshnessService(
                new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(fx.Blob("schedule_options"))),
            new WorkOrderOutboxStore(fx.Blob(WorkOrderOutboxStore.BlobKey)), orders, cases, new FakeRuleStore(), new FakeIssueOwnerStore());
        var clock = LocalClock(now);
        return (new AdminReminderService(settings, orders, cases, hosts, users, groups, state, mail, clock: clock), state, clock, fx);
    }

    private static NightlyDispatchSummary Summary(params (string Label, string Reason, int Hosts)[] items) => new()
    {
        Unassignable = items.Select(i => new NightlyUnassignableIssue(i.Label, i.Reason, i.Hosts)).ToList()
    };

    [Fact]
    public void 派不出去清單_部分趟的空結果不清掉既有項目()
    {
        var (service, state, _, fx) = NewUnassignableService(new DateTime(2026, 11, 17, 23, 0, 0));
        using var _fx = fx;
        service.RunAfterNightly(Summary(("disk 154", "no_pool", 3), ("ntfs 55", "all_paused", 1)));

        service.RunAfterNightly(Summary());

        Assert.Equal(new[] { "disk 154", "ntfs 55" }, state.Get().LastUnassignable.Select(u => u.IssueLabel).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void 派不出去清單_同鍵更新台數_同日部分趟不把台數蓋小()
    {
        var (service, state, clock, fx) = NewUnassignableService(new DateTime(2026, 11, 17, 23, 0, 0));
        using var _fx = fx;
        service.RunAfterNightly(Summary(("disk 154", "no_pool", 5)));

        clock.SetUtcNow(LocalClock(new DateTime(2026, 11, 17, 23, 30, 0)).GetUtcNow());
        service.RunAfterNightly(Summary(("disk 154", "no_pool", 2)));
        Assert.Equal(5, Assert.Single(state.Get().LastUnassignable).Hosts);

        clock.SetUtcNow(LocalClock(new DateTime(2026, 11, 18, 23, 0, 0)).GetUtcNow());
        service.RunAfterNightly(Summary(("disk 154", "no_pool", 2)));
        Assert.Equal(2, Assert.Single(state.Get().LastUnassignable).Hosts);

        // 同標籤不同原因是兩個項目
        service.RunAfterNightly(Summary(("disk 154", "all_paused", 1)));
        Assert.Equal(2, state.Get().LastUnassignable.Count);
    }

    [Fact]
    public void 派不出去清單_連續兩個日曆日沒再觀察到才移除_一天內保留()
    {
        var (service, state, clock, fx) = NewUnassignableService(new DateTime(2026, 11, 17, 23, 0, 0));
        using var _fx = fx;
        service.RunAfterNightly(Summary(("disk 154", "no_pool", 3)));

        clock.SetUtcNow(LocalClock(new DateTime(2026, 11, 18, 23, 0, 0)).GetUtcNow());
        service.RunAfterNightly(Summary());
        Assert.Single(state.Get().LastUnassignable);

        clock.SetUtcNow(LocalClock(new DateTime(2026, 11, 19, 23, 0, 0)).GetUtcNow());
        service.RunAfterNightly(Summary());
        Assert.Empty(state.Get().LastUnassignable);
    }

    [Fact]
    public void 派不出去清單_舊資料沒有ObservedAt_不擲例外_依LastUnassignableAt判斷()
    {
        var (service, state, clock, fx) = NewUnassignableService(new DateTime(2026, 11, 19, 23, 0, 0));
        using var _fx = fx;
        void Seed(DateTime lastAt) => state.Update(s =>
        {
            s.LastUnassignable = new List<UnassignableIssue> { new() { IssueLabel = "old 1", Reason = "no_pool", Hosts = 1, ObservedAt = null } };
            s.LastUnassignableAt = lastAt;
        });

        Seed(new DateTime(2026, 11, 18, 23, 0, 0));
        service.RunAfterNightly(Summary());
        Assert.Single(state.Get().LastUnassignable);   // 11/18 不早於 11/18，保留

        Seed(new DateTime(2026, 11, 17, 23, 0, 0));
        service.RunAfterNightly(Summary());
        Assert.Empty(state.Get().LastUnassignable);    // 11/17 早於 11/18，移除
    }

    [Fact]
    public void 排程的管理者提醒呼叫點直接傳本趟結果_不再依請求形狀判斷()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Services", "SchedulerHostedService.cs"));
        Assert.Contains("RunAfterNightly(dispatchSummary)", source);
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

    private static ReminderDetailItem Esc(string caseId, string text, int total) => new()
    {
        Category = ReminderDetailItem.CategoryEscalated, Key = caseId, Text = text, Total = total,
        Heading = "【上報後逾 3 天沒人處理】", Unit = "個成員"
    };

    private static WorkOrderOutboxItem StructuredReminder(DateTime at, int escalated, params ReminderDetailItem[] items)
    {
        var item = Reminder(escalated, 0, 0, null);
        item.EnqueuedAt = at;
        item.ReminderItems = items.ToList();
        return item;
    }

    [Fact]
    public void 同一成員隔天又入列_只列一次取最新文字_計數為1()
    {
        var day1 = StructuredReminder(new DateTime(2026, 11, 16, 23, 0, 0), 1, Esc("case-1", "單號 5：disk 153（HOST-A），已上報 4 天", 1));
        var day2 = StructuredReminder(new DateTime(2026, 11, 17, 23, 0, 0), 1, Esc("case-1", "單號 5：disk 153（HOST-A），已上報 5 天", 1));
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (day2, (WorkOrderDigestOrder?)null), (day1, (WorkOrderDigestOrder?)null) });

        Assert.Contains("上報逾期未處理 1 個成員", body);
        Assert.Contains("【上報後逾 3 天沒人處理】共 1 個成員", body);
        Assert.Contains("已上報 5 天", body);
        Assert.DoesNotContain("已上報 4 天", body);
        Assert.DoesNotContain("…等", body);
    }

    [Fact]
    public void 截斷筆與未截斷筆合併_取最大總數_截斷行在段落結尾()
    {
        var truncated = StructuredReminder(new DateTime(2026, 11, 16, 23, 0, 0), 25,
            Enumerable.Range(1, 20).Select(n => Esc($"c{n}", $"單號 {n}：x（H{n}），已上報 4 天", 25)).ToArray());
        var small = StructuredReminder(new DateTime(2026, 11, 17, 23, 0, 0), 3,
            Enumerable.Range(1, 3).Select(n => Esc($"c{n}", $"單號 {n}：x（H{n}），已上報 5 天", 3)).ToArray());
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (truncated, (WorkOrderDigestOrder?)null), (small, (WorkOrderDigestOrder?)null) });

        Assert.Contains("上報逾期未處理 25 個成員", body);
        Assert.Contains("【上報後逾 3 天沒人處理】共 25 個成員", body);
        Assert.Contains("…等共 25 個成員", body);
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Matches(body, "單號 \\d+：x").Count);
        var truncLine = body.IndexOf("…等共 25", StringComparison.Ordinal);
        Assert.True(truncLine > body.LastIndexOf("單號 20：x", StringComparison.Ordinal));
        Assert.Contains("單號 1：x（H1），已上報 5 天", body);
    }

    [Fact]
    public void 只有文字的舊項目與結構化項目同封_都照常寄()
    {
        var legacy = Reminder(0, 1, 0, "【本趟派不出去的問題】共 1 項\n  disk 7：派工池沒有人（3 台）");
        legacy.EnqueuedAt = legacy.EnqueuedAt.AddDays(-1);
        var structured = StructuredReminder(new DateTime(2026, 11, 17, 9, 0, 0), 1, Esc("case-1", "單號 5：disk 153（HOST-A），已上報 4 天", 1));
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (legacy, (WorkOrderDigestOrder?)null), (structured, (WorkOrderDigestOrder?)null) });

        Assert.Contains("上報逾期未處理 1 個成員、本趟派不出去 1 項", body);
        Assert.Contains("disk 7：派工池沒有人（3 台）", body);
        Assert.Contains("單號 5：disk 153", body);
    }

    [Fact]
    public void 明細超過上限被截斷_不擲例外()
    {
        var clamped = WorkOrderOutboxItem.ClampDetail(new string('字', WorkOrderOutboxItem.DetailMaxLength + 500));
        Assert.Equal(WorkOrderOutboxItem.DetailMaxLength, clamped!.Length);
        Assert.Null(WorkOrderOutboxItem.ClampDetail(null));
    }
}

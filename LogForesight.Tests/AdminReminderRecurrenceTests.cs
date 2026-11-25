using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 管理者提醒的第四類「反覆結案又復發」：進行中成員的復發次數達門檻（3）才列入、結案為不處理類後不列、
/// 站內提醒與摘要信同一個彙整、同一成員依間隔天數至多提醒一次。
/// </summary>
public sealed class AdminReminderRecurrenceTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 20);
    private static readonly string DiskKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Error);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeTimeProvider _clock;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly MailNotifyStateStore _state;
    private readonly WorkOrderOutboxStore _outbox;
    private readonly AdminReminderService _service;
    private readonly long _orderId;
    private int _seq;

    public AdminReminderRecurrenceTests()
    {
        _clock = LocalClock(Today.AddHours(23));
        _cases.Clock = _clock;
        _orders = new FakeWorkOrderStore(_cases, _clock);
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => { s.MailEnabled = true; s.MailNotifyWorkOrders = true; s.EscalationReminderDays = 3; });
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var admin = groups.Upsert(new UserGroup { GroupName = "管理者", Role = UserRole.Admin, Active = true });
        users.Upsert(new WebUser { Account = "boss", DisplayName = "boss", Email = "boss@example.com", Active = true, GroupIds = new List<long> { admin.GroupId } });
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        _outbox = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey));
        var mail = new MailNotificationService(
            settings, new FakeSmtpMailSender(), _hosts, users, groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), _state,
            new ScheduleFreshnessService(
                new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_fx.Blob("schedule_options"))),
            _outbox, _orders, _cases, new FakeRuleStore(), new FakeIssueOwnerStore());
        _service = new AdminReminderService(settings, _orders, _cases, _hosts, users, groups, _state, mail, new FakeRuleStore(), clock: _clock);
        _orderId = _orders.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = 153, IssueLabel = "disk 153", HandlerId = 77, Origin = WorkOrderOrigins.Manual,
            ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin", CreatedAt = Today.AddDays(-60)
        });
    }

    public void Dispose() => _fx.Dispose();

    private static FakeTimeProvider LocalClock(DateTime localNow)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        return clock;
    }

    private void SetNow(DateTime local) => _clock.SetUtcNow(LocalClock(local).GetUtcNow());

    private IssueCase Case(string host, string status, DateTime created, DateTime? closed = null, long? orderId = null, int eventId = 153)
    {
        if (_hosts.FindByName(host) == null) _hosts.Upsert(new WebHost { HostName = host });
        var c = new IssueCase
        {
            CaseId = $"case-{++_seq:D4}", HostName = host, IssueKey = IssueSignatureKey.For("System", "disk", eventId, LogEntryType.Error),
            IssueLabel = $"disk {eventId}", Status = status, HandlerId = 77, FirstLinkedDate = created.Date, LastLinkedDate = created.Date,
            CreatedAt = created, CreatedByAccount = "admin", UpdatedAt = created, ClosedAt = closed, WorkOrderId = orderId
        };
        _cases.Save(c);
        return c;
    }

    /// <summary>一台主機：<paramref name="resolvedPrior"/> 件已處理的前案（結案日依序是 5、15、25 天前）加一件進行中成員</summary>
    private IssueCase HostWithRecurrences(string host, int resolvedPrior)
    {
        for (var i = 0; i < resolvedPrior; i++)
            Case(host, IssueHandlingStatuses.Resolved, Today.AddDays(-9 - 10 * i), Today.AddDays(-5 - 10 * i));
        return Case(host, IssueHandlingStatuses.InProgress, Today.AddDays(-1), orderId: _orderId);
    }

    // ── 門檻 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 復發次數2不列_3列入_站內提醒帶出次數與單號()
    {
        HostWithRecurrences("H2", 2);
        HostWithRecurrences("H3", 3);

        var dto = _service.Compute();

        Assert.Equal(1, dto.RecurrenceCount);
        var item = Assert.Single(dto.Recurrence);
        Assert.Equal("H3", item.HostName);
        Assert.Equal(3, item.RecurrenceCount);
        Assert.Equal(_orderId, item.WorkOrderId);
        Assert.Equal("disk EventId 153", item.IssueLabel);
        Assert.True(dto.Any);
        Assert.Equal(3, AdminReminderService.RecurrenceReminderThreshold);
    }

    [Fact]
    public void 前案在三十天窗外的不計次數_兩件在窗內加一件窗外仍不列()
    {
        Case("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-45), Today.AddDays(-32));   // 本件建立日（−1）起算第 31 天：窗外
        Case("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-19), Today.AddDays(-15));
        Case("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-9), Today.AddDays(-5));
        Case("H1", IssueHandlingStatuses.InProgress, Today.AddDays(-1), orderId: _orderId);

        Assert.Equal(0, _service.Compute().RecurrenceCount);

        // 把最早那件的結案日挪進第 30 天：剛好 3 次
        var cases = _cases.GetMany(new[] { "H1" }).Where(c => c.ClosedAt == Today.AddDays(-32)).ToList();
        cases[0].ClosedAt = Today.AddDays(-31);   // 建立日（−1）起算第 30 天
        Assert.Equal(1, _service.Compute().RecurrenceCount);
    }

    [Fact]
    public void 成員之後結案為不處理類_不再列入_結案為已處理也不再是進行中成員()
    {
        var member = HostWithRecurrences("H3", 3);
        Assert.Equal(1, _service.Compute().RecurrenceCount);

        member.Status = IssueHandlingStatuses.WontFix;
        member.ClosedAt = Today.AddHours(8);
        _cases.Save(member);

        Assert.Equal(0, _service.Compute().RecurrenceCount);
    }

    [Fact]
    public void 停用主機上的成員不列()
    {
        HostWithRecurrences("H3", 3);
        Assert.Equal(1, _service.Compute().RecurrenceCount);

        _hosts.FindByName("H3")!.Active = false;

        Assert.Equal(0, _service.Compute().RecurrenceCount);
    }

    // ── 摘要信 ───────────────────────────────────────────────────────────────

    private List<WorkOrderOutboxItem> Reminders() =>
        _outbox.Get().Items.Values.Where(i => i.Kind == WorkOrderOutboxKinds.AdminReminder).OrderBy(i => i.EnqueuedAt).ToList();

    [Fact]
    public void 摘要信含新類別_計數與明細與站內提醒同一個彙整()
    {
        var member = HostWithRecurrences("H3", 3);

        _service.RunAfterNightly(null);

        var item = Assert.Single(Reminders());
        Assert.Equal(1, item.ReminderRecurrence);
        var detail = Assert.Single(item.ReminderItems!);
        Assert.Equal(ReminderDetailItem.CategoryRecurrence, detail.Category);
        Assert.Equal(AdminReminderService.RecurrenceKey(member.CaseId), detail.Key);
        Assert.Equal(_service.Compute().Recurrence.Single().HostName, "H3");

        var (subject, body) = WorkOrderDigestBuilder.Build(new[] { (item, (WorkOrderDigestOrder?)null) });

        Assert.Equal("管理者提醒", subject);
        Assert.Contains("、反覆結案又復發 1 個成員。", body);
        Assert.Contains("【反覆結案又復發】共 1 個成員", body);
        Assert.Contains($"單號 {_orderId}：disk EventId 153（H3），已處理後又復發 3 次", body);
    }

    [Fact]
    public void 沒有復發成員時摘要句不出現新類別字樣_與既有寫法相同()
    {
        var item = new WorkOrderOutboxItem
        {
            Kind = WorkOrderOutboxKinds.AdminReminder, RecipientUserId = 1, EnqueuedAt = Today, ActorAccount = "system",
            ReminderEscalated = 1, Detail = "【上報後逾 3 天沒人處理】共 1 個成員\n  單號 5：disk 153（HOST-A），已上報 4 天"
        };

        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (item, (WorkOrderDigestOrder?)null) });

        Assert.Contains("上報逾期未處理 1 個成員、本趟派不出去 0 項、上報時沒有收件人 0 張單。", body);
        Assert.DoesNotContain("反覆結案又復發", body);
    }

    [Fact]
    public void 只有文字的舊項目帶復發計數_摘要句也列出()
    {
        var item = new WorkOrderOutboxItem
        {
            Kind = WorkOrderOutboxKinds.AdminReminder, RecipientUserId = 1, EnqueuedAt = Today, ActorAccount = "system", ReminderRecurrence = 2,
            Detail = "【反覆結案又復發】共 2 個成員\n  單號 5：disk 153（HOST-A），已處理後又復發 3 次"
        };

        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (item, (WorkOrderDigestOrder?)null) });

        Assert.Contains("、反覆結案又復發 2 個成員。", body);
    }

    // ── 同一成員只提醒一次 ───────────────────────────────────────────────────

    [Fact]
    public void 同一成員同一天重複執行_不重複入列()
    {
        HostWithRecurrences("H3", 3);

        _service.RunAfterNightly(null);
        _service.RunAfterNightly(null);

        Assert.Single(Reminders());
    }

    [Fact]
    public void 同一成員依間隔天數提醒_第2天不再提醒_第3天再提醒一次_間隔邊界兩側()
    {
        var member = HostWithRecurrences("H3", 3);
        _service.RunAfterNightly(null);
        Assert.Single(Reminders());
        Assert.Equal(Today.ToString("yyyy-MM-dd"), _state.Get().AdminReminderLastSent[AdminReminderService.RecurrenceKey(member.CaseId)]);

        SetNow(Today.AddDays(2).AddHours(23));
        _service.RunAfterNightly(null);
        Assert.Single(Reminders());   // 間隔 2 天 < 3

        SetNow(Today.AddDays(3).AddHours(23));
        _service.RunAfterNightly(null);
        Assert.Equal(2, Reminders().Count);   // 間隔 3 天 >= 3
        Assert.Equal(Today.AddDays(3).ToString("yyyy-MM-dd"), _state.Get().AdminReminderLastSent[AdminReminderService.RecurrenceKey(member.CaseId)]);
    }

    [Fact]
    public void 成員不再符合後_提醒紀錄清掉_站內提醒不受間隔影響仍顯示()
    {
        var member = HostWithRecurrences("H3", 3);
        _service.RunAfterNightly(null);
        var key = AdminReminderService.RecurrenceKey(member.CaseId);
        Assert.True(_state.Get().AdminReminderLastSent.ContainsKey(key));
        // 間隔內再看站內提醒：仍然列出（總覽不受提醒間隔影響）
        Assert.Equal(1, _service.Compute().RecurrenceCount);

        member.Status = IssueHandlingStatuses.WontFix;
        member.ClosedAt = Today.AddDays(1);
        _cases.Save(member);
        _service.RunAfterNightly(null);

        Assert.False(_state.Get().AdminReminderLastSent.ContainsKey(key));
    }

    [Fact]
    public void 復發鍵與上報鍵分開_同一成員兩類都符合時各自去重_互不覆蓋()
    {
        var member = HostWithRecurrences("H3", 3);
        member.Status = IssueHandlingStatuses.Escalated;
        member.EscalatedAt = Today.AddDays(-10);
        _cases.Save(member);

        _service.RunAfterNightly(null);

        var sent = _state.Get().AdminReminderLastSent;
        Assert.True(sent.ContainsKey(member.CaseId));                                          // 上報類
        Assert.True(sent.ContainsKey(AdminReminderService.RecurrenceKey(member.CaseId)));       // 復發類
        var item = Assert.Single(Reminders());
        Assert.Equal(1, item.ReminderEscalated);
        Assert.Equal(1, item.ReminderRecurrence);
    }

    [Fact]
    public void 提醒間隔設為0_不寄信_站內提醒仍列出()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => { s.MailEnabled = true; s.MailNotifyWorkOrders = true; s.EscalationReminderDays = 0; });
        var service = new AdminReminderService(settings, _orders, _cases, _hosts, new FakeUserStore(), new FakeUserGroupStore(), _state,
            new MailNotificationService(settings, new FakeSmtpMailSender(), _hosts, new FakeUserStore(), new FakeUserGroupStore(),
                new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(), _state,
                new ScheduleFreshnessService(
                    new BatchRunStore(_fx.LogStore("batch_runs2"), _fx.LogStore("batch_run_logs2")),
                    new ScheduleOptionsStore(_fx.Blob("schedule_options2"))),
                _outbox, _orders, _cases, new FakeRuleStore(), new FakeIssueOwnerStore()), new FakeRuleStore(), clock: _clock);
        HostWithRecurrences("H3", 3);

        service.RunAfterNightly(null);

        Assert.Empty(Reminders());
        Assert.Equal(1, service.Compute().RecurrenceCount);
    }
}

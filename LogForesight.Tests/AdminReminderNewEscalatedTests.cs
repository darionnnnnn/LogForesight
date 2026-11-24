using Microsoft.Extensions.Time.Testing;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 管理者提醒「今天新上報」一類：上報時間（EscalatedAt，舊資料退回 UpdatedAt）是今天的上報成員，
/// 與「超過 N 天」分開計數與列出；已退回、已結案的不列。逾期類行為不變。
/// </summary>
public sealed class AdminReminderNewEscalatedTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 24, 15, 0, 0);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly AdminReminderService _service;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeHostStore _hosts = new();
    private int _seq;

    public AdminReminderNewEscalatedTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        var mail = new MailNotificationService(
            _settings, new FakeSmtpMailSender(), _hosts, users, groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), state,
            new ScheduleFreshnessService(
                new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_fx.Blob("schedule_options"))),
            new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)), _orders, _cases, new FakeRuleStore(), new FakeIssueOwnerStore());
        var clock = new FakeTimeProvider(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        _service = new AdminReminderService(_settings, _orders, _cases, _hosts, users, groups, state, mail, clock: clock);
    }

    public void Dispose() => _fx.Dispose();

    /// <summary>一張單掛一個成員；回傳成員。escalatedAt 為 null 代表升級前的舊資料（退回 UpdatedAt）</summary>
    private IssueCase AddMember(string status, DateTime? escalatedAt, DateTime updatedAt, DateTime? closedAt = null)
    {
        var n = ++_seq;
        _hosts.Upsert(new WebHost { HostName = $"SRV{n}", Active = true });
        var orderId = _orders.Insert(new WorkOrder { SourceName = "disk", EventId = 100 + n, IssueLabel = $"disk {100 + n}", HandlerId = 1, CreatedAt = Now.AddDays(-10) });
        var member = new IssueCase
        {
            CaseId = $"c{n}", HostName = $"SRV{n}", IssueKey = "k", IssueLabel = "disk 153", Status = status, WorkOrderId = orderId,
            HandlerId = 1, CreatedAt = Now.AddDays(-10), UpdatedAt = updatedAt, EscalatedAt = escalatedAt, ClosedAt = closedAt,
            FirstLinkedDate = Now.Date, LastLinkedDate = Now.Date
        };
        _cases.Save(member);
        return member;
    }

    [Fact]
    public void 今天上報_列入新類別且不算逾期()
    {
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddHours(8), Now.Date.AddHours(8));

        var dto = _service.Compute();

        Assert.Equal(1, dto.NewEscalatedCount);
        var row = Assert.Single(dto.NewEscalated);
        Assert.Equal("SRV1", row.HostName);
        Assert.Equal(0, dto.EscalatedCount);
        Assert.True(dto.Any);
    }

    [Fact]
    public void 昨天上報_不列入新類別()
    {
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddDays(-1).AddHours(23), Now.Date.AddDays(-1).AddHours(23));

        var dto = _service.Compute();

        Assert.Equal(0, dto.NewEscalatedCount);
        Assert.Empty(dto.NewEscalated);
    }

    [Fact]
    public void 今天上報但已退回或已結案_不列()
    {
        // 已退回：狀態不再是 escalated（EscalatedAt 在轉出時清空）
        AddMember(IssueHandlingStatuses.InProgress, null, Now.Date.AddHours(9));
        // 已結案
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddHours(8), Now.Date.AddHours(8), closedAt: Now.Date.AddHours(10));

        var dto = _service.Compute();

        Assert.Equal(0, dto.NewEscalatedCount);
        Assert.False(dto.Any);
    }

    [Fact]
    public void 舊資料沒有EscalatedAt_退回UpdatedAt判定是不是今天()
    {
        AddMember(IssueHandlingStatuses.Escalated, null, Now.Date.AddHours(1));                 // UpdatedAt 今天 → 新上報
        AddMember(IssueHandlingStatuses.Escalated, null, Now.Date.AddDays(-1).AddHours(23));    // UpdatedAt 昨天 → 不是

        var dto = _service.Compute();

        Assert.Equal(1, dto.NewEscalatedCount);
        Assert.Equal("SRV1", Assert.Single(dto.NewEscalated).HostName);
    }

    [Fact]
    public void 新上報與逾期分開計數_逾期類行為不變()
    {
        var threshold = SystemSettings.DefaultEscalationReminderDays;
        // 恰好超過門檻 1 天 → 逾期；恰好等於門檻 → 都不是（邊界兩側）
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddDays(-(threshold + 1)), Now.Date.AddDays(-(threshold + 1)));
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddDays(-threshold), Now.Date.AddDays(-threshold));
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddHours(7), Now.Date.AddHours(7));

        var dto = _service.Compute();

        Assert.Equal(1, dto.EscalatedCount);
        Assert.Equal(threshold + 1, Assert.Single(dto.Escalated).Days);
        Assert.Equal(1, dto.NewEscalatedCount);
        Assert.Equal("SRV3", Assert.Single(dto.NewEscalated).HostName);
    }

    [Fact]
    public void 新上報明細上限沿用每類上限_計數是全部()
    {
        for (var i = 0; i < AdminReminderService.OverviewItemLimit + 5; i++)
            AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddHours(6), Now.Date.AddHours(6));

        var dto = _service.Compute();

        Assert.Equal(AdminReminderService.OverviewItemLimit + 5, dto.NewEscalatedCount);
        Assert.Equal(AdminReminderService.OverviewItemLimit, dto.NewEscalated.Count);
    }

    [Fact]
    public void 新上報不進郵件的逾期提醒彙整()
    {
        _settings.Update(s => { s.MailEnabled = true; s.MailNotifyWorkOrders = true; s.EscalationReminderDays = 3; });
        AddMember(IssueHandlingStatuses.Escalated, Now.Date.AddHours(8), Now.Date.AddHours(8));

        var result = _service.ComputeResult();

        Assert.Equal(1, result.Dto.NewEscalatedCount);
        Assert.Equal(0, result.MailEscalatedCount);
        Assert.Empty(AdminReminderService.BuildMailItems(result, includeUnassignable: false));
    }
}

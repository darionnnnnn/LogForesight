using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理說明的後端把關：交辦單回覆／代為結案的長度上限（經 JSON 綁定＋DataAnnotations，
/// 與 [ApiController] 自動回 400 同一條驗證）、事件欄位優先保留使用者原文、不處理必填理由、
/// 多單回覆中途失敗時已成功的單逐張稽核且回應交代成功／失敗／未處理。
/// </summary>
public class HandlingNoteBackendTests : IDisposable
{
    private const string Source = "disk";
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FailingEventStore _orders;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly MailNotificationService _mail;
    private readonly WebUser _alice;

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    public HandlingNoteBackendTests()
    {
        _orders = new FailingEventStore(_cases);
        var caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, caseCoordinator, _handlingLog, _hosts);
        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲", Active = true });

        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        _mail = new MailNotificationService(
            _settings, new FakeSmtpMailSender(), _hosts, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlingLog, new MailNotifyStateStore(_fx.Blob("mail_notify_state")),
            freshness, new FakeIssueOwnerStore());

        foreach (var host in new[] { "H1", "H2", "H3" })
        {
            var h = _hosts.Upsert(new WebHost { HostName = host });
            _records.Add(new DailyAnalysisRecord
            {
                Date = D2, HostId = h.HostId, Host = host,
                TopIssues = new List<LogIssueSignature> { Signature(153), Signature(154), Signature(155) }
            });
        }
    }

    /// <summary>指定單寫回覆事件時擲例外：模擬寫入中途某張失敗（檢查全數通過之後）</summary>
    private sealed class FailingEventStore : FakeWorkOrderStore
    {
        public FailingEventStore(IIssueCaseStore cases) : base(cases) { }

        public long? FailOnReplyOf { get; set; }
        public Exception Failure { get; set; } = new InvalidOperationException("注入的寫入失敗");

        public override void AppendEvent(WorkOrderEvent evt)
        {
            if (evt.WorkOrderId == FailOnReplyOf && evt.Action == WorkOrderEventActions.Replied) throw Failure;
            base.AppendEvent(evt);
        }
    }

    private static LogIssueSignature Signature(int eventId) =>
        new() { LogName = "System", Source = Source, EventId = eventId, EntryType = EventLogEntryType.Error };

    private long CreateOrder(int eventId, string host) =>
        _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = Source, EventId = eventId, IssueLabel = $"{Source} {eventId}", HandlerId = _alice.UserId,
            Members = new List<WorkOrderMember>
            {
                new() { HostName = host, IssueKey = IssueSignatureKey.For(Signature(eventId)), IssueLabel = $"{Source} {eventId}", TriggerDate = D2 }
            },
            Actor = new WorkOrderActor { ActorAccount = "DOMAIN\\boss", OccurredAt = DateTime.Now.AddMinutes(-5) }
        }).WorkOrderId;

    private WorkOrderReplyService Service() =>
        new(_coordinator, _orders, _cases, FakeCurrentUser.ForUser(_alice.UserId, Capability.Handle), _audit, _mail);

    private static bool IsValid(object request) =>
        Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), validateAllProperties: true);

    // ── 長度上限（JSON 綁定＋模型驗證）──────────────────────────────────────

    [Fact]
    public void 交辦單回覆_說明1001字_模型驗證不通過_1000字通過()
    {
        var tooLong = JsonSerializer.Deserialize<WorkOrderReplyRequest>(
            JsonSerializer.Serialize(new { status = "resolved", note = new string('說', 1001) }), WebDefaults)!;
        var ok = JsonSerializer.Deserialize<WorkOrderReplyRequest>(
            JsonSerializer.Serialize(new { status = "resolved", note = new string('說', 1000) }), WebDefaults)!;

        Assert.Equal(1001, tooLong.Note!.Length);
        Assert.False(IsValid(tooLong));
        Assert.True(IsValid(ok));
    }

    [Fact]
    public void 代為結案_原因1001字_模型驗證不通過()
    {
        var request = JsonSerializer.Deserialize<AdminCloseWorkOrderRequest>(
            JsonSerializer.Serialize(new { status = "resolved", reason = new string('因', 1001) }), WebDefaults)!;

        Assert.Equal(1001, request.Reason!.Length);
        Assert.False(IsValid(request));
    }

    [Fact]
    public void 多單回覆_說明1001字_模型驗證不通過()
    {
        var request = JsonSerializer.Deserialize<WorkOrderReplyManyRequest>(
            JsonSerializer.Serialize(new { workOrderIds = new[] { 1L }, status = "resolved", note = new string('說', 1001) }), WebDefaults)!;

        Assert.False(IsValid(request));
    }

    // ── 回覆事件說明截斷 ──────────────────────────────────────────────────

    [Fact]
    public void 回覆事件說明_1000字說明_完整保留使用者原文()
    {
        var note = WorkOrderCoordinator.ReplyNoteOf("wont_fix", new string('x', 1000), 2);

        Assert.True(note.Length <= 1000, $"長度 {note.Length}");
        Assert.Equal(new string('x', 1000), note);
    }

    [Fact]
    public void 回覆事件說明_說明空白_只有狀態與台數()
    {
        Assert.Equal("wont_fix（2 台）", WorkOrderCoordinator.ReplyNoteOf("wont_fix", "  ", 2));
    }

    [Fact]
    public void 回覆事件說明_短說明不截()
    {
        Assert.Equal("resolved：修好了（1 台）", WorkOrderCoordinator.ReplyNoteOf("resolved", "修好了", 1));
    }

    [Fact]
    public void 交辦單回覆_1000字說明_事件表完整保留原文()
    {
        var id = CreateOrder(153, "H1");

        Service().Reply(id, new WorkOrderReplyRequest { Status = IssueHandlingStatuses.Resolved, Note = new string('x', 1000) });

        var evt = _orders.ListEvents(id).Single(e => e.Action == WorkOrderEventActions.Replied);
        Assert.True(evt.Note!.Length <= EfWorkOrderStore.NoteMaxLength);
        Assert.Equal(new string('x', 1000), evt.Note);
    }

    // ── 不處理必填理由 ────────────────────────────────────────────────────

    [Fact]
    public void 不處理_理由空白_Validation_有理由通過()
    {
        var ex = Assert.Throws<DomainException>(() => IssueStatusValidation.Validate("wont_fix", null, false, "  "));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Throws<DomainException>(() => IssueStatusValidation.Validate("wont_fix", null, false, null));

        IssueStatusValidation.Validate("wont_fix", null, false, "評估後不值得處理");
    }

    // ── 多單回覆中途失敗 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 多單回覆_第二張寫入失敗_第一張已寫且有稽核_回傳成功失敗未處理(bool domainLike)
    {
        var a = CreateOrder(153, "H1");
        var b = CreateOrder(154, "H2");
        var c = CreateOrder(155, "H3");
        _orders.FailOnReplyOf = b;
        // InvalidOperationException 經 Guard 轉成 DomainException；一般例外走未預期分支
        _orders.Failure = domainLike ? new InvalidOperationException("注入的寫入失敗") : new Exception("內部細節");

        var result = Service().ReplyMany(new WorkOrderReplyManyRequest
        {
            WorkOrderIds = new List<long> { a, b, c }, Status = IssueHandlingStatuses.Resolved, Note = "修好了"
        });

        Assert.Equal(new List<long> { a }, result.Succeeded);
        Assert.Equal(b, result.FailedWorkOrderId);
        Assert.Equal(new List<long> { c }, result.NotProcessed);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
        if (domainLike) Assert.Equal("注入的寫入失敗", result.FailureMessage);
        else Assert.DoesNotContain("內部細節", result.FailureMessage);
        Assert.Equal(1, result.WorkOrders);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(a.ToString(), entry.TargetId);
        Assert.Contains($"回覆交辦單 #{a}：1 台標為", entry.Summary);
        Assert.Null(_orders.Get(c)!.LastReplyAt);
    }

    [Fact]
    public void 多單回覆_三張全部成功_稽核三筆_無失敗()
    {
        var ids = new List<long> { CreateOrder(153, "H1"), CreateOrder(154, "H2"), CreateOrder(155, "H3") };

        var result = Service().ReplyMany(new WorkOrderReplyManyRequest
        {
            WorkOrderIds = ids, Status = IssueHandlingStatuses.Resolved, Note = "修好了"
        });

        Assert.Null(result.FailedWorkOrderId);
        Assert.Null(result.FailureMessage);
        Assert.Equal(ids, result.Succeeded);
        Assert.Empty(result.NotProcessed);
        Assert.Equal(3, _audit.Entries.Count);
        Assert.Equal(ids.Select(i => i.ToString()), _audit.Entries.Select(e => e.TargetId));
    }
}

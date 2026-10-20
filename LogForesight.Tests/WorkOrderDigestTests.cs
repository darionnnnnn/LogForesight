namespace LogForesight.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

/// <summary>
/// 交辦摘要寄送（第 55 輪 C-1b）：時間一律以參數 now 注入，不依賴真實時間。
/// 基準日 D＝2026-10-20；預設摘要時刻 08:00、12:00。
/// </summary>
public class WorkOrderDigestTests : IDisposable
{
    private static readonly DateTime D = new(2026, 10, 20);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeRuleStore _rules = new();
    private readonly WorkOrderOutboxStore _outbox;
    private readonly MailNotifyStateStore _state;
    private readonly MailNotificationService _mail;
    private readonly WebUser _alice;
    private readonly WebUser _bob;

    public WorkOrderDigestTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        _outbox = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey));
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        _mail = new MailNotificationService(
            _settingsStore, _sender, new FakeHostStore(), _users, new FakeUserGroupStore(), new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), _state, freshness, _outbox,
            _orders, _cases, _rules, new FakeIssueOwnerStore());

        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.MailNotifyWorkOrders = true;
            s.MailWorkOrderDigestTimes = new List<string> { "08:00", "12:00" };
        });

        _alice = _users.Upsert(new WebUser { Account = "alice", Email = "alice@test.local", Active = true });
        _bob = _users.Upsert(new WebUser { Account = "bob", Email = "bob@test.local", Active = true });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private long SeedOrder(string label, int hostCount, long handlerId, string? note = null, DateTime? due = null,
        string? source = null, int? eventId = null)
    {
        var id = _orders.Insert(new WorkOrder
        {
            IssueLabel = label,
            SourceName = source,
            EventId = eventId,
            HandlerId = handlerId,
            Note = note,
            DueDate = due,
            CreatedAt = D
        });
        for (var i = 1; i <= hostCount; i++)
        {
            _cases.Save(new IssueCase
            {
                CaseId = $"case-{id}-{i:D2}",
                HostName = $"host-{i:D2}",
                IssueKey = $"key-{id}",
                IssueLabel = label,
                WorkOrderId = id,
                Status = IssueHandlingStatuses.Open
            });
        }
        return id;
    }

    private void Enqueue(string kind, long workOrderId, long recipientId, DateTime at, bool immediate = false)
    {
        _outbox.Enqueue(new WorkOrderOutboxItem
        {
            Kind = kind,
            WorkOrderId = workOrderId,
            RecipientUserId = recipientId,
            EnqueuedAt = at,
            ActorAccount = "admin",
            Immediate = immediate
        });
    }

    private static DateTime At(int dayOffset, int hour, int minute = 0) => D.AddDays(dayOffset).AddHours(hour).AddMinutes(minute);

    // ── (a) 到時刻寄出、同人合併 ────────────────────────────────────────────

    [Fact]
    public async Task A1_十一點半入列_十二點寄一封且內文含單號()
    {
        var id = SeedOrder("Schannel 36888", 2, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 11, 30));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "alice@test.local" }, sent.Message.To);
        Assert.Contains("交辦摘要", sent.Message.Subject);
        Assert.Contains($"單號 {id}", sent.Message.Body);
        Assert.Empty(_outbox.Get().Items);
    }

    [Fact]
    public async Task A2_同一人兩張單_同一封含兩個單號()
    {
        var id1 = SeedOrder("Schannel 36888", 2, _alice.UserId);
        var id2 = SeedOrder("DCOM 10016", 1, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id1, _alice.UserId, At(0, 11, 30));
        Enqueue(WorkOrderOutboxKinds.Created, id2, _alice.UserId, At(0, 11, 40));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains($"單號 {id1}", sent.Message.Body);
        Assert.Contains($"單號 {id2}", sent.Message.Body);
    }

    // ── (b) 時刻之後入列的等下一個時刻 ──────────────────────────────────────

    [Fact]
    public async Task B_十三點入列_當天二十三點五十九不寄_隔天八點寄()
    {
        var id = SeedOrder("Schannel 36888", 2, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 13));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 23, 59));
        Assert.Empty(_sender.Sent);
        Assert.Single(_outbox.Get().Items);

        await _mail.CheckAndSendWorkOrderDigestAsync(At(1, 8));
        var sent = Assert.Single(_sender.Sent);
        Assert.Contains($"單號 {id}", sent.Message.Body);
        Assert.Empty(_outbox.Get().Items);
    }

    // ── (c) 同一時刻不重寄 ──────────────────────────────────────────────────

    [Fact]
    public async Task C_同一時刻第二次呼叫不重寄()
    {
        var id = SeedOrder("Schannel 36888", 2, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));
        Assert.Single(_sender.Sent);

        // 08:00 已寄過：之後才入列的項目不會被 08:00 補寄，要等 12:00
        var id2 = SeedOrder("DCOM 10016", 1, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id2, _alice.UserId, At(0, 8, 30));
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8, 1));
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 9));
        Assert.Single(_sender.Sent);

        Assert.Equal("2026-10-20", _state.Get().WorkOrderDigestLastSentSlots["08:00"]);

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));
        Assert.Equal(2, _sender.Sent.Count);
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12, 1));
        Assert.Equal(2, _sender.Sent.Count);
    }

    // ── (d) 部分失敗保留、下一時刻重寄 ──────────────────────────────────────

    [Fact]
    public async Task D_A寄送失敗B成功_A保留_下一時刻只重寄A()
    {
        var idA = SeedOrder("Schannel 36888", 2, _alice.UserId);
        var idB = SeedOrder("DCOM 10016", 1, _bob.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, idA, _alice.UserId, At(0, 7));
        Enqueue(WorkOrderOutboxKinds.Created, idB, _bob.UserId, At(0, 7));
        _sender.ThrowOnSendForRecipient = "alice@test.local";

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        var sentB = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "bob@test.local" }, sentB.Message.To);
        var remaining = Assert.Single(_outbox.Get().Items.Values);
        Assert.Equal(_alice.UserId, remaining.RecipientUserId);

        _sender.ThrowOnSendForRecipient = null;
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Equal(new[] { "alice@test.local" }, _sender.Sent[1].Message.To);
        Assert.Contains($"單號 {idA}", _sender.Sent[1].Message.Body);
        Assert.Empty(_outbox.Get().Items);
    }

    // ── (e) 無 email 保留、逾七天清除 ───────────────────────────────────────

    [Fact]
    public async Task E_無email不寄_項目標NoEmail保留_八天後清除()
    {
        var carol = _users.Upsert(new WebUser { Account = "carol", Email = "", Active = true });
        var id = SeedOrder("Schannel 36888", 2, carol.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, carol.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));

        Assert.Empty(_sender.Sent);
        var item = Assert.Single(_outbox.Get().Items.Values);
        Assert.True(item.NoEmail);

        await _mail.CheckAndSendWorkOrderDigestAsync(At(8, 12));

        Assert.Empty(_sender.Sent);
        Assert.Empty(_outbox.Get().Items);
    }

    [Fact]
    public async Task E2_無email的處理人之後補上email_項目照寄()
    {
        var carol = _users.Upsert(new WebUser { Account = "carol", Email = "  ", Active = true });
        var id = SeedOrder("Schannel 36888", 2, carol.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, carol.UserId, At(0, 7));
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));
        Assert.Empty(_sender.Sent);

        carol.Email = "carol@test.local";
        _users.Upsert(carol);
        await _mail.CheckAndSendWorkOrderDigestAsync(At(8, 12));

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "carol@test.local" }, sent.Message.To);
        Assert.Empty(_outbox.Get().Items);
    }

    [Fact]
    public async Task E3_處理人沒有email時略過_其他人照寄()
    {
        var carol = _users.Upsert(new WebUser { Account = "carol", Email = "", Active = true });
        var idC = SeedOrder("Disk 153", 1, carol.UserId);
        var idB = SeedOrder("Schannel 36888", 2, _bob.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, idC, carol.UserId, At(0, 7));
        Enqueue(WorkOrderOutboxKinds.Created, idB, _bob.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(new[] { "bob@test.local" }, sent.Message.To);
        Assert.Contains($"單號 {idB}", sent.Message.Body);
        Assert.DoesNotContain($"單號 {idC}：", sent.Message.Body);
    }

    [Fact]
    public async Task E4_處理人已停用_項目移除不寄()
    {
        var dave = _users.Upsert(new WebUser { Account = "dave", Email = "dave@test.local", Active = false });
        var id = SeedOrder("Schannel 36888", 2, dave.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, dave.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        Assert.Empty(_sender.Sent);
        Assert.Empty(_outbox.Get().Items);
    }

    // ── (f) 停機跨多個時刻：合併成一封 ──────────────────────────────────────

    [Fact]
    public async Task F_停機後十三點第一次呼叫_只寄一封含兩個時刻之間的項目_兩個slot都記今天()
    {
        var id1 = SeedOrder("Schannel 36888", 2, _alice.UserId);
        var id2 = SeedOrder("DCOM 10016", 1, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id1, _alice.UserId, At(0, 7));
        Enqueue(WorkOrderOutboxKinds.Created, id2, _alice.UserId, At(0, 10));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 13));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains($"單號 {id1}", sent.Message.Body);
        Assert.Contains($"單號 {id2}", sent.Message.Body);
        var slots = _state.Get().WorkOrderDigestLastSentSlots;
        Assert.Equal("2026-10-20", slots["08:00"]);
        Assert.Equal("2026-10-20", slots["12:00"]);
    }

    // ── (g) 立即通知 ────────────────────────────────────────────────────────

    [Fact]
    public async Task G_立即項目在非時刻的十點十五就寄出並移除_一般項目留著()
    {
        var idNow = SeedOrder("Schannel 36888", 2, _alice.UserId);
        var idLater = SeedOrder("DCOM 10016", 1, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, idNow, _alice.UserId, At(0, 10, 10), immediate: true);
        Enqueue(WorkOrderOutboxKinds.Created, idLater, _alice.UserId, At(0, 10, 11));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 10, 15));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("交辦通知", sent.Message.Subject);
        Assert.Contains($"單號 {idNow}", sent.Message.Body);
        Assert.DoesNotContain($"單號 {idLater}", sent.Message.Body);
        var remaining = Assert.Single(_outbox.Get().Items.Values);
        Assert.Equal(idLater, remaining.WorkOrderId);
    }

    // ── (h) 開關 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task H_MailNotifyWorkOrders關閉_不寄且outbox不變_打開後照寄()
    {
        var id = SeedOrder("Schannel 36888", 2, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 7));
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 7, 1), immediate: true);
        _settingsStore.Update(s => s.MailNotifyWorkOrders = false);

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12));

        Assert.Empty(_sender.Sent);
        Assert.Equal(2, _outbox.Get().Items.Count);

        _settingsStore.Update(s => s.MailNotifyWorkOrders = true);
        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 12, 5));
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Empty(_outbox.Get().Items);
    }

    // ── (i) 單已刪除 ────────────────────────────────────────────────────────

    [Fact]
    public async Task I_單已刪除_內文標已刪除()
    {
        Enqueue(WorkOrderOutboxKinds.Created, 9999, _alice.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("單號 9999（已刪除）", sent.Message.Body);
    }

    // ── (j) 組信純函數 ──────────────────────────────────────────────────────

    private static WorkOrderOutboxItem Item(string kind, long id, string actor = "admin", int delta = 0, int recurrence = 0, string? reason = null) =>
        new() { Kind = kind, WorkOrderId = id, RecipientUserId = 1, ActorAccount = actor, MemberDelta = delta, RecurrenceMembers = recurrence, Reason = reason };

    private static WorkOrderDigestOrder Order(long id, string label = "Schannel 36888", int hostCount = 2, IReadOnlyList<string>? hosts = null,
        string? plain = null, string? note = null, DateTime? due = null) =>
        new(id, label, plain, hostCount, hosts ?? new[] { "host-a", "host-b" }, note, due);

    [Fact]
    public void J_Build_主機二十五台只列二十個名稱並註明總數()
    {
        var names = Enumerable.Range(1, 25).Select(i => $"host-{i:D2}").ToList();

        var (_, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Created, 7), (WorkOrderDigestOrder?)Order(7, hostCount: 25, hosts: names))
        });

        Assert.Contains($"主機：{string.Join("、", names.Take(20))}…等 25 台", body);
        Assert.Contains("host-20", body);
        Assert.DoesNotContain("host-21", body);
    }

    [Fact]
    public void J2_Build_建單內文含單號_主機數_期限_說明()
    {
        var due = new DateTime(2026, 10, 25);
        var (summary, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Created, 101), (WorkOrderDigestOrder?)Order(101, plain: "TLS 握手失敗", note: "請優先排查", due: due))
        });

        Assert.Contains("新交辦 1 張", summary);
        Assert.Contains("單號 101：Schannel 36888（2 台）", body);
        Assert.Contains("主機：host-a、host-b", body);
        Assert.Contains("說明：TLS 握手失敗", body);
        Assert.Contains("交辦說明：請優先排查", body);
        Assert.Contains("期限：2026-10-25", body);
        Assert.Contains("請至站台的「我的交辦」檢視上列單號。", body);
    }

    [Fact]
    public void J3_Build_沒有白話說明時不出現說明行()
    {
        var (_, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Created, 101), (WorkOrderDigestOrder?)Order(101, plain: null))
        });

        Assert.DoesNotContain("說明：", body);
    }

    [Fact]
    public void J4_Build_移交與取消措辭不同_各含操作者與原因()
    {
        var (summary, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Transferred, 201, actor: "admin1", reason: "改由 DBA 處理"), (WorkOrderDigestOrder?)Order(201, label: "Disk Full")),
            (Item(WorkOrderOutboxKinds.Cancelled, 202, actor: "admin2", reason: "誤判已結案"), (WorkOrderDigestOrder?)Order(202, label: "Memory Leak"))
        });

        Assert.Contains("移交 1", summary);
        Assert.Contains("取消 1", summary);
        Assert.Contains("由 admin1 移交", body);
        Assert.Contains("原因：改由 DBA 處理", body);
        Assert.Contains("由 admin2 取消", body);
        Assert.Contains("原因：誤判已結案", body);
        Assert.DoesNotContain("由 admin1 取消", body);
        Assert.DoesNotContain("由 admin2 移交", body);
    }

    [Fact]
    public void J4b_Build_退回段含單號問題操作者與說明_計入主旨()
    {
        var (summary, body) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Returned, 301, actor: "admin3", reason: "磁碟已換新，請重試"), (WorkOrderDigestOrder?)Order(301, label: "Disk Full"))
        });

        Assert.Contains("退回 1", summary);
        Assert.Contains("【退回】", body);
        Assert.Contains("單號 301：Disk Full，由 admin3 退回：磁碟已換新，請重試", body);
        Assert.DoesNotContain("【取消】", body);
    }

    [Fact]
    public void J5_Build_復發台數寫進該行_無復發時不出現()
    {
        var (_, withRecurrence) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Appended, 101, delta: 3, recurrence: 2), (WorkOrderDigestOrder?)Order(101))
        });
        var (_, without) = WorkOrderDigestBuilder.Build(new[]
        {
            (Item(WorkOrderOutboxKinds.Appended, 101, delta: 3, recurrence: 0), (WorkOrderDigestOrder?)Order(101))
        });

        Assert.Contains("新增 3 台", withRecurrence);
        Assert.Contains("其中 2 台是復發", withRecurrence);
        Assert.Contains("新增 3 台", without);
        Assert.DoesNotContain("復發", without);
    }

    // ── 服務層移植：夜間派工、白話說明、超過二十台 ──────────────────────────

    [Fact]
    public async Task K_夜間派工每位處理人一封且只列各自的單()
    {
        var id1 = SeedOrder("disk 153", 3, _alice.UserId);
        var id2 = SeedOrder("DCOM 10016", 5, _alice.UserId);
        var id3 = SeedOrder("Schannel 36888", 2, _bob.UserId);
        var summary = new NightlyDispatchSummary
        {
            PerHandler = new Dictionary<long, IReadOnlyList<NightlyDispatchOrderLine>>
            {
                [_alice.UserId] = new List<NightlyDispatchOrderLine>
                {
                    new(id1, CreatedThisRun: true, AddedMembers: 3, IssueLabel: "disk 153") { RecurrenceMembers = 2 },
                    new(id2, CreatedThisRun: false, AddedMembers: 5, IssueLabel: "DCOM 10016"),
                },
                [_bob.UserId] = new List<NightlyDispatchOrderLine>
                {
                    new(id3, CreatedThisRun: true, AddedMembers: 2, IssueLabel: "Schannel 36888"),
                }
            }
        };
        _mail.EnqueueNightlyDispatch(summary);

        // 入列時間是真實的本地現在；以「明天下午」呼叫讓兩個時刻都已過
        await _mail.CheckAndSendWorkOrderDigestAsync(DateTime.Now.Date.AddDays(1).AddHours(13));

        Assert.Equal(2, _sender.Sent.Count);
        var mailA = _sender.Sent.Single(s => s.Message.To.Contains("alice@test.local")).Message;
        var mailB = _sender.Sent.Single(s => s.Message.To.Contains("bob@test.local")).Message;
        Assert.Contains($"單號 {id1}", mailA.Body);
        Assert.Contains($"單號 {id2}", mailA.Body);
        Assert.DoesNotContain($"單號 {id3}", mailA.Body);
        Assert.Contains("其中 2 台是復發", mailA.Body);
        Assert.Contains($"單號 {id3}", mailB.Body);
        Assert.DoesNotContain($"單號 {id1}", mailB.Body);
        Assert.DoesNotContain("復發", mailB.Body);
    }

    [Fact]
    public async Task L_超過二十台只列前二十台並註明總數()
    {
        var id = SeedOrder("Schannel 36888", 25, _alice.UserId);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("（25 台）", sent.Message.Body);
        Assert.Contains("…等 25 台", sent.Message.Body);
        Assert.Contains("host-20", sent.Message.Body);
        Assert.DoesNotContain("host-21", sent.Message.Body);
    }

    [Fact]
    public async Task M_單有來源與事件ID_內文帶規則的白話說明()
    {
        _rules.Content = new RuleFileContent
        {
            Rules = new List<KnownIssueRule>
            {
                new()
                {
                    Id = "test-rule", Platform = "windows", SourcePattern = "Schannel",
                    EventIds = new[] { 36888 }, PlainExplanation = "TLS 握手失敗"
                }
            }
        };
        var id = SeedOrder("Schannel 36888", 2, _alice.UserId, source: "Schannel", eventId: 36888);
        Enqueue(WorkOrderOutboxKinds.Created, id, _alice.UserId, At(0, 7));

        await _mail.CheckAndSendWorkOrderDigestAsync(At(0, 8));

        var sent = Assert.Single(_sender.Sent);
        Assert.Contains("說明：TLS 握手失敗", sent.Message.Body);
    }
}

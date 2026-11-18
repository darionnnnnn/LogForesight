using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 即時通知待送項目的出口與重試退避（第 78 輪批次 0）：升級相容的舊鍵辨識、「已被取代」終態、
/// 停用即時通知、重試沒有進度時加倍退避。以真的通知服務與工作流執行。
/// </summary>
public class MailNotificationServiceTestsSupersede : IDisposable
{
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly EfSqliteFixture _fx = new();
    private readonly ScheduleFreshnessService _freshness;
    private readonly string _workflowRoot = Path.Combine(Path.GetTempPath(), "lf-mail-supersede-" + Guid.NewGuid().ToString("N"));
    private readonly HostDayWorkflowService _workflow;
    private readonly MailNotifyStateStore _state;
    private readonly ManualClock _clock = new(DateTime.UtcNow);

    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    private sealed class ManualClock(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNowValue { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(UtcNowValue, DateTimeKind.Utc));
    }

    public MailNotificationServiceTestsSupersede()
    {
        var batchRuns = new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs"));
        _freshness = new ScheduleFreshnessService(batchRuns, new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        var backend = new StorageBackend(new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite" }, _workflowRoot);
        _workflow = new HostDayWorkflowService(new HostDayWorkflowStore(backend));
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        _countingRecords = DispatchProxy.Create<IAnalysisRecordQuery, ThrowingQueryProxy>();
        ((ThrowingQueryProxy)(object)_countingRecords).Inner = _records;
        ((ThrowingQueryProxy)(object)_countingRecords).ThrowOnQuery = false;
    }

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_workflowRoot, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private readonly IAnalysisRecordQuery _countingRecords;
    private int QueryCount => ((ThrowingQueryProxy)(object)_countingRecords).QueryCalls;

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess, _countingRecords, new FakeHandlingStore(), _state, _freshness,
            new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
            new FakeIssueCaseStore(), new FakeRuleStore(), new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(),
            workflow: _workflow, clock: _clock);

    private void EnableMail(bool summaryLane)
    {
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = new List<string> { "ops@test.local" };
            s.MailOnRunCompleted = summaryLane;
            s.MailUrgentEnabled = !summaryLane;
            s.MailMinRiskLevel = RiskLevels.Medium;
            s.MailNotifyHostOwners = false;
        });
    }

    private void CreateViewAllAccount(string email)
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = email, Email = email, Active = true, GroupIds = new List<long> { group.GroupId } });
    }

    private DailyAnalysisRecord SeedRecord(bool aiPending = true)
    {
        var host = _hosts.Upsert(new WebHost { HostName = "host", Active = true, Source = "netiq" });
        var record = new DailyAnalysisRecord
        {
            RecordId = 4001, HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = RiskLevels.High,
            LogSource = AnalysisLogSource.Netiq, AiPending = aiPending, AiAnalyzed = !aiPending,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Disk", EventId = 7, EventKey = "Disk/7",
                    Category = IssueCategory.Other, Severity = IssueSeverity.High, Count = 1 }
            }
        };
        _records.Add(record);
        _workflow.RestoreFromRecord(record, aiEnabled: true, prtgEnabled: false);
        return record;
    }

    private void RemoveRecord(DailyAnalysisRecord record) => _records.Remove(record);

    private List<MailUrgentIntent> NonTerminal() =>
        _state.Get().UrgentOutbox.Values.Where(i => !MailNotifyStateStore.IsTerminalUrgentStatus(i.Status)).ToList();

    private static string LegacyKey(MailNotificationService _, string lane, DailyAnalysisRecord record,
        HostDayWorkflowVersion version, string recipientsRoute, string ctx, bool analyzed, bool pending, bool pruned)
    {
        var fingerprint = HostDayWorkflowFingerprint.HashParts([
            lane, record.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version.ParentRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version.DecisionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HostDayWorkflowFingerprint.ForRecordWithFlags(record, analyzed, pending, pruned),
            PrtgFindingMapper.Fingerprint(record.TopIssues), ctx]);
        return $"{record.HostId}|{record.Date:yyyy-MM-dd}|parent:{version.ParentRecordId}|decision:{version.DecisionVersion}|content:{lane}:{fingerprint}|route:{recipientsRoute}|{lane}";
    }

    /// <summary>先寄一次取得「新算法」的已寄鍵，再把鍵換成舊算法（AiPending=true）版本，模擬升級前存下的狀態。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 升級前舊算法鍵已寄_旗標翻轉後不重寄也不留待送項目(bool summaryLane)
    {
        EnableMail(summaryLane);
        CreateViewAllAccount("ops@test.local");
        var record = SeedRecord(aiPending: true);
        var service = Create();
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);

        // 以舊算法（AiPending=true, AiAnalyzed=false）改寫已寄鍵：把新鍵的 content 段換成舊內容指紋
        var newFingerprint = HostDayWorkflowFingerprint.ForMailContent(record);
        var oldFingerprint = HostDayWorkflowFingerprint.ForRecordWithFlags(record, false, true, false);
        Assert.NotEqual(newFingerprint, oldFingerprint);
        var version = _workflow.CaptureDeliveryVersion(record.HostId, record.Date, record.RecordId, out _)!;
        string Convert(string key)
        {
            var parts = key.Split('|');
            var route = parts.First(p => p.StartsWith("route:", StringComparison.Ordinal));
            var ctxPart = parts.First(p => p.StartsWith("content:", StringComparison.Ordinal));
            return LegacyKey(service, summaryLane ? "summary" : "urgent", record, version, route["route:".Length..],
                ExtractContext(summaryLane ? "summary" : "urgent", record, version, ctxPart), false, true, false);
        }
        _state.Update(s =>
        {
            var set = summaryLane ? s.SummarySentKeys : s.UrgentSentKeys;
            var converted = set.Select(Convert).ToList();
            set.Clear();
            foreach (var key in converted) set.Add(key);
            if (!summaryLane)
            {
                var outbox = s.UrgentOutbox.Values.ToList();
                s.UrgentOutbox.Clear();
                foreach (var intent in outbox) { intent.Key = Convert(intent.Key); s.UrgentOutbox[intent.Key] = intent; }
            }
        });

        // 升級後 AI 寫回：旗標翻成 (AiPending=false, AiAnalyzed=true)
        record.AiAnalyzed = true;
        record.AiPending = false;
        await service.NotifyAfterRunAsync();
        await service.RetryPendingUrgentAsync();

        Assert.Single(_sender.Sent);
        Assert.Single(_sender.Attempts);
        Assert.Empty(NonTerminal());
    }

    /// <summary>郵件情境指紋：與寄信路徑同一個方法（internal，測試專案可見）。</summary>
    private string ExtractContext(string lane, DailyAnalysisRecord record, HostDayWorkflowVersion version, string contentPart) =>
        Create().MailContextFingerprint(_settingsStore.Get());

    [Theory]
    [InlineData("recordId")]
    [InlineData("expired")]
    [InlineData("deleted")]
    public async Task 待送項目不再對應現行紀錄_標為已被取代且重試不再寄送(string how)
    {
        EnableMail(summaryLane: false);
        var record = SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await service.NotifyAfterRunAsync();
        Assert.Single(NonTerminal());

        switch (how)
        {
            case "recordId": record.RecordId = 4002; break;
            case "expired": record.Date = DateTime.Today.AddDays(-20); break;
            case "deleted": RemoveRecord(record); break;
        }
        await service.NotifyAfterRunAsync();

        var superseded = _state.Get().UrgentOutbox.Values.Where(i => i.Status == "superseded").ToList();
        Assert.Single(superseded);
        var attempts = _sender.Attempts.Count;
        _clock.UtcNowValue = _clock.UtcNowValue.AddHours(1);
        await service.RetryPendingUrgentAsync();
        if (how == "recordId") return;   // 新鍵對應的現行紀錄本身仍待送，另由重試處理
        Assert.Equal(attempts, _sender.Attempts.Count);
    }

    [Fact]
    public async Task 寄信失敗而鍵仍對應現行紀錄_有帳號收件人維持待送並於恢復後寄出()
    {
        EnableMail(summaryLane: false);
        CreateViewAllAccount("ops@test.local");
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await service.NotifyAfterRunAsync();
        Assert.Single(NonTerminal());
        Assert.Empty(_sender.Sent);

        _sender.ThrowOnSend = null;
        _clock.UtcNowValue = _clock.UtcNowValue.AddMinutes(1);
        await service.RetryPendingUrgentAsync();

        Assert.Single(_sender.Sent);
        Assert.Empty(NonTerminal());
    }

    [Fact]
    public async Task 紀錄查詢擲例外的那一趟_沒有項目被標為已被取代()
    {
        EnableMail(summaryLane: false);
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await service.NotifyAfterRunAsync();
        Assert.Single(NonTerminal());

        var throwing = DispatchProxy.Create<IAnalysisRecordQuery, ThrowingQueryProxy>();
        ((ThrowingQueryProxy)(object)throwing).Inner = _records;
        var broken = new MailNotificationService(_settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess, throwing,
            new FakeHandlingStore(), _state, _freshness, new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)),
            new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore(),
            new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(), workflow: _workflow, clock: _clock);
        await broken.NotifyAfterRunAsync();

        Assert.DoesNotContain(_state.Get().UrgentOutbox.Values, i => i.Status == "superseded");
        Assert.Single(NonTerminal());
    }

    [Fact]
    public async Task 即時通知停用_待送項目標為已被取代_重新啟用後未寄的主機日寄出()
    {
        EnableMail(summaryLane: false);
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await service.NotifyAfterRunAsync();
        Assert.Single(NonTerminal());

        _settingsStore.Update(s => s.MailUrgentEnabled = false);
        await service.NotifyAfterRunAsync();
        Assert.Empty(NonTerminal());
        Assert.Contains(_state.Get().UrgentOutbox.Values, i => i.Status == "superseded");

        _sender.ThrowOnSend = null;
        _settingsStore.Update(s => s.MailUrgentEnabled = true);
        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
        Assert.Empty(NonTerminal());
    }

    /// <summary>每分鐘呼叫一次重試，回傳實際重跑（有寄信嘗試）的分鐘。每分鐘清掉收件人連續失敗計數，
    /// 避免「單一收件人跨輪排除」讓嘗試中斷而干擾對退避時間點的觀察。</summary>
    private int RunRetryMinutes(MailNotificationService service, int minutes, List<int> executedAt, int startMinute)
    {
        var minute = startMinute;
        for (var i = 0; i < minutes; i++)
        {
            minute++;
            _clock.UtcNowValue = _clock.UtcNowValue.AddMinutes(1);
            _state.Update(s => s.RecipientFailureStreaks.Clear());
            var before = QueryCount;
            service.RetryPendingUrgentAsync().GetAwaiter().GetResult();
            if (QueryCount > before) executedAt.Add(minute);
        }
        return minute;
    }

    [Fact]
    public async Task 重試沒有進度_間隔加倍至三十分鐘封頂_有進度後回到一分鐘()
    {
        EnableMail(summaryLane: false);
        _settingsStore.Update(s => s.MailRecipients = new List<string> { "a@test.local", "b@test.local" });
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new System.Net.Mail.SmtpException("down");
        await service.NotifyAfterRunAsync();
        Assert.Single(NonTerminal());

        var executed = new List<int>();
        var minute = RunRetryMinutes(service, 100, executed, 0);
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 62, 92 }, executed);   // 間隔 1、2、4、8、16、30、30、30

        // SMTP 部分恢復（a 寄成功、b 仍失敗）：有進度 → 間隔回到 1 分鐘，之後沒有進度再重新加倍
        _sender.ThrowOnSend = null;
        _sender.ThrowOnSendForRecipient = "b@test.local";
        var after = new List<int>();
        minute = RunRetryMinutes(service, 40, after, minute);
        Assert.Equal(new[] { 122, 123, 124, 126, 130, 138 }, after);     // 122 有進度、1 分鐘後重跑；其後沒有進度，間隔 1、2、4、8
    }

    [Fact]
    public async Task 設定版次改變_退避回到一分鐘()
    {
        EnableMail(summaryLane: false);
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new System.Net.Mail.SmtpException("down");
        await service.NotifyAfterRunAsync();

        var executed = new List<int>();
        var minute = RunRetryMinutes(service, 12, executed, 0);   // 1、2、4、8
        Assert.Equal(new[] { 1, 2, 4, 8 }, executed);

        _settingsStore.Update(s => s.Revision = "rev-changed");
        var after = new List<int>();
        RunRetryMinutes(service, 1, after, minute);               // 第 13 分鐘：版次改變，立刻重跑
        Assert.Equal(new[] { 13 }, after);
    }

    [Fact]
    public async Task 退避期間取數觸發的通知照常執行()
    {
        EnableMail(summaryLane: false);
        SeedRecord();
        var service = Create();
        _sender.ThrowOnSend = new System.Net.Mail.SmtpException("down");
        await service.NotifyAfterRunAsync();
        _clock.UtcNowValue = _clock.UtcNowValue.AddMinutes(1);
        await service.RetryPendingUrgentAsync();           // 重試一次、沒有進度 → 進入退避
        var afterRetry = QueryCount;
        _clock.UtcNowValue = _clock.UtcNowValue.AddSeconds(5);
        await service.RetryPendingUrgentAsync();           // 退避中：不執行
        Assert.Equal(afterRetry, QueryCount);
        _sender.ThrowOnSend = null;
        await service.NotifyAfterRunAsync();               // 取數觸發：照常執行並寄出
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public void 已被取代的項目超過保留期被清除()
    {
        _state.Update(s =>
        {
            s.UrgentOutbox["old"] = new MailUrgentIntent
            {
                Key = "old", Status = "superseded", RecordDate = DateTime.UtcNow.AddDays(-200),
                Recipients = { ["x@test.local"] = "failed-or-unknown" }
            };
            s.UrgentOutbox["fresh"] = new MailUrgentIntent
            {
                Key = "fresh", Status = "superseded", RecordDate = DateTime.UtcNow.AddDays(-5)
            };
        });
        var removed = _state.PruneTerminalUrgentIntents(90, DateTime.UtcNow);
        Assert.Equal(1, removed);
        Assert.False(_state.Get().UrgentOutbox.ContainsKey("old"));
        Assert.True(_state.Get().UrgentOutbox.ContainsKey("fresh"));
    }

    /// <summary>代理：Query 擲例外，其餘全部轉給 inner。</summary>
    public class ThrowingQueryProxy : DispatchProxy
    {
        public IAnalysisRecordQuery Inner { get; set; } = null!;
        public bool ThrowOnQuery { get; set; } = true;
        public int QueryCalls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAnalysisRecordQuery.Query))
            {
                QueryCalls++;
                if (ThrowOnQuery) throw new InvalidOperationException("query failed");
            }
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }
}

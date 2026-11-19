using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 郵件去重鍵只看信件內容（第 78 輪批次 0）：AI 寫回只翻轉「AI 已分析／AI 待補／詳情已清除」三個狀態旗標，
/// 信件內容沒有改變，不得重寄、不得新增停在待送的即時通知項目。
/// 以真的通知服務與工作流執行；紀錄一律用「昨天」造（分析只產出到昨天）。
/// </summary>
public class MailNotificationServiceTestsDedupe : IDisposable
{
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly EfSqliteFixture _fx = new();
    private readonly ScheduleFreshnessService _freshness;
    private readonly string _workflowRoot = Path.Combine(Path.GetTempPath(), "lf-mail-dedupe-" + Guid.NewGuid().ToString("N"));
    private readonly HostDayWorkflowService _workflow;
    private readonly MailNotifyStateStore _state;

    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    public MailNotificationServiceTestsDedupe()
    {
        var batchRuns = new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs"));
        _freshness = new ScheduleFreshnessService(batchRuns, new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        var backend = new StorageBackend(new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite" }, _workflowRoot);
        _workflow = new HostDayWorkflowService(new HostDayWorkflowStore(backend));
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
    }

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_workflowRoot, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess, _records, _handlings, _state, _freshness,
            new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
            new FakeIssueCaseStore(), new FakeRuleStore(), _issueOwners, _issueAggregates, workflow: _workflow);

    private void EnableMail(bool summaryLane) =>
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

    private void CreateViewAllAccount(string email)
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = email, Email = email, Active = true, GroupIds = new List<long> { group.GroupId } });
    }

    private DailyAnalysisRecord SeedRecord(string riskLevel)
    {
        var host = _hosts.Upsert(new WebHost { HostName = "host", Active = true, Source = "netiq" });
        var record = new DailyAnalysisRecord
        {
            RecordId = 4001,
            HostId = host.HostId,
            Host = host.HostName,
            Date = Yesterday,
            RiskLevel = riskLevel,
            LogSource = AnalysisLogSource.Netiq,
            AiPending = true,
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

    private IReadOnlyList<MailUrgentIntent> NonTerminalUrgentIntents() =>
        _state.Get().UrgentOutbox.Values
            .Where(i => !MailNotifyStateStore.IsTerminalUrgentStatus(i.Status))
            .ToList();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AI寫回只翻轉狀態旗標_不重寄也不留下待送項目(bool withAccount, bool summaryLane)
    {
        EnableMail(summaryLane);
        if (withAccount) CreateViewAllAccount("ops@test.local");
        var record = SeedRecord(RiskLevels.High);
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);
        var sentKeysBefore = summaryLane ? _state.Get().SummarySentKeys.Count : _state.Get().UrgentSentKeys.Count;

        // AI 寫回：只翻轉狀態旗標，信件內容（風險等級、問題清單、告警）不變
        record.AiAnalyzed = true;
        record.AiPending = false;
        await service.NotifyAfterRunAsync();
        await service.NotifyAfterRunAsync();
        await service.RetryPendingUrgentAsync();

        Assert.Single(_sender.Sent);
        Assert.Single(_sender.Attempts);
        Assert.Empty(NonTerminalUrgentIntents());
        var sentKeysAfter = summaryLane ? _state.Get().SummarySentKeys.Count : _state.Get().UrgentSentKeys.Count;
        Assert.Equal(sentKeysBefore, sentKeysAfter);
    }

    [Fact]
    public async Task AI寫回同時把風險由中上調為高_即時通知寄出一封()
    {
        EnableMail(summaryLane: false);
        var record = SeedRecord(RiskLevels.Medium);
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Empty(_sender.Sent);

        record.AiAnalyzed = true;
        record.AiPending = false;
        record.RiskLevel = RiskLevels.High;   // 真的內容變更：重新達到即時通知門檻
        await service.NotifyAfterRunAsync();

        Assert.Single(_sender.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 詳情被清除只翻轉旗標_不重寄(bool summaryLane)
    {
        EnableMail(summaryLane);
        var record = SeedRecord(RiskLevels.High);
        var service = Create();

        await service.NotifyAfterRunAsync();
        Assert.Single(_sender.Sent);

        record.DetailPruned = true;
        record.AiPending = false;
        await service.NotifyAfterRunAsync();

        Assert.Single(_sender.Sent);
        Assert.Empty(NonTerminalUrgentIntents());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AI寫回後工作流的郵件意圖不變_郵件腿維持完成(bool summaryLane)
    {
        EnableMail(summaryLane);
        CreateViewAllAccount("ops@test.local");
        var record = SeedRecord(RiskLevels.High);
        var service = Create();

        await service.NotifyAfterRunAsync();
        var before = _workflow.Get(record.HostId, record.Date)!;
        Assert.True(before.MailIsComplete);

        record.AiAnalyzed = true;
        record.AiPending = false;
        await service.NotifyAfterRunAsync();

        // 郵件意圖只由信件內容決定：旗標翻轉後意圖鍵與已接受的部分都不變
        var after = _workflow.Get(record.HostId, record.Date)!;
        Assert.Equal(before.MailIntents.OrderBy(x => x), after.MailIntents.OrderBy(x => x));
        Assert.True(after.MailIsComplete);
    }
}

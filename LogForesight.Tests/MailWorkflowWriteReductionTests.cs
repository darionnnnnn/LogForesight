using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 1（1-1、1-2）：郵件收尾對「已是目標狀態」的主機日不再寫入工作流。
/// 對帳測試以真的通知服務與工作流跑兩趟，記錄每個主機日最終狀態（去掉時間戳）與寄出的信，
/// 與改動前量到的摘要逐項比對；寫入次數以工作流 blob 版本前後相減取得，與機器快慢無關。
/// </summary>
public class MailWorkflowWriteReductionTests : IDisposable
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
    private readonly string _workflowRoot = Path.Combine(Path.GetTempPath(), "lf-mail-wr-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostDayWorkflowService _workflow;
    private readonly MailNotifyStateStore _state;

    // 固定時鐘與日期：預期摘要含日期（意圖鍵、主旨），用真實「昨天」會讓測試只在錄製當天通過（自查修正）
    private static readonly DateTime Today = new(2026, 11, 18);
    private static readonly DateTime Yesterday = Today.AddDays(-1);
    private readonly FixedClock _clock = new(new DateTimeOffset(Today.AddHours(12), TimeZoneInfo.Local.GetUtcOffset(Today.AddHours(12))));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    public MailWorkflowWriteReductionTests()
    {
        var batchRuns = new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs"));
        _freshness = new ScheduleFreshnessService(batchRuns, new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        _backend = new StorageBackend(new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite" }, _workflowRoot);
        _workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
    }

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_workflowRoot, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private MailNotificationService Create() =>
        new(_settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess, _records, new FakeHandlingStore(), _state, _freshness,
            new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()),
            new FakeIssueCaseStore(), new FakeRuleStore(), new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(),
            workflow: _workflow, clock: _clock);

    private void Configure(string scenario)
    {
        _settingsStore.Update(s =>
        {
            s.MailEnabled = scenario != "disabled";
            s.SmtpServer = "smtp.test.local";
            s.MailFrom = "logforesight@test.local";
            s.MailRecipients = scenario == "both-recipient" ? new List<string> { "ops@test.local" } : new List<string>();
            s.MailOnRunCompleted = true;
            s.MailUrgentEnabled = true;
            s.MailMinRiskLevel = RiskLevels.Medium;
            s.MailNotifyHostOwners = false;
        });
        if (scenario == "both-recipient")
        {
            var group = _userGroups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
            _users.Upsert(new WebUser { Account = "ops@test.local", Email = "ops@test.local", Active = true, GroupIds = new List<long> { group.GroupId } });
        }
    }

    private DailyAnalysisRecord Seed(string name, long recordId, string risk)
    {
        var host = _hosts.Upsert(new WebHost { HostName = name, Active = true, Source = "netiq" });
        var record = new DailyAnalysisRecord
        {
            RecordId = recordId, HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = risk,
            LogSource = AnalysisLogSource.Netiq, AiAnalyzed = true,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Disk", EventId = 7, EventKey = "Disk/7",
                    Category = IssueCategory.Other, Severity = IssueSeverity.High, Count = 1 }
            }
        };
        _records.Add(record);
        _workflow.RestoreFromRecord(record, aiEnabled: false, prtgEnabled: false);
        return record;
    }

    private long Version(DailyAnalysisRecord r) =>
        _backend.Blob($"workflow_{r.HostId}_{r.Date:yyyyMMdd}").ReadVersion();

    private long WriteSum(IEnumerable<DailyAnalysisRecord> rs) => rs.Sum(Version);

    private static readonly Regex Timestamp = new("\"\\d{4}-\\d{2}-\\d{2}T[0-9:.+\\-Z]+\"", RegexOptions.Compiled);

    private string StateDigest(IEnumerable<DailyAnalysisRecord> rs)
    {
        var sb = new StringBuilder();
        foreach (var r in rs)
        {
            var state = _workflow.Get(r.HostId, r.Date);
            var json = System.Text.Json.JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
            sb.Append(r.RecordId).Append(':').AppendLine(Timestamp.Replace(json, "\"T\""));
        }
        foreach (var (_, m) in _sender.Sent)
            sb.Append("MAIL:").Append(string.Join(",", m.To.OrderBy(x => x))).Append('|').AppendLine(m.Subject);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    // 改動前（cefb4470，固定時鐘 2026-11-18）實測的摘要；改動後必須逐項相同。
    private static readonly Dictionary<string, string> Golden = new()
    {
        ["both-recipient"] = "939712BF9BF1D09F",
        ["both-norecipient"] = "18AB4E3E7BCEFE63",
        ["disabled"] = "FA1881780A51143F",
    };

    [Theory]
    [InlineData("both-recipient")]
    [InlineData("both-norecipient")]
    [InlineData("disabled")]
    public async Task 兩趟通知後的工作流最終狀態與寄出的信與改動前相同_第二趟對已結案主機日不再寫入(string scenario)
    {
        Configure(scenario);
        var rs = new[] { Seed("a", 5001, RiskLevels.High), Seed("b", 5002, RiskLevels.Medium), Seed("c", 5003, RiskLevels.Low) };
        var service = Create();
        await service.NotifyAfterRunAsync();
        var afterFirst = WriteSum(rs);
        await service.NotifyAfterRunAsync();
        var secondRunWrites = WriteSum(rs) - afterFirst;
        var digest = StateDigest(rs);

        Assert.True(Golden.TryGetValue(scenario, out var expected), $"缺少 golden：{scenario} => {digest}（第二趟寫入 {secondRunWrites}）");
        Assert.Equal(expected, digest);
        // 改動前（HEAD cefb4470）實測第二趟寫入 12／12／9。資料沒變時收尾不再寫入；
        // 沒有合格收件人的情境仍有 5 次：那些主機日沒有任何收件人可寄、不會被標成已寄，
        // 每趟都重新走寄送路徑的規劃（寄送路徑的 ReplaceMailPlan 刻意維持每次寫入，見批次 1 規格）。
        var expectedSecondRunWrites = scenario == "both-norecipient" ? 5 : 0;
        Assert.Equal(expectedSecondRunWrites, secondRunWrites);
    }

    /// <summary>A3：兩趟之間一個主機日的父紀錄版本變了、另一個主機日的應走通道變了；最終狀態與改動前相同，未受影響的主機日不寫入。</summary>
    [Fact]
    public async Task 兩趟之間父版本與應走通道變動_最終狀態與改動前相同_未變動主機日不寫入()
    {
        Configure("both-recipient");
        var a = Seed("a", 5001, RiskLevels.High);
        var b = Seed("b", 5002, RiskLevels.Medium);
        var c = Seed("c", 5003, RiskLevels.Low);
        var rs = new[] { a, b, c };
        var service = Create();
        await service.NotifyAfterRunAsync();

        // a：父紀錄內容改變（指紋不同）→ 版本推進；b：風險等級降到摘要門檻以下 → 應走通道改變
        a.TopIssues.Add(new LogIssueSignature { LogName = "System", Source = "Disk", EventId = 9, EventKey = "Disk/9",
            Category = IssueCategory.Other, Severity = IssueSeverity.High, Count = 2 });
        _workflow.RestoreFromRecord(a, aiEnabled: false, prtgEnabled: false);
        b.RiskLevel = RiskLevels.Low;
        var before = rs.Select(Version).ToArray();
        await service.NotifyAfterRunAsync();
        var writes = rs.Select(Version).Zip(before, (x, y) => x - y).ToArray();
        var digest = StateDigest(rs);

        Assert.Equal(GoldenA3, digest);
        Assert.True(writes[0] > 0 && writes[1] > 0, $"變動的兩個主機日應被寫入：{string.Join(",", writes)}");
        Assert.Equal(0, writes[2]);
    }

    private const string GoldenA3 = "225A5601B9064DB8";

    /// <summary>A5：一次通知內，同一通道、同一筆紀錄物件的郵件內容意圖只算一次（其餘為記憶命中）。</summary>
    [Fact]
    public async Task 一次通知內同一筆紀錄的郵件內容意圖只算一次()
    {
        Configure("both-recipient");
        Seed("a", 5001, RiskLevels.High);
        Seed("b", 5002, RiskLevels.Medium);
        var events = new List<(string Kind, DailyAnalysisRecord Record, bool Hit)>();
        var service = Create();
        service.IntentComputedProbe = (kind, record, hit) => events.Add((kind, record, hit));

        await service.NotifyAfterRunAsync();

        Assert.NotEmpty(events);
        Assert.True(events.Any(e => e.Hit), "同一通知內應有命中記憶的取用");
        foreach (var group in events.GroupBy(e => (e.Kind, (object)e.Record), new KindRefComparer()))
            Assert.Equal(1, group.Count(e => !e.Hit));

        // 記憶只活在一次呼叫內：第二次通知會重算（不是跨呼叫保存）
        events.Clear();
        await service.NotifyAfterRunAsync();
        Assert.Contains(events, e => !e.Hit);
    }

    private sealed class KindRefComparer : IEqualityComparer<(string Kind, object Record)>
    {
        public bool Equals((string Kind, object Record) x, (string Kind, object Record) y) =>
            x.Kind == y.Kind && ReferenceEquals(x.Record, y.Record);
        public int GetHashCode((string Kind, object Record) value) =>
            HashCode.Combine(value.Kind, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Record));
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 主機權限異動待辦（從 Security 事件產生）後處理步驟測試（task-H1）。
/// </summary>
public sealed class NetiqPermissionChangePostProcessorTests : IDisposable
{
    private static ConcurrentDictionary<string, byte> Keys() => new(StringComparer.Ordinal);

    /// <summary>比照 pipeline：每輪執行開頭從 store 載入一次去重鍵快照</summary>
    private static ConcurrentDictionary<string, byte> KeysFrom(PermissionChangeStore store) =>
        new(store.GetDedupeKeys().Select(k => new KeyValuePair<string, byte>(k, 0)), StringComparer.Ordinal);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-perm-postproc-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeAiService _ai = new();
    private readonly FakeSentinelSearchClient _client = new();
    private readonly List<string> _console = new();

    public NetiqPermissionChangePostProcessorTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "p.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private Sentinel AddSentinel(string name = "S1") =>
        _sentinels.Upsert(new Sentinel { Name = name, BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });

    private WebHost AddWindowsHost(Sentinel sentinel, string ip, string hostName) =>
        _hosts.Upsert(new WebHost
        {
            HostName = hostName, IpAddress = ip, Os = WebHost.OsWindows,
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        });

    private static SentinelEvent SentinelPermEvent(string ip, string hostName, string eventId, string message) => new(new Dictionary<string, string>
    {
        [SentinelFieldMap.HostIp] = ip,
        [SentinelFieldMap.HostName] = hostName,
        [SentinelFieldMap.EventId] = eventId,
        [SentinelFieldMap.Source] = "Microsoft-Windows-Security-Auditing",
        [SentinelFieldMap.LogName] = "Security",
        [SentinelFieldMap.Timestamp] = DateTime.UtcNow.ToString("O"),
        [SentinelFieldMap.Severity] = "4",
        [SentinelFieldMap.Message] = message,
        [SentinelFieldMap.XdasOutcome] = "1"
    });

    private NetiqPipelineService MakePipeline(bool useAi = false)
    {
        var netiqOptions = new NetiqOptions { BackfillDays = 1 };
        var reportSink = new FakeReportSink();
        var reportService = new RiskReportService(_ai, reportSink);
        var batchRunStore = new BatchRunStore(_backend.LogStore("test_runs"), _backend.LogStore("test_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), _hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var console = new RecordingRunConsole(_console);

        return new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: null, riskyEventRetentionDays: 14, useAi: useAi, progress: null,
            clientFactory: FakeSentinelSearchClientFactory.Single(_client));
    }

    [Fact]
    public void 主機日事件含4756_寫出一筆PermissionChangeRecord_欄位對應正確()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var hostName = "SRV-DC01";
        var eventTime = new DateTime(2026, 8, 19, 10, 30, 0);

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4756,
                Source = "Microsoft-Windows-Security-Auditing",
                LogName = "Security",
                TimeGenerated = eventTime,
                EntryType = EventLogEntryType.Information,
                Message = "已將成員新增到安全性通用群組。\r\n群組名稱:\tEnterprise Admins\r\n成員名稱:\tCONTOSO\\AdminUser"
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, eventTime.Date);

        var records = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(records);
        var record = records[0];

        Assert.Equal(hostName, record.HostName);
        Assert.Equal(eventTime, record.DetectedAt);
        Assert.Equal("成員新增", record.ChangeType);
        Assert.Equal("NetIQ 事件", record.Source);
        Assert.Equal(4756, record.EventId);
        Assert.Equal("Enterprise Admins", record.Target);
        Assert.Equal("（不在群組中）", record.Before);
        Assert.Equal("CONTOSO\\AdminUser", record.After);
        Assert.Contains("Enterprise Admins", record.AlertText);
    }

    [Fact]
    public void 同一主機日重跑兩次_紀錄不重複()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var hostName = "SRV-APP01";
        var date = new DateTime(2026, 8, 19);

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4756,
                Source = "Microsoft-Windows-Security-Auditing",
                LogName = "Security",
                TimeGenerated = new DateTime(2026, 8, 19, 11, 0, 0),
                EntryType = EventLogEntryType.Information,
                Message = "A member was added to a security-enabled universal group.\nGroup Name: Domain Admins\nMember Name: user1"
            }
        };

        // 第一次執行
        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);
        var firstRunRecords = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(firstRunRecords);

        // 第二次執行（重跑/回補）：比照 pipeline 由 store 重新載入快照
        HostDayPostProcessor.RecordPermissionChanges(store, KeysFrom(store), hostName, WebHost.OsWindows, events, date);
        var secondRunRecords = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(secondRunRecords);

        // 同一輪內同一主機日被處理兩次（共用同一份快照）也不重複
        var sameRunKeys = KeysFrom(store);
        HostDayPostProcessor.RecordPermissionChanges(store, sameRunKeys, hostName, WebHost.OsWindows, events, date);
        HostDayPostProcessor.RecordPermissionChanges(store, sameRunKeys, hostName, WebHost.OsWindows, events, date);
        Assert.Single(store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    [Fact]
    public void 不在集合內的EventId_不產生紀錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var hostName = "SRV-SQL01";
        var date = new DateTime(2026, 8, 19);

        var events = new List<EventLogEntryData>
        {
            new() { EventId = 1102, Source = "Microsoft-Windows-Security-Auditing", Message = "稽核日誌已清除", TimeGenerated = DateTime.Now },
            new() { EventId = 4624, Source = "Microsoft-Windows-Security-Auditing", Message = "帳戶成功登入", TimeGenerated = DateTime.Now },
            new() { EventId = 7036, Source = "Service Control Manager", Message = "服務已停止", TimeGenerated = DateTime.Now }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(records);
    }

    [Fact]
    public void Linux主機與EventId0_不產生紀錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var date = new DateTime(2026, 8, 19);

        // Linux 主機，即使有 4756 也應被略過
        var eventsWith4756 = new List<EventLogEntryData>
        {
            new() { EventId = 4756, Source = "Security", Message = "Group changed", TimeGenerated = DateTime.Now }
        };
        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "LINUX-SRV", WebHost.OsLinux, eventsWith4756, date);
        Assert.Empty(store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, "LINUX-SRV", StringComparison.OrdinalIgnoreCase)).ToList());

        // Windows 主機但 EventId 為 0
        var eventsWithZeroId = new List<EventLogEntryData>
        {
            new() { EventId = 0, Source = "Syslog", Message = "Some message", TimeGenerated = DateTime.Now }
        };
        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "WIN-SRV", WebHost.OsWindows, eventsWithZeroId, date);
        Assert.Empty(store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, "WIN-SRV", StringComparison.OrdinalIgnoreCase)).ToList());
    }

    [Fact]
    public void 異動類型與欄位擷取_各類事件對應正確()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var hostName = "SRV-TEST";
        var date = new DateTime(2026, 8, 19);

        var events = new List<EventLogEntryData>
        {
            // 成員新增類：4728, 4732
            new() { EventId = 4728, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(1), Message = "Group Name:\tGlobalAdmins\nMember Name:\tUserA" },
            new() { EventId = 4732, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(2), Message = "群組名稱: LocalAdmins\n成員名稱: UserB" },
            // 成員移除類：4729, 4733, 4757
            new() { EventId = 4729, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(3), Message = "Group Name:\tGlobalAdmins\nMember Name:\tUserC" },
            new() { EventId = 4733, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(4), Message = "群組名稱: LocalAdmins\n成員名稱: UserD" },
            new() { EventId = 4757, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(5), Message = "Group Name: UniversalAdmins\nMember Name: UserE" },
            // ACL 權限變更：4670
            new() { EventId = 4670, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(6), Message = "Object Name:\tC:\\secret.txt\nOriginal Security Descriptor:\tD:(A;;GA;;;BA)\nNew Security Descriptor:\tD:(A;;GA;;;WD)" },
            // 稽核政策變更：4717, 4718, 4719, 4907
            new() { EventId = 4717, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(7), Message = "Target Account:\tServiceAcct\nAccess Granted:\tSeServiceLogonRight" },
            new() { EventId = 4718, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(8), Message = "Target Account:\tServiceAcct\nAccess Removed:\tSeServiceLogonRight" },
            new() { EventId = 4719, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(9), Message = "System audit policy was changed." },
            new() { EventId = 4907, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(10), Message = "Object Name:\tC:\\audit_target.txt\nAuditing settings changed." }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList().OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(10, records.Count);

        // 成員新增
        Assert.Equal("成員新增", records[0].ChangeType);
        Assert.Equal("GlobalAdmins", records[0].Target);
        Assert.Equal("UserA", records[0].After);

        Assert.Equal("成員新增", records[1].ChangeType);
        Assert.Equal("LocalAdmins", records[1].Target);
        Assert.Equal("UserB", records[1].After);

        // 成員移除
        Assert.Equal("成員移除", records[2].ChangeType);
        Assert.Equal("GlobalAdmins", records[2].Target);
        Assert.Equal("UserC", records[2].Before);
        Assert.Equal("（已移出群組）", records[2].After);

        Assert.Equal("成員移除", records[3].ChangeType);
        Assert.Equal("LocalAdmins", records[3].Target);
        Assert.Equal("UserD", records[3].Before);

        Assert.Equal("成員移除", records[4].ChangeType);
        Assert.Equal("UniversalAdmins", records[4].Target);
        Assert.Equal("UserE", records[4].Before);

        // 權限變更 4670
        Assert.Equal("權限變更", records[5].ChangeType);
        Assert.Equal("C:\\secret.txt", records[5].Target);
        Assert.Equal("D:(A;;GA;;;BA)", records[5].Before);
        Assert.Equal("D:(A;;GA;;;WD)", records[5].After);

        // 稽核政策變更 4717, 4718, 4719, 4907
        Assert.Equal("稽核政策變更", records[6].ChangeType);
        Assert.Equal("ServiceAcct", records[6].Target);

        Assert.Equal("稽核政策變更", records[7].ChangeType);
        Assert.Equal("ServiceAcct", records[7].Target);

        Assert.Equal("稽核政策變更", records[8].ChangeType);
        Assert.Equal("Microsoft-Windows-Security-Auditing (EventId 4719)", records[8].Target); // 退路值

        Assert.Equal("稽核政策變更", records[9].ChangeType);
        Assert.Equal("C:\\audit_target.txt", records[9].Target);
    }

    [Fact]
    public void Target退路值_擷取不到群組或物件名稱時不留空字串()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var date = new DateTime(2026, 8, 19);

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4756,
                Source = "Microsoft-Windows-Security-Auditing",
                Message = "無結構化欄位的文字訊息",
                TimeGenerated = date
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "SRV-TEST", WebHost.OsWindows, events, date);

        var record = store.Query(null, null, 1000).Where(c => string.Equals(c.HostName, "SRV-TEST", StringComparison.OrdinalIgnoreCase)).ToList().Single();
        Assert.False(string.IsNullOrWhiteSpace(record.Target));
        Assert.Equal("Microsoft-Windows-Security-Auditing (EventId 4756)", record.Target);
    }

    [Fact]
    public async Task 接線測試_NetIQ主機日分析路徑確實呼叫權限異動後處理步驟()
    {
        var sentinel = AddSentinel();
        var host = AddWindowsHost(sentinel, "10.0.0.1", "HOST-NETIQ-WIRING");

        var permMessage = "已將成員新增到安全性通用群組。\r\n群組名稱:\tEnterprise Admins\r\n成員名稱:\tCONTOSO\\SuperAdmin";
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { SentinelPermEvent("10.0.0.1", "HOST-NETIQ-WIRING", "4756", permMessage) },
            Found = 1,
            State = SentinelJobState.Completed
        };

        var permStore = new PermissionChangeStore(_backend.LogStore("perm_changes"), _backend.Blob("perm_confirms"));
        var pipeline = MakePipeline(useAi: false);

        var hostList = HostListSelection.FromStore(_hosts, _sentinels);
        await pipeline.RunAsync(hostList, trendWindowDays: 1);

        var records = permStore.Query(null, null, 1000).Where(c => string.Equals(c.HostName, "HOST-NETIQ-WIRING", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(records);
        var record = records[0];
        Assert.Equal("HOST-NETIQ-WIRING", record.HostName);
        Assert.Equal("成員新增", record.ChangeType);
        Assert.Equal("NetIQ 事件", record.Source);
        Assert.Equal(4756, record.EventId);
        Assert.Equal("Enterprise Admins", record.Target);
        Assert.Equal("CONTOSO\\SuperAdmin", record.After);
        Assert.Equal("（不在群組中）", record.Before);
    }

    [Fact]
    public void 同主機日超過上限_只逐則寫前50筆並多一筆彙總()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.LogStore("perm_changes"), fixture.Blob("perm_confirms"));
        var date = new DateTime(2026, 8, 19);
        var events = Enumerable.Range(0, 80).Select(i => new EventLogEntryData
        {
            EventId = 4670, Source = "Microsoft-Windows-Security-Auditing",
            Message = "Permissions on an object were changed. Object Name: C:/share/file" + i + ".txt",
            TimeGenerated = date.AddMinutes(i)
        }).ToList();

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "SRV-DC01", WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 1000);
        Assert.Equal(HostDayPostProcessor.MaxPermissionChangeRecordsPerHostDay + 1, records.Count);
        var summary = Assert.Single(records, r => r.ChangeType == "權限異動（彙總）");
        Assert.Contains("30 則", summary.AlertText);
    }
}

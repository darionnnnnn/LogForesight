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
        var store = new PermissionChangeStore(fixture.NewContext);
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
        var store = new PermissionChangeStore(fixture.NewContext);
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
        var store = new PermissionChangeStore(fixture.NewContext);
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
        var store = new PermissionChangeStore(fixture.NewContext);
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
        var store = new PermissionChangeStore(fixture.NewContext);
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
        Assert.Equal(string.Empty, records[8].Target); // 剖不出對象時為空字串，不塞事件來源退路值

        Assert.Equal("稽核政策變更", records[9].ChangeType);
        Assert.Equal("C:\\audit_target.txt", records[9].Target);
    }

    [Fact]
    public void Target退路值_擷取不到群組或物件名稱時不留空字串()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
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
        Assert.Equal(string.Empty, record.Target);
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

        var permStore = _backend.PermissionChanges();
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

    /// <summary>權限異動不設每主機日筆數上限：全部逐則入庫才查得到、篩得到，
    /// 也不再產生「權限異動（彙總）」這種只講筆數的替代列。</summary>
    [Fact]
    public void 同主機日大量異動全數逐則寫入且不產生彙總列()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var date = new DateTime(2026, 8, 19);
        var events = Enumerable.Range(0, 80).Select(i => new EventLogEntryData
        {
            EventId = 4670, Source = "Microsoft-Windows-Security-Auditing",
            Message = "Permissions on an object were changed. Object Name: C:/share/file" + i + ".txt",
            TimeGenerated = date.AddMinutes(i)
        }).ToList();

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "SRV-DC01", WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 1000);
        Assert.Equal(80, records.Count);
        Assert.DoesNotContain(records, r => r.ChangeType == "權限異動（彙總）");
    }

    /// <summary>取消筆數上限後，重跑同一主機日仍不得產生重複列。第二輪比照 pipeline
    /// 從 store 重新載入去重鍵快照——上限拿掉後這份快照才第一次承受數萬筆的量。</summary>
    [Fact]
    public void 大量異動重跑同一主機日不產生重複列()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var date = new DateTime(2026, 8, 19);
        var events = Enumerable.Range(0, 60).Select(i => new EventLogEntryData
        {
            EventId = 4670, Source = "Microsoft-Windows-Security-Auditing",
            Message = "Permissions on an object were changed. Object Name: C:/share/file" + i + ".txt",
            TimeGenerated = date.AddMinutes(i)
        }).ToList();

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "SRV-DC01", WebHost.OsWindows, events, date);
        HostDayPostProcessor.RecordPermissionChanges(store, KeysFrom(store), "SRV-DC01", WebHost.OsWindows, events, date);

        Assert.Equal(60, store.Query(null, null, 1000).Count);
    }

    [Fact]
    public void 訊息含Subject與Member區段時_正確分離操作者與被異動成員且After不為操作者()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-DC01";
        var date = new DateTime(2026, 8, 20);

        var msg = "A member was added to a security-enabled global group.\r\n" +
                  "Subject:\r\n" +
                  "    Security ID: DOMAIN\\admin_user\r\n" +
                  "    Account Name: admin_user\r\n" +
                  "    Account Domain: DOMAIN\r\n" +
                  "Member:\r\n" +
                  "    Security ID: DOMAIN\\alice\r\n" +
                  "    Account Name: alice\r\n" +
                  "Group:\r\n" +
                  "    Group Name: Domain Admins\r\n";

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4728,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(1),
                Message = msg
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var record = Assert.Single(store.Query(null, null, 100), c => c.HostName == hostName);
        Assert.Equal("Domain Admins", record.Target);
        Assert.Equal("admin_user", record.InitiatorAccount);
        Assert.Equal("alice", record.TargetAccount);
        Assert.Equal("alice", record.After);
        Assert.Equal("（不在群組中）", record.Before);
        Assert.NotEqual("admin_user", record.After);
    }

    [Fact]
    public void 訊息只有Subject區段而無成員名時_操作者不被寫入After或Before()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-DC01";
        var date = new DateTime(2026, 8, 20);

        // 成員新增但訊息中只有 Subject，無 Member 區段
        var msgAdded = "A member was added to a security-enabled global group.\r\n" +
                       "Subject:\r\n" +
                       "    Security ID: DOMAIN\\admin_operator\r\n" +
                       "    Account Name: admin_operator\r\n" +
                       "Group:\r\n" +
                       "    Group Name: Domain Admins\r\n";

        // 成員移除但訊息中只有 Subject，無 Member 區段
        var msgRemoved = "A member was removed from a security-enabled global group.\r\n" +
                         "Subject:\r\n" +
                         "    Security ID: DOMAIN\\admin_operator\r\n" +
                         "    Account Name: admin_operator\r\n" +
                         "Group:\r\n" +
                         "    Group Name: Domain Admins\r\n";

        var events = new List<EventLogEntryData>
        {
            new() { EventId = 4728, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(1), Message = msgAdded },
            new() { EventId = 4729, Source = "Microsoft-Windows-Security-Auditing", TimeGenerated = date.AddHours(2), Message = msgRemoved }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 100).Where(c => c.HostName == hostName).OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(2, records.Count);

        // 成員新增：After 不應被操作者頂替，應為空字串；Before 為「（不在群組中）」
        Assert.Equal("admin_operator", records[0].InitiatorAccount);
        Assert.Null(records[0].TargetAccount);
        Assert.Equal(string.Empty, records[0].After);
        Assert.Equal("（不在群組中）", records[0].Before);
        Assert.NotEqual("admin_operator", records[0].After);

        // 成員移除：Before 不應被操作者頂替，應為空字串；After 為「（已移出群組）」
        Assert.Equal("admin_operator", records[1].InitiatorAccount);
        Assert.Null(records[1].TargetAccount);
        Assert.Equal(string.Empty, records[1].Before);
        Assert.Equal("（已移出群組）", records[1].After);
        Assert.NotEqual("admin_operator", records[1].Before);
    }

    [Fact]
    public void 事件本身帶有InitiatorAccount時_優先採用該值而非訊息文字()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-DC01";
        var date = new DateTime(2026, 8, 20);

        var msg = "Subject:\r\n" +
                  "    Account Name: msg_subject_user\r\n" +
                  "Member:\r\n" +
                  "    Account Name: target_user\r\n" +
                  "Group:\r\n" +
                  "    Group Name: Domain Admins\r\n";

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4728,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(1),
                Message = msg,
                InitiatorAccount = "netiq_sun_operator" // NetIQ sun 欄位優先
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var record = Assert.Single(store.Query(null, null, 100), c => c.HostName == hostName);
        Assert.Equal("netiq_sun_operator", record.InitiatorAccount);
        Assert.Equal("target_user", record.TargetAccount);
        Assert.Equal("target_user", record.After);
    }

    [Fact]
    public void 事件4719稽核政策變更_產生的Before與After皆非空字串()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-DC01";
        var date = new DateTime(2026, 8, 20);

        // 1. 純文字未帶結構化欄位的 4719
        var event1 = new EventLogEntryData
        {
            EventId = 4719,
            Source = "Microsoft-Windows-Security-Auditing",
            TimeGenerated = date.AddHours(1),
            Message = "System audit policy was changed."
        };

        // 2. 帶類別、子類別與變更內容的 4719
        var event2 = new EventLogEntryData
        {
            EventId = 4719,
            Source = "Microsoft-Windows-Security-Auditing",
            TimeGenerated = date.AddHours(2),
            Message = "Subject:\r\n  Account Name: secadmin\r\nAudit Policy Change:\r\n  Category: Object Access\r\n  Subcategory: File System\r\n  Changes: Success removed"
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, new List<EventLogEntryData> { event1, event2 }, date);

        var records = store.Query(null, null, 100).Where(c => c.HostName == hostName).OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(2, records.Count);

        // 兩筆的 Before 與 After 皆不得為空字串
        Assert.False(string.IsNullOrWhiteSpace(records[0].Before));
        Assert.False(string.IsNullOrWhiteSpace(records[0].After));
        Assert.Equal(PermissionChangeExtractor.DefaultNotProvided, records[0].Before);
        Assert.Equal(PermissionChangeExtractor.DefaultNotProvided, records[0].After);

        Assert.False(string.IsNullOrWhiteSpace(records[1].Before));
        Assert.False(string.IsNullOrWhiteSpace(records[1].After));
        Assert.Equal("secadmin", records[1].InitiatorAccount);
        // 逐字比對而不是 Contains：「Subcategory:」這個字串本身含有「Category:」，
        // 用 Contains("File System") 的話類別被子類別覆寫（變成「File System - File System」）
        // 一樣會通過，等於對類別欄位零覆蓋。
        Assert.Equal("Object Access - File System：Success removed", records[1].After);
    }

    [Fact]
    public void 各類NetIQ事件產生的紀錄_Category皆非空且與純函式推導一致()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-TEST";
        var date = new DateTime(2026, 8, 20);

        var events = new List<EventLogEntryData>
        {
            new() { EventId = 4728, Source = "Security", TimeGenerated = date.AddMinutes(1), Message = "Group Name: G1\nMember Name: U1" },
            new() { EventId = 4729, Source = "Security", TimeGenerated = date.AddMinutes(2), Message = "Group Name: G1\nMember Name: U1" },
            new() { EventId = 4670, Source = "Security", TimeGenerated = date.AddMinutes(3), Message = "Object Name: C:\\f.txt" },
            new() { EventId = 4717, Source = "Security", TimeGenerated = date.AddMinutes(4), Message = "Target Account: Svc\nAccess Granted: SeLogon" },
            new() { EventId = 4718, Source = "Security", TimeGenerated = date.AddMinutes(5), Message = "Target Account: Svc\nAccess Removed: SeLogon" },
            new() { EventId = 4719, Source = "Security", TimeGenerated = date.AddMinutes(6), Message = "Audit changed" },
            new() { EventId = 4907, Source = "Security", TimeGenerated = date.AddMinutes(7), Message = "Object Name: C:\\a.txt" }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 100).Where(c => c.HostName == hostName).OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(7, records.Count);

        foreach (var record in records)
        {
            Assert.False(string.IsNullOrWhiteSpace(record.Category));
            var expectedCategory = PermissionCategory.Resolve(record.ChangeType, record.EventId);
            Assert.Equal(expectedCategory, record.Category);
        }
    }

    [Fact]
    public void 本機監控產生的明細_Category非空_InitiatorAccount為null且群組成員TargetAccount正確()
    {
        var detailAdd = new PermissionChangeDetail
        {
            Target = "本機 Administrators 群組",
            ChangeType = "成員新增",
            Before = "（不在群組中）",
            After = "charlie",
            Category = PermissionCategory.Resolve("成員新增"),
            IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget("本機 Administrators 群組", "成員新增"),
            InitiatorAccount = null,
            TargetAccount = "charlie"
        };

        var detailRemove = new PermissionChangeDetail
        {
            Target = "本機 Administrators 群組",
            ChangeType = "成員移除",
            Before = "bob",
            After = "（已移出群組）",
            Category = PermissionCategory.Resolve("成員移除"),
            IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget("本機 Administrators 群組", "成員移除"),
            InitiatorAccount = null,
            TargetAccount = "bob"
        };

        var detailAcl = new PermissionChangeDetail
        {
            Target = "C:\\Share",
            ChangeType = "權限新增（ACL 規則）",
            Before = "（無此規則）",
            After = "DOMAIN\\david｜Allow｜Read｜明確設定",
            Category = PermissionCategory.Resolve("權限新增（ACL 規則）"),
            IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget("C:\\Share", "權限新增（ACL 規則）"),
            InitiatorAccount = null,
            TargetAccount = null
        };

        // 驗證成員新增
        Assert.Equal(PermissionCategory.GroupMember, detailAdd.Category);
        Assert.True(detailAdd.IsPrivilegedTarget);
        Assert.Null(detailAdd.InitiatorAccount);
        Assert.Equal("charlie", detailAdd.TargetAccount);

        // 驗證成員移除
        Assert.Equal(PermissionCategory.GroupMember, detailRemove.Category);
        Assert.False(detailRemove.IsPrivilegedTarget);
        Assert.Null(detailRemove.InitiatorAccount);
        Assert.Equal("bob", detailRemove.TargetAccount);

        // 驗證資料夾 ACL
        Assert.Equal(PermissionCategory.FolderAcl, detailAcl.Category);
        Assert.False(detailAcl.IsPrivilegedTarget);
        Assert.Null(detailAcl.InitiatorAccount);
        Assert.Null(detailAcl.TargetAccount);
    }

    [Fact]
    public void 加入特權群組時IsPrivilegedTarget為true_加入一般資料夾時為false()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-TEST";
        var date = new DateTime(2026, 8, 20);

        var events = new List<EventLogEntryData>
        {
            // 加入特權群組（Domain Admins）
            new()
            {
                EventId = 4728,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(1),
                Message = "Group Name: Domain Admins\nMember Name: PrivUser"
            },
            // 加入一般群組（Normal Group）
            new()
            {
                EventId = 4728,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(2),
                Message = "Group Name: Normal Users Group\nMember Name: RegularUser"
            },
            // 物件權限變更（ACL）
            new()
            {
                EventId = 4670,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(3),
                Message = "Object Name: C:\\SharedFolder\nOriginal Security Descriptor: D:(A;;GA;;;BA)\nNew Security Descriptor: D:(A;;GA;;;WD)"
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 100).Where(c => c.HostName == hostName).OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(3, records.Count);

        Assert.True(records[0].IsPrivilegedTarget);
        Assert.Equal(PermissionCategory.GroupMember, records[0].Category);

        Assert.False(records[1].IsPrivilegedTarget);
        Assert.Equal(PermissionCategory.GroupMember, records[1].Category);

        Assert.False(records[2].IsPrivilegedTarget);
        Assert.Equal(PermissionCategory.FolderAcl, records[2].Category);
    }

    [Fact]
    public void 事件4717與4718_正確擷取TargetAccount與非空BeforeAfter()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-TEST";
        var date = new DateTime(2026, 8, 20);

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4717,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(1),
                Message = "Subject:\r\n  Account Name: admin1\r\nTarget Account:\tServiceAcct1\r\nAccess Granted:\tSeServiceLogonRight"
            },
            new()
            {
                EventId = 4718,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(2),
                Message = "Subject:\r\n  Account Name: admin2\r\nTarget Account:\tServiceAcct2\r\nAccess Removed:\tSeServiceLogonRight"
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var records = store.Query(null, null, 100).Where(c => c.HostName == hostName).OrderBy(r => r.DetectedAt).ToList();
        Assert.Equal(2, records.Count);

        // 4717 授與權限
        Assert.Equal("ServiceAcct1", records[0].Target);
        Assert.Equal("ServiceAcct1", records[0].TargetAccount);
        Assert.Equal("admin1", records[0].InitiatorAccount);
        Assert.Equal("（未授與）", records[0].Before);
        Assert.Equal("SeServiceLogonRight", records[0].After);

        // 4718 移除權限
        Assert.Equal("ServiceAcct2", records[1].Target);
        Assert.Equal("ServiceAcct2", records[1].TargetAccount);
        Assert.Equal("admin2", records[1].InitiatorAccount);
        Assert.Equal("SeServiceLogonRight", records[1].Before);
        Assert.Equal("（已移除）", records[1].After);
    }

    [Fact]
    public void 中文區段標頭與欄位_正確擷取操作者與成員帳號()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var hostName = "SRV-DC01";
        var date = new DateTime(2026, 8, 20);

        var msg = "已將成員新增到安全性通用群組。\r\n" +
                  "主體:\r\n" +
                  "    安全性識別碼: CONTOSO\\admin_zh\r\n" +
                  "    帳戶名稱: admin_zh\r\n" +
                  "成員:\r\n" +
                  "    安全性識別碼: CONTOSO\\user_zh\r\n" +
                  "    帳戶名稱: user_zh\r\n" +
                  "群組:\r\n" +
                  "    群組名稱: Administrators\r\n";

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4756,
                Source = "Microsoft-Windows-Security-Auditing",
                TimeGenerated = date.AddHours(1),
                Message = msg
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), hostName, WebHost.OsWindows, events, date);

        var record = Assert.Single(store.Query(null, null, 100), c => c.HostName == hostName);
        Assert.Equal("Administrators", record.Target);
        Assert.Equal("admin_zh", record.InitiatorAccount);
        Assert.Equal("user_zh", record.TargetAccount);
        Assert.Equal("user_zh", record.After);
        Assert.Equal("（不在群組中）", record.Before);
        Assert.True(record.IsPrivilegedTarget);
        Assert.Equal(PermissionCategory.GroupMember, record.Category);
    }

    [Fact]
    public void 訊息剖不出對象時_Target為空字串()
    {
        var details = PermissionChangeExtractor.Extract(
            message: "無任何群組或物件欄位的純文字訊息",
            changeType: "成員新增",
            eventId: 4756);

        Assert.Equal(string.Empty, details.Target);
    }

    [Fact]
    public void 事件來源名不再出現在Target()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var date = new DateTime(2026, 8, 20);

        var events = new List<EventLogEntryData>
        {
            new()
            {
                EventId = 4719,
                Source = "Microsoft-Windows-Security-Auditing",
                Message = "System audit policy was changed.",
                TimeGenerated = date
            }
        };

        HostDayPostProcessor.RecordPermissionChanges(store, Keys(), "SRV-TEST", WebHost.OsWindows, events, date);

        var record = Assert.Single(store.Query(null, null, 100), c => c.HostName == "SRV-TEST");
        Assert.DoesNotContain("Microsoft-Windows-Security-Auditing", record.Target);
        Assert.DoesNotContain("EventId", record.Target);
        Assert.Equal(string.Empty, record.Target);
    }

    [Theory]
    [InlineData("CN=33951 [Li Zhihui],OU=1220000000,DC=corp", "33951 [Li Zhihui]")]
    [InlineData("CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=corp,DC=com", "33951 [Li Zhihui]")]
    [InlineData("cn=Admin User,ou=Admins,ou=IT,dc=domain,dc=local", "Admin User")]
    [InlineData("CN=SingleCNOnly", "SingleCNOnly")]
    [InlineData("OU=Admins,CN=NestedUser,DC=corp", "NestedUser")]
    public void 帳號顯示短名_多層OU的DN取CN值(string dn, string expected)
    {
        var result = AccountDisplayFormatter.ToShortName(dn);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"DOMAIN\name", @"DOMAIN\name")]
    [InlineData(@"CONTOSO\alice", @"CONTOSO\alice")]
    [InlineData("name@domain.com", "name@domain.com")]
    [InlineData("alice", "alice")]
    [InlineData("S-1-5-21-1234567890-1234567890-1234567890-1001", "S-1-5-21-1234567890-1234567890-1234567890-1001")]
    [InlineData("OU=Sales,OU=HQ,DC=corp,DC=com", "OU=Sales,OU=HQ,DC=corp,DC=com")]
    public void 帳號顯示短名_Domain帳號與其他非DN格式原樣返回(string account, string expected)
    {
        var result = AccountDisplayFormatter.ToShortName(account);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void 帳號顯示短名_Null或空白返回空字串(string? input)
    {
        var result = AccountDisplayFormatter.ToShortName(input);
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(@"CN=Wang\, Ming,OU=Users,DC=corp", "Wang, Ming")]
    [InlineData(@"CN=Special\, Name\, Jr.,OU=Admins,DC=corp", "Special, Name, Jr.")]
    [InlineData(@"CN=Smith\, John,OU=Engineering,OU=HQ,DC=example,DC=com", "Smith, John")]
    public void 帳號顯示短名_含反斜線跳脫逗號的CN值不被截斷且不留跳脫符號(string dn, string expected)
    {
        var result = AccountDisplayFormatter.ToShortName(dn);
        Assert.Equal(expected, result);
    }
}

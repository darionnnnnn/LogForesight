using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 3a：夜間與 PRTG 背景路徑的整份資料重複讀取。
/// 原則是不改結果、寫入內容與取消時機，只減少一次操作內對同一份資料的重複讀取。
/// </summary>
public sealed class BackgroundRepeatedReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-bg-repeat-read-" + Guid.NewGuid().ToString("N"));
    private readonly Recorder _recorder = new();
    private readonly StorageBackend _backend;

    private readonly ITestOutputHelper _output;

    public BackgroundRepeatedReadTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")}" },
            _dir, _recorder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>記錄所有 SELECT；「整份內容讀取」＝讀取 Content 欄且參數帶 blob 鍵。</summary>
    private sealed class Recorder : DbCommandInterceptor
    {
        private readonly List<(string Text, List<object?> Params)> _selects = [];
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var values = command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList();
                lock (_selects) _selects.Add((command.CommandText, values));
            }
            return base.ReaderExecuting(command, eventData, result);
        }
        public void Reset() { lock (_selects) _selects.Clear(); }
        public int AllSelects() { lock (_selects) return _selects.Count; }
        public int ContentReads(string key)
        {
            lock (_selects)
                return _selects.Count(s => s.Text.Contains("\"content\"", StringComparison.Ordinal) &&
                                           s.Params.Any(p => p is string text && text == key));
        }
    }

    private static void Seed(StorageBackend backend, string key, string json) =>
        backend.Blob(key).Mutate<object?>(_ => (json, null));

    // ── A1：夜間掛接的問題負責人清單一趟讀一次 ────────────────────────────────────

    private static IssueProfile Conclusion(string source, int eventId, string note) => new()
    {
        SourceName = source, EventId = eventId, ConclusionStatus = IssueHandlingStatuses.KnownNoise,
        ConclusionNote = note, AutoApply = true
    };

    private sealed class CoordinatorRig
    {
        public readonly FakeHostStore Hosts = new();
        public readonly FakeAnalysisRecordQuery Records = new();
        public readonly FakeIssueCaseStore Cases = new();
        public readonly FakeIssueHandlingStore Handlings = new();
        public readonly FakeHandlingStore Logs = new();
        public IssueCaseCoordinator Create(IIssueOwnerStore owners) => new(Cases, Handlings, Logs, Records, Hosts, owners);
    }

    private static List<LogIssueSignature> Issues() =>
    [
        new() { LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error },
        new() { LogName = "System", Source = "nic", EventId = 4202, EntryType = LogEntryType.Warning }
    ];

    private (string Signature, int Reads) RunThreeDays(Func<IIssueOwnerStore> make)
    {
        var rig = new CoordinatorRig();
        var hostNames = new[] { "SRV-1", "SRV-2", "SRV-3" };
        foreach (var name in hostNames) rig.Hosts.Upsert(new WebHost { HostName = name });
        _recorder.Reset();
        var coordinator = rig.Create(make());
        var day = new DateTime(2026, 11, 10);
        var attached = hostNames.Select(h => coordinator.AttachNewDay(h, day, Issues(), day.AddHours(3)).AttachedCount).ToArray();
        var rows = hostNames.SelectMany(h => rig.Handlings.GetForDay(h, day))
            .OrderBy(r => r.HostName).ThenBy(r => r.IssueKey)
            .Select(r => $"{r.HostName}|{r.IssueKey}|{r.Status}|{r.Note}|{r.CaseId}");
        var logs = hostNames.SelectMany(h => rig.Logs.GetLogs(h, day))
            .Select(l => $"{l.HostName}|{l.Status}|{l.Action}|{l.IssueKey}|{l.Note}").OrderBy(x => x);
        return (string.Join(";", attached) + "/" + string.Join(",", rows) + "/" + string.Join(",", logs),
            _recorder.ContentReads("issue_owners"));
    }

    [Fact]
    public void A1_夜間掛接_三個主機日只讀一次問題負責人_結果與逐次讀取相同()
    {
        var owners = new IssueOwnerStore(_backend.Blob("issue_owners"));
        owners.Upsert(Conclusion("disk", 153, "已知雜訊"));
        owners.Upsert(Conclusion("nic", 4202, "已知抖動"));

        var plain = RunThreeDays(() => new IssueOwnerStore(_backend.Blob("issue_owners")));
        var wrapped = RunThreeDays(() => new PerRunIssueOwnerStore(new IssueOwnerStore(_backend.Blob("issue_owners"))));

        Assert.Equal(3, plain.Reads);
        Assert.Equal(1, wrapped.Reads);
        Assert.Equal(plain.Signature, wrapped.Signature);
        Assert.Contains("已知雜訊", wrapped.Signature);
    }

    [Fact]
    public void A1_包裝_GetAll回傳複本清單_更新後重讀_Get與Delete委派()
    {
        var inner = new IssueOwnerStore(_backend.Blob("issue_owners"));
        inner.Upsert(Conclusion("disk", 153, "舊結論"));
        var wrapper = new PerRunIssueOwnerStore(inner);
        _recorder.Reset();

        var first = wrapper.GetAll();
        first.Clear();                       // 清單是複本：呼叫端改動不影響下一次
        var second = wrapper.GetAll();
        Assert.Single(second);
        Assert.Equal(1, _recorder.ContentReads("issue_owners"));

        wrapper.Update("disk", 153, p => p.ConclusionNote = "新結論");
        Assert.Equal("新結論", Assert.Single(wrapper.GetAll()).ConclusionNote);
        Assert.Equal("新結論", wrapper.Get("disk", 153)!.ConclusionNote);

        wrapper.Delete("disk", 153);
        Assert.Empty(wrapper.GetAll());
    }

    [Fact]
    public void A1_網頁端協調器直接用內層_負責人剛被修改後讀到新值()
    {
        var owners = new IssueOwnerStore(_backend.Blob("issue_owners"));
        owners.Upsert(Conclusion("disk", 153, "舊"));
        var rig = new CoordinatorRig();
        rig.Hosts.Upsert(new WebHost { HostName = "SRV-1" });
        var coordinator = rig.Create(owners);   // 與 DI singleton 的接法相同：不包裝
        var day = new DateTime(2026, 11, 10);

        owners.Update("disk", 153, p => p.ConclusionNote = "新");
        coordinator.AttachNewDay("SRV-1", day, Issues(), day);

        Assert.Equal("〔機房結論〕新", rig.Handlings.GetForDay("SRV-1", day).Single(r => r.Note!.StartsWith("〔機房結論〕")).Note);
    }

    [Fact]
    public void A1_夜間協調器建立點使用包裝_網頁端不使用()
    {
        var root = RepoRoot();
        var orchestrator = File.ReadAllText(Path.Combine(root, "LogForesight.Core/Service/AnalysisOrchestrator.cs"));
        Assert.Contains("new IssueCaseCoordinator(", orchestrator);
        var start = orchestrator.IndexOf("new IssueCaseCoordinator(", StringComparison.Ordinal);
        Assert.Contains("PerRunIssueOwnerStore", orchestrator.Substring(start, 600));
        var web = File.ReadAllText(Path.Combine(root, "LogForesight.Web/Extensions/ServiceCollectionExtensions.cs"));
        Assert.DoesNotContain("PerRunIssueOwnerStore", web);
    }

    // ── A2／A3：PRTG 取消檢查點 ───────────────────────────────────────────────────

    private (SystemSettingsStore Settings, PrtgScopeRevisionReader Reader) PrtgHarness()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(s => { s.PrtgEnabled = true; s.PrtgUrl = "https://prtg.example"; });
        return (settings, new PrtgScopeRevisionReader(_backend, new HostStore(_backend.Blob("hosts"))));
    }

    /// <summary>連續檢查；回傳第一次擲取消的檢查序號（1 起算），沒擲回 0。mutate 在指定序號之前執行。</summary>
    private int CancelledAt(Action<SystemSettingsStore> mutate, bool sharedSettings, int mutateBefore = 3, int total = 5)
    {
        var (settings, reader) = PrtgHarness();
        Func<SystemSettings> read = sharedSettings ? () => settings.Get()
            : () => new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        using var scope = new PrtgOperationScope(settings.Get(), read, default, reader.ReadRawSemantic, kind: Guid.NewGuid().ToString("N"));
        for (var i = 1; i <= total; i++)
        {
            if (i == mutateBefore) mutate(settings);
            try { scope.Checkpoint(); }
            catch (OperationCanceledException) { return i; }
        }
        return 0;
    }

    private void SameTimestampImport() =>
        _backend.Blob("system_settings").Mutate<object?>(raw =>
        {
            var node = JsonNode.Parse(raw!)!;
            node["PrtgUrl"] = "https://imported.example";   // 內容變、UpdatedAt／Revision 都不變
            return (node.ToJsonString(), null);
        });

    public static IEnumerable<object[]> Shares => [[true], [false]];

    [Theory, MemberData(nameof(Shares))]
    public void A2_設定變更_在變更後第一次檢查取消(bool shared) =>
        Assert.Equal(3, CancelledAt(s => s.Update(x => x.PrtgTimeoutSeconds += 7), shared));

    [Theory, MemberData(nameof(Shares))]
    public void A2_停用PRTG_在變更後第一次檢查取消(bool shared) =>
        Assert.Equal(3, CancelledAt(s => s.Update(x => x.PrtgEnabled = false), shared));

    [Theory, MemberData(nameof(Shares))]
    public void A2_同一時間戳的匯入_仍在變更後第一次檢查取消(bool shared) =>
        Assert.Equal(3, CancelledAt(_ => SameTimestampImport(), shared));

    [Theory, MemberData(nameof(Shares))]
    public void A2_範圍變更_在變更後第一次檢查取消(bool shared) =>
        Assert.Equal(3, CancelledAt(_ => Seed(_backend, PrtgMonitoringPolicyStore.BlobKey,
            "{\"Revision\":\"r2\",\"HostIds\":[5],\"SensorIds\":[]}"), shared));

    [Fact]
    public void A2_沒有變更_不取消()
    {
        Assert.Equal(0, CancelledAt(_ => { }, sharedSettings: true));
    }

    [Fact]
    public void A2_穩態檢查_資料庫往返數不超過一次設定版本探測加一次範圍版本查詢()
    {
        var (settings, reader) = PrtgHarness();
        using var scope = new PrtgOperationScope(settings.Get(), () => settings.Get(), default, reader.ReadRawSemantic,
            kind: Guid.NewGuid().ToString("N"));
        scope.Checkpoint();
        scope.Checkpoint();   // 暖機：版本快取、指紋快取都已就緒
        _recorder.Reset();
        scope.Checkpoint();
        _output.WriteLine($"穩態單次檢查 SELECT 次數={_recorder.AllSelects()}");
        Assert.True(_recorder.AllSelects() <= 2, $"實際 SELECT 次數 {_recorder.AllSelects()}");
        Assert.Equal(0, _recorder.ContentReads(PrtgMonitoringPolicyStore.BlobKey));
        Assert.Equal(0, _recorder.ContentReads("system_settings"));
    }

    [Fact]
    public void A2_一次檢查內範圍字串只讀一次()
    {
        var calls = 0;
        var initial = new SystemSettings { PrtgEnabled = true, Revision = "a" };
        using var scope = new PrtgOperationScope(initial, () => initial, default, () => { calls++; return "scope"; },
            kind: Guid.NewGuid().ToString("N"));
        scope.Checkpoint();   // 首次：擷取初始範圍
        calls = 0;
        scope.Checkpoint();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A2_每日評估建立檢查點的設定讀取器為共用實例()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Core/Service/PrtgDailyPipeline.cs"));
        Assert.DoesNotContain("() => new SystemSettingsStore(", text);
    }

    [Fact]
    public void A3_ReadVersions內容_採用與預期的範圍雜湊與狀態()
    {
        static string Hash(string v) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v)));
        var current = new SystemSettings { PrtgEnabled = true, Revision = "r1" };
        var scopeValue = "s1"; var kind = Guid.NewGuid().ToString("N");
        using var scope = new PrtgOperationScope(current, () => current, default, () => scopeValue, kind);
        var v0 = Assert.Single(PrtgOperationScope.ReadVersions(), v => v.Kind == kind);
        Assert.Equal("尚未讀取", v0.AdoptedScopeRevision);

        scope.Checkpoint();
        var v1 = Assert.Single(PrtgOperationScope.ReadVersions(), v => v.Kind == kind);
        Assert.Equal(Hash("s1"), v1.AdoptedScopeRevision);
        Assert.Equal(Hash("s1"), v1.ExpectedScopeRevision);
        Assert.Equal("執行中", v1.State);
        Assert.False(v1.StopRequested);

        scopeValue = "s2";
        var v2 = Assert.Single(PrtgOperationScope.ReadVersions(), v => v.Kind == kind);
        Assert.Equal(Hash("s1"), v2.AdoptedScopeRevision);
        Assert.Equal(Hash("s2"), v2.ExpectedScopeRevision);
        Assert.Equal("等待安全取消點", v2.State);

        Assert.Throws<OperationCanceledException>(scope.Checkpoint);
        scope.Dispose();
        var v3 = Assert.Single(PrtgOperationScope.ReadVersions(), v => v.Kind == kind);
        Assert.Equal(Hash("s2"), v3.ExpectedScopeRevision);
        Assert.Equal("已取消：設定或範圍變更", v3.State);
        Assert.True(v3.StopRequested);
    }

    // ── A4：範圍讀取器 ────────────────────────────────────────────────────────────

    private static string Reference(string structural, string policyJson)
    {
        var p = System.Text.Json.JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyJson)!;
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            HostIds = p.HostIds.Order().ToArray(), SensorIds = p.SensorIds.Order().ToArray()
        });
        return $"{structural}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))}";
    }

    [Fact]
    public void A4_三種輸出與改動前做法逐字相同_政策沒變時政策blob只讀一次()
    {
        var reader = new PrtgScopeRevisionReader(_backend, new HostStore(_backend.Blob("hosts")));
        Seed(_backend, PrtgMonitoringPolicyStore.BlobKey, "{\"Revision\":\"r1\",\"HostIds\":[9,3],\"SensorIds\":[2,1]}");
        _recorder.Reset();

        var first = reader.ReadRawSemantic();
        var second = reader.ReadRawSemantic();
        Assert.Equal(first, second);
        Assert.Equal(1, _recorder.ContentReads(PrtgMonitoringPolicyStore.BlobKey));
        Assert.Equal(Reference(reader.ReadStructural(), "{\"HostIds\":[9,3],\"SensorIds\":[2,1]}"), first);
        Assert.Matches("^\\d+:\\d+:0:\\[\\]:[0-9A-F]{64}:[0-9A-F]{64}$", first);
        Assert.Matches("^\\d+:0:0:\\[\\]:[0-9A-F]{64}$", reader.ReadStructural());
        Assert.Matches("^\\d+:\\d+:\\d+:\\[\\]:[0-9A-F]{64}$", reader.Read());
    }

    [Theory]
    [InlineData("{\"Revision\":\"r2\",\"HostIds\":[9,3],\"SensorIds\":[2,1]}", false)]   // 只改縮圈以外欄位
    [InlineData("{\"Revision\":\"r1\",\"HostIds\":[9,3,4],\"SensorIds\":[2,1]}", true)]   // 只改縮圈
    public void A4_政策被修改後_下一次輸出與改動前做法相同(string newPolicy, bool rawChanges)
    {
        var reader = new PrtgScopeRevisionReader(_backend, new HostStore(_backend.Blob("hosts")));
        Seed(_backend, PrtgMonitoringPolicyStore.BlobKey, "{\"Revision\":\"r1\",\"HostIds\":[9,3],\"SensorIds\":[2,1]}");
        var before = reader.ReadRawSemantic();

        Seed(_backend, PrtgMonitoringPolicyStore.BlobKey, newPolicy);
        _recorder.Reset();
        var after = reader.ReadRawSemantic();

        Assert.Equal(1, _recorder.ContentReads(PrtgMonitoringPolicyStore.BlobKey));
        Assert.Equal(Reference(reader.ReadStructural(), newPolicy), after);
        Assert.Equal(rawChanges, before != after);
    }

    [Fact]
    public void A4_政策不存在時使用預設政策_之後建立即反映()
    {
        var reader = new PrtgScopeRevisionReader(_backend, new HostStore(_backend.Blob("hosts")));
        Assert.Equal(Reference(reader.ReadStructural(), "{}"), reader.ReadRawSemantic());
        Seed(_backend, PrtgMonitoringPolicyStore.BlobKey, "{\"HostIds\":[1]}");
        Assert.Equal(Reference(reader.ReadStructural(), "{\"HostIds\":[1]}"), reader.ReadRawSemantic());
    }

    // ── A5：快照服務共用讀取器；主機指紋只算一次 ──────────────────────────────────

    public class CountingProxy : DispatchProxy
    {
        public IHostStore Target = null!;
        public int GetAllCalls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IHostStore.GetAll)) Interlocked.Increment(ref GetAllCalls);
            try { return targetMethod.Invoke(Target, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException!; }
        }
    }

    [Fact]
    public void A5_同一個讀取器多次讀取_主機指紋只算一次_清單被修改後下一次重算()
    {
        var store = new HostStore(_backend.Blob("hosts"));
        store.Upsert(new WebHost { HostName = "H1", IpAddress = "10.0.0.1", Active = true });
        var proxy = DispatchProxy.Create<IHostStore, CountingProxy>();
        var counting = (CountingProxy)(object)proxy;
        counting.Target = store;
        var reader = new PrtgScopeRevisionReader(_backend, proxy);

        var a = reader.Read(); reader.ReadRawSemantic(); reader.ReadStructural(); reader.Read();
        Assert.Equal(1, counting.GetAllCalls);

        store.Upsert(new WebHost { HostName = "H2", IpAddress = "10.0.0.2", Active = true });
        var b = reader.Read();
        Assert.Equal(2, counting.GetAllCalls);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void A5_快照服務只持有一個讀取器實例()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web/Services/PrtgSnapshotHostedService.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "new PrtgScopeRevisionReader\\("));
    }

    [Fact]
    public void A4_讀取器多執行緒並行呼叫_輸出一致()
    {
        var reader = new PrtgScopeRevisionReader(_backend, new HostStore(_backend.Blob("hosts")));
        var expected = reader.ReadRawSemantic();
        var results = Enumerable.Range(0, 8).AsParallel().Select(_ => reader.ReadRawSemantic()).ToArray();
        Assert.All(results, r => Assert.Equal(expected, r));
    }

    // ── A6：主機對應重算共用主機清單讀取器 ────────────────────────────────────────

    private (PrtgHostMapRefresher Refresher, HostStore Hosts) MapperRig()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(s => s.PrtgEnabled = true);
        var hosts = new HostStore(_backend.Blob("hosts"));
        hosts.Upsert(new WebHost { HostName = "srv-app01", IpAddress = "10.9.0.1", Active = true });
        _backend.PrtgStore().UpsertDevices([new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01", Ip = "10.9.0.1" }], DateTime.Now);
        return (new PrtgHostMapRefresher(settings, new PrtgAddressResolver(), _backend), hosts);
    }

    [Fact]
    public void A6_兩次重算之間清單沒變_主機blob只整份讀一次_結果正確_修改後讀到新內容()
    {
        var (refresher, hosts) = MapperRig();
        _recorder.Reset();

        Assert.Null(refresher.TryRefreshToday());
        var rows = _backend.PrtgStore().GetHostMapForDate(DateTime.Today);
        var row = Assert.Single(rows);
        Assert.Equal(1001, row.DeviceObjid);
        Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
        Assert.Null(refresher.TryRefreshToday());
        Assert.Equal(1, _recorder.ContentReads("hosts"));

        // 主機改 IP：下一次重算讀到新內容，原裝置不再對到
        hosts.MutateBatch(list => list.Single(h => h.HostId == row.HostId).IpAddress = "10.9.0.77");
        _recorder.Reset();
        Assert.Null(refresher.TryRefreshToday());
        Assert.Equal(1, _recorder.ContentReads("hosts"));
        Assert.NotEqual(PrtgMapStatus.Ok, Assert.Single(_backend.PrtgStore().GetHostMapForDate(DateTime.Today)).MapStatus);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LogForesight.sln")) &&
               !Directory.Exists(Path.Combine(dir, "LogForesight.Core"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("找不到專案根目錄");
    }
}

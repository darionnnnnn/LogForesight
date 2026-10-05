using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 每日路徑（docs/PRTG-SPEC.md §3）的**就緒保底**：
/// 不論走哪條路離開（PRTG 未啟用、初始化失敗、取消），都必須發佈 finding 登錄簿並送
/// `prtg-findings-ready`。少了這道，AI 分析排程會一路等到整趟取數結束——等於這個機制沒做，
/// 而症狀只是「AI 晚了幾小時」，不會有任何錯誤訊息。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class PrtgDailyPipelineTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public PrtgDailyPipelineTests()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class CollectingConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public Action<string>? OnWriteLine { get; set; }
        public void WriteLine(string message = "")
        {
            Lines.Add(message);
            OnWriteLine?.Invoke(message);
        }
    }

    private sealed class CollectingProgress : IRunProgress
    {
        public List<string> Phases { get; } = new();
        public List<(string Phase, int Done, int Total)> Reports { get; } = new();
        public Action<string, int, int>? OnReport { get; set; }
        public void Report(string phase, int done, int total)
        {
            Phases.Add(phase);
            Reports.Add((phase, done, total));
            OnReport?.Invoke(phase, done, total);
        }
    }

    private sealed class TrackingHostStore(IHostStore inner) : IHostStore
    {
        private readonly Dictionary<string, int> _getAllCallsByCaller = new(StringComparer.Ordinal);
        public int GetAllCalls { get; private set; }
        public int CapturePrtgSnapshotCalls { get; private set; }
        public IReadOnlyDictionary<string, int> GetAllCallsByCaller => _getAllCallsByCaller;
        public List<WebHost> GetAll()
        {
            GetAllCalls++;
            var method = new StackTrace().GetFrame(1)?.GetMethod();
            var declaringType = method?.DeclaringType;
            var callerType = declaringType?.DeclaringType ?? declaringType;
            var callerMethod = method?.Name;
            if (callerMethod == "MoveNext" && declaringType?.Name is { } stateMachine && stateMachine.StartsWith('<'))
            {
                var stateMachineEnd = stateMachine.IndexOf('>');
                if (stateMachineEnd > 1) callerMethod = stateMachine[1..stateMachineEnd];
            }
            var caller = $"{callerType?.Name}.{callerMethod}";
            _getAllCallsByCaller[caller] = _getAllCallsByCaller.GetValueOrDefault(caller) + 1;
            return inner.GetAll();
        }
        public PrtgHostSnapshot CapturePrtgSnapshot()
        { CapturePrtgSnapshotCalls++; return inner.CapturePrtgSnapshot(); }
        public long DataVersion => inner.DataVersion;
        public WebHost? Get(long hostId) => inner.Get(hostId);
        public WebHost? FindByName(string hostName) => inner.FindByName(hostName);
        public WebHost Upsert(WebHost host) => inner.Upsert(host);
        public WebHost Touch(string hostName, DateTime reportedAt, string source = "local") => inner.Touch(hostName, reportedAt, source);
        public WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt) => inner.TouchNetiq(hostId, displayName, reportedAt);
        public void SetGroups(long hostId, IEnumerable<long> groupIds) => inner.SetGroups(hostId, groupIds);
        public void SetHighVolume(long hostId, bool isHighVolume) => inner.SetHighVolume(hostId, isHighVolume);
        public HostGroupsBatchResult SetGroupsBatch(IEnumerable<long> hostIds, IEnumerable<long> groupIds, bool replace) =>
            inner.SetGroupsBatch(hostIds, groupIds, replace);
        public void SetOwners(long hostId, IEnumerable<long> userIds) => inner.SetOwners(hostId, userIds);
        public void Merge(long sourceHostId, long targetHostId) => inner.Merge(sourceHostId, targetHostId);
        public void Unmerge(long hostId) => inner.Unmerge(hostId);
        public TResult MutateBatch<TResult>(Func<List<WebHost>, TResult> mutation) => inner.MutateBatch(mutation);
        public void MutateBatch(Action<List<WebHost>> mutation) => inner.MutateBatch(mutation);
    }

    private (AnalysisRunContext Ctx, CollectingConsole Console, CollectingProgress Progress, PrtgFindingsRegistry Registry)
        CreateContext(CancellationToken ct = default)
    {
        // 此組測試原本只造訊息列；正式判定現在需另外明列來源、資源及涵蓋。
        // 固定樣本的涵蓋是測試輸入，不宣稱失敗的 HTTP 查詢提供了它。
        var fixtureSensors = _backend.PrtgStore().GetSensorStatuses();
        var fixtureHosts = new HostStore(_backend.Blob("hosts")).GetAll();
        var fixtureSettings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        if (fixtureSensors.Count > 0 && Uri.TryCreate(fixtureSettings.PrtgUrl, UriKind.Absolute, out var fixtureUri) &&
            fixtureUri.Scheme is "http" or "https")
        {
            new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
            {
                p.CoreSystemId = "fixture-core"; p.SourceGeneration = "test-source";
                p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US";
                p.EndpointHint = LogForesight.Core.Persistence.Sql.EfPrtgObservationStore.SourceHintFor(fixtureSettings.PrtgUrl);
                p.ValidFrom = DateTimeOffset.Now.AddDays(-31); p.Revision = "fixture";
                p.HostIds = fixtureHosts.Select(h => h.HostId).ToList(); p.SensorIds = fixtureSensors.Select(s => s.Objid).ToList();
            });
            var fixtureChanges = _backend.PrtgStore().GetStateChanges(DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1));
            foreach (var sensor in fixtureSensors)
            {
                var device = _backend.PrtgStore().GetAllDevices().First(d => d.Objid == sensor.DeviceObjid);
                var host = fixtureHosts.FirstOrDefault(h => h.IpAddress == device.Ip);
                if (host == null) continue;
                new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + sensor.Objid)).Update(e =>
                {
                    e.SensorId = sensor.Objid; e.HostId = host.HostId; e.SourceGeneration = "test-source";
                    e.ResourceGeneration = "test-resource-" + sensor.Objid; e.IdentityFingerprint = "fixture";
                    e.ValidFrom = DateTimeOffset.Now.AddDays(-31);
                    e.Accept(new DateTimeOffset(DateTime.Today.AddDays(-30)), DateTimeOffset.Now,
                        fixtureChanges.Where(c => c.SensorObjid == sensor.Objid).Select(c =>
                            new PrtgTimedState(sensor.Objid, new DateTimeOffset(c.ChangedAt), c.Status, e.SourceGeneration, e.ResourceGeneration)));
                });
            }
        }
        var console = new CollectingConsole();
        var progress = new CollectingProgress();
        var registry = new PrtgFindingsRegistry();

        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(),
            _backend.RecordStore(),
            new HostStore(_backend.Blob("hosts")),
            new IssueOwnerStore(_backend.Blob("issue_owners")));
        var dispatch = NightlyDispatchFakes.Create(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            new HostStore(_backend.Blob("hosts")), new IssueOwnerStore(_backend.Blob("issue_owners")), caseCoordinator);

        var recorder = new BatchRunRecorder(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            "test-host", Array.Empty<string>());

        var ctx = new AnalysisRunContext(
            new RunRequest(), new AppSettings(), new RetentionOptions(), console, ct,
            new EventLogService(), caseCoordinator, _backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), UseAi: false, progress, registry, dispatch);

        return (ctx, console, progress, registry);
    }

    /// <summary>
    /// PRTG 未啟用時整條路徑短路，但**仍要宣告就緒**——
    /// 「算不出東西」與「還沒算完」必須分得出來，否則 AI 會空等一整晚。
    /// </summary>
    [Fact]
    public async Task PRTG未啟用時仍宣告就緒並送出訊號()
    {
        // 預設設定即為 PrtgEnabled = false
        var (ctx, console, progress, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null);

        Assert.True(registry.IsReady);
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);
        Assert.Contains(console.Lines, l => l.Contains("PRTG 未啟用"));
    }

    [Fact]
    public async Task 執行中停用PRTG_逐日開始前停止且不取消其他分析()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://127.0.0.1:1";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 1;
        });
        var (ctx, console, progress, registry) = CreateContext();
        progress.OnReport = (phase, _, total) =>
        {
            if (phase == RunPhases.PrtgDateRange && total == 0)
                new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgEnabled = false);
        };
        var otherAnalysis = Task.CompletedTask;

        await PrtgDailyPipeline.RunAsync(ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-2) }, otherAnalysis, hostIds: null);

        Assert.True(otherAnalysis.IsCompletedSuccessfully);
        Assert.True(registry.IsReady);
        Assert.Contains(console.Lines, line => line.Contains("停止後續 PRTG 工作"));
        Assert.DoesNotContain(progress.Reports, report => report.Phase == RunPhases.PrtgDateRange && report.Total > 0);
        ctx.RunRecorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"))
            .GetRun(ctx.RunRecorder.RunId);
        Assert.Equal(BatchRun.PrtgOutcomePartial, run!.PrtgOutcome);
    }

    [Fact]
    public async Task 執行中修改非PRTG設定_不中斷PRTG逐日分析()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://127.0.0.1:1";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 1;
        });
        var (ctx, console, progress, _) = CreateContext();
        progress.OnReport = (phase, _, total) =>
        {
            if (phase == RunPhases.PrtgDateRange && total == 0)
                new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.MailEnabled = !s.MailEnabled);
        };

        await PrtgDailyPipeline.RunAsync(ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-2) }, Task.CompletedTask, hostIds: null);

        Assert.DoesNotContain(console.Lines, line => line.Contains("停止後續 PRTG 工作"));
        Assert.Equal(2, progress.Reports.Count(report => report.Phase == RunPhases.PrtgDateRange && report.Total > 0));
    }

    /// <summary>
    /// 設定了啟用但連線位址無效（初始化就失敗）時同樣要就緒——
    /// 失敗隔離的另一面：PRTG 壞掉不能讓 AI 跟著卡住。
    /// </summary>
    [Fact]
    public async Task 初始化失敗時仍宣告就緒()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            // 非 http/https 的 scheme 讓 PrtgClient 在建構時就擲例外——這才是「初始化失敗」，
            // 走的是最外層 catch → finally 的保底；連不上的位址只會讓階段 1 失敗、流程照常往下走。
            s.PrtgUrl = "ftp://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, _, progress, registry) = CreateContext();
        var days = new[] { DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-2) };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")), days, Task.CompletedTask, hostIds: null, guard: null);

        Assert.True(registry.IsReady);
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);

        // 逐日迴圈之前就失敗：每一天都要記成 failed，總表才不會把這幾天當成沒有逐日統計的舊紀錄去猜
        ctx.RunRecorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"))
            .GetRun(ctx.RunRecorder.RunId);
        Assert.NotNull(run!.PrtgDays);
        Assert.Equal(days.Select(d => d.Date), run.PrtgDays!.Select(s => s.Date));
        Assert.All(run.PrtgDays, s => Assert.Equal(BatchRun.PrtgOutcomeFailed, s.Outcome));
    }

    /// <summary>
    /// 就緒訊號**必須排在 `prtg-done` 之前**：AI 排程據此決定「當日待補現在可不可以判讀」，
    /// 排在收尾之後等於白做——那時整條路徑已經結束了。
    /// </summary>
    [Fact]
    public async Task 就緒訊號排在收尾訊號之前()
    {
        var (ctx, _, progress, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null);

        var readyAt = progress.Phases.IndexOf(RunPhases.PrtgFindingsReady);
        var doneAt = progress.Phases.IndexOf(RunPhases.PrtgDone);

        Assert.True(readyAt >= 0 && doneAt >= 0);
        Assert.True(readyAt < doneAt, "prtg-findings-ready 必須早於 prtg-done");
    }

    [Fact]
    public async Task 停用磁碟趨勢規則時不產生正式finding且仍正常宣告就緒()
    {
        Assert.DoesNotContain(KnownIssueCatalog.Rules, r =>
            r.PrtgRuleCode == PrtgDiskRuleDecision.RuleCode && r.PrtgSensorCategory == PrtgSensorCategories.Disk);
        Assert.Contains(KnownIssueSeed.CreateRules(), r =>
            r.PrtgRuleCode == PrtgDiskRuleDecision.RuleCode && r.PrtgSensorCategory == PrtgSensorCategories.Disk && !r.Enabled);

        // 無效 scheme 讓結構擷取階段立即失敗，後續規則與發布流程仍會繼續，不呼叫 PRTG。
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "ftp://invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
        });

        var (ctx, console, progress, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null);

        Assert.True(registry.IsReady);
        Assert.DoesNotContain(console.Lines, line => line.Contains("磁碟趨勢正式評估完成"));
        Assert.True(progress.Phases.IndexOf(RunPhases.PrtgFindingsReady) < progress.Phases.IndexOf(RunPhases.PrtgDone));
    }

    /// <summary>可控的同步閘門：回報執行中，被等待後轉為閒置並記錄呼叫次數。</summary>
    private sealed class FakeStructureSyncGate : IPrtgStructureSyncGate
    {
        private bool _running;

        public FakeStructureSyncGate(bool running) => _running = running;

        public int WaitCalls { get; private set; }

        public bool IsRunning => _running;

        /// <summary>false＝模擬等到上限仍未結束（對方卡住）。</summary>
        public bool ResultToReturn { get; set; } = true;

        public Task<bool> WaitUntilIdleAsync(CancellationToken ct)
        {
            WaitCalls++;
            _running = false;
            return Task.FromResult(ResultToReturn);
        }
    }

    /// <summary>
    /// 等到上限對方仍未結束時，本趟**不得**跳過結構同步——鏡像不是新的。
    /// 少了這條，PRTG 卡住的那一晚鏡像沒更新，畫面與執行紀錄卻都顯示正常。
    /// </summary>
    [Fact]
    public async Task 等待手動同步逾時則本趟自行同步結構()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, _, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: true) { ResultToReturn = false };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: gate);

        Assert.Equal(1, gate.WaitCalls);
        Assert.Contains(console.Lines, l => l.Contains("手動同步未成功結束"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("沿用剛更新的鏡像結構"));
        // 真的去爬結構了（第一階段的開場訊息在 HTTP 呼叫之前就印）
        Assert.Contains(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
    }

    /// <summary>
    /// 手動同步進行中時，PRTG 日路徑要先等它，並在執行輸出說明原因、送出等待 phase
    /// （docs/PRTG-SPEC.md §5a）。畫面上看不到原因的話，那條軌會像是卡死。
    /// </summary>
    [Fact]
    public async Task 手動同步進行中時先等待並送出等待訊號()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, progress, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: true);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: gate);

        Assert.Equal(1, gate.WaitCalls);
        Assert.Contains(RunPhases.PrtgWaitSync, progress.Phases);
        Assert.Contains(console.Lines, l => l.Contains("等它完成後再繼續"));
        Assert.Contains(console.Lines, l => l.Contains("沿用剛更新的鏡像結構"));

        // 等完之後**真的沒有重新爬結構**：結構同步的第一階段會印「開始同步 PRTG 裝置結構鏡像」，
        // 跳過時走的是讀鏡像那條路，完全不進那三個階段。
        Assert.DoesNotContain(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
        // 但對應照做（對昨天）——跳過的只有結構同步這一步
        Assert.Contains(console.Lines, l => l.Contains("對應完成"));
    }

    /// <summary>閘門閒置（或根本沒接上）時行為與沒有這個機制時完全相同。</summary>
    [Fact]
    public async Task 手動同步未執行時不等待也不送等待訊號()
    {
        var (ctx, console, progress, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: false);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: gate);

        Assert.Equal(0, gate.WaitCalls);
        Assert.DoesNotContain(RunPhases.PrtgWaitSync, progress.Phases);
        Assert.DoesNotContain(console.Lines, l => l.Contains("等它完成後再繼續"));
    }

    /// <summary>
    /// 對照組：沒有手動同步時本趟照常爬結構，因此連不上的位址會印出擷取失敗。
    /// 少了這一條，上面那條的「沒有失敗訊息」可能只是因為整段根本沒跑到。
    /// </summary>
    [Fact]
    public async Task 沒有手動同步時仍會嘗試結構同步()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, _, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: null);

        Assert.Contains(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
    }

    /// <summary>裝置結構本趟沒有成功更新：仍依既有鏡像重算最新日對應並說明。</summary>
    [Fact]
    public async Task 裝置同步失敗時仍依既有鏡像重算對應()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-MIRROR", Active = true, IpAddress = "192.168.1.150" });
        _backend.PrtgStore().UpsertDevices(new[] { new PrtgDeviceRow { Objid = 77, Name = "SRV-MIRROR", Ip = "192.168.1.150" } }, DateTime.Now);

        var (ctx, console, _, _) = CreateContext();
        var newest = DateTime.Today.AddDays(-1);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { newest }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: null);

        Assert.Contains(console.Lines, l => l.Contains("主機對應依既有鏡像重算"));
        var row = Assert.Single(_backend.PrtgStore().GetHostMapForDate(newest), r => r.DeviceObjid == 77);
        Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
        Assert.Equal(host.HostId, row.HostId);
    }

    /// <summary>手動同步剛成功更新鏡像（本趟跳過結構同步）：仍要重算對應，不印沿用訊息。</summary>
    [Fact]
    public async Task 跳過結構同步時仍重算對應()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, _, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: true);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: gate);

        Assert.Contains(console.Lines, l => l.Contains("對應完成"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("主機對應依既有鏡像重算"));
    }

    /// <summary>
    /// 保守策略下：不執行觸發式取數、印出保守說明與策略狀態，其餘階段（finding、done）照跑。
    /// </summary>
    [Fact]
    public async Task 保守策略_不執行觸發式取數_印出策略狀態與略過說明()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, progress, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null);

        Assert.True(registry.IsReady);
        // 印出策略狀態行
        Assert.Contains(console.Lines, l => l.Contains("PRTG 取數策略：保守（快照間隔 15 分鐘）。"));
        // 印出保守策略略過說明
        Assert.Contains(console.Lines, l => l.Contains("取數策略為保守，夜間不逐顆查詢歷史值，數值由快照供應。"));
        // 不應包含觸發式取數階段
        Assert.DoesNotContain(RunPhases.PrtgTriggered, progress.Phases);
        // 其餘階段照常完成
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);
    }

    /// <summary>
    /// 激進策略下：進入觸發式取數階段、印出激進策略狀態。
    /// </summary>
    [Fact]
    public async Task 激進策略_進入觸發式取數_印出激進策略狀態()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, progress, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null);

        // 印出激進策略狀態行
        Assert.Contains(console.Lines, l => l.Contains("PRTG 取數策略：激進（快照間隔 5 分鐘）。"));
        // 激進策略會進入觸發式取數階段
        Assert.Contains(RunPhases.PrtgTriggered, progress.Phases);
        // 不含保守策略跳過訊息
        Assert.DoesNotContain(console.Lines, l => l.Contains("取數策略為保守"));
    }

    [Fact]
    public void 夜間同步狀態_全部成功_回傳夜間來源的完整狀態()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(
            Ok: 5,
            Conflict: 1,
            Unmatched: 2,
            SkippedNoIp: 3,
            Manual: 4,
            SkippedExcluded: 6,
            SkippedManualSibling: 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.NotNull(status);
        Assert.Equal("nightly", status.Source);
        Assert.True(status.Success);
        Assert.Null(status.ErrorMessage);
        Assert.Equal(day, status.MapDate);
        Assert.Equal(now, status.CompletedAt);
        Assert.Equal(42.5, status.ElapsedSeconds);
        Assert.Equal(12, status.Devices);
        Assert.Equal(34, status.Sensors);
        Assert.Equal(5, status.MapOk);
        Assert.Equal(4, status.MapManual);
        Assert.Equal(1, status.MapConflict);
        Assert.Equal(2, status.MapUnmatched);
        Assert.Equal(3, status.MapSkippedNoIp);
        Assert.Equal(6, status.MapSkippedExcluded);
        Assert.Equal(7, status.MapSkippedManualSibling);
    }

    [Fact]
    public void 夜間同步狀態_有失敗階段_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 1);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_擷取擲例外_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: true,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_跳過結構同步_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: true,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_對應失敗_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: null,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public async Task 夜間取數連不上PRTG_不覆寫既有同步狀態()
    {
        var store = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var fixedTime = new DateTime(2026, 3, 31, 12, 0, 0);
        store.Update(s =>
        {
            s.CompletedAt = fixedTime;
            s.Success = true;
            s.Source = PrtgStructureSyncStatus.SourceManual;
            s.Devices = 10;
            s.Sensors = 50;
            s.MapOk = 8;
        });

        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, _, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null, structureSyncGate: null);

        var after = store.GetOrNull();
        Assert.NotNull(after);
        Assert.Equal(fixedTime, after.CompletedAt);
        Assert.True(after.Success);
        Assert.Equal("manual", after.Source);
        Assert.Equal(10, after.Devices);
        Assert.Equal(50, after.Sensors);
        Assert.Equal(8, after.MapOk);
    }

    [Fact]
    public void BuildPrtgDays_回望天數與範圍決定PRTG處理哪些天()
    {
        var today = new DateTime(2026, 9, 15);
        var retention = new RetentionOptions { RetentionDays = 30 };

        // request.BackfillOverride = null 時回傳 [today.AddDays(-1)]
        var daysNull = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = null }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, daysNull);

        // request.BackfillOverride = 1 時回傳 [today.AddDays(-1)]
        var days1 = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 1 }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, days1);

        // request.BackfillOverride = 5 時回傳由近到遠 5 天
        var days5 = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 5 }, retention, today);
        Assert.Equal(5, days5.Count);
        Assert.Equal(today.AddDays(-1), days5[0]);
        Assert.Equal(today.AddDays(-2), days5[1]);
        Assert.Equal(today.AddDays(-3), days5[2]);
        Assert.Equal(today.AddDays(-4), days5[3]);
        Assert.Equal(today.AddDays(-5), days5[4]);

        // request.BackfillOverride 超過保留期上限時截斷至保留天數上限
        var expectedMax = LogForesight.Core.Models.NetiqOptions.GetEffectiveBackfillDaysLimit(retention.RetentionDays);
        var daysExceed = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 50 }, retention, today);
        Assert.Equal(expectedMax, daysExceed.Count);
        Assert.Equal(today.AddDays(-1), daysExceed[0]);
        Assert.Equal(today.AddDays(-expectedMax), daysExceed[^1]);

        // request.Scope = NetiqHosts 時回傳 [today-1]（指定主機更新只查昨天）
        var daysNetiq = AnalysisOrchestrator.BuildPrtgDays(
            new RunRequest { Scope = RunScope.NetiqHosts, BackfillOverride = 5 }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, daysNetiq);

        // 只跑本機（LocalOnly）與指定主機更新一樣只處理昨天：對一台主機的更新不該觸發全機房 N 天的 PRTG 查詢
        var localOnly = AnalysisOrchestrator.BuildPrtgDays(
            new RunRequest { Scope = RunScope.LocalOnly, BackfillOverride = 30 }, new RetentionOptions(), today);
        Assert.Single(localOnly);
        Assert.Equal(today.AddDays(-1), localOnly[0]);
    }

    [Fact]
    public async Task 多日執行逐日發佈與單一FindingsReady()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, progress, registry) = CreateContext();
        var day3 = DateTime.Today.AddDays(-3);
        var day2 = DateTime.Today.AddDays(-2);
        var day1 = DateTime.Today.AddDays(-1);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { day1, day2, day3 }, Task.CompletedTask, hostIds: null, guard: null);

        // progress 收到 RunPhases.PrtgDateRange
        Assert.Contains(progress.Reports, r => r.Phase == RunPhases.PrtgDateRange && r.Done == 3 && r.Total == 0);

        // 登錄簿逐日已發佈
        Assert.True(registry.IsPublished(day1));
        Assert.True(registry.IsPublished(day2));
        Assert.True(registry.IsPublished(day3));

        // RunPhases.PrtgFindingsReady 只出現一次
        Assert.Equal(1, progress.Phases.Count(p => p == RunPhases.PrtgFindingsReady));

        // 且在 DateRange 之後
        var rangeIndex = progress.Phases.IndexOf(RunPhases.PrtgDateRange);
        var readyIndex = progress.Phases.IndexOf(RunPhases.PrtgFindingsReady);
        Assert.True(readyIndex > rangeIndex);
    }

    [Fact]
    public async Task 過去日查無對應表時安全跳過()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day1 = DateTime.Today.AddDays(-2);
        var day2 = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        // 兩天都有 state change
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddHours(2), Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddHours(2), Status = "Down" }
        });

        var (ctx, console, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day2, day1 }, Task.CompletedTask, hostIds: null, guard: null);

        // prtgConsole 輸出包含「無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶」
        Assert.Contains(console.Lines, l => l.Contains("無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶"));

        // day1 的 finding 不進行主機關聯（登錄簿為空清單）
        Assert.Empty(registry.For(host.HostId, day1));

        // 但不影響 day2 正常評估
        Assert.NotEmpty(registry.For(host.HostId, day2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 過去日沿用最新歷史對應表(bool hostActive)
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day3 = DateTime.Today.AddDays(-3);
        var day1 = DateTime.Today.AddDays(-2);
        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TEST", Active = hostActive, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        // 準備 day-3 的 HostMap，但無 day-1 的 HostMap
        prtgStore.ReplaceHostMapForDate(day3, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day3,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        // day-1 發生持續 Down
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day1.Date.AddHours(2),
                Status = "Down"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day, day1 }, Task.CompletedTask, hostIds: null, guard: null);

        // day-1 評估時成功取用 day-3 的對應表，找到主機並完成 finding 歸屬
        var findings = registry.For(host.HostId, day1);
        if (!hostActive)
        {
            Assert.Empty(findings);
            Assert.Empty(_backend.PrtgObservationStore().ReadPage([host.HostId], day1, day1, 0, 100));
            return;
        }
        Assert.Single(findings);
        Assert.Equal("prtg:down:2001:test-source:test-resource-2001", findings[0].EventKey);
    }

    [Fact]
    public async Task 鏡像Unknown不能冒稱可信沉默故障()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today;

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-UNKNOWN", Active = true, IpAddress = "192.168.1.102" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        // 準備未暫停且全 Unknown 的 device 2
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 2, Name = "SRV-UNKNOWN", Ip = "192.168.1.102" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 3001, DeviceObjid = 2, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Unknown" }
        }, now);

        // 過去日 day1 的 HostMap
        prtgStore.ReplaceHostMapForDate(day1, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day1,
                DeviceObjid = 2,
                HostId = host.HostId,
                HostName = "SRV-UNKNOWN",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day2, day1 }, Task.CompletedTask, hostIds: null, guard: null);

        // day1 不產生 silent finding
        Assert.DoesNotContain(registry.For(host.HostId, day1), f => f.EventKey.StartsWith("prtg:silent"));

        // 鏡像 Unknown 只表示資料品質，不能據此發布故障。
        Assert.Empty(registry.For(host.HostId, day2));
    }

    [Fact]
    public async Task 保守策略多日提示回填()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, _, _) = CreateContext();
        var days = new[]
        {
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-3)
        };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            days, Task.CompletedTask, hostIds: null, guard: null);

        Assert.Contains(console.Lines, l => l.Contains("其餘 2 天的 PRTG 數值不在立即執行內取，請用排程作業頁的「開始回填」。"));
    }

    [Fact]
    public async Task 多日執行逐日回報PRTG日期範圍與當前天次()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, progress, _) = CreateContext();
        var days = new[]
        {
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-3)
        };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            days, Task.CompletedTask, hostIds: null, guard: null);

        var dateRangeReports = progress.Reports.Where(r => r.Phase == RunPhases.PrtgDateRange).ToList();
        Assert.Equal(4, dateRangeReports.Count);
        // 第一次為迴圈前：(3, 0)
        Assert.Equal(3, dateRangeReports[0].Done);
        Assert.Equal(0, dateRangeReports[0].Total);
        // 後三次為逐日迴圈開始時：(3, 1), (3, 2), (3, 3)
        Assert.Equal(3, dateRangeReports[1].Done);
        Assert.Equal(1, dateRangeReports[1].Total);
        Assert.Equal(3, dateRangeReports[2].Done);
        Assert.Equal(2, dateRangeReports[2].Total);
        Assert.Equal(3, dateRangeReports[3].Done);
        Assert.Equal(3, dateRangeReports[3].Total);
    }

    [Fact]
    public async Task 評估同代碼多條規則時在Console輸出警告()
    {
        var rules = KnownIssueSeed.CreateRules().ToList();
        rules.Add(new KnownIssueRule
        {
            Id = "a-custom-down",
            Platform = "prtg",
            PrtgRuleCode = "down",
            PrtgThreshold = 60,
            Enabled = true,
            Severity = IssueSeverity.Critical,
            ElevatesDayRisk = true,
            Description = "Custom duplicate down rule"
        });
        KnownIssueCatalog.Initialize(rules);

        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, _, _) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, hostIds: null, guard: null);

        Assert.Contains(console.Lines, l => l.Contains("規則代碼 down 有多條啟用規則，採用 a-custom-down"));
    }

    [Fact]
    public async Task Site範圍規則型抑制使PRTG發佈簽章Suppressed且追加後不拉高日風險()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day.Date.AddHours(2),
                Status = "Down"
            }
        });

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = RiskLevels.Low,
            RiskBasis = "baseline"
        });

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                RuleId = "builtin-prtg-down",
                Scope = SuppressionScopes.Site,
                Reason = "測試抑制"
            }
        });

        var (ctx, console, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var findings = registry.For(host.HostId, day);
        var downSig = Assert.Single(findings);
        Assert.Equal("builtin-prtg-down", downSig.RuleId);
        Assert.True(downSig.Suppressed);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Contains(record.TopIssues, i => i.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.DoesNotContain("prtg:down", record.RiskBasis ?? string.Empty);
        Assert.Contains(console.Lines, l => l.Contains("已抑制 1 筆"));
    }

    /// <summary>問題靜音經包裝層在 PRTG 路徑生效：紀錄日（day）落在靜音區間→finding Suppressed、不拉高日風險；
    /// 區間只涵蓋今天（不含 day）→不標（以紀錄日判定，不以執行時間）</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 問題靜音以紀錄日判定_PRTG發佈簽章Suppressed與否(bool dayInInterval)
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);
        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day, DeviceObjid = 1, HostId = host.HostId, HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now
            }
        });
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(2), Status = "Down" }
        });

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        // Source 以不同大小寫存入，驗證不分大小寫比對
        new IssueOwnerStore(_backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = "prtg:down", EventId = 0,
            Mutes = new List<MuteInterval>
            {
                new() { From = dayInInterval ? day : DateTime.Today, To = DateTime.Today, Reason = "機房搬遷", ByAccount = "admin" }
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(ctx, _backend, hostStore, new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var downSig = Assert.Single(registry.For(host.HostId, day));
        Assert.Equal("PRTG:down", downSig.Source);
        Assert.Equal(0, downSig.EventId);
        Assert.Equal(dayInInterval, downSig.Suppressed);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        if (dayInInterval)
            Assert.Equal(RiskLevels.Low, record.RiskLevel);
        else
            Assert.NotEqual(RiskLevels.Low, record.RiskLevel);
        // 靜音不寫進抑制 blob
        Assert.DoesNotContain(new SuppressionStore(_backend.Blob("suppressions")).LoadAll(),
            s => s.TargetType == SuppressionTargetTypes.IssueMute);
    }

    [Fact]
    public async Task Group範圍規則型抑制當主機不在群組時不抑制且日風險上調()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost
        {
            Source = "netiq",
            HostName = "SRV-TEST",
            Active = true,
            IpAddress = "192.168.1.101",
            GroupIds = new List<long> { 101 }
        });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day.Date.AddHours(2),
                Status = "Down"
            }
        });

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = RiskLevels.Low,
            RiskBasis = "baseline"
        });

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                RuleId = "builtin-prtg-down",
                Scope = SuppressionScopes.Group,
                HostGroupId = 999,
                Reason = "群組 999 抑制"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var findings = registry.For(host.HostId, day);
        var downSig = Assert.Single(findings);
        Assert.False(downSig.Suppressed);

        // 磁碟 sensor 的 down 走不限分類規則（seed v7 非重大、嚴重度高）→ 日風險上調到「中」
        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Equal(RiskLevels.Medium, record.RiskLevel);
        Assert.Equal("prtg:down", record.RiskBasis);
    }

    [Fact]
    public async Task 簽章型抑制只抑制該sensor同規則另一sensor不受影響()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk C", SensorType = "SNMP Disk Free", Status = "Down" },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1, Name = "Disk D", SensorType = "SNMP Disk Free", Status = "Down" }
        }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(2), Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2002, ChangedAt = day.Date.AddHours(2), Status = "Down" }
        });

        var targetSig = new LogIssueSignature
        {
            LogName = PrtgFindingMapper.PrtgLogName,
            Source = "PRTG:down",
            EventId = 0,
            EntryType = System.Diagnostics.EventLogEntryType.Warning,
            EventKey = "prtg:down:2001:test-source:test-resource-2001"
        };
        var targetKey = IssueSignatureKey.For(targetSig);

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Signature,
                SignatureKey = targetKey,
                Scope = SuppressionScopes.Site,
                Reason = "僅抑制 sensor 2001"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var findings = registry.For(host.HostId, day);
        Assert.Equal(2, findings.Count);
        var sig2001 = findings.Single(f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        var sig2002 = findings.Single(f => f.EventKey == "prtg:down:2002:test-source:test-resource-2002");

        Assert.True(sig2001.Suppressed);
        Assert.False(sig2002.Suppressed);
    }

    private void EnableConservativePrtgWithDefaultWhitelist()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>(SystemSettings.DefaultPrtgSensorTypeWhitelist);
        });
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("evidence")]
    [InlineData("verification")]
    [InlineData("host")]
    public async Task 磁碟候選第二頁映射或metadata改變_丟棄前頁結果且標記部分完成(string mutationKind)
    {
        EnableConservativePrtgWithDefaultWhitelist();
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules().Select(r =>
            r.PrtgRuleCode == PrtgDiskRuleDecision.RuleCode && r.PrtgSensorCategory == PrtgSensorCategories.Disk
                ? r.CloneForSeedOverwrite(enabled: true) : r).ToList());
        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-DISK-PAGE", Active = true, IpAddress = "192.168.1.141" });
        var changedHost = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-DISK-PAGE-CHANGED", Active = true, IpAddress = "192.168.1.142" });

        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var baseUrl = $"http://127.0.0.1:{port}/";
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgUrl = baseUrl);
        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        var deviceRows = Enumerable.Range(1, 100).Select(id => new PrtgDeviceRow
        {
            Objid = id, Name = $"SRV-DISK-PAGE-{id}", Ip = id == 1 ? host.IpAddress : null
        }).ToArray();
        prtgStore.UpsertDevices(deviceRows, now);
        var sensors = new List<PrtgSensorRow>
        {
            new() { Objid = 4000, DeviceObjid = 1, Name = "Disk C:", SensorType = "SNMP Disk Free", Unit = "%", Status = "Up", Category = PrtgSensorCategories.Disk }
        };
        sensors.AddRange(Enumerable.Range(1, 99).Select(i => new PrtgSensorRow
        {
            Objid = 4000 + i, DeviceObjid = 1 + i, Name = $"Disk {i}", SensorType = "SNMP Disk Free",
            Status = "Up", Category = PrtgSensorCategories.Disk
        }));
        // 第二頁候選使用另一台裝置；第一頁裝置映射變更不能被第二頁的局部查詢掩蓋。
        sensors.Add(new PrtgSensorRow { Objid = 4100, DeviceObjid = 100, Name = "Disk C: duplicate candidate", SensorType = "SNMP Disk Free", Unit = "%", Status = "Up", Category = PrtgSensorCategories.Disk });
        prtgStore.UpsertSensors(sensors, now);

        var persistedPoints = new List<PrtgValueRow>();
        var hostMapDates = new List<PrtgHostMapRow>();
        for (var offset = -27; offset <= 0; offset++)
        {
            var mapDay = day.AddDays(offset);
            hostMapDates.Add(new PrtgHostMapRow
            {
                MapDate = mapDay, DeviceObjid = 1, HostId = host.HostId, HostName = host.HostName,
                MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            });
            for (var device = 2; device <= 100; device++)
                hostMapDates.Add(new PrtgHostMapRow
                {
                    MapDate = mapDay, DeviceObjid = device, HostId = host.HostId,
                    HostName = host.HostName, MapStatus = PrtgMapStatus.Ok, CreatedAt = now
                });
            var available = Math.Max(0.5, 95.0 - (offset + 27) * 3.5);
            for (var hour = 0; hour < 12; hour++)
                persistedPoints.Add(new PrtgValueRow
                {
                    SensorObjid = 4000, PeriodStart = mapDay.AddHours(hour), AvgValue = available,
                    MinValue = available, MaxValue = available, Coverage = 100, Quality = PrtgDataQuality.Ok, CreatedAt = now
                });
        }
        for (var offset = -27; offset <= 0; offset++)
            prtgStore.ReplaceHostMapForDate(day.AddDays(offset), hostMapDates.Where(m => m.MapDate == day.AddDays(offset)).ToArray());
        prtgStore.UpsertValues(persistedPoints);

        var (ctx, console, _, registry) = CreateContext();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(p =>
        {
            p.CoreSystemId = "fixture-core"; p.SourceGeneration = "test-source";
            p.SourceTimeZoneId = TimeZoneInfo.Local.Id; p.SourceCultureName = "en-US";
            p.EndpointHint = LogForesight.Core.Persistence.Sql.EfPrtgObservationStore.SourceHintFor(baseUrl);
            p.ValidFrom = DateTimeOffset.Now.AddDays(-31); p.Revision = "candidate-page-failure";
            p.HostIds = new List<long> { host.HostId }; p.SensorIds = sensors.Select(s => s.Objid).ToList();
        });

        const long deviceId = 1;
        var mappingRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var timeline = new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + 4000));
        var identityFingerprint = $"{deviceId}|SNMP Disk Free|2000|map:{mappingRevision}";
        var diskFingerprint = JsonSerializer.Serialize(new
        { ChannelIdentifier = "5", ChannelName = "Free", Unit = "%", Scale = (double?)1.0, Direction = "descending-danger" });
        timeline.Update(e =>
        {
            e.SensorId = 4000; e.HostId = host.HostId; e.SourceGeneration = "test-source";
            e.ResourceGeneration = "test-resource-4000"; e.IdentityFingerprint = identityFingerprint;
            e.MappingRevision = mappingRevision; e.ValidFrom = DateTimeOffset.Now.AddDays(-31);
            e.Accept(DateTimeOffset.Now.AddDays(-31), DateTimeOffset.Now, Array.Empty<PrtgTimedState>());
            e.DiskSemanticValidFrom = DateTimeOffset.Now.AddDays(-31); e.DiskSemanticCheckedAt = DateTimeOffset.Now;
            e.DiskSemanticFingerprint = diskFingerprint;
        });
        new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).ConfirmManually(
            new PrtgDiskSemanticContext(4000, deviceId, host.HostId, "SNMP Disk Free", "5", "Free", "%", 1, "descending-danger"),
            1, "R10 consumer failure fixture", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
        new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(
            new PrtgDiskVerificationResult(4000, deviceId, host.HostId, "SNMP Disk Free", "Verified", "fixture",
                "5", "Free", "%", 1, "descending-danger", 5, true, DateTime.UtcNow, day, PrtgDiskAssessmentService.ParserSemanticVersion));

        var diskRule = KnownIssueCatalog.Rules.Single(r => r.PrtgRuleCode == PrtgDiskRuleDecision.RuleCode &&
            r.PrtgSensorCategory == PrtgSensorCategories.Disk && r.Enabled);
        var baselineBatch = new PrtgDiskAssessmentService(prtgStore, hostStore,
            new SystemSettingsStore(_backend.Blob("system_settings")),
            new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)))
            .Assess(DateOnly.FromDateTime(day), diskRule, PrtgDiskDecisionMode.Formal, 100, 0,
                selectedSensorObjids: sensors.Select(s => s.Objid).ToArray());
        var baselineFinding = Assert.Single(baselineBatch.Rows, r => r.SensorObjid == 4000);
        Assert.True(baselineFinding.Decision.Finding is not null,
            $"Baseline candidate did not hit: exclusion={baselineFinding.Decision.Exclusion}; reason={baselineFinding.Decision.Reason}; readiness={baselineFinding.Readiness.Status}/{baselineFinding.Readiness.Reason}; semantic={baselineFinding.EvidenceValidity?.InvalidReason}; trend={baselineFinding.Decision.Trend?.Explanation}");
        var scopeRevisionBeforeRun = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();

        using var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        using var serverStop = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!serverStop.IsCancellationRequested)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync().WaitAsync(serverStop.Token); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                var content = request.Request.QueryString["content"];
                var id = long.TryParse(request.Request.QueryString["id"], out var parsedId) ? parsedId : 0;
                object response = content switch
                {
                    "sensors" => new { sensors = new[] { new { objid = id.ToString(), parentid = sensors.FirstOrDefault(s => s.Objid == id)?.DeviceObjid.ToString() ?? "1", type = "SNMP Disk Free", status = "Up", cumsince_raw = "2000" } } },
                    "messages" => new { messages = Array.Empty<object>() },
                    "channels" => new { channels = new[] { new { objid = "5", channel = "Free", unit = "%", scaling = 1 } } },
                    _ when request.Request.Url?.AbsolutePath.EndsWith("historicdata.json", StringComparison.OrdinalIgnoreCase) == true => new
                    {
                        histdata = id == 4000
                            ? persistedPoints.Where(p => p.PeriodStart.Date == day).Take(12)
                                .Select(p => (object)new { datetime_raw = p.PeriodStart.ToOADate(), value_raw = new[] { p.AvgValue!.Value } }).ToArray()
                            : Array.Empty<object>()
                    },
                    _ => new { sensors = Array.Empty<object>(), devices = Array.Empty<object>(), messages = Array.Empty<object>() }
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes, serverStop.Token);
                request.Response.Close();
            }
        });

        var pageCallbacks = 0;
        var trackedHostStore = new TrackingHostStore(hostStore);
        var getAllCallsAfterFirstCandidatePage = -1;
        var getAllCallsAtFinalCandidatePage = -1;
        var getAllCallsAfterHostMutation = -1;
        console.OnWriteLine = message =>
        {
            var firstPage = message.Contains("磁碟候選評估 100/101；已有趨勢 finding 1 筆，尚未提交判定。", StringComparison.Ordinal);
            var finalPage = message.Contains("磁碟候選評估 101/101；已有趨勢 finding 1 筆，尚未提交判定。", StringComparison.Ordinal);
            var finalPageBeforeFence = mutationKind == "host" && finalPage;
            if (firstPage) getAllCallsAfterFirstCandidatePage = trackedHostStore.GetAllCalls;
            if (finalPage) getAllCallsAtFinalCandidatePage = trackedHostStore.GetAllCalls;
            if (firstPage && mutationKind != "host" || finalPageBeforeFence)
            {
                pageCallbacks++;
                switch (mutationKind)
                {
                    case "mapping":
                        MapDeviceToHost(day, 1, changedHost);
                        break;
                    case "evidence":
                        new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).ConfirmManually(
                            new PrtgDiskSemanticContext(4000, deviceId, host.HostId, "SNMP Disk Free", "5", "Free", "%", 1, "descending-danger"),
                            1, "Metadata changed after first page.", DateTime.UtcNow,
                            PrtgDiskAssessmentService.ParserSemanticVersion);
                        break;
                    case "verification":
                        new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(
                            new PrtgDiskVerificationResult(4000, deviceId, host.HostId, "SNMP Disk Free", "Failed",
                                "Metadata changed after first page.", null, null, null, null, null, 0, null,
                                DateTime.UtcNow, day, PrtgDiskAssessmentService.ParserSemanticVersion));
                        break;
                    case "host":
                        hostStore.Upsert(new WebHost { HostName = host.HostName, Active = false });
                        getAllCallsAfterHostMutation = trackedHostStore.GetAllCalls;
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected test mutation: {mutationKind}");
                }
            }
        };
        try
        {
            await PrtgDailyPipeline.RunAsync(ctx, _backend, trackedHostStore, new[] { day }, Task.CompletedTask,
                hostIds: null, guard: null, structureSyncGate: new FakeStructureSyncGate(running: true) { ResultToReturn = true });
        }
        finally
        {
            serverStop.Cancel();
            listener.Close();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        }

        Assert.True(pageCallbacks == 1, string.Join(Environment.NewLine, console.Lines));
        Assert.Contains(console.Lines, line => line.Contains("磁碟候選評估 101/101；已有趨勢 finding 1 筆，尚未提交判定。", StringComparison.Ordinal));
        var expectedFence = mutationKind == "mapping"
            ? "日映射版本於磁碟候選快照處理期間改變"
            : mutationKind == "host"
                ? "主機授權或顯示名稱於磁碟候選快照處理期間改變"
                : "語意 metadata 於候選評估期間改變";
        Assert.Contains(console.Lines, line => line.Contains("磁碟趨勢評估失敗") && line.Contains(expectedFence));
        Assert.Equal(1, trackedHostStore.CapturePrtgSnapshotCalls); // one bounded immutable host capture for all disk pages
        Assert.True(getAllCallsAfterFirstCandidatePage >= 0);
        Assert.True(getAllCallsAtFinalCandidatePage >= 0);
        Assert.Equal(getAllCallsAfterFirstCandidatePage, getAllCallsAtFinalCandidatePage); // no GetAll between candidate page 1 and the final page/fence
        if (mutationKind == "host")
            Assert.Equal(getAllCallsAtFinalCandidatePage, getAllCallsAfterHostMutation);
        var expectedHostReadsByCaller = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PrtgDailyPipeline.SelectActivePilotHostIds"] = 1,
            ["PrtgScopeRevisionReader.Read"] = mutationKind == "host" ? 2 : 1,
            ["PrtgHostMapper.MapForDate"] = 1,
            ["PrtgHostMapper.BuildActiveHostIpLookup"] = 2,
            ["PrtgScopeDevices.Compute"] = 1,
            ["PrtgDailyPipeline.RunAsync"] = mutationKind == "host" ? 1 : 2 // host fence 在第二段前取消；其他案例會讀取抑制所需主機資訊
        };
        Assert.Equal(expectedHostReadsByCaller.OrderBy(x => x.Key),
            trackedHostStore.GetAllCallsByCaller.OrderBy(x => x.Key));
        Assert.Equal(expectedHostReadsByCaller.Values.Sum(), trackedHostStore.GetAllCalls);
        Assert.True(registry.IsPublished(day));
        Assert.Empty(registry.For(host.HostId, day));
        Assert.Empty(registry.For(changedHost.HostId, day));
        Assert.Null(timeline.Get().DiskIncidentStartedAt);
        Assert.Equal("test-resource-4000", timeline.Get().ResourceGeneration);
        Assert.Equal("test-source", timeline.Get().SourceGeneration);
        if (mutationKind != "mapping")
            Assert.Equal(scopeRevisionBeforeRun, _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion());

        ctx.RunRecorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"))
            .GetRun(ctx.RunRecorder.RunId);
        Assert.Equal(BatchRun.PrtgOutcomePartial, run!.PrtgOutcome);
        Assert.Equal(BatchRun.PrtgOutcomePartial, Assert.Single(run.PrtgDays!).Outcome);
    }

    private void MapDeviceToHost(DateTime day, long deviceObjid, WebHost host)
    {
        _backend.PrtgStore().ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = deviceObjid,
                HostId = host.HostId,
                HostName = host.HostName,
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task 預設白名單不含Ping_PingDown仍進規則評估產生Down(bool hasLogRecord, bool outsideSelectedScope)
    {
        EnableConservativePrtgWithDefaultWhitelist();
        Assert.DoesNotContain(SystemSettings.DefaultPrtgSensorTypeWhitelist,
            t => string.Equals(t, "Ping", StringComparison.OrdinalIgnoreCase));

        var today = DateTime.Today;
        var day = today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-PING", Active = true, IpAddress = "192.168.1.111" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-PING", Ip = "192.168.1.111" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        // 22:30 進入 Down，持續到午夜 90 分鐘（預設門檻 60）
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        if (hasLogRecord) hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { today, day }, Task.CompletedTask, hostIds: outsideSelectedScope ? Array.Empty<long>() : null, guard: null);

        if (outsideSelectedScope)
        {
            Assert.Empty(registry.For(host.HostId, day));
            Assert.Empty(_backend.PrtgObservationStore().ReadPage([host.HostId], day, day, 0, 100));
            Assert.Empty(hostRecordStore.ReadRecent(day, 1));
            return;
        }

        // 連通性分類 → 挑到 availability 規則（門檻 30、重大）→ 日風險「高」
        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Equal("builtin-prtg-down-availability", sig.RuleId);
        if (hasLogRecord)
            Assert.Equal(RiskLevels.High, Assert.Single(hostRecordStore.ReadRecent(day, 1)).RiskLevel);
        else
            Assert.Empty(hostRecordStore.ReadRecent(day, 1));
        var observation = Assert.Single(_backend.PrtgObservationStore().ReadPage([host.HostId], day, day, 0, 100));
        Assert.Equal(2001, observation.SensorObjid);
        Assert.Equal("test-source", observation.SourceGeneration);
    }

    [Fact]
    public async Task 非連通性sensorDown_走不限分類規則_日風險只到中()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-TRAFFIC", Active = true, IpAddress = "192.168.1.112" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TRAFFIC", Ip = "192.168.1.112" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Traffic", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Equal("builtin-prtg-down", sig.RuleId);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal(RiskLevels.Medium, Assert.Single(hostRecordStore.ReadRecent(day, 1)).RiskLevel);
    }

    [Fact]
    public async Task 同裝置Ping與TrafficDown保留各sensor世代證據()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var today = DateTime.Today;
        var day = today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-FOLD", Active = true, IpAddress = "192.168.1.112" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-FOLD", Ip = "192.168.1.112" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1, Name = "Port 1", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic },
            new PrtgSensorRow { Objid = 2003, DeviceObjid = 1, Name = "Port 2", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic }
        }, now);
        var downAt = day.Date.AddHours(22).AddMinutes(30);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = downAt, Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2002, ChangedAt = downAt, Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2003, ChangedAt = downAt, Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { today, day }, Task.CompletedTask, hostIds: null, guard: null);

        var signatures = registry.For(host.HostId, day);
        Assert.Contains(signatures, f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Contains(signatures, f => f.EventKey == "prtg:down:2002:test-source:test-resource-2002");
        Assert.Contains(signatures, f => f.EventKey == "prtg:down:2003:test-source:test-resource-2003");
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("已合併 0 筆"));
    }

    /// <summary>
    /// 兩段式的核心：回望 3 天、同一 sensor 三天都 Warning、資料庫沒有任何歷史。
    /// 最新一天處理的當下，較舊兩天還沒寫進資料庫，只靠第一段保存的本趟簽章才數得到第 3 次。
    /// </summary>
    [Fact]
    public async Task 多日回望同sensor連續Warning_最新日跨日標註並升級且就緒只送一次()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today.AddDays(-2);
        var day3 = DateTime.Today.AddDays(-3);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-WARN", Active = true, IpAddress = "192.168.1.121" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-WARN", Ip = "192.168.1.121" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "CPU", SensorType = "WMI CPU Load", Status = "Warning" }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day3.Date.AddMinutes(30), Status = "Warning" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddMinutes(30), Status = "Warning" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddMinutes(30), Status = "Warning" }
        });
        MapDeviceToHost(day3, 1, host);
        MapDeviceToHost(day2, 1, host);
        MapDeviceToHost(day1, 1, host);

        var (ctx, console, progress, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day1, day2, day3 }, Task.CompletedTask, hostIds: null, guard: null);

        var newestSig = Assert.Single(registry.For(host.HostId, day1), f => f.EventKey == "prtg:warning:2001:test-source:test-resource-2001");
        Assert.Contains("第 3 次，連續第 3 日", newestSig.SampleMessages[0]);
        Assert.Equal(IssueSeverity.High, newestSig.Severity);

        var middleSig = Assert.Single(registry.For(host.HostId, day2), f => f.EventKey == "prtg:warning:2001:test-source:test-resource-2001");
        Assert.Contains("第 2 次，連續第 2 日", middleSig.SampleMessages[0]);
        Assert.Equal(IssueSeverity.Medium, middleSig.Severity);

        var oldestSig = Assert.Single(registry.For(host.HostId, day3), f => f.EventKey == "prtg:warning:2001:test-source:test-resource-2001");
        Assert.DoesNotContain("近 14 日", oldestSig.SampleMessages[0]);

        Assert.Equal(1, progress.Phases.Count(p => p == RunPhases.PrtgFindingsReady));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day1:yyyy-MM-dd}）") && l.Contains("跨日升級 1 筆、長期 Down 0 筆"));
    }

    /// <summary>
    /// 重跑時本趟重評的日期只認本趟結果：資料庫裡前一天留有上一趟寫入的 warning（這一趟那天已不成立），
    /// 不得被算成歷史，最新一天不應出現「第 2 次」。
    /// </summary>
    [Fact]
    public async Task 回望重跑時本趟重評日期不採用資料庫的舊命中()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today.AddDays(-2);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-RERUN", Active = true, IpAddress = "192.168.1.123" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-RERUN", Ip = "192.168.1.123" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "CPU", SensorType = "WMI CPU Load", Status = "Warning" }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddMinutes(10), Status = "Up" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddMinutes(30), Status = "Warning" }
        });
        MapDeviceToHost(day2, 1, host);
        MapDeviceToHost(day1, 1, host);

        // 上一趟在 day2 留下的 warning（門檻或狀態改過後這一趟已不成立）
        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day2, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:warning", EventId = 0,
                    EntryType = System.Diagnostics.EventLogEntryType.Warning, EventKey = "prtg:warning:2001:test-source:test-resource-2001",
                    Count = 1, Severity = IssueSeverity.Medium
                }
            }
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day1, day2 }, Task.CompletedTask, hostIds: null, guard: null);

        Assert.Empty(registry.For(host.HostId, day2));
        var sig = Assert.Single(registry.For(host.HostId, day1), f => f.EventKey == "prtg:warning:2001:test-source:test-resource-2001");
        Assert.DoesNotContain("近 14 日", sig.SampleMessages[0]);
    }

    /// <summary>
    /// 長期 Down 仍維持故障風險：資料庫已有連續 13 天的 down（重大規則 availability），
    /// 當日第 14 天仍帶重大旗標，執行輸出計入長期 Down。
    /// </summary>
    [Fact]
    public async Task 資料庫已有連續13日Down_當日仍維持故障風險()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-DEAD", Active = true, IpAddress = "192.168.1.122" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-DEAD", Ip = "192.168.1.122" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(1), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        for (var n = 1; n <= 13; n++)
        {
            hostRecordStore.Append(new DailyAnalysisRecord
            {
            LogSource = AnalysisLogSource.Netiq,
                Date = day.AddDays(-n),
                HostId = host.HostId,
                Host = host.HostName,
                RiskLevel = RiskLevels.High,
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:down", EventId = 0,
                        EntryType = System.Diagnostics.EventLogEntryType.Warning, EventKey = "prtg:down:2001:test-source:test-resource-2001",
                        Count = 1, Severity = IssueSeverity.High, ElevatesDayRisk = true
                    }
                }
            });
        }
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Equal("builtin-prtg-down-availability", sig.RuleId);
        Assert.True(sig.ElevatesDayRisk);
        Assert.Contains("已連續 14 日，故障尚未恢復，請確認處置狀態", sig.SampleMessages[0]);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Contains(console.Lines, l => l.Contains("長期 Down 1 筆"));
    }

    /// <summary>
    /// 端到端：評估帶出 sensor 分類 → 映射進簽章 → 補追加時與事件日誌的非預期關機佐證，
    /// 執行輸出計入「跨來源佐證（補追加階段）」。
    /// </summary>
    [Fact]
    public async Task 補追加時事件日誌非預期關機與PingDown佐證_寫入關聯欄位並計入執行輸出()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-OUTAGE", Active = true, IpAddress = "192.168.1.123" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-OUTAGE", Ip = "192.168.1.123" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Count = 1 }
            }
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001:test-source:test-resource-2001");
        Assert.Equal(PrtgSensorCategories.Availability, sig.PrtgSensorCategory);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Equal(CorrelationPatternIds.PrtgOutageCorroborated, Assert.Single(record.CorrelationAlertRefs).PatternId);
        Assert.StartsWith("【關機與監測異常同日訊號】", Assert.Single(record.CorrelationAlerts));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("跨來源佐證（補追加階段）1 筆"));
    }

    /// <summary>
    /// 關聯抑制（TargetType=Correlation）在 PRTG 路徑發佈時一併算進登錄簿：
    /// 佐證模式被抑制時只進已抑制清單、不進關聯告警、不計入執行輸出。
    /// </summary>
    [Fact]
    public async Task 佐證模式被關聯抑制時登錄簿帶出集合且補追加只進已抑制清單()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { Source = "netiq", HostName = "SRV-OUTAGE2", Active = true, IpAddress = "192.168.1.124" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-OUTAGE2", Ip = "192.168.1.124" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Count = 1 }
            }
        });

        new SuppressionStore(_backend.Blob("suppressions")).SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Correlation,
                CorrelationPatternId = CorrelationPatternIds.PrtgOutageCorroborated,
                Scope = SuppressionScopes.Site,
                Reason = "機房例行斷電演練"
            }
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, hostIds: null, guard: null);

        Assert.Contains(CorrelationPatternIds.PrtgOutageCorroborated, registry.SuppressedPatternIdsFor(host.HostId, day));

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Empty(record.CorrelationAlerts);
        Assert.StartsWith("【關機與監測異常同日訊號】", Assert.Single(record.SuppressedCorrelationAlerts));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("跨來源佐證（補追加階段）0 筆"));
    }
}

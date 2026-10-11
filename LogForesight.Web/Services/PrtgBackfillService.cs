using LogForesight.Core;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>PRTG 歷史回填的進度快照。</summary>
public record PrtgBackfillProgress(
    int DaysDone, int DaysTotal, DateTime? CurrentDate,
    int SensorsDone, int SensorsTotal,
    int StateChangesRead, int StateChangesTotal, bool ReadingStateChanges);

public sealed record PrtgBackfillAccessSnapshot(string? RunId, string? RunKind,
    IReadOnlySet<long>? SelectedHostIds, PrtgBackfillStatusDto Status);

/// <summary>
/// PRTG 歷史回填的行程內單例執行狀態＋併發 1 的 gate。
/// 繼承自 <see cref="PrtgProbeRunState"/> 避免重複實作。
/// </summary>
public class PrtgBackfillRunState : PrtgProbeRunState
{
    private const int SelectedHostScopeMaxCount = 5;
    private readonly object _runIdentityLock = new();
    private string? _runId;
    private string? _runKind;
    private long[]? _selectedHostIds;

    public (string? RunId, string? RunKind) GetRunIdentity()
    {
        lock (_runIdentityLock) return (_runId, _runKind);
    }

    public PrtgBackfillAccessSnapshot GetAccessSnapshot()
    {
        lock (_runIdentityLock)
        {
            var state = Snapshot();
            var progress = GetProgress();
            var status = new PrtgBackfillStatusDto
            {
                IsRunning = state.IsRunning,
                StartedAt = state.StartedAt,
                CompletedAt = state.CompletedAt,
                Success = state.Success,
                LatestMessage = state.LatestMessage,
                Output = state.Output,
                DaysDone = progress.DaysDone,
                DaysTotal = progress.DaysTotal,
                CurrentDate = progress.CurrentDate,
                SensorsDone = progress.SensorsDone,
                SensorsTotal = progress.SensorsTotal,
                StateChangesRead = progress.StateChangesRead,
                StateChangesTotal = progress.StateChangesTotal,
                ReadingStateChanges = progress.ReadingStateChanges,
                Cancelled = Cancelled,
                RunId = _runId,
                RunKind = _runKind
            };
            return new PrtgBackfillAccessSnapshot(_runId, _runKind,
                _selectedHostIds == null ? null : _selectedHostIds.ToHashSet(), status);
        }
    }

    public bool TryBeginIdentifiedRun(string runKind, out CancellationToken token,
        IReadOnlyCollection<long>? selectedHostIds = null)
    {
        lock (_runIdentityLock)
        {
            if (!TryBeginRun(out token)) return false;
            _runId = Guid.NewGuid().ToString("N");
            _runKind = runKind;
            _selectedHostIds = string.Equals(runKind, "selected", StringComparison.Ordinal)
                ? SnapshotSelectedHostIds(selectedHostIds)
                : null;
            ResetProgress();
            return true;
        }
    }

    private static long[]? SnapshotSelectedHostIds(IReadOnlyCollection<long>? selectedHostIds)
    {
        if (selectedHostIds == null || selectedHostIds.Count == 0 || selectedHostIds.Count > 64) return null;
        var ids = new HashSet<long>();
        foreach (var id in selectedHostIds)
        {
            if (id <= 0) return null;
            ids.Add(id);
            if (ids.Count > SelectedHostScopeMaxCount) return null;
        }
        return ids.Count == 0 ? null : ids.Order().ToArray();
    }

    public bool TryCancelIdentifiedRun(string? runKind, string? runId)
    {
        lock (_runIdentityLock)
        {
            if (string.IsNullOrWhiteSpace(runId) || !string.Equals(_runKind, runKind, StringComparison.Ordinal) ||
                !string.Equals(_runId, runId, StringComparison.Ordinal)) return false;
            return TryCancel();
        }
    }

    public bool TryCancelSelected(string? runId)
        => TryCancelIdentifiedRun("selected", runId);

    private readonly object _progressLock = new();

    private int _daysDone;
    private int _daysTotal;
    private DateTime? _currentDate;
    private int _sensorsDone;
    private int _sensorsTotal;
    private int _stateChangesRead;
    private int _stateChangesTotal;
    private bool _readingStateChanges;

    public void ResetProgress()
    {
        lock (_progressLock)
        {
            _daysDone = 0;
            _daysTotal = 0;
            _currentDate = null;
            _sensorsDone = 0;
            _sensorsTotal = 0;
            _stateChangesRead = 0;
            _stateChangesTotal = 0;
            _readingStateChanges = false;
        }
    }

    public void UpdateDay(int daysDone, int daysTotal, DateTime? currentDate)
    {
        lock (_progressLock)
        {
            _daysDone = daysDone;
            _daysTotal = daysTotal;
            _currentDate = currentDate;
            _sensorsDone = 0;
            _sensorsTotal = 0;
            // 進到逐日階段代表狀態變更已翻完
            _readingStateChanges = false;
        }
    }

    public void UpdateSensors(int sensorsDone, int sensorsTotal)
    {
        lock (_progressLock)
        {
            _sensorsDone = sensorsDone;
            _sensorsTotal = sensorsTotal;
        }
    }

    /// <summary>狀態變更逐裝置查詢進度（已完成台數, 總台數）；欄位沿用 StateChangesRead／StateChangesTotal 的名稱。</summary>
    public void UpdateStateChanges(int done, int total)
    {
        lock (_progressLock)
        {
            _stateChangesRead = done;
            _stateChangesTotal = total;
            _readingStateChanges = true;
        }
    }

    public PrtgBackfillProgress GetProgress()
    {
        lock (_progressLock)
        {
            return new PrtgBackfillProgress(
                _daysDone, _daysTotal, _currentDate,
                _sensorsDone, _sensorsTotal,
                _stateChangesRead, _stateChangesTotal, _readingStateChanges);
        }
    }
}

/// <summary>
/// 極薄的 IRunConsole adapter：將回填輸出逐行收集至 <see cref="PrtgBackfillRunState"/>。
/// </summary>
public class PrtgBackfillConsole : IRunConsole
{
    private readonly PrtgBackfillRunState _state;

    public PrtgBackfillConsole(PrtgBackfillRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// PRTG 歷史回填服務：Singleton，背景執行 PRTG 歷史回填任務並維護狀態。
/// 同時是立即執行「一併補齊 PRTG 數值」的接續執行者（<see cref="IPrtgBackfillTail"/>），兩條入口共用同一個執行狀態。
/// </summary>
public class PrtgBackfillService : IPrtgBackfillTail
{
    public const int SelectedBackfillMaxHosts = 5;
    public const int SelectedBackfillMaxDays = 7;
    public const string FullBackfillPreviewContractVersion = "prtg-full-backfill-preview-v1";
    private static readonly TimeSpan FullBackfillPreviewLifetime = TimeSpan.FromMinutes(5);
    private const int MaxPendingFullBackfillPreviews = 128;
    private readonly object _fullPreviewLock = new();
    private readonly ConcurrentDictionary<string, PendingFullBackfillPreview> _fullPreviews = new(StringComparer.Ordinal);
    private readonly ISystemSettingsStore _settings;
    private readonly StorageBackend _backend;
    private readonly PrtgBackfillRunState _state;
    private readonly PrtgProbeRunState _probeState;

    private readonly IHostStore _hosts;

    // 必要相依，不設預設值：回填與取數、結構同步會打同一台 PRTG，這兩道互斥是保護；
    // 做成可選的話漏注入時保護會靜默消失。
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncRunState _structureSyncState;
    private readonly ISentinelStore _sentinels;

    /// <summary>鏡像沒有感測器時，回填自己先做一次結構同步。</summary>
    private readonly PrtgStructureSyncService _structureSync;
    private readonly TimeProvider _timeProvider;
    private readonly Func<SystemSettings, PrtgClient>? _clientFactory;

    private sealed record PendingFullBackfillPreview(DateTime AnchorDate, DateTime ExpiresAtUtc,
        string SettingsRevision, string ScopeRevision, string TargetFingerprint, string BuildVersion, string BuildRevision);

    public PrtgBackfillService(
        ISystemSettingsStore settings,
        StorageBackend backend,
        PrtgBackfillRunState state,
        PrtgProbeRunState probeState,
        IHostStore hosts,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncRunState structureSyncState,
        ISentinelStore sentinels,
        PrtgStructureSyncService structureSync,
        TimeProvider? timeProvider = null,
        Func<SystemSettings, PrtgClient>? clientFactory = null)
    {
        _sentinels = sentinels;
        _settings = settings;
        _backend = backend;
        _state = state;
        _probeState = probeState;
        _hosts = hosts;
        _schedulerRunState = schedulerRunState;
        _structureSyncState = structureSyncState;
        _structureSync = structureSync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientFactory = clientFactory;
    }

    public PrtgBackfillAccessSnapshot GetAccessSnapshot() => _state.GetAccessSnapshot();

    public PrtgBackfillStatusDto GetStatus() => GetAccessSnapshot().Status;

    /// <summary>
    /// 清除監看範圍外資料（PRTG 維護頁確認）現在能不能做：取數執行、結構同步、回填任一在跑就不行，回傳原因；null＝可以。
    /// 放在這裡是因為本服務本來就持有這三個執行狀態（與 <see cref="TryStart"/> 的互斥同一組）。
    /// </summary>
    public string? ScopePurgeConflict()
    {
        if (_schedulerRunState.IsRunning)
            return $"取數執行中{ElapsedSuffix(_schedulerRunState.StartedAt)}，請等它結束後再清除。";
        var syncSnapshot = _structureSyncState.Snapshot();
        if (syncSnapshot.IsRunning)
            return $"「同步結構與對應」執行中{ElapsedSuffix(syncSnapshot.StartedAt)}，請等它完成後再清除。";
        if (_state.Snapshot().IsRunning)
            return "歷史回填執行中，請等它完成或按停止後再清除。";
        return null;
    }

    /// <summary>要求停止進行中的回填；沒有執行中回 false。</summary>
    public bool TryCancel() => _state.TryCancel();

    public bool TryCancelSelected(string? runId) => _state.TryCancelSelected(runId);

    public bool TryCancelIdentifiedRun(string? runKind, string? runId) =>
        _state.TryCancelIdentifiedRun(runKind, runId);

    /// <summary>「（已 N 分鐘）」後綴；取不到開始時間時回空字串（兩道執行中閘門共用）。</summary>
    private static string ElapsedSuffix(DateTime? startedAt)
    {
        if (!startedAt.HasValue) return "";
        var minutes = (int)Math.Max(0, (DateTime.Now - startedAt.Value).TotalMinutes);
        return $"（已 {minutes} 分鐘）";
    }

    /// <summary>回填啟動前準備好的一趟（已佔住執行狀態）。</summary>
    private sealed record PreparedRun(SystemSettings Settings, int Days, PrtgClient Client, CancellationToken Token,
        bool NeedsStructureSync, PrtgFullBackfillPlan? ExpectedPlan = null);

    /// <summary>「近 N 天」的對應視窗：逐日迴圈以每個回填日為基準往回找 HostMapLookbackDays 天，閘門要涵蓋「最舊回填日再往回」整段。</summary>
    private static int MapGateWindow(int days) => days + PrtgTriggeredValueFetcher.HostMapLookbackDays;

    /// <summary>近 <see cref="MapGateWindow"/> 天是否有任何主機對應（<see cref="TryStart"/> 與接續回填共用，天數各自帶入）。</summary>
    private static bool HasAnyMapping(EfPrtgStore prtgStore, int days) =>
        prtgStore.GetLatestHostMapWithDate(MapGateWindow(days)).Rows
            .Any(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId != null);

    /// <param name="error">拒絕原因；成功時為 null。</param>
    /// <param name="isConflict">
    /// true＝被互斥擋下（環境探測／取數／結構同步／回填自己正在跑）——狀態衝突，呼叫端該回 409；
    /// false＝設定或前提不齊，該回 400。與 <see cref="PrtgStructureSyncService.TryStart"/> 同一套。
    /// </param>
    public bool TryStart(out string? error, out bool isConflict)
    {
        var s = _settings.Get();
        if (!TryPrepare(s, s.PrtgBackfillDays, blockWhenSchedulerRunning: true, runKind: "full", out var run, out error, out isConflict))
            return false;

        _ = Task.Run(() => ExecuteAsync(run!, new PrtgBackfillConsole(_state), hostIds: null));
        return true;
    }

    /// <summary>Builds a bounded, read-only estimate from local mirror data and retained records.</summary>
    public PrtgFullBackfillPreviewDto PreviewFull()
    {
        var settings = _settings.Get();
        var snapshot = BuildFullPreviewSnapshot(settings, _timeProvider.GetLocalNow().Date);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.Add(FullBackfillPreviewLifetime);
        var previewId = Guid.NewGuid().ToString("N");
        var (buildVersion, buildRevision) = CurrentBuildIdentity();
        lock (_fullPreviewLock)
        {
            foreach (var stale in _fullPreviews.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
                _fullPreviews.TryRemove(stale, out _);
            if (_fullPreviews.Count >= MaxPendingFullBackfillPreviews)
                throw DomainException.Conflict("目前已有太多尚未使用的全量回填預覽，請稍後再試或使用既有預覽。 ");
            _fullPreviews[previewId] = new PendingFullBackfillPreview(snapshot.AnchorDate, expires,
                snapshot.SettingsRevision, snapshot.ScopeRevision, snapshot.Plan.TargetFingerprint, buildVersion, buildRevision);
        }

        return new PrtgFullBackfillPreviewDto
        {
            PreviewId = previewId,
            ContractVersion = FullBackfillPreviewContractVersion,
            BuildVersion = buildVersion,
            BuildRevision = buildRevision,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires,
            PlanAnchorLocalDate = snapshot.AnchorDate,
            FromDate = snapshot.Plan.FromDate,
            ToDate = snapshot.Plan.ToDate,
            DayCount = snapshot.Plan.DayCount,
            EstimatedHistoricRequests = snapshot.Plan.HistoricRequests,
            HistoricQuotaLowerBoundSeconds = snapshot.Plan.HistoricQuotaLowerBoundSeconds,
            DaysWithTargets = snapshot.Plan.DaysWithTargets,
            StateChangeObjects = snapshot.Plan.StateChangeObjects,
            SettingsRevision = snapshot.SettingsRevision,
            ScopeRevision = snapshot.ScopeRevision,
            Days = snapshot.Plan.Days.Select(day => new PrtgFullBackfillDayPreviewDto
            {
                Day = day.Day, TriggeredHosts = day.TriggeredHosts, MappedHosts = day.MappedHosts,
                SelectedHosts = day.SelectedHosts, TargetSensors = day.TargetSensors
            }).ToArray(),
            Message = FullPreviewMessage(snapshot.Plan)
        };
    }

    /// <summary>Starts only the exact preview accepted by the operator. Preview IDs are single-use and expire.</summary>
    public bool TryStartFull(PrtgFullBackfillStartRequest? request, out string? error, out bool isConflict)
    {
        error = null;
        isConflict = false;
        if (request == null || string.IsNullOrWhiteSpace(request.PreviewId))
        {
            error = "全量回填必須先取得最新成本預覽，再確認啟動。";
            return false;
        }
        if (!request.Confirmed)
        {
            error = "尚未確認全量回填成本預覽。";
            return false;
        }
        if (!_fullPreviews.TryRemove(request.PreviewId, out var pending))
        {
            error = "預覽已使用、過期或不屬於目前服務版本，請重新預覽。";
            return false;
        }
        var currentLocalDate = _timeProvider.GetLocalNow().Date;
        if (pending.ExpiresAtUtc <= _timeProvider.GetUtcNow().UtcDateTime || pending.AnchorDate != currentLocalDate)
        {
            error = "預覽已過期或日期已跨日，請重新預覽。";
            return false;
        }

        var settings = _settings.Get();
        var current = BuildFullPreviewSnapshot(settings, currentLocalDate);
        var (buildVersion, buildRevision) = CurrentBuildIdentity();
        if (pending.SettingsRevision != current.SettingsRevision || pending.ScopeRevision != current.ScopeRevision ||
            pending.TargetFingerprint != current.Plan.TargetFingerprint || pending.BuildVersion != buildVersion ||
            pending.BuildRevision != buildRevision)
        {
            error = "設定、主機範圍、歷史記錄或對應已在預覽後變更，請重新預覽確認最新成本。";
            return false;
        }

        if (!TryPrepare(settings, settings.PrtgBackfillDays, blockWhenSchedulerRunning: true, runKind: "full",
                out var run, out error, out isConflict, expectedFullPreview: current,
                expectedAnchorDate: pending.AnchorDate, expectedPreviewExpiryUtc: pending.ExpiresAtUtc))
            return false;

        var prepared = run! with { ExpectedPlan = current.Plan };
        _ = Task.Run(() => ExecuteAsync(prepared, new PrtgBackfillConsole(_state), hostIds: null));
        return true;
    }

    private sealed record FullPreviewSnapshot(DateTime AnchorDate, PrtgFullBackfillPlan Plan,
        string SettingsRevision, string ScopeRevision);

    private FullPreviewSnapshot BuildFullPreviewSnapshot(SystemSettings settings, DateTime anchorDate)
    {
        if (settings.PrtgBackfillDays is < 1 or > 365)
            throw DomainException.Validation("歷史回填天數設定無效，請儲存 1 至 365 天的範圍後重新預覽。");
        var anchor = anchorDate.Date;
        var scopeReader = new PrtgScopeRevisionReader(_backend, _hosts);
        var scopeBefore = scopeReader.Read();
        var settingsRevision = Hash(JsonSerializer.Serialize(settings));
        var store = _backend.PrtgStore();
        if (store.GetSensorTargets().Count == 0)
            throw DomainException.Validation("PRTG 結構鏡像目前沒有感測器，請先同步結構，再預覽全量回填成本。");
        var scopeResult = PrtgScopeDevices.Compute(store, _hosts, new PrtgMirrorGuardSource(store), settings,
            _sentinels.GetAll(), new SilentBackfillConsole(), new PrtgAddressResolver(), hostIds: null);
        var (extraScopeHosts, _) = PrtgValueFetchScope.ResolveHostNames(settings.PrtgValueFetchExtraHosts,
            _hosts.GetAll().Select(host => (host.HostId, host.HostName, host.Active, host.MergedInto.HasValue)));
        var plan = PrtgFullBackfillPlanBuilder.Build(store, _backend.RecordStore(), anchor,
            settings.PrtgBackfillDays, scopeResult.DeviceObjids, settings.PrtgSensorTypeWhitelist,
            settings.PrtgValueFetchScope, extraScopeHosts);
        var scopeAfter = scopeReader.Read();
        var settingsAfter = Hash(JsonSerializer.Serialize(_settings.Get()));
        if (scopeBefore != scopeAfter || settingsRevision != settingsAfter)
            throw DomainException.Conflict("預覽時計算範圍或設定正好變更，請重新預覽。");
        return new FullPreviewSnapshot(anchor, plan, settingsRevision, Hash(scopeAfter));
    }

    private static string FullPreviewMessage(PrtgFullBackfillPlan plan)
    {
        var lowerBound = TimeSpan.FromSeconds(plan.HistoricQuotaLowerBoundSeconds);
        var stateText = plan.StateChangeObjects == 0
            ? "沒有狀態變更查詢對象。"
            : $"另有 {plan.StateChangeObjects:N0} 個狀態變更查詢對象；其 table 分頁成本要等來源回應，預覽不估 ETA。";
        return $"數值歷史約 {plan.HistoricRequests:N0} 次請求；依每滾動 60 秒最多 5 次，僅配額下界為 {lowerBound.TotalMinutes:N0} 分鐘，不含 PRTG 回應延遲。{stateText}";
    }

    private static (string Version, string Revision) CurrentBuildIdentity()
    {
        var assembly = typeof(PrtgBackfillService).Assembly;
        return (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                assembly.GetName().Version?.ToString() ?? "unknown",
            assembly.ManifestModule.ModuleVersionId.ToString("N"));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class SilentBackfillConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    /// <summary>預估指定主機與日期的數值請求量；只讀鏡像資料，不連線 PRTG。</summary>
    public PrtgSelectedBackfillPreviewDto PreviewSelected(PrtgSelectedBackfillRequest request)
    {
        var settings = _settings.Get();
        var (hostIds, from, to) = ValidateSelectedRequest(request, settings);
        var maps = _backend.PrtgStore();
        var requestCount = 0;
        var daysWithTargets = 0;
        var sampledTargetsByDay = new Dictionary<DateTime, long[]>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var deviceIds = maps.GetHostMapForDate(day)
                .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && hostIds.Contains(m.HostId.Value))
                .Select(m => m.DeviceObjid).Distinct().ToArray();
            var targets = maps.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist, deviceIds);
            if (targets.Count == 0) continue;
            daysWithTargets++;
            requestCount += targets.Count;
            sampledTargetsByDay[day] = targets.Distinct().ToArray();
        }

        // Bound each IN query well below SQL Server's parameter limit. Query one selected day
        // at a time so sampled rows cannot bleed across the requested historical-day targets.
        const int readinessBatchSize = 500;
        var sampledRowsToReplace = 0;
        foreach (var (day, targets) in sampledTargetsByDay)
        {
            for (var offset = 0; offset < targets.Length; offset += readinessBatchSize)
            {
                var batch = targets.Skip(offset).Take(readinessBatchSize).ToArray();
                sampledRowsToReplace += maps.GetReadinessValues(batch, day, day.AddDays(1))
                    .Count(row => string.Equals(row.Quality, "sampled", StringComparison.OrdinalIgnoreCase));
            }
        }

        return new PrtgSelectedBackfillPreviewDto
        {
            FromDate = from, ToDate = to, HostIds = hostIds.Order().ToArray(),
            HostCount = hostIds.Count, DayCount = (to - from).Days + 1,
            EstimatedRequests = requestCount, DaysWithTargets = daysWithTargets,
            EstimatedSampledRowsToReplace = sampledRowsToReplace,
            Message = requestCount == 0 ? "此範圍沒有可回填的歷史 sensor；不會發出 PRTG 請求。" : $"預估逐 sensor 發出 {requestCount} 次每日歷史查詢。"
        };
    }

    /// <summary>重新計算範圍後啟動指定歷史值回填；空目標不會佔用執行狀態或呼叫 PRTG。</summary>
    public bool TryStartSelected(PrtgSelectedBackfillRequest request, out PrtgSelectedBackfillPreviewDto preview,
        out string? error, out bool isConflict)
    {
        preview = PreviewSelected(request);
        error = null;
        isConflict = false;
        if (!preview.HasTargets)
        {
            error = preview.Message;
            return false;
        }

        var settings = _settings.Get();
        var days = preview.DayCount;
        if (!TryPrepare(settings, days, blockWhenSchedulerRunning: true, runKind: "selected", out var run, out error, out isConflict,
                requireRecentMapping: false, selectedHostIds: preview.HostIds))
            return false;
        var selectedPreview = preview;
        _ = Task.Run(() => ExecuteSelectedAsync(run!, selectedPreview.FromDate, selectedPreview.ToDate, selectedPreview.HostIds));
        return true;
    }

    private (HashSet<long> HostIds, DateTime From, DateTime To) ValidateSelectedRequest(
        PrtgSelectedBackfillRequest request, SystemSettings settings)
    {
        if (request == null) throw DomainException.Validation("請指定回填主機與日期範圍。");
        var ids = (request.HostIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToHashSet();
        if (ids.Count == 0) throw DomainException.Validation("請至少選擇一台主機；空白選取不會轉成全站回填。");
        if (ids.Count > SelectedBackfillMaxHosts) throw DomainException.Validation($"一次最多選擇 {SelectedBackfillMaxHosts} 台主機。");
        var hosts = _hosts.GetAll().Where(h => ids.Contains(h.HostId)).ToDictionary(h => h.HostId);
        if (hosts.Count != ids.Count || hosts.Values.Any(h => !h.Active || h.MergedInto.HasValue))
            throw DomainException.Validation("所選主機不存在、未啟用或已合併，請重新選擇有效主機。");

        var from = request.FromDate.Date;
        var to = request.ToDate.Date;
        var yesterday = DateTime.Today.AddDays(-1);
        var retentionDays = Math.Min(settings.PrtgRetentionDays, settings.RetentionDays);
        var oldest = yesterday.AddDays(-Math.Max(1, retentionDays) + 1);
        if (from > to) throw DomainException.Validation("起始日期不得晚於結束日期。");
        if (to > yesterday) throw DomainException.Validation("指定回填日期必須是昨天或更早。");
        if (from < oldest) throw DomainException.Validation($"指定日期超出目前 {retentionDays} 天的資料保留範圍（最早可選 {oldest:yyyy-MM-dd}）。");
        if ((to - from).Days + 1 > SelectedBackfillMaxDays)
            throw DomainException.Validation($"一次最多回填 {SelectedBackfillMaxDays} 天。");
        if (settings.PrtgSensorTypeWhitelist == null || settings.PrtgSensorTypeWhitelist.Count == 0)
            throw DomainException.Validation("PRTG 感測器類型白名單為空，為避免查詢所有 sensor，局部回填已拒絕。");
        return (ids, from, to);
    }

    private async Task ExecuteSelectedAsync(PreparedRun run, DateTime from, DateTime to, IReadOnlyCollection<long> hostIds)
    {
        using var operation = new PrtgOperationScope(run.Settings, _settings.Get, run.Token, new PrtgScopeRevisionReader(_backend, _hosts).Read, "歷史補值");
        run.Client.OperationCheckpoint = operation.Checkpoint;
        var success = false;
        var cancelled = false;
        var console = new PrtgBackfillConsole(_state);
        try
        {
            using (run.Client)
            {
                operation.Checkpoint();
                var store = _backend.PrtgStore();
                var fetch = new PrtgFetchService(run.Client, store,
                    new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), console,
                    PrtgSensorTypeCategoryMap.ParseOverrides(run.Settings.PrtgSensorTypeCategoryOverrides).Map);
                console.WriteLine($"開始指定主機 PRTG 數值回填（{from:yyyy-MM-dd}～{to:yyyy-MM-dd}，主機 {hostIds.Count} 台）；不回填狀態、不補派歷史 finding。");
                success = await PrtgBackfillRunner.RunValuesForHostsAsync(
                    fetch, from, to, hostIds, run.Settings.PrtgFetchConcurrency,
                    run.Settings.PrtgSensorTypeWhitelist, store, console, operation.Token,
                    (done, total, date) => _state.UpdateDay(done, total, date),
                    (done, total) => _state.UpdateSensors(done, total));
                operation.CompletedStage("指定主機補值已返回");
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            cancelled = true;
            success = false;
        }
        catch (Exception ex)
        {
            console.WriteLine($"指定主機回填發生未預期錯誤：{ex.Message}");
        }
        finally
        {
            _state.FinishRun(success, cancelled);
        }
    }

    /// <summary>
    /// 立即執行的接續回填：同一趟的一部分，因此**不**以「取數執行中」擋下；其餘擋門照舊。
    /// 天數用傳入值（不讀 PrtgBackfillDays）。輸出同時寫進回填狀態卡與本趟的執行紀錄。
    /// </summary>
    public async Task<bool> RunTailAsync(int days, IReadOnlyCollection<long>? hostIds, IRunConsole console, CancellationToken ct)
    {
        var s = _settings.Get();
        if (!TryPrepare(s, days, blockWhenSchedulerRunning: false, runKind: "tail", out var run, out var error, out _))
        {
            console.WriteLine($"  ⚠ 接續補 PRTG 數值未執行：{error}");
            return false;
        }


        // 整趟被停止時一併停止回填；回填也可以在維護頁的狀態卡單獨停止
        bool success;
        using (ct.Register(() => _state.TryCancel()))
        {
            success = await ExecuteAsync(run!, new TeeRunConsole(new PrtgBackfillConsole(_state), console), hostIds);
        }
        ct.ThrowIfCancellationRequested();
        return success;
    }

    /// <summary>
    /// 兩條入口共用的擋門與啟動：通過時已佔住執行狀態並建立 PRTG 連線，呼叫端必須接著呼叫 <see cref="ExecuteAsync"/>。
    /// </summary>
    /// <param name="blockWhenSchedulerRunning">手動回填要避開取數執行；接續回填本身就是那一趟，不檢查。</param>
    private bool TryPrepare(SystemSettings s, int days, bool blockWhenSchedulerRunning, string runKind,
        out PreparedRun? run, out string? error, out bool isConflict, bool requireRecentMapping = true,
        FullPreviewSnapshot? expectedFullPreview = null, DateTime? expectedAnchorDate = null,
        DateTime? expectedPreviewExpiryUtc = null, IReadOnlyCollection<long>? selectedHostIds = null)
    {
        run = null;
        error = null;
        isConflict = false;

        if (!s.PrtgEnabled)
        {
            error = "PRTG 擷取未啟用，請先在 PRTG 維護頁「擷取參數」選擇取數範圍。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行回填。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法執行回填。";
            return false;
        }

        if (_probeState.Snapshot().IsRunning)
        {
            error = "環境探測執行中，請稍後再試。";
            isConflict = true;
            return false;
        }

        // 取數執行與回填同時打同一台 PRTG，兩邊都會變慢且互相拖累
        if (blockWhenSchedulerRunning && _schedulerRunState.IsRunning)
        {
            error = $"取數執行中{ElapsedSuffix(_schedulerRunState.StartedAt)}，回填會與它同時查詢同一台 PRTG。請等它結束，或在取數執行卡按「停止執行」後再回填。";
            isConflict = true;
            return false;
        }

        // 判斷方式與快照服務一致（PrtgStructureSyncService.IsRunning＝執行狀態快照的 IsRunning）
        var syncSnapshot = _structureSyncState.Snapshot();
        if (syncSnapshot.IsRunning)
        {
            error = $"「同步結構與對應」執行中{ElapsedSuffix(syncSnapshot.StartedAt)}，請等它完成，或在 PRTG 卡按停止後再回填。";
            isConflict = true;
            return false;
        }

        // 回填的 sensor 清單來自鏡像：鏡像空的就在背景先同步一次結構（對應閘門留到同步後再判斷）。
        // 鏡像有東西時照舊在入口判斷對應：沒有任何主機對應時逐日目標 sensor 一律是 0 個，放行只會空跑並報成功。
        var prtgStore = _backend.PrtgStore();
        var needsStructureSync = prtgStore.GetSensorTargets().Count == 0;
        if (requireRecentMapping && !needsStructureSync && !HasAnyMapping(prtgStore, days))
        {
            error = $"近 {MapGateWindow(days)} 天沒有任何 PRTG 主機對應，回填找不到要取數的主機。請先按「同步結構與對應」建立對應後再回填。";
            return false;
        }

        // Re-read the complete preview contract immediately before claiming the run gate or creating a PRTG client.
        // The runner performs a second per-day check before source calls to catch changes after this admission point.
        if (expectedFullPreview != null)
        {
            try
            {
                var live = BuildFullPreviewSnapshot(_settings.Get(), expectedFullPreview.AnchorDate);
                if (live.SettingsRevision != expectedFullPreview.SettingsRevision ||
                    live.ScopeRevision != expectedFullPreview.ScopeRevision ||
                    live.Plan.TargetFingerprint != expectedFullPreview.Plan.TargetFingerprint)
                {
                    error = "設定、範圍、歷史記錄或對應已在啟動前變更，請重新預覽確認最新成本。";
                    return false;
                }
            }
            catch (DomainException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Recheck the preview's temporal fence after the potentially expensive plan recompute and
        // immediately before claiming the run. Once claimed, execution keeps this admitted anchor.
        if (expectedAnchorDate.HasValue && _timeProvider.GetLocalNow().Date != expectedAnchorDate.Value.Date)
        {
            error = "預覽計算期間日期已跨日，請重新預覽確認最新成本。";
            return false;
        }
        if (expectedPreviewExpiryUtc.HasValue && _timeProvider.GetUtcNow().UtcDateTime >= expectedPreviewExpiryUtc.Value)
        {
            error = "預覽計算期間已過期，請重新預覽確認最新成本。";
            return false;
        }

        if (!_state.TryBeginIdentifiedRun(runKind, out var runToken, selectedHostIds))
        {
            error = "回填已在執行中。";
            isConflict = true;
            return false;
        }

        PrtgClient client;
        try
        {
            client = _clientFactory?.Invoke(s) ?? PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _state.FinishRun(false, cancelled: false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        run = new PreparedRun(s, days, client, runToken, needsStructureSync);
        return true;
    }

    /// <summary>回填主體（兩條入口共用）：必要時先同步結構，再逐日回填；結束時釋放執行狀態。回傳是否成功。</summary>
    private async Task<bool> ExecuteAsync(PreparedRun run, IRunConsole console, IReadOnlyCollection<long>? hostIds)
    {
        var s = run.Settings;
        using var operation = new PrtgOperationScope(s, _settings.Get, run.Token, new PrtgScopeRevisionReader(_backend, _hosts).Read, "歷史補值");
        var runToken = operation.Token;
        run.Client.OperationCheckpoint = operation.Checkpoint;
        var prtgStore = _backend.PrtgStore();
        var success = false;
        var cancelled = false;
        try
        {
            using (run.Client)
            {
                operation.Checkpoint();
                if (run.NeedsStructureSync)
                {
                    console.WriteLine("鏡像尚無感測器結構，先同步結構…");
                    var syncError = await _structureSync.SyncForBackfillAsync(runToken);
                    runToken.ThrowIfCancellationRequested();
                    if (syncError != null)
                    {
                        console.WriteLine($"結構同步失敗：{syncError}，請到 PRTG 維護頁檢查連線。");
                        return false;
                    }

                    if (!HasAnyMapping(prtgStore, run.Days))
                    {
                        console.WriteLine("PRTG 裝置沒有任何一台對應到主機清單中的主機（請檢查主機 IP 與 PRTG 裝置位址，或在鏡像狀態頁手動指派）。");
                        return false;
                    }

                    console.WriteLine("結構同步完成，開始回填。");
                }

                var fetchService = new PrtgFetchService(run.Client, prtgStore,
                    new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), console,
                    PrtgSensorTypeCategoryMap.ParseOverrides(s.PrtgSensorTypeCategoryOverrides).Map);

                // 數值取數對象與排程取數共用同一份設定與判定（docs/PRTG-SPEC.md §3a）
                var (scopeHostIds, unresolvedHosts) = PrtgValueFetchScope.ResolveHostNames(
                    s.PrtgValueFetchExtraHosts,
                    _hosts.GetAll().Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

                if (unresolvedHosts.Count > 0)
                {
                    console.WriteLine($"⚠ 取數範圍的指定主機有 {unresolvedHosts.Count} 個對不到主機主檔，已略過：" +
                                      string.Join("、", unresolvedHosts.Take(10)));
                }

                // 監看裝置：回填不做主機對應，直接以既有對應算一次，整趟共用；指定主機時只算那些主機
                var scopeResult = PrtgScopeDevices.Compute(
                    prtgStore, _hosts, new PrtgMirrorGuardSource(prtgStore), s, _sentinels.GetAll(),
                    console, new PrtgAddressResolver(), hostIds);
                console.WriteLine($"監看裝置：{scopeResult.DeviceObjids.Count} 台");

                success = await PrtgBackfillRunner.RunAsync(
                    fetchService, run.Days, s.PrtgFetchConcurrency, console, runToken,
                    scopeResult.DeviceObjids,
                    prtgStore, _backend.RecordStore(), s.PrtgSensorTypeWhitelist,
                    dayProgress: (dDone, dTotal, curDate) => _state.UpdateDay(dDone, dTotal, curDate),
                    sensorProgress: (sDone, sTotal) => _state.UpdateSensors(sDone, sTotal),
                    scope: s.PrtgValueFetchScope,
                    extraScopeHosts: scopeHostIds,
                    stateChangeProgress: (doneDevices, totalDevices) => _state.UpdateStateChanges(doneDevices, totalDevices),
                    expectedDayFingerprints: run.ExpectedPlan?.Days.ToDictionary(day => day.Day, day => day.TargetFingerprint),
                    expectedPlanAnchorDate: run.ExpectedPlan?.ToDate.AddDays(1));
                operation.CompletedStage("歷史補值已返回");
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            if (operation.SettingsChanged) console.WriteLine("PRTG 設定已變更，回填停止；已完成資料保留。");
            // 摘要已由 runner 印出（已停止：完成 x／N 天）
            cancelled = true;
            success = false;
        }
        catch (Exception ex)
        {
            console.WriteLine($"回填過程發生未預期錯誤：{ex.Message}");
            success = false;
        }
        finally
        {
            _state.FinishRun(success, cancelled);
        }

        return success;
    }

    /// <summary>接續回填的輸出同時寫進回填狀態卡與本趟執行紀錄。</summary>
    private sealed class TeeRunConsole : IRunConsole
    {
        private readonly IRunConsole _first;
        private readonly IRunConsole _second;

        public TeeRunConsole(IRunConsole first, IRunConsole second)
        {
            _first = first;
            _second = second;
        }

        public void WriteLine(string message = "")
        {
            _first.WriteLine(message);
            _second.WriteLine(message);
        }
    }
}

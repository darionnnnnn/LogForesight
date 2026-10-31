using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 取數範圍規模估算、資源守門預覽、鏡像狀態與範圍外清除預覽（唯讀端點）</summary>
[ApiController]
[Route("api/admin/settings")]
[Permission(Capability.Maintain)]
public class PrtgScopeEstimateController : ControllerBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ISystemSettingsService _settings;
    private readonly StorageBackend _backend;
    private readonly IHostStore _hosts;
    private readonly PrtgSnapshotHostedService _snapshot;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly PrtgScopePurgeAuthorizationService _scopePurgeAuthorization;

    public PrtgScopeEstimateController(
        ISystemSettingsService settings,
        StorageBackend backend,
        IHostStore hosts,
        PrtgSnapshotHostedService snapshot,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        PrtgScopePurgeAuthorizationService scopePurgeAuthorization)
    {
        _settings = settings;
        _backend = backend;
        _hosts = hosts;
        _snapshot = snapshot;
        _visibility = visibility;
        _currentUser = currentUser;
        _scopePurgeAuthorization = scopePurgeAuthorization;
    }

    // ── PRTG 資源守門預覽（批次F 階段4）────────────────────────────────────

    /// <summary>
    /// 取數範圍的規模估算（docs/PRTG-SPEC.md §3a）：把範圍放寬之前先看得到「一晚要抓幾個 sensor」。
    /// 跑不完的症狀是隔天資料不全、不是當下報錯，所以要在設定當下就講出來。
    /// </summary>
    /// <param name="scope">要估算的模式；未帶時用目前已儲存的設定</param>
    [HttpGet("prtg-fetch-scope/estimate")]
    public ApiResponse<PrtgValueFetchScopeEstimateDto> EstimatePrtgFetchScope([FromQuery] string? scope)
    {
        // 維護頁下拉的「關閉」不是後端的合法 scope；Normalize 會把它退回 triggered 而給出一組
        // 看起來正常的數字——關閉狀態下不會取數，回一個估算值是語意矛盾。
        if (string.Equals(scope, "off", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse<PrtgValueFetchScopeEstimateDto>.Ok(new PrtgValueFetchScopeEstimateDto
            {
                Success = false,
                ErrorMessage = "PRTG 擷取已關閉，沒有規模可估算。"
            });
        }

        var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        var whitelist = settings.PrtgSensorTypeWhitelist ?? new List<string>();
        var requestedScope = scope ?? settings.PrtgValueFetchScope;

        // 估算的是**實際會生效**的範圍：白名單為空時 all-mapped 會退回 triggered
        // （見 PrtgValueFetchScope 的第二道防線），估設定值會給出一個永遠不會發生的數字。
        var effectiveScope = PrtgValueFetchScope.EffectiveScope(requestedScope, whitelist.Count == 0);
        var prtgStore = _backend.PrtgStore();

        // 與校準同一個來源：錨點今天、回看 30 天的最近一次對應
        var mapRows = prtgStore.GetLatestHostMapWithDate(PrtgHostMapLookup.LatestLookbackDays, DateTime.Today).Rows
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .ToList();

        List<long> selectedHostIds;
        if (effectiveScope == PrtgValueFetchScope.AllMapped)
        {
            selectedHostIds = mapRows.Select(m => m.HostId!.Value).Distinct().ToList();
        }
        else
        {
            // triggered 與 triggered-plus-list 的觸發主機數逐日變動、事前算不出來，
            // 這裡只估「指定清單」那部分——把它講成觸發主機的估計值會是假數字。
            var allHosts = _hosts.GetAll();
            var (extraIds, _) = PrtgValueFetchScope.ResolveHostNames(
                settings.PrtgValueFetchExtraHosts,
                allHosts.Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));
            selectedHostIds = effectiveScope == PrtgValueFetchScope.TriggeredPlusList ? extraIds : new List<long>();
        }

        var deviceObjids = mapRows
            .Where(m => selectedHostIds.Contains(m.HostId!.Value))
            .Select(m => m.DeviceObjid)
            .Distinct()
            .ToList();

        var sensors = prtgStore.GetValueFetchTargets(whitelist, deviceObjids);

        var allOkDevices = mapRows.Select(m => m.DeviceObjid).Distinct().ToList();
        var snapshotTargets = prtgStore.GetValueFetchTargets(whitelist, allOkDevices).Count;
        var snapshotRowsPerDay = snapshotTargets * 24L;
        // 這是與 RuntimeSettingsResolver 的 PRTG 保留期規則對齊的顯示用近似值（不含低於下限的退回邏輯）。
        var snapshotRetentionDays = Math.Min(settings.PrtgRetentionDays, settings.RetentionDays);
        var snapshotRowsAtRetention = snapshotRowsPerDay * snapshotRetentionDays;

        // 兩個條件各自成立各自講：最危險的組合（不限制＋數千萬列）不能只看到其中一句。
        var snapshotWarnings = new List<string>();
        if (whitelist.Count == 0)
        {
            snapshotWarnings.Add("sensor type 白名單為空：快照會存下已對應裝置上的全部感測器，資料表成長最快。建議設定白名單。");
        }
        if (snapshotRowsAtRetention >= SnapshotRowsWarnThreshold)
        {
            snapshotWarnings.Add($"快照在保留期內約累積 {snapshotRowsAtRetention:N0} 列，建議縮小 sensor type 白名單或調短 PRTG 保留天數。");
        }
        string? snapshotWarning = snapshotWarnings.Count > 0 ? string.Join(" ", snapshotWarnings) : null;

        string? warning = null;
        if (PrtgValueFetchScope.ShouldWarnUnsafeAllMapped(requestedScope, whitelist.Count == 0))
        {
            warning = "sensor type 白名單留空等於對全部 sensor 取數，這個模式不允許——" +
                      "夜間批次會退回「只抓觸發主機」。請先設定白名單。";
        }
        else if (sensors.Count >= PrtgFetchScopeSensorWarnThreshold)
        {
            warning = $"估算 {sensors.Count} 個 sensor，一晚可能跑不完。建議縮小 sensor type 白名單，或改用「只抓觸發主機」。";
        }

        return ApiResponse<PrtgValueFetchScopeEstimateDto>.Ok(new PrtgValueFetchScopeEstimateDto
        {
            Success = true,
            Scope = effectiveScope,
            Hosts = selectedHostIds.Count,
            Devices = deviceObjids.Count,
            Sensors = sensors.Count,
            WhitelistEmpty = whitelist.Count == 0,
            Warning = warning,
            SnapshotTargets = snapshotTargets,
            SnapshotRowsPerDay = snapshotRowsPerDay,
            SnapshotRetentionDays = snapshotRetentionDays,
            SnapshotRowsAtRetention = snapshotRowsAtRetention,
            SnapshotWarning = snapshotWarning,
            SnapshotBatchCount = (snapshotTargets + PrtgSnapshotTargetResolver.BatchSize - 1) / PrtgSnapshotTargetResolver.BatchSize
        });
    }

    /// <summary>粗估用的每次 historicdata 查詢平均秒數（暫定）。</summary>
    private const double PrtgValueQuerySeconds = 1.5;

    /// <summary>「一併補齊」查詢次數達到這個數時，確認框的勾選預設不勾（暫定）：讓使用者看過估算再主動勾，不是閘門。</summary>
    internal const int LargeBackfillQueryThreshold = 2000;

    /// <summary>
    /// 立即執行「一併補齊 PRTG 逐小時數值」的查詢量粗估：目前監看裝置上的白名單感測器數 × 天數。
    /// 帶 segment 時只算那個網段的主機（與立即執行的網段範圍同一套解析）。
    /// </summary>
    [HttpGet("prtg-estimate")]
    public ApiResponse<PrtgValuesEstimateDto> EstimatePrtgValues([FromQuery] int days, [FromQuery] string? segment = null)
    {
        if (days < 1)
            throw DomainException.Validation("天數必須大於等於 1。");
        var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        var sentinels = new SentinelStore(_backend.Blob("sentinels"));
        var hostIds = segment == null ? null : ScheduleController.ResolveSegmentHostIds(segment, _hosts, sentinels);

        // 與「一併補齊」實際取數同一個母體（保守策略＝全部已對應主機，PrtgValueFetchScope.BackfillScope）：
        // 最新對應中 ok 的主機與其裝置上的白名單 sensor；帶網段時只算那些主機。
        var store = _backend.PrtgStore();
        var okRows = store.GetLatestHostMap()
            .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue &&
                          (hostIds == null || hostIds.Contains(row.HostId.Value)))
            .ToList();
        var blocked = PrtgValueFetchScope.BackfillBlockedReason(settings.PrtgFetchStrategy, settings.PrtgSensorTypeWhitelist);
        var sensors = blocked != null ? 0 : store.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist,
            okRows.Select(row => row.DeviceObjid).Distinct().ToList()).Count;
        var queries = (long)sensors * days;
        var concurrency = Math.Max(1, settings.PrtgFetchConcurrency);

        return ApiResponse<PrtgValuesEstimateDto>.Ok(new PrtgValuesEstimateDto
        {
            Hosts = okRows.Select(row => row.HostId!.Value).Distinct().Count(),
            Sensors = sensors,
            Queries = queries,
            Minutes = (int)Math.Ceiling(queries * PrtgValueQuerySeconds / concurrency / 60),
            Large = queries >= LargeBackfillQueryThreshold,
            BlockedReason = blocked
        });
    }

    /// <summary>估算量達到這個數就提醒「一晚可能跑不完」。實機併發上限 8、單次 historicdata 往返
    /// 以秒計，五千個 sensor 已是數小時等級。刻意不開設定——它是提醒不是閘門。</summary>
    private const int PrtgFetchScopeSensorWarnThreshold = 5000;

    /// <summary>快照在保留期內累積列數達到這個數就發出警告（暫定值，約 2000 萬列）。</summary>
    private const long SnapshotRowsWarnThreshold = 20_000_000;

    /// <summary>PRTG 資源守門受監看感測器預覽（批次F 階段4）</summary>
    /// <param name="forceAuto">
    /// true＝忽略覆寫清單、強制走自動偵測。維護頁「自動偵測並填入」按鈕用它重抓一份 objid；
    /// 不帶這個參數時維持既有行為（覆寫清單非空就原樣回傳）。
    /// </param>
    [HttpGet("prtg-resource-guard/preview")]
    public async Task<ApiResponse<PrtgResourceGuardPreviewResultDto>> PreviewPrtgResourceGuard(
        CancellationToken ct, [FromQuery] bool forceAuto = false)
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Get();
        var (_, autoDetectedAt) = PrtgResourceGuardAutoDetect.Describe(_backend, settings);

        var isOverride = !forceAuto
            && settings.PrtgResourceGuardSensorObjids != null
            && settings.PrtgResourceGuardSensorObjids.Count > 0;
        var source = isOverride ? "override" : "auto";  // 實際來源在下方依鏡像有無資料再決定

        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings))
        {
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = "尚未設定 PRTG 連線位址或認證資訊，無法預覽受監看感測器。",
                Source = source,
                AutoDetectedAt = autoDetectedAt,
                Warnings = Array.Empty<string>(),
                Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
            });
        }

        var console = new ResourceGuardWarningConsole();
        var sentinelStore = new SentinelStore(_backend.Blob("sentinels"));
        var sentinels = sentinelStore.GetAll();
        var prtgStore = _backend.PrtgStore();

        // 資料來源（docs/PRTG-SPEC.md §12）：鏡像是空的、或使用者按了「自動偵測並填入」時直接查 PRTG。
        // 讀鏡像的偵測在 PRTG 剛啟用時必然一無所獲，那正是使用者最需要這顆按鈕的時候。
        // 走覆寫清單時不需要裝置資料，維持讀鏡像即可（那條路徑只用 sensor 分類）。
        var mirrorDeviceCount = prtgStore.GetAllDevices().Count;
        var useLive = !isOverride && (forceAuto || mirrorDeviceCount == 0);

        PrtgResourceGuardTargetResult targets;
        PrtgClient? liveClient = null;
        try
        {
            if (useLive)
            {
                try
                {
                    liveClient = PrtgClientFactory.Create(settings);
                    targets = PrtgResourceGuardTargets.Resolve(
                        new PrtgLiveGuardSource(liveClient, ct, console), settings, sentinels, console,
                        new PrtgAddressResolver(), ignoreOverride: forceAuto);
                    source = "live";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 請求本身被取消（使用者關掉分頁）要穿透，不能當成「PRTG 連不上」繼續往下打第二個連線。
                    // 只在 ct 真的取消時放行：HttpClient 逾時也是 OperationCanceledException，那種要退回鏡像。
                    throw;
                }
                catch (Exception liveEx)
                {
                    // 直接查 PRTG 失敗（連不上、認證錯、逾時）就退回鏡像，並把原因說出來——
                    // 靜默退回會讓使用者以為「PRTG 上真的沒有這些裝置」。
                    console.WriteLine($"[PRTG資源守門] 直接查詢 PRTG 失敗（{liveEx.Message}），改用本機鏡像資料。");
                    targets = PrtgResourceGuardTargets.Resolve(
                        new PrtgMirrorGuardSource(prtgStore), settings, sentinels, console,
                        new PrtgAddressResolver(), ignoreOverride: forceAuto);
                    source = "mirror-fallback";
                }
            }
            else
            {
                targets = PrtgResourceGuardTargets.Resolve(
                    new PrtgMirrorGuardSource(prtgStore), settings, sentinels, console,
                    new PrtgAddressResolver(), ignoreOverride: forceAuto);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // 釋放交給下面的 finally，這裡不重複做
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = $"解析受監看目標失敗：{ex.Message}",
                Source = source,
                AutoDetectedAt = autoDetectedAt,
                Warnings = console.Messages,
                Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
            });
        }
        finally
        {
            liveClient?.Dispose();
        }

        try
        {
            using var client = PrtgClientFactory.Create(settings);
            var probeResult = await PrtgResourceGuardProbe.FetchSensorValuesAsync(client, targets.SensorObjids, ct);

            if (!probeResult.IsSuccess)
            {
                return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
                {
                    Success = false,
                    ErrorMessage = probeResult.FailureReason ?? "PRTG 取值失敗。",
                    Source = source,
                    AutoDetectedAt = autoDetectedAt,
                    Warnings = console.Messages,
                    Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
                });
            }

            var sensors = new List<PrtgResourceGuardSensorPreviewDto>();
            foreach (var id in targets.SensorObjids)
            {
                probeResult.Values.TryGetValue(id, out var val);
                targets.SensorCategories.TryGetValue(id, out var cat);

                sensors.Add(new PrtgResourceGuardSensorPreviewDto
                {
                    Objid = id,
                    Device = val?.Device,
                    Sensor = val?.Sensor,
                    Category = cat ?? PrtgResourceGuardTargets.CategoryUnknown,
                    Status = val?.Status,
                    Percentage = val?.Percentage,
                    UnmeasurableReason = val?.UnmeasurableReason
                });
            }

            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = true,
                Source = source,
                AutoDetectedAt = autoDetectedAt,
                Warnings = console.Messages,
                Sensors = sensors
            });
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = $"PRTG 預覽失敗：{ex.Message}",
                Source = source,
                AutoDetectedAt = autoDetectedAt,
                Warnings = console.Messages,
                Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
            });
        }
    }

    // ── PRTG 鏡像狀態（PRTG 第 1 輪批次F）────────────────────────────────────────

    [HttpGet("prtg-mirror")]
    public ApiResponse<PrtgMirrorStatusDto> GetPrtgMirrorStatus()
    {
        var store = _backend.PrtgStore();
        var summary = store.GetMirrorSummary();

        // 搜尋最近有對應資料的日期（至多往前找 30 天）
        var (mapDate, hostMaps) = store.GetLatestHostMapWithDate(PrtgHostMapLookup.LatestLookbackDays);

        var whitelist = _settings.Get().PrtgSensorTypeWhitelist;
        var coverage = store.GetWhitelistCoverage(whitelist, mapDate);

        // 主機口徑：與「未對應主機」清單、主機頁篩選同一份判定（PrtgHostCoverage）
        var unmapped = PrtgHostCoverage.Unmapped(_hosts.GetAll(), hostMaps, store.GetIpExcludes().Select(e => e.Ip), new PrtgAddressResolver());
        var nameMismatchHosts = PrtgHostCoverage.NameMismatchHostIds(hostMaps).Count;

        DateTime? snapshotLastAt = null;
        int snapshotSensors = 0;
        int snapshotIntervalMinutes = 0;
        int snapshotConsecutiveFailures = 0;
        bool snapshotBackingOff = false;
        string? snapshotSkipReason = null;
        int snapshotPendingSamples = 0;

        var st = _snapshot.GetStatus();
        snapshotLastAt = st.LastSuccessAt;
        snapshotSensors = st.LastSensorCount;
        snapshotIntervalMinutes = st.IntervalMinutes;
        snapshotConsecutiveFailures = st.ConsecutiveFailures;
        snapshotBackingOff = st.IntervalMinutes > PrtgFetchStrategy.Profile(_settings.Get().PrtgFetchStrategy).SnapshotIntervalMinutes;
        snapshotSkipReason = st.LastSkipReason;
        snapshotPendingSamples = st.PendingSamples;

        return ApiResponse<PrtgMirrorStatusDto>.Ok(new PrtgMirrorStatusDto
        {
            MappedHosts = PrtgHostCoverage.MappedHostIds(hostMaps).Count,
            UnmappedHosts = unmapped.Count,
            ConflictHosts = unmapped.Count(h => h.Reason == PrtgHostCoverage.ReasonConflict),
            NameMismatchHosts = nameMismatchHosts,
            SensorCount = summary.SensorCount,
            LastDeviceSync = summary.LastDeviceSync,
            LastSensorSync = summary.LastSensorSync,
            LastValueAt = summary.LastValueAt,
            LastStateChangeAt = summary.LastStateChangeAt,
            MapDate = mapDate,
            WhitelistSensorCount = coverage.WhitelistSensorCount,
            OnMappedDeviceCount = coverage.OnMappedDeviceCount,
            IpExcludeCount = store.GetIpExcludes().Count,
            SnapshotLastAt = snapshotLastAt,
            SnapshotSensors = snapshotSensors,
            SnapshotIntervalMinutes = snapshotIntervalMinutes,
            SnapshotConsecutiveFailures = snapshotConsecutiveFailures,
            SnapshotBackingOff = snapshotBackingOff,
            SnapshotSkipReason = snapshotSkipReason,
            SnapshotPendingSamples = snapshotPendingSamples,
            Freshness = PrtgFreshnessDto.FromStore(new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)))
        });
    }

    // ── 監看範圍外資料清除（docs/PRTG-SPEC.md §3c）────────────────────────────

    /// <summary>受影響裝置名稱最多列幾台</summary>
    private const int ScopePurgeTopDevices = 20;

    /// <summary>
    /// 以目前設定重新計算全站監看裝置，並套用範圍可信的判斷（規則 1）。
    /// 人工確認沒有「本趟」，裝置鏡像的條件以「裝置鏡像非空」判斷。預覽與確認共用這一份。
    /// </summary>
    private (EfPrtgStore Store, PrtgScopeResult? Scope, string? Blocked) ComputeScopeForPurge(StorageBackend backend)
    {
        var store = backend.PrtgStore();
        PrtgScopeResult? scope = null;
        try
        {
            var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            scope = PrtgScopeDevices.Compute(
                store, _hosts, new PrtgMirrorGuardSource(store), settings,
                new SentinelStore(backend.Blob("sentinels")).GetAll(), new ResourceGuardWarningConsole(), new PrtgAddressResolver(),
                hostIds: null);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "監看裝置計算失敗，不提供範圍外清除");
        }
        var blocked = PrtgScopePurge.CheckScope(scope, store.GetMirrorSummary().DeviceCount > 0);
        return (store, scope, blocked);
    }

    [HttpGet("prtg-scope-purge/preview")]
    public ApiResponse<PrtgScopePurgePreviewDto> PreviewPrtgScopePurge()
    {
        var authorization = _scopePurgeAuthorization.Check(_currentUser.UserId);
        if (!authorization.Allowed) throw DomainException.Forbidden(authorization.DenialReason ?? "目前沒有維護權限。");

        var (store, scope, blocked) = ComputeScopeForPurge(_backend);
        var baseline = store.ScopeBaseline().Get();
        var dto = new PrtgScopePurgePreviewDto
        {
            MonitoredDevices = scope?.DeviceObjids.Count ?? 0,
            BlockedReason = baseline.BlockedReason,
            BlockedAt = baseline.BlockedAt,
            BaselineAt = baseline.HasBaseline ? baseline.At : null,
            BaselineDeviceCount = baseline.DeviceCount
        };
        if (blocked != null)
        {
            dto.ErrorMessage = $"{blocked}，目前不能清除範圍外資料。";
            return ApiResponse<PrtgScopePurgePreviewDto>.Ok(dto);
        }

        var capabilities = _scopePurgeAuthorization.CurrentCapabilities();
        var detailsRestricted = !capabilities.Contains(Capability.ViewAll);
        IReadOnlySet<long>? visibleDevices = null;
        if (detailsRestricted)
        {
            var visibleHosts = _currentUser.IsServerAdmin
                ? new HashSet<long>()
                : _visibility.GetVisibleHostIds();
            var visible = store.GetLatestHostMap().Where(x => x.HostId.HasValue && visibleHosts.Contains(x.HostId.Value))
                .Select(x => x.DeviceObjid).ToHashSet();
            visible.UnionWith(store.GetManualMaps().Where(x => visibleHosts.Contains(x.HostId)).Select(x => x.DeviceObjid));
            visibleDevices = visible;
        }
        var keepSet = PrtgScopePurge.KeepSet(scope!);
        var fingerprint = PrtgScopePurge.ComputeFingerprint(store, scope!);
        var jobs = store.ScopePurgeJobs();
        var previous = jobs.GetLatestStoppedPartial(_currentUser.UserId, authorization.AuthFingerprint);
        PrtgScopePurgeJobRow purgePreview;
        try
        {
            purgePreview = jobs.CreatePreview(_currentUser.UserId,
                authorization.PermissionRevision, authorization.AuthFingerprint, fingerprint, keepSet,
                scope!.DeviceObjids.Count, DateTimeOffset.UtcNow, previous?.JobId, scope.ScopeFenceJson);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        PrtgOutOfScopePreview preview;
        try
        {
            var boundary = jobs.GetPreviewBoundary(purgePreview.JobId, _currentUser.UserId);
            preview = store.PreviewOutOfScopeCandidateDetails(boundary.CandidateSensorIds, ScopePurgeTopDevices,
                visibleDevices);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        // The detail projection runs after the candidate/watermark snapshot. If scope, mapping,
        // or source generation moved while details were read, do not return a preview that could
        // only be rejected at confirmation time.
        if (!jobs.IsCurrentScopeFence(purgePreview.JobId, _currentUser.UserId) ||
            !string.Equals(fingerprint, PrtgScopePurge.ComputeFingerprint(store, scope!), StringComparison.Ordinal))
            throw DomainException.Conflict("監看範圍或對應在預覽期間已變更，請重新預覽。");

        dto.Success = true;
        dto.Values = checked((int)purgePreview.CandidateValues);
        dto.StateChanges = checked((int)purgePreview.CandidateStateChanges);
        dto.AffectedDevices = preview.AffectedDevices;
        dto.TopDeviceNames = preview.TopDeviceNames.ToList();
        dto.UnknownSensors = preview.UnknownSensors;
        dto.PreviewId = purgePreview.JobId;
        dto.ExpiresAtUtc = purgePreview.AcceptedAtUtc.AddMinutes(5);
        dto.VisibleAffectedDevices = preview.VisibleAffectedDevices;
        dto.DetailsRestricted = detailsRestricted;
        if (previous != null)
        {
            dto.PreviousJobId = previous.JobId;
            dto.PreviousDeletedValues = previous.DeletedValues;
            dto.PreviousDeletedStateChanges = previous.DeletedStateChanges;
            dto.PreviousRemainingValues = previous.RemainingValues;
            dto.PreviousRemainingStateChanges = previous.RemainingStateChanges;
        }
        return ApiResponse<PrtgScopePurgePreviewDto>.Ok(dto);
    }

    private sealed class ResourceGuardWarningConsole : IRunConsole
    {
        private readonly List<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages;

        public void WriteLine(string message = "")
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Add(message);
            }
        }
    }
}

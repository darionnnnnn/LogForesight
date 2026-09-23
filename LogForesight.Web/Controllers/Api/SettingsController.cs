using System.Text.Encodings.Web;
using System.Text.Json;
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

/// <summary>全站系統設定（「系統管理 > 設定」頁）。整個 Controller 需要 Maintain 能力</summary>
[ApiController]
[Route("api/admin/settings")]
[Permission(Capability.Maintain)]
public class SettingsController : ControllerBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ISystemSettingsService _settings;
    private readonly AiUsageStore _aiUsage;
    private readonly IAuditService _audit;
    private readonly PrtgProbeService? _prtgProbe;
    private readonly PrtgBackfillService? _prtgBackfill;
    private readonly PrtgStructureSyncService? _prtgStructureSync;
    private readonly IPrtgHostMapRefresher? _mapRefresher;
    private readonly StorageBackend? _backend;
    private readonly IHostStore? _hosts;
    private readonly PrtgSnapshotHostedService? _snapshot;
    private readonly PrtgDeviceIndexCache? _deviceIndexCache;

    public SettingsController(
        ISystemSettingsService settings,
        AiUsageStore aiUsage,
        IAuditService audit,
        PrtgProbeService? prtgProbe = null,
        PrtgBackfillService? prtgBackfill = null,
        PrtgStructureSyncService? prtgStructureSync = null,
        IPrtgHostMapRefresher? mapRefresher = null,
        StorageBackend? backend = null,
        IHostStore? hosts = null,
        PrtgSnapshotHostedService? snapshot = null,
        PrtgDeviceIndexCache? deviceIndexCache = null)
    {
        _hosts = hosts;
        _settings = settings;
        _aiUsage = aiUsage;
        _audit = audit;
        _prtgProbe = prtgProbe;
        _prtgBackfill = prtgBackfill;
        _prtgStructureSync = prtgStructureSync;
        _mapRefresher = mapRefresher;
        _backend = backend;
        _snapshot = snapshot;
        _deviceIndexCache = deviceIndexCache;
    }

    [HttpGet]
    public ApiResponse<SystemSettingsDto> Get() =>
        ApiResponse<SystemSettingsDto>.Ok(_settings.Get());

    [HttpPut]
    public ApiResponse<SystemSettingsDto> Update([FromBody] UpdateSystemSettingsRequest request)
    {
        // 使用者名稱顯示規則是自由文字的正則表達式，資料註解擋不住語法錯誤——
        // 存檔前先驗一次，訊息指出是第幾行（回饋三十四輪 B1）
        if (!AccountDisplayFormatter.TryValidateRules(request.AccountDisplayRules, out var error))
        {
            throw DomainException.Validation(error);
        }

        return ApiResponse<SystemSettingsDto>.Ok(_settings.Update(request));
    }

    /// <summary>AD 測試連線（docs/archive/HISTORY.md #9）：用表單目前填的值＋管理者當場輸入的帳密試 bind</summary>
    [HttpPost("ad-test")]
    public ApiResponse<TestAdConnectionResultDto> TestAdConnection([FromBody] TestAdConnectionRequest request) =>
        ApiResponse<TestAdConnectionResultDto>.Ok(_settings.TestAdConnection(request));

    /// <summary>測試寄信（回饋十五輪批次D）：用表單目前填的值試寄一封信，密碼留空沿用已儲存的密碼</summary>
    [HttpPost("mail-test")]
    public async Task<ApiResponse<TestMailResultDto>> TestMail([FromBody] TestMailRequest request) =>
        ApiResponse<TestMailResultDto>.Ok(await _settings.TestMail(request));

    /// <summary>PRTG 測試連線（PRTG 第 1 輪批次B）：用表單目前填的值試連線，token 留空沿用已儲存的 token</summary>
    [HttpPost("prtg-test")]
    public async Task<ApiResponse<TestPrtgConnectionResultDto>> TestPrtg([FromBody] TestPrtgConnectionRequest request, CancellationToken ct)
        => ApiResponse<TestPrtgConnectionResultDto>.Ok(await _settings.TestPrtgAsync(request, ct));

    /// <summary>PRTG 設定專屬更新（PRTG 維護頁，docs/archive/FEEDBACK-37-PLAN.md 批次F1）：只更新 PRTG 欄位，不動其他設定</summary>
    [HttpPut("prtg")]
    public ApiResponse<SystemSettingsDto> UpdatePrtg([FromBody] UpdatePrtgSettingsRequest request) =>
        ApiResponse<SystemSettingsDto>.Ok(_settings.UpdatePrtg(request));

    /// <summary>AI token 用量統計（回饋二十七輪作業 B）：今日／累計＋近 30 天每日明細</summary>
    [HttpGet("ai-usage")]
    public ApiResponse<AiUsageDto> GetAiUsage() =>
        ApiResponse<AiUsageDto>.Ok(AiUsageDto.From(_aiUsage, AiUsageDto.TableDays));

    /// <summary>清空重新計算：每日與累計歸零、起算日重設為今天（不可復原）</summary>
    [HttpPost("ai-usage/reset")]
    public ApiResponse<AiUsageDto> ResetAiUsage()
    {
        // 清空前先取一次舊值：這個操作不可復原，稽核只留「清掉了」而不留「清掉多少」的話，
        // 事後就沒有任何依據能回答「當時累計到哪裡」（同 controller 其餘破壞性操作都有稽核）
        var before = _aiUsage.Get().Total;

        _aiUsage.Reset();

        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: $"清空 AI token 用量統計（清空前累計 {before.Calls} 次呼叫、{before.TotalTokens} tokens）",
            targetKind: "ai_usage",
            targetId: AiUsageStore.BlobKey,
            detail: new
            {
                BeforeCalls = before.Calls,
                BeforePromptTokens = before.PromptTokens,
                BeforeCompletionTokens = before.CompletionTokens,
                BeforeTotalTokens = before.TotalTokens
            });

        return ApiResponse<AiUsageDto>.Ok(AiUsageDto.From(_aiUsage, AiUsageDto.TableDays));
    }

    // ── PRTG API 探測（probe，PRTG 第 1 輪批次B-3）──────────────────────────────────

    [HttpGet("prtg-probe/status")]
    public ApiResponse<PrtgProbeStatusDto> GetPrtgProbeStatus() =>
        ApiResponse<PrtgProbeStatusDto>.Ok(_prtgProbe?.GetStatus() ?? new PrtgProbeStatusDto());

    [HttpPost("prtg-probe/start")]
    public ApiResponse<StartPrtgProbeResultDto> StartPrtgProbe()
    {
        if (_prtgProbe == null)
            throw DomainException.Validation("PRTG 探測服務未啟用。");

        if (!_prtgProbe.TryStart(out var error, out var isConflict))
        {
            // 與回填、結構同步同一套：被另一個執行擋下是 409，設定不齊是 400（§7.2）
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 探測。");
        }

        _audit.Record(
            action: AuditActions.PrtgProbeRun,
            summary: "執行 PRTG API 環境探測",
            targetKind: "system_settings",
            targetId: "prtg_probe",
            detail: new { });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(new StartPrtgProbeResultDto { Started = true });
    }

    [HttpPost("prtg-probe/data-flow/start")]
    public ApiResponse<StartPrtgProbeResultDto> StartPrtgDataFlowProbe()
    {
        if (_prtgProbe == null)
            throw DomainException.Validation("PRTG 探測服務未啟用。");

        if (!_prtgProbe.TryStartDataFlow(out var error, out var isConflict))
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 小範圍驗證。");

        _audit.Record(
            action: AuditActions.PrtgProbeRun,
            summary: "執行 PRTG 小範圍資料流驗證",
            targetKind: "system_settings",
            targetId: "prtg_probe_data_flow",
            detail: new { Scope = "one_host_one_sensor_one_day" });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(new StartPrtgProbeResultDto { Started = true });
    }

    /// <summary>
    /// 停止進行中的環境探測。
    /// </summary>
    [HttpPost("prtg-probe/cancel")]
    public ApiResponse<StartPrtgProbeResultDto> CancelPrtgProbe()
    {
        if (_prtgProbe == null)
            throw DomainException.Validation("PRTG 探測服務未啟用。");

        if (!_prtgProbe.TryCancel())
            throw DomainException.Conflict("目前沒有進行中的環境探測。");

        _audit.Record(
            action: AuditActions.PrtgProbeCancel,
            summary: "停止環境探測",
            targetKind: "system_settings",
            targetId: "prtg_probe",
            detail: new { });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(
            new StartPrtgProbeResultDto { Started = false });
    }

    // ── PRTG 歷史回填（PRTG 第 1 輪批次E）────────────────────────────────────────

    [HttpGet("prtg-backfill/status")]
    public ApiResponse<PrtgBackfillStatusDto> GetPrtgBackfillStatus() =>
        ApiResponse<PrtgBackfillStatusDto>.Ok(_prtgBackfill?.GetStatus() ?? new PrtgBackfillStatusDto());

    [HttpPost("prtg-backfill/start")]
    public ApiResponse<StartPrtgBackfillResultDto> StartPrtgBackfill()
    {
        if (_prtgBackfill == null)
            throw DomainException.Validation("PRTG 回填服務未啟用。");

        if (!_prtgBackfill.TryStart(out var error, out var isConflict))
        {
            // 被互斥擋下（探測／取數／結構同步／回填自己在跑）是狀態衝突，回 409；
            // 設定或前提不齊仍是 400。與結構同步端點同一套分支。
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 歷史回填。");
        }

        _audit.Record(
            action: AuditActions.PrtgBackfillRun,
            summary: "執行 PRTG 歷史資料回填",
            targetKind: "system_settings",
            targetId: "prtg_backfill",
            detail: new { });

        return ApiResponse<StartPrtgBackfillResultDto>.Ok(new StartPrtgBackfillResultDto { Started = true });
    }

    /// <summary>
    /// 停止進行中的歷史回填：回填逐日打 PRTG，天數多時可跑上數小時；
    /// 沒有這顆鈕時唯一的中止方式是重啟站台。
    /// </summary>
    [HttpPost("prtg-backfill/cancel")]
    public ApiResponse<StartPrtgBackfillResultDto> CancelPrtgBackfill()
    {
        if (_prtgBackfill == null)
            throw DomainException.Validation("PRTG 回填服務未啟用。");

        // 沒有執行中就不是「停止成功」——回 409，與結構同步停止同一種語意。
        if (!_prtgBackfill.TryCancel())
            throw DomainException.Conflict("目前沒有進行中的歷史回填。");

        _audit.Record(
            action: AuditActions.PrtgBackfillCancel,
            summary: "停止 PRTG 歷史回填",
            targetKind: "system_settings",
            targetId: "prtg_backfill",
            detail: new { });

        return ApiResponse<StartPrtgBackfillResultDto>.Ok(
            new StartPrtgBackfillResultDto { Started = false });
    }

    // ── PRTG 同步結構與對應（docs/PRTG-SPEC.md §5a）───────────────────────

    [HttpGet("prtg-structure-sync/status")]
    public ApiResponse<PrtgStructureSyncStatusDto> GetPrtgStructureSyncStatus() =>
        ApiResponse<PrtgStructureSyncStatusDto>.Ok(
            _prtgStructureSync?.GetStatus() ?? new PrtgStructureSyncStatusDto());

    [HttpPost("prtg-structure-sync/start")]
    public ApiResponse<StartPrtgStructureSyncResultDto> StartPrtgStructureSync()
    {
        if (_prtgStructureSync == null)
            throw DomainException.Validation("PRTG 同步服務未啟用。");

        if (!_prtgStructureSync.TryStart(out var error, out var isConflict))
        {
            // 被互斥擋下（取數執行中、或同步已在跑）是狀態衝突而非輸入錯誤——
            // 回 409 讓呼叫端分得出「你送錯了」與「現在不行，等一下再試」。
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 結構同步。");
        }

        _audit.Record(
            action: AuditActions.PrtgStructureSyncRun,
            summary: "執行 PRTG 結構同步與主機對應",
            targetKind: "system_settings",
            targetId: "prtg_structure_sync",
            detail: new { });

        return ApiResponse<StartPrtgStructureSyncResultDto>.Ok(
            new StartPrtgStructureSyncResultDto { Started = true });
    }

    /// <summary>
    /// 中止進行中的結構同步（docs/PRTG-SPEC.md §5a）。這條路徑要對 PRTG 爬整棵樹，
    /// 大型環境會跑上數十分鐘；沒有這顆鈕時唯一的中止方式是重啟站台。
    /// </summary>
    [HttpPost("prtg-structure-sync/cancel")]
    public ApiResponse<StartPrtgStructureSyncResultDto> CancelPrtgStructureSync()
    {
        if (_prtgStructureSync == null)
            throw DomainException.Validation("PRTG 同步服務未啟用。");

        // 沒有執行中就不是「停止成功」——回 409 與 start 被互斥擋下時同一種語意。
        if (!_prtgStructureSync.TryCancel())
            throw DomainException.Conflict("目前沒有進行中的結構同步。");

        _audit.Record(
            action: AuditActions.PrtgStructureSyncCancel,
            summary: "中止 PRTG 結構同步",
            targetKind: "system_settings",
            targetId: "prtg_structure_sync",
            detail: new { });

        return ApiResponse<StartPrtgStructureSyncResultDto>.Ok(
            new StartPrtgStructureSyncResultDto { Started = false });
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

        if (_backend == null)
        {
            return ApiResponse<PrtgValueFetchScopeEstimateDto>.Ok(new PrtgValueFetchScopeEstimateDto
            {
                Success = false,
                ErrorMessage = "資料存放區未啟用，無法估算。"
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
        var mapRows = prtgStore.GetLatestHostMapWithDate(30, DateTime.Today).Rows
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
            var allHosts = _hosts?.GetAll() ?? new List<WebHost>();
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
            SnapshotWarning = snapshotWarning
        });
    }

    /// <summary>粗估用的每次 historicdata 查詢平均秒數（暫定）。</summary>
    private const double PrtgValueQuerySeconds = 1.5;

    /// <summary>
    /// 立即執行「一併補齊 PRTG 逐小時數值」的查詢量粗估：目前監看裝置上的白名單感測器數 × 天數。
    /// 帶 segment 時只算那個網段的主機（與立即執行的網段範圍同一套解析）。
    /// </summary>
    [HttpGet("prtg-estimate")]
    public ApiResponse<PrtgValuesEstimateDto> EstimatePrtgValues([FromQuery] int days, [FromQuery] string? segment = null)
    {
        if (days < 1)
            throw DomainException.Validation("天數必須大於等於 1。");
        if (_backend == null || _hosts == null)
            throw DomainException.Validation("資料存放區未啟用，無法估算。");

        var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        var sentinels = new SentinelStore(_backend.Blob("sentinels"));
        var hostIds = segment == null ? null : ScheduleController.ResolveSegmentHostIds(segment, _hosts, sentinels);

        var store = _backend.PrtgStore();
        var scope = PrtgScopeDevices.Compute(
            store, _hosts, new PrtgMirrorGuardSource(store), settings, sentinels.GetAll(),
            new ResourceGuardWarningConsole(), new PrtgAddressResolver(), hostIds);
        var sensors = store.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist, scope.DeviceObjids.ToList()).Count;
        var queries = (long)sensors * days;
        var concurrency = Math.Max(1, settings.PrtgFetchConcurrency);

        return ApiResponse<PrtgValuesEstimateDto>.Ok(new PrtgValuesEstimateDto
        {
            Sensors = sensors,
            Queries = queries,
            Minutes = (int)Math.Ceiling(queries * PrtgValueQuerySeconds / concurrency / 60)
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
        if (_backend == null)
        {
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = "資料存放區未啟用，無法預覽。",
                Source = "auto",
                Warnings = Array.Empty<string>(),
                Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
            });
        }

        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Get();

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
        catch (Exception ex)
        {
            // 釋放交給下面的 finally，這裡不重複做
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = $"解析受監看目標失敗：{ex.Message}",
                Source = source,
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
                Warnings = console.Messages,
                Sensors = sensors
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ApiResponse<PrtgResourceGuardPreviewResultDto>.Ok(new PrtgResourceGuardPreviewResultDto
            {
                Success = false,
                ErrorMessage = $"PRTG 預覽失敗：{ex.Message}",
                Source = source,
                Warnings = console.Messages,
                Sensors = Array.Empty<PrtgResourceGuardSensorPreviewDto>()
            });
        }
    }

    // ── PRTG 鏡像狀態（PRTG 第 1 輪批次F）────────────────────────────────────────

    [HttpGet("prtg-mirror")]
    public ApiResponse<PrtgMirrorStatusDto> GetPrtgMirrorStatus()
    {
        if (_backend == null)
        {
            return ApiResponse<PrtgMirrorStatusDto>.Ok(new PrtgMirrorStatusDto());
        }

        var store = _backend.PrtgStore();
        var summary = store.GetMirrorSummary();

        // 搜尋最近有對應資料的日期（至多往前找 30 天）
        var (mapDate, hostMaps) = store.GetLatestHostMapWithDate(30);

        var whitelist = _settings.Get().PrtgSensorTypeWhitelist;
        var coverage = store.GetWhitelistCoverage(whitelist, mapDate);

        var mapOk = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Ok);
        var mapConflict = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Conflict);
        var mapUnmatched = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Unmatched);

        DateTime? snapshotLastAt = null;
        int snapshotSensors = 0;
        int snapshotIntervalMinutes = 0;
        int snapshotConsecutiveFailures = 0;
        bool snapshotBackingOff = false;
        string? snapshotSkipReason = null;

        if (_snapshot != null)
        {
            var st = _snapshot.GetStatus();
            snapshotLastAt = st.LastSuccessAt;
            snapshotSensors = st.LastSensorCount;
            snapshotIntervalMinutes = st.IntervalMinutes;
            snapshotConsecutiveFailures = st.ConsecutiveFailures;
            snapshotBackingOff = st.IntervalMinutes > PrtgFetchStrategy.Profile(_settings.Get().PrtgFetchStrategy).SnapshotIntervalMinutes;
            snapshotSkipReason = st.LastSkipReason;
        }

        return ApiResponse<PrtgMirrorStatusDto>.Ok(new PrtgMirrorStatusDto
        {
            DeviceCount = summary.DeviceCount,
            SensorCount = summary.SensorCount,
            LastDeviceSync = summary.LastDeviceSync,
            LastSensorSync = summary.LastSensorSync,
            LastValueAt = summary.LastValueAt,
            LastStateChangeAt = summary.LastStateChangeAt,
            MapDate = mapDate,
            MapOk = mapOk,
            MapConflict = mapConflict,
            MapUnmatched = mapUnmatched,
            WhitelistSensorCount = coverage.WhitelistSensorCount,
            OnMappedDeviceCount = coverage.OnMappedDeviceCount,
            IpExcludeCount = store.GetIpExcludes().Count,
            SnapshotLastAt = snapshotLastAt,
            SnapshotSensors = snapshotSensors,
            SnapshotIntervalMinutes = snapshotIntervalMinutes,
            SnapshotConsecutiveFailures = snapshotConsecutiveFailures,
            SnapshotBackingOff = snapshotBackingOff,
            SnapshotSkipReason = snapshotSkipReason,
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
                store, new HostStore(backend.Blob("hosts")), new PrtgMirrorGuardSource(store), settings,
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
        if (_backend == null)
        {
            return ApiResponse<PrtgScopePurgePreviewDto>.Ok(new PrtgScopePurgePreviewDto
            {
                ErrorMessage = "資料存放區未啟用，無法預覽。"
            });
        }

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

        var preview = store.PreviewOutOfScopeData(PrtgScopePurge.KeepSet(scope!), ScopePurgeTopDevices);
        dto.Success = true;
        dto.Values = preview.Values;
        dto.StateChanges = preview.StateChanges;
        dto.AffectedDevices = preview.AffectedDevices;
        dto.TopDeviceNames = preview.TopDeviceNames.ToList();
        dto.UnknownSensors = preview.UnknownSensors;
        return ApiResponse<PrtgScopePurgePreviewDto>.Ok(dto);
    }

    [HttpPost("prtg-scope-purge/confirm")]
    public ApiResponse<PrtgScopePurgeResultDto> ConfirmPrtgScopePurge()
    {
        if (_backend == null)
            throw DomainException.Validation("資料存放區未啟用，無法清除。");
        if (_prtgBackfill == null)
            throw DomainException.Validation("PRTG 回填服務未啟用，無法判斷是否有其他 PRTG 作業執行中。");

        // 與取數、結構同步、回填互斥：它們正在寫鏡像，範圍與資料都還在變
        var conflict = _prtgBackfill.ScopePurgeConflict();
        if (conflict != null)
            throw DomainException.Conflict(conflict);

        var (store, scope, blocked) = ComputeScopeForPurge(_backend);
        if (blocked != null)
            throw DomainException.Validation($"{blocked}，目前不能清除範圍外資料。");

        var (values, stateChanges) = store.DeleteOutOfScopeData(PrtgScopePurge.KeepSet(scope!));
        PrtgScopePurge.RecordBaseline(store, scope!.DeviceObjids.Count);

        _audit.Record(
            action: AuditActions.PrtgScopePurge,
            summary: $"清除監看範圍外的 PRTG 資料：數值 {values} 筆、狀態變更 {stateChanges} 筆",
            targetKind: "system_settings",
            targetId: "prtg_scope_purge",
            detail: new { values, stateChanges, monitoredDevices = scope.DeviceObjids.Count });

        return ApiResponse<PrtgScopePurgeResultDto>.Ok(new PrtgScopePurgeResultDto
        {
            Values = values,
            StateChanges = stateChanges,
            MonitoredDevices = scope.DeviceObjids.Count
        });
    }

    /// <summary>PRTG 主機對應衝突清單分頁</summary>
    [HttpGet("prtg-host-map")]
    public ApiResponse<PrtgHostMapPageDto> GetPrtgHostMap(
        [FromQuery] string status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var isConflict = string.Equals(status, PrtgMapStatus.Conflict, StringComparison.OrdinalIgnoreCase);
        var isUnmatched = string.Equals(status, PrtgMapStatus.Unmatched, StringComparison.OrdinalIgnoreCase);
        if (!isConflict && !isUnmatched)
        {
            throw DomainException.Validation("status 僅支援 conflict 或 unmatched 查詢。");
        }

        var (normPage, normPageSize) = Paging.Normalize(page, pageSize);

        if (_backend == null)
        {
            return ApiResponse<PrtgHostMapPageDto>.Ok(new PrtgHostMapPageDto
            {
                Page = normPage,
                PageSize = normPageSize
            });
        }

        var store = _backend.PrtgStore();
        var (mapDate, hostMaps) = store.GetLatestHostMapWithDate(30);

        var targetStatus = isConflict ? PrtgMapStatus.Conflict : PrtgMapStatus.Unmatched;

        // 依 DeviceObjid 排序後才分頁：GetLatestHostMapWithDate 的查詢沒有 ORDER BY，
        // 未排序就分頁時同一列可能在兩頁重複出現、也可能整列被跳過。
        var filteredRows = hostMaps
            .Where(m => string.Equals(m.MapStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeviceObjid)
            .ToList();

        var total = filteredRows.Count;
        var pagedRows = filteredRows
            .Skip((normPage - 1) * normPageSize)
            .Take(normPageSize)
            .ToList();

        // 裝置索引（objid → 裝置、正規化 IP → 同 IP 裝置）改走短 TTL 的跨請求快取
        // （回饋四十五輪 B5）：原本每翻一頁都把裝置全表讀回來重建一次索引，
        // 而裝置表是結構鏡像、一天只在夜間同步時變一次。
        // 沒注入快取時（單元測試直接 new controller）退回「當場建一份」，行為完全相同。
        var deviceIndex = _deviceIndexCache != null
            ? _deviceIndexCache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, () => store.GetAllDevices())
            : new PrtgDeviceIndex(store.GetAllDevices());

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var activeHosts = hostStore.GetAll()
            .Where(h => h.Active && h.MergedInto == null)
            .ToList();

        var hostsByNormIp = new Dictionary<string, List<WebHost>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in activeHosts)
        {
            var normIp = PrtgHostMapper.NormalizeIp(h.IpAddress);
            if (normIp == null) continue;
            if (!hostsByNormIp.TryGetValue(normIp, out var list))
            {
                list = new List<WebHost>();
                hostsByNormIp[normIp] = list;
            }
            list.Add(h);
        }

        var items = new List<PrtgConflictItemDto>(pagedRows.Count);
        foreach (var row in pagedRows)
        {
            var device = deviceIndex.ByObjid(row.DeviceObjid);
            var normIp = PrtgHostMapper.NormalizeIp(row.Ip);

            var sameDevices = deviceIndex.ByNormIp(normIp);

            var isMultiDevice = sameDevices.Count > 1;
            string conflictKind;
            if (isUnmatched)
            {
                conflictKind = "unmatched";
            }
            else
            {
                conflictKind = isMultiDevice ? "multi-device" : "multi-host";
            }

            List<PrtgConflictDeviceDto> sameIpDevices;
            if (isMultiDevice)
            {
                sameIpDevices = sameDevices
                    .Select(d => new PrtgConflictDeviceDto
                    {
                        Objid = d.Objid,
                        Name = d.Name,
                        GroupPath = d.GroupPath
                    })
                    .ToList();
            }
            else
            {
                sameIpDevices = new List<PrtgConflictDeviceDto>
                {
                    new()
                    {
                        Objid = row.DeviceObjid,
                        Name = device?.Name,
                        GroupPath = device?.GroupPath
                    }
                };
            }

            List<PrtgCandidateHostDto> candidateHosts;
            if (normIp != null && hostsByNormIp.TryGetValue(normIp, out var matchedHosts))
            {
                candidateHosts = matchedHosts
                    .Select(h => new PrtgCandidateHostDto
                    {
                        HostId = h.HostId,
                        HostName = h.HostName,
                        IpAddress = h.IpAddress
                    })
                    .ToList();
            }
            else
            {
                candidateHosts = new List<PrtgCandidateHostDto>();
            }

            items.Add(new PrtgConflictItemDto
            {
                DeviceObjid = row.DeviceObjid,
                DeviceName = device?.Name,
                GroupPath = device?.GroupPath,
                Ip = row.Ip,
                HostName = isMultiDevice ? null : row.HostName,
                Note = row.Note,
                MapStatus = row.MapStatus ?? targetStatus,
                ConflictKind = conflictKind,
                SameIpDevices = sameIpDevices,
                CandidateHosts = candidateHosts
            });
        }

        return ApiResponse<PrtgHostMapPageDto>.Ok(new PrtgHostMapPageDto
        {
            MapDate = mapDate,
            Total = total,
            Page = normPage,
            PageSize = normPageSize,
            Items = items
        });
    }

    // ── PRTG 人工主機對應（PRTG 第 2 輪任務E-1）──────────────────────────────────

    /// <summary>取得全部 PRTG 人工主機對應清單（含主機名稱，供畫面顯示）</summary>
    [HttpGet("prtg-manual-map")]
    public ApiResponse<List<PrtgManualMapDto>> GetPrtgManualMaps()
    {
        if (_backend == null)
        {
            return ApiResponse<List<PrtgManualMapDto>>.Ok(new List<PrtgManualMapDto>());
        }

        var store = _backend.PrtgStore();
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var hostMap = hostStore.GetAll().ToDictionary(h => h.HostId);
        var manualMaps = store.GetManualMaps();

        var allDevices = store.GetAllDevices();
        var deviceByObjid = new Dictionary<long, PrtgDeviceRow>();
        var countByNormIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // 同 IP 上「也有人工對應」的 device 走的是人工分支，不是被略過——扣掉它們才是真正被略過的台數，
        // 否則同 IP 兩台各自人工對應時，兩列都會顯示「另有 1 台已略過」，使用者會去找一台不存在的裝置。
        var manualCountByNormIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var manualObjids = manualMaps.Select(m => m.DeviceObjid).ToHashSet();

        foreach (var d in allDevices)
        {
            deviceByObjid[d.Objid] = d;
            var normIp = PrtgHostMapper.NormalizeIp(d.Ip);
            if (normIp != null)
            {
                countByNormIp[normIp] = countByNormIp.GetValueOrDefault(normIp) + 1;
                if (manualObjids.Contains(d.Objid))
                    manualCountByNormIp[normIp] = manualCountByNormIp.GetValueOrDefault(normIp) + 1;
            }
        }

        var dtos = manualMaps.Select(m =>
        {
            var skipped = 0;
            if (deviceByObjid.TryGetValue(m.DeviceObjid, out var dev))
            {
                var normIp = PrtgHostMapper.NormalizeIp(dev.Ip);
                if (normIp != null && countByNormIp.TryGetValue(normIp, out var totalCount))
                {
                    skipped = Math.Max(0, totalCount - manualCountByNormIp.GetValueOrDefault(normIp));
                }
            }

            return new PrtgManualMapDto
            {
                DeviceObjid = m.DeviceObjid,
                HostId = m.HostId,
                HostName = hostMap.TryGetValue(m.HostId, out var host) ? host.HostName : null,
                Note = m.Note,
                CreatedBy = m.CreatedBy,
                CreatedAt = m.CreatedAt,
                SameIpSkippedCount = skipped
            };
        }).OrderBy(m => m.DeviceObjid).ToList();

        return ApiResponse<List<PrtgManualMapDto>>.Ok(dtos);
    }

    /// <summary>新增或更新一筆 PRTG 人工主機對應</summary>
    [HttpPut("prtg-manual-map")]
    public ApiResponse<PrtgManualMapDto> SetPrtgManualMap([FromBody] SetPrtgManualMapRequest request)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        var hostStore = _hosts ?? new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Get(request.HostId);
        if (host == null || !host.Active || host.MergedInto != null)
            throw DomainException.Validation("指定的主機不存在或已停用。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var store = _backend.PrtgStore();
        var row = new PrtgManualMapRow
        {
            DeviceObjid = request.DeviceObjid,
            HostId = request.HostId,
            Note = request.Note,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now
        };
        store.UpsertManualMap(row);

        _audit.Record(
            action: AuditActions.PrtgManualMapSet,
            summary: $"設定 PRTG device {request.DeviceObjid} 人工對應到主機 {host.HostName}",
            targetKind: "prtg_manual_map",
            targetId: request.DeviceObjid.ToString(),
            detail: new
            {
                request.DeviceObjid,
                request.HostId,
                host.HostName,
                request.Note
            });

        var remapWarning = _mapRefresher?.TryRefreshToday();

        var saved = store.GetManualMaps().FirstOrDefault(m => m.DeviceObjid == request.DeviceObjid);

        return ApiResponse<PrtgManualMapDto>.Ok(new PrtgManualMapDto
        {
            DeviceObjid = request.DeviceObjid,
            HostId = request.HostId,
            HostName = host.HostName,
            Note = request.Note,
            CreatedBy = saved?.CreatedBy ?? createdBy,
            CreatedAt = saved?.CreatedAt ?? row.CreatedAt,
            RemapWarning = remapWarning
        });
    }

    /// <summary>批次設定 PRTG 人工主機對應</summary>
    [HttpPut("prtg-manual-map/batch")]
    public ApiResponse<PrtgManualMapBatchResultDto> SetPrtgManualMapBatch([FromBody] SetPrtgManualMapBatchRequest request)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        if (request == null)
            throw DomainException.Validation("請求內容不可為空。");

        if (request.DeviceObjids == null || request.DeviceObjids.Count == 0)
            throw DomainException.Validation("請提供至少一個欲指派的 PRTG 裝置。");

        if (request.DeviceObjids.Any(id => id <= 0))
            throw DomainException.Validation("PRTG 裝置編號必須為正數。");

        if (request.DeviceObjids.Distinct().Count() != request.DeviceObjids.Count)
            throw DomainException.Validation("欲指派的 PRTG 裝置清單包含重複項目。");

        if (request.DeviceObjids.Count > 100)
            throw DomainException.Validation("批次指派每次最多處理 100 筆裝置。");

        if (request.Note?.Length > 512)
            throw DomainException.Validation("指派說明不可超過 512 字。");

        var hostStore = _hosts ?? new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Get(request.HostId);
        if (host == null || !host.Active || host.MergedInto != null)
            throw DomainException.Validation("指定的主機不存在或已停用。");

        var store = _backend.PrtgStore();
        var allDevices = store.GetAllDevices();
        var existingDeviceIds = allDevices.Select(d => d.Objid).ToHashSet();
        var unknownIds = request.DeviceObjids.Where(id => !existingDeviceIds.Contains(id)).ToList();
        if (unknownIds.Count > 0)
            throw DomainException.Validation($"指定的一或多個 PRTG 裝置不存在於裝置鏡像中：{string.Join(", ", unknownIds)}。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var succeededIds = new List<long>();
        long? failedDeviceObjid = null;
        var notProcessedIds = new List<long>();
        string? failureMessage = null;
        var auditFailures = 0;

        for (int i = 0; i < request.DeviceObjids.Count; i++)
        {
            var deviceObjid = request.DeviceObjids[i];
            try
            {
                var row = new PrtgManualMapRow
                {
                    DeviceObjid = deviceObjid,
                    HostId = request.HostId,
                    Note = request.Note,
                    CreatedBy = createdBy,
                    CreatedAt = DateTime.Now
                };
                store.UpsertManualMap(row);
                succeededIds.Add(deviceObjid);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "批次設定 PRTG 人工主機對應失敗，裝置 ID: {DeviceObjid}", deviceObjid);
                failedDeviceObjid = deviceObjid;
                failureMessage = "儲存裝置對應資料時發生伺服器錯誤。";
                for (int j = i + 1; j < request.DeviceObjids.Count; j++)
                    notProcessedIds.Add(request.DeviceObjids[j]);
                break;
            }

            // 對應已落盤後，稽核故障不能把該裝置誤報為未成功。
            try
            {
                _audit.Record(
                    action: AuditActions.PrtgManualMapSet,
                    summary: $"設定 PRTG device {deviceObjid} 人工對應到主機 {host.HostName}",
                    targetKind: "prtg_manual_map",
                    targetId: deviceObjid.ToString(),
                    detail: new
                    {
                        DeviceObjid = deviceObjid,
                        request.HostId,
                        host.HostName,
                        request.Note
                    });

            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG 人工對應已儲存，但稽核寫入失敗，裝置 ID: {DeviceObjid}", deviceObjid);
                auditFailures++;
            }
        }

        string? remapWarning = null;
        if (succeededIds.Count > 0)
        {
            remapWarning = _mapRefresher?.TryRefreshToday();
        }

        return ApiResponse<PrtgManualMapBatchResultDto>.Ok(new PrtgManualMapBatchResultDto
        {
            SucceededIds = succeededIds,
            FailedDeviceObjid = failedDeviceObjid,
            NotProcessedIds = notProcessedIds,
            FailureMessage = failureMessage,
            AuditWarning = auditFailures > 0 ? $"有 {auditFailures} 筆對應已儲存，但稽核紀錄寫入失敗，請通知管理員檢查伺服器紀錄。" : null,
            RemapWarning = remapWarning
        });
    }

    /// <summary>刪除一筆 PRTG 人工主機對應</summary>
    [HttpDelete("prtg-manual-map/{deviceObjid:long}")]
    public ApiResponse<PrtgDeleteResultDto> DeletePrtgManualMap(long deviceObjid)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        var store = _backend.PrtgStore();
        var deleted = store.DeleteManualMap(deviceObjid);

        _audit.Record(
            action: AuditActions.PrtgManualMapDelete,
            summary: $"刪除 PRTG device {deviceObjid} 的人工主機對應",
            targetKind: "prtg_manual_map",
            targetId: deviceObjid.ToString(),
            detail: new { DeviceObjid = deviceObjid, Deleted = deleted });

        var remapWarning = _mapRefresher?.TryRefreshToday();

        return ApiResponse<PrtgDeleteResultDto>.Ok(new PrtgDeleteResultDto
        {
            Deleted = deleted > 0,
            RemapWarning = remapWarning
        });
    }

    // ── PRTG IP 排除清單（批次B 階段2）──────────────────────────────────────────

    /// <summary>取得全部 PRTG IP 排除清單，依 IP 排序</summary>
    [HttpGet("prtg-ip-excludes")]
    public ApiResponse<List<PrtgIpExcludeDto>> GetPrtgIpExcludes()
    {
        if (_backend == null)
        {
            return ApiResponse<List<PrtgIpExcludeDto>>.Ok(new List<PrtgIpExcludeDto>());
        }

        var store = _backend.PrtgStore();
        var dtos = store.GetIpExcludes()
            .OrderBy(e => e.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(e => new PrtgIpExcludeDto
            {
                Ip = e.Ip,
                Note = e.Note,
                CreatedBy = e.CreatedBy,
                CreatedAt = e.CreatedAt
            })
            .ToList();

        return ApiResponse<List<PrtgIpExcludeDto>>.Ok(dtos);
    }

    /// <summary>新增或更新一筆 PRTG IP 排除</summary>
    [HttpPut("prtg-ip-excludes")]
    public ApiResponse<PrtgIpExcludeDto> SetPrtgIpExclude([FromBody] SetPrtgIpExcludeRequest request)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        var normIp = PrtgHostMapper.NormalizeIp(request?.Ip);
        if (normIp == null)
            throw DomainException.Validation(
                string.IsNullOrWhiteSpace(request?.Ip)
                    ? "IP 位址不能為空白。"
                    : $"「{request!.Ip.Trim()}」不是有效的 IP 位址。");

        var createdBy = User?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(createdBy)) createdBy = null;

        var store = _backend.PrtgStore();
        var row = new PrtgIpExcludeRow
        {
            Ip = normIp,
            Note = request!.Note,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now
        };
        store.UpsertIpExclude(row);

        _audit.Record(
            action: AuditActions.PrtgIpExcludeSet, // prtg_ip_exclude_set
            summary: $"設定 PRTG 排除 IP {normIp}",
            targetKind: "prtg_ip_exclude",
            targetId: normIp,
            detail: new
            {
                Ip = normIp,
                request.Note
            });

        var remapWarning = _mapRefresher?.TryRefreshToday();

        var saved = store.GetIpExcludes().FirstOrDefault(e => e.Ip == normIp);

        return ApiResponse<PrtgIpExcludeDto>.Ok(new PrtgIpExcludeDto
        {
            Ip = normIp,
            Note = request.Note,
            CreatedBy = saved?.CreatedBy ?? createdBy,
            CreatedAt = saved?.CreatedAt ?? row.CreatedAt,
            RemapWarning = remapWarning
        });
    }

    /// <summary>刪除一筆 PRTG IP 排除</summary>
    [HttpDelete("prtg-ip-excludes/{ip}")]
    public ApiResponse<PrtgDeleteResultDto> DeletePrtgIpExclude(string ip)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        // **刪除要把原字串交給 store**：正規化語意收緊為「只有合法 IP 才回值」之後，
        // 舊資料裡非 IP 的排除列（早期不驗證內容時存進去的）正規化會回 null。
        // 在這裡先正規化再擋 null，等於讓那些列永遠刪不掉——store 的原字串退路根本走不到。
        var raw = ip?.Trim();
        if (string.IsNullOrEmpty(raw))
            throw DomainException.Validation("要刪除的排除項目不能為空白。");

        var store = _backend.PrtgStore();
        var deleted = store.DeleteIpExclude(raw);

        // 稽核記使用者實際送出的值：正規化後的值可能與畫面上那筆不同（甚至是 null）
        _audit.Record(
            action: AuditActions.PrtgIpExcludeDelete, // prtg_ip_exclude_delete
            summary: $"刪除 PRTG 排除 IP {raw}",
            targetKind: "prtg_ip_exclude",
            targetId: raw,
            detail: new { Ip = raw, Deleted = deleted });

        var remapWarning = _mapRefresher?.TryRefreshToday();

        return ApiResponse<PrtgDeleteResultDto>.Ok(new PrtgDeleteResultDto
        {
            Deleted = deleted > 0,
            RemapWarning = remapWarning
        });
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


    // ── PRTG 鏡像資料匯出／匯入（PRTG 任務G）──────────────────────────────────────

    private static readonly JsonSerializerOptions PrtgDataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>匯出指定期間的 PRTG 鏡像資料為 JSON 檔案</summary>
    [HttpGet("prtg-export")]
    public IActionResult ExportPrtgData([FromQuery] string? from, [FromQuery] string? to)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            throw DomainException.Validation("請指定起始與結束日期。");

        var fromDate = QueryStringParsing.ParseRequiredDate(from);
        var toDate = QueryStringParsing.ParseRequiredDate(to);

        if (fromDate > toDate)
            throw DomainException.Validation("起始日期不得大於結束日期。");

        var days = (toDate.Date - fromDate.Date).Days + 1;
        if (days > 366)
            throw DomainException.Validation($"匯出期間不可超過 366 天（目前 {days} 天）。");

        var store = _backend.PrtgStore();
        var package = PrtgDataTransfer.Export(store, fromDate, toDate);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(package, PrtgDataJsonOptions);
        var fileName = $"prtg-export-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.json";

        _audit.Record(
            action: AuditActions.PrtgDataExport,
            summary: $"匯出 PRTG 鏡像資料（期間：{fromDate:yyyy-MM-dd} ~ {toDate:yyyy-MM-dd}，裝置 {package.Devices.Count} 筆、感測器 {package.Sensors.Count} 筆、狀態變更 {package.StateChanges.Count} 筆、數值 {package.Values.Count} 筆、主機對應 {package.HostMaps.Count} 筆、人工對應 {package.ManualMaps.Count} 筆）",
            targetKind: "prtg_data",
            targetId: $"{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}",
            detail: new
            {
                From = fromDate.ToString("yyyy-MM-dd"),
                To = toDate.ToString("yyyy-MM-dd"),
                Devices = package.Devices.Count,
                Sensors = package.Sensors.Count,
                StateChanges = package.StateChanges.Count,
                Values = package.Values.Count,
                HostMaps = package.HostMaps.Count,
                ManualMaps = package.ManualMaps.Count
            });

        return File(bytes, "application/json", fileName);
    }

    /// <summary>匯入 PRTG 鏡像資料 JSON 檔案</summary>
    [HttpPost("prtg-import")]
    public ApiResponse<PrtgImportResult> ImportPrtgData([FromForm] IFormFile? file)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        if (file == null || file.Length == 0)
            throw DomainException.Validation("請選擇要匯入的 JSON 檔案。");

        PrtgDataPackage? package;
        try
        {
            using var stream = file.OpenReadStream();
            package = JsonSerializer.Deserialize<PrtgDataPackage>(stream, PrtgDataJsonOptions);
        }
        catch (Exception ex)
        {
            throw DomainException.Validation($"檔案解析失敗：{ex.Message}");
        }

        if (package == null)
            throw DomainException.Validation("匯入檔案內容為空或格式不符。");

        if (package.FormatVersion != PrtgDataTransfer.CurrentFormatVersion)
            throw DomainException.Validation($"不支援的格式版本 {package.FormatVersion}（目前支援版本為 {PrtgDataTransfer.CurrentFormatVersion}）。");

        var store = _backend.PrtgStore();
        var result = PrtgDataTransfer.Import(store, package);

        _audit.Record(
            action: AuditActions.PrtgDataImport,
            summary: $"匯入 PRTG 鏡像資料（期間：{package.FromDate:yyyy-MM-dd} ~ {package.ToDate:yyyy-MM-dd}，裝置 {result.Devices} 筆、感測器 {result.Sensors} 筆、狀態變更 {result.StateChanges} 筆、數值 {result.Values} 筆、主機對應 {result.HostMaps} 筆、人工對應 {result.ManualMaps} 筆）",
            targetKind: "prtg_data",
            targetId: $"{package.FromDate:yyyyMMdd}-{package.ToDate:yyyyMMdd}",
            detail: new
            {
                From = package.FromDate.ToString("yyyy-MM-dd"),
                To = package.ToDate.ToString("yyyy-MM-dd"),
                result.Devices,
                result.Sensors,
                result.StateChanges,
                result.Values,
                result.HostMaps,
                result.ManualMaps
            });

        return ApiResponse<PrtgImportResult>.Ok(result);
    }
}


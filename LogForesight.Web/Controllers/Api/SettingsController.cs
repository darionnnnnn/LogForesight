using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>全站系統設定（「系統管理 > 設定」頁）。整個 Controller 需要 Maintain 能力</summary>
[ApiController]
[Route("api/admin/settings")]
[Permission(Capability.Maintain)]
public class SettingsController : ControllerBase
{
    private readonly ISystemSettingsService _settings;
    private readonly AiUsageStore _aiUsage;
    private readonly IAuditService _audit;
    private readonly PrtgProbeService? _prtgProbe;
    private readonly PrtgBackfillService? _prtgBackfill;
    private readonly StorageBackend? _backend;

    public SettingsController(
        ISystemSettingsService settings,
        AiUsageStore aiUsage,
        IAuditService audit,
        PrtgProbeService? prtgProbe = null,
        PrtgBackfillService? prtgBackfill = null,
        StorageBackend? backend = null)
    {
        _settings = settings;
        _aiUsage = aiUsage;
        _audit = audit;
        _prtgProbe = prtgProbe;
        _prtgBackfill = prtgBackfill;
        _backend = backend;
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

    /// <summary>PRTG 總開關（排程作業頁）：只更新這一個欄位，不動其他設定</summary>
    [HttpPut("prtg-enabled")]
    public ApiResponse<bool> SetPrtgEnabled([FromBody] SetPrtgEnabledRequest request) =>
        ApiResponse<bool>.Ok(_settings.SetPrtgEnabled(request.Enabled));

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

        if (!_prtgProbe.TryStart(out var error))
            throw DomainException.Validation(error ?? "無法啟動 PRTG 探測。");

        _audit.Record(
            action: AuditActions.PrtgProbeRun,
            summary: "執行 PRTG API 環境探測",
            targetKind: "system_settings",
            targetId: "prtg_probe",
            detail: new { });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(new StartPrtgProbeResultDto { Started = true });
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

        if (!_prtgBackfill.TryStart(out var error))
            throw DomainException.Validation(error ?? "無法啟動 PRTG 歷史回填。");

        _audit.Record(
            action: AuditActions.PrtgBackfillRun,
            summary: "執行 PRTG 歷史資料回填",
            targetKind: "system_settings",
            targetId: "prtg_backfill",
            detail: new { });

        return ApiResponse<StartPrtgBackfillResultDto>.Ok(new StartPrtgBackfillResultDto { Started = true });
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
        DateTime? mapDate = null;
        List<PrtgHostMapRow> hostMaps = new();
        for (var d = DateTime.Today; d >= DateTime.Today.AddDays(-30); d = d.AddDays(-1))
        {
            var rows = store.GetHostMapForDate(d);
            if (rows.Count > 0)
            {
                mapDate = d;
                hostMaps = rows;
                break;
            }
        }

        var whitelist = _settings.Get().PrtgSensorTypeWhitelist;
        var coverage = store.GetWhitelistCoverage(whitelist, mapDate);

        var mapOk = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Ok);
        var mapConflict = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Conflict);
        var mapUnmatched = hostMaps.Count(m => m.MapStatus == PrtgMapStatus.Unmatched);

        var conflicts = hostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Conflict)
            .Take(20)
            .Select(m => new PrtgHostMapItemDto(m.DeviceObjid, m.Ip, m.HostName, m.Note))
            .ToList();

        var unmatched = hostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Unmatched)
            .Take(20)
            .Select(m => new PrtgHostMapItemDto(m.DeviceObjid, m.Ip, m.HostName, m.Note))
            .ToList();

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
            Conflicts = conflicts,
            Unmatched = unmatched
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

        var dtos = manualMaps.Select(m => new PrtgManualMapDto
        {
            DeviceObjid = m.DeviceObjid,
            HostId = m.HostId,
            HostName = hostMap.TryGetValue(m.HostId, out var host) ? host.HostName : null,
            Note = m.Note,
            CreatedBy = m.CreatedBy,
            CreatedAt = m.CreatedAt
        }).OrderBy(m => m.DeviceObjid).ToList();

        return ApiResponse<List<PrtgManualMapDto>>.Ok(dtos);
    }

    /// <summary>新增或更新一筆 PRTG 人工主機對應</summary>
    [HttpPut("prtg-manual-map")]
    public ApiResponse<PrtgManualMapDto> SetPrtgManualMap([FromBody] SetPrtgManualMapRequest request)
    {
        if (_backend == null)
            throw DomainException.Validation("PRTG 鏡像服務未啟用。");

        var hostStore = new HostStore(_backend.Blob("hosts"));
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

        var saved = store.GetManualMaps().FirstOrDefault(m => m.DeviceObjid == request.DeviceObjid);

        return ApiResponse<PrtgManualMapDto>.Ok(new PrtgManualMapDto
        {
            DeviceObjid = request.DeviceObjid,
            HostId = request.HostId,
            HostName = host.HostName,
            Note = request.Note,
            CreatedBy = saved?.CreatedBy ?? createdBy,
            CreatedAt = saved?.CreatedAt ?? row.CreatedAt
        });
    }

    /// <summary>刪除一筆 PRTG 人工主機對應</summary>
    [HttpDelete("prtg-manual-map/{deviceObjid:long}")]
    public ApiResponse<bool> DeletePrtgManualMap(long deviceObjid)
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

        return ApiResponse<bool>.Ok(deleted > 0);
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


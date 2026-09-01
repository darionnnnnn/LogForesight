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
}


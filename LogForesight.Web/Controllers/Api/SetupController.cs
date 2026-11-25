using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>首次啟動精靈 API（回饋十八輪批次H）。整個 Controller 需要 Maintain 能力——
/// 精靈本身就是給 admin/serverAdmin 做初始設定用的。</summary>
[ApiController]
[Route("api/admin/setup")]
[Permission(Capability.Maintain)]
public class SetupController : ControllerBase
{
    private readonly SetupReadinessService _service;
    private readonly IAuditService _audit;

    public SetupController(SetupReadinessService service, IAuditService audit)
    {
        _service = service;
        _audit = audit;
    }

    /// <summary>
    /// 精靈狀態：AI 步驟讀<b>上次探測結果</b>，不等探測——AI 不通時探測要等逾時（約 2 秒以上），
    /// 每分鐘第一個開管理頁的人不該為此等待。探測在背景更新；要這一刻的結果走 <see cref="ProbeAi"/>。
    /// </summary>
    [HttpGet("status")]
    public ApiResponse<SetupStatusDto> Status()
    {
        _service.RequestBackgroundProbe();
        return ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());
    }

    /// <summary>
    /// 明確的同步 AI 探測（精靈「儲存並測試」、設定頁「測試連線」）：不看快取、重新探一次並等結果；逾時由探測本身限制（10 秒）。
    /// 探測的是<b>已儲存</b>的 AI 設定。探測不寫入任何資料，所以不寫稽核。
    /// </summary>
    [SkipDataVersionBump]   // 只探測、不寫資料：不該讓全站快取因此失效
    [HttpPost("ai-probe")]
    public async Task<ApiResponse<AiProbeResultDto>> ProbeAi(CancellationToken ct)
    {
        var result = await _service.ProbeNowAsync(ct);
        return ApiResponse<AiProbeResultDto>.Ok(AiProbeResultDto.From(result));
    }

    [HttpPost("skip/{stepId}")]
    public ApiResponse<SetupStatusDto> Skip(string stepId, [FromBody] SetSkippedRequest request)
    {
        _service.SetSkipped(stepId, request.Skipped);
        _service.RequestBackgroundProbe();
        return ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());
    }

    [HttpPost("hidden")]
    public ApiResponse<SetupStatusDto> SetHidden([FromBody] SetHiddenRequest request)
    {
        _service.SetHidden(request.Hidden);
        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: request.Hidden ? "隱藏教學文件裡的啟動精靈入口" : "重新顯示教學文件裡的啟動精靈入口",
            targetKind: "setup_wizard");
        _service.RequestBackgroundProbe();
        return ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());
    }
}

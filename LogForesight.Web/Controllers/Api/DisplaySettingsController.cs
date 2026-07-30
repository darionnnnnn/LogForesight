using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 顯示層設定的公開子集（docs/FEEDBACK-3-PLAN.md #8）。
///
/// **沒有 [Permission] 標註是刻意的**：完整設定 API（SettingsController，需要 Maintain 能力）
/// 一般使用者拿不到，但「哪些日風險等級目前顯示」是任何已登入者的前端都要知道的資訊——
/// 用來決定儀表板 KPI 卡、報表趨勢線、問題查詢篩選 chips 要不要出現。答案本身就是顯示範圍，
/// 不是需要額外授權才能問的問題，比照 HostsController 的先例。
/// </summary>
[ApiController]
[Route("api/settings/display")]
public class DisplaySettingsController : ControllerBase
{
    private readonly ISystemSettingsService _settings;

    public DisplaySettingsController(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ApiResponse<DisplaySettingsDto> Get() =>
        ApiResponse<DisplaySettingsDto>.Ok(new DisplaySettingsDto
        {
            // null（全顯示）轉為完整清單——前端要的是明確的顯示範圍，不是「不過濾」的內部語意
            VisibleDayRiskLevels = _settings.GetVisibleDayRiskLevels()?.ToList() ?? RiskLevels.All.ToList()
        });
}

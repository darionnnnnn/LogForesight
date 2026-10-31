using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 顯示層設定的公開子集（docs/archive/FEEDBACK-3-PLAN.md #8）。
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
    private readonly ISystemSettingsStore _store;

    public DisplaySettingsController(ISystemSettingsService settings, ISystemSettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    [HttpGet]
    public ApiResponse<DisplaySettingsDto> Get()
    {
        // 每次頁面載入都會呼叫；完整 Get() 會統計報告用量（lf_reports 全表），這裡只需兩個郵件布林，改讀輕量設定庫
        var all = _store.Get();
        return ApiResponse<DisplaySettingsDto>.Ok(new DisplaySettingsDto
        {
            // null（全顯示）轉為完整清單——前端要的是明確的顯示範圍，不是「不過濾」的內部語意
            VisibleDayRiskLevels = _settings.GetVisibleDayRiskLevels()?.ToList() ?? RiskLevels.All.ToList(),
            VisibleSeverities = _settings.GetVisibleSeverities()?.ToList() ?? SystemSettingsService.ValidSeverities.ToList(),
            MailWorkOrdersEnabled = all.MailEnabled && all.MailNotifyWorkOrders
        });
    }
}

using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>儀表板（docs/WEB-SPEC.md §9.1）。一次回傳全部區塊，避免首頁發五個請求</summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    /// <summary>days 未指定時的預設期間（§12：原 appsettings 的 Ui:DashboardDefaultDays，
    /// 前端另有 localStorage 期間記憶，這個值只是 API 的 fallback，改為常數）</summary>
    public const int DefaultDays = 7;

    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet("summary")]
    public ApiResponse<DashboardDto> Summary([FromQuery] int? days) =>
        // Clamp 同 host-detail 慣例（1..90）：未夾住時任意整數（含負數／超大值）會直接進
        // DateTime.Today.AddDays(-days+1) 算出離譜的查詢區間
        ApiResponse<DashboardDto>.Ok(_dashboard.GetSummary(ClampDays(days)));

    /// <summary>summary／today-focus 共用的天數夾範圍（1..90）</summary>
    public static int ClampDays(int? days) => Math.Clamp(days ?? DefaultDays, 1, 90);
}

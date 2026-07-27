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

    public SettingsController(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet]
    public ApiResponse<SystemSettingsDto> Get() =>
        ApiResponse<SystemSettingsDto>.Ok(_settings.Get());

    [HttpPut]
    public ApiResponse<SystemSettingsDto> Update([FromBody] UpdateSystemSettingsRequest request) =>
        ApiResponse<SystemSettingsDto>.Ok(_settings.Update(request));
}

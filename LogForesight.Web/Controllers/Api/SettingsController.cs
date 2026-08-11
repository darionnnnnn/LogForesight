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

    /// <summary>AD 測試連線（docs/archive/HISTORY.md #9）：用表單目前填的值＋管理者當場輸入的帳密試 bind</summary>
    [HttpPost("ad-test")]
    public ApiResponse<TestAdConnectionResultDto> TestAdConnection([FromBody] TestAdConnectionRequest request) =>
        ApiResponse<TestAdConnectionResultDto>.Ok(_settings.TestAdConnection(request));

    /// <summary>測試寄信（回饋十五輪批次D）：用表單目前填的值試寄一封信，密碼留空沿用已儲存的密碼</summary>
    [HttpPost("mail-test")]
    public async Task<ApiResponse<TestMailResultDto>> TestMail([FromBody] TestMailRequest request) =>
        ApiResponse<TestMailResultDto>.Ok(await _settings.TestMail(request));
}

using System.Globalization;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 風險日的處理狀態與指派（docs/WEB-SPEC.md §9.3）。
///
/// 注意兩個端點的能力要求不同：狀態更新是 <c>Handle</c>（user 也有），
/// 指派是 <c>Assign</c>（只有 admin）。這是「admin 才能指派、user 可以維護處理狀態」的實作點。
/// </summary>
[ApiController]
[Route("api/records/{hostId:long}/{date}/handling")]
public class HandlingController : ControllerBase
{
    private readonly IHandlingService _service;

    public HandlingController(IHandlingService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<HandlingDto> Get(long hostId, string date) =>
        ApiResponse<HandlingDto>.Ok(_service.Get(hostId, RequireDate(date)));

    [HttpPut]
    [Permission(Capability.Handle)]
    public ApiResponse<HandlingDto> Update(long hostId, string date, [FromBody] UpdateHandlingRequest request) =>
        ApiResponse<HandlingDto>.Ok(_service.Update(hostId, RequireDate(date), request));

    /// <summary>設定單一問題的處理狀態（方案 B 逐列狀態）。與日層級更新同為 Handle 能力</summary>
    [HttpPut("issues")]
    [Permission(Capability.Handle)]
    public ApiResponse<IssueStatusResultDto> SetIssueStatus(long hostId, string date, [FromBody] SetIssueStatusRequest request) =>
        ApiResponse<IssueStatusResultDto>.Ok(_service.SetIssueStatus(hostId, RequireDate(date), request));

    [HttpPut("assign")]
    [Permission(Capability.Assign)]
    public ApiResponse<HandlingDto> Assign(long hostId, string date, [FromBody] AssignHandlerRequest request) =>
        ApiResponse<HandlingDto>.Ok(_service.Assign(hostId, RequireDate(date), request.HandlerId));

    [HttpGet("logs")]
    public ApiResponse<List<HandlingLogDto>> GetLogs(long hostId, string date) =>
        ApiResponse<List<HandlingLogDto>>.Ok(_service.GetLogs(hostId, RequireDate(date)));

    private static DateTime RequireDate(string value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : throw DomainException.Validation("日期格式必須為 yyyy-MM-dd。");
}

/// <summary>權限異動待辦（§9.5）</summary>
[ApiController]
[Route("api/permission-changes")]
public class PermissionChangesController : ControllerBase
{
    private readonly IPermissionChangeService _service;

    public PermissionChangesController(IPermissionChangeService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<List<PermissionChangeDto>> Query(
        [FromQuery] string? status, [FromQuery] int maxCount = 200) =>
        ApiResponse<List<PermissionChangeDto>>.Ok(_service.Query(status, Math.Clamp(maxCount, 1, 1000)));

    [HttpPut("{changeId}/confirm")]
    [Permission(Capability.ConfirmPermission)]
    public ApiResponse<PermissionChangeDto> Confirm(
        string changeId, [FromBody] ConfirmPermissionChangeRequest request) =>
        ApiResponse<PermissionChangeDto>.Ok(_service.Confirm(changeId, request));
}

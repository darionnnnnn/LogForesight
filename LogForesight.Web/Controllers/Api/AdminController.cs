using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 使用者與群組維護 API（docs/WEB-SPEC.md §9.8）。
/// 整個 Controller 需要 Maintain 能力——admin 與 serverAdmin 持有。
/// </summary>
[ApiController]
[Route("api/admin")]
[Permission(Capability.Maintain)]
public class AdminController : ControllerBase
{
    private readonly IUserAdminService _users;
    private readonly IHostAdminService _hosts;
    private readonly INetiqHostService _netiq;
    private readonly IGroupAdminService _groups;

    public AdminController(
        IUserAdminService users,
        IHostAdminService hosts,
        INetiqHostService netiq,
        IGroupAdminService groups)
    {
        _users = users;
        _hosts = hosts;
        _netiq = netiq;
        _groups = groups;
    }

    // ── 使用者 ───────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public ApiResponse<List<UserDto>> GetUsers() =>
        ApiResponse<List<UserDto>>.Ok(_users.GetUsers());

    [HttpPost("users")]
    public ApiResponse<UserDto> SaveUser([FromBody] SaveUserRequest request) =>
        ApiResponse<UserDto>.Ok(_users.SaveUser(request));

    [HttpPut("users/{userId:long}/groups")]
    public ApiResponse<UserDto> SetUserGroups(long userId, [FromBody] SetUserGroupsRequest request) =>
        ApiResponse<UserDto>.Ok(_users.SetUserGroups(userId, request.GroupIds));

    // ── 使用者群組 ───────────────────────────────────────────────────────────

    [HttpGet("groups")]
    public ApiResponse<List<UserGroupDto>> GetUserGroups() =>
        ApiResponse<List<UserGroupDto>>.Ok(_groups.GetUserGroups());

    [HttpPost("groups")]
    public ApiResponse<UserGroupDto> SaveUserGroup([FromBody] SaveUserGroupRequest request) =>
        ApiResponse<UserGroupDto>.Ok(_groups.SaveUserGroup(request));

    [HttpDelete("groups/{groupId:long}")]
    public ApiResponse DeleteUserGroup(long groupId)
    {
        _groups.DeleteUserGroup(groupId);
        return ApiResponse.Ok();
    }

    // ── 主機 ─────────────────────────────────────────────────────────────────

    [HttpGet("hosts")]
    public ApiResponse<List<HostDto>> GetHosts() =>
        ApiResponse<List<HostDto>>.Ok(_hosts.GetHosts());

    [HttpPost("hosts")]
    public ApiResponse<HostDto> SaveHost([FromBody] SaveHostRequest request) =>
        ApiResponse<HostDto>.Ok(_hosts.SaveHost(request));

    [HttpPut("hosts/{hostId:long}/groups")]
    public ApiResponse<HostDto> SetHostGroups(long hostId, [FromBody] SetIdsRequest request) =>
        ApiResponse<HostDto>.Ok(_hosts.SetHostGroups(hostId, request.Ids));

    [HttpPut("hosts/{hostId:long}/owners")]
    public ApiResponse<HostDto> SetHostOwners(long hostId, [FromBody] SetIdsRequest request) =>
        ApiResponse<HostDto>.Ok(_hosts.SetHostOwners(hostId, request.Ids));

    [HttpPost("hosts/merge")]
    public ApiResponse MergeHost([FromBody] MergeHostRequest request)
    {
        _hosts.MergeHost(request.SourceHostId, request.TargetHostId);
        return ApiResponse.Ok();
    }

    [HttpPost("hosts/{hostId:long}/unmerge")]
    public ApiResponse UnmergeHost(long hostId)
    {
        _hosts.UnmergeHost(hostId);
        return ApiResponse.Ok();
    }

    // ── NetIQ 主機清單 ────────────────────────────────────────────────────────

    [HttpGet("netiq/overview")]
    public ApiResponse<NetiqOverviewDto> GetNetiqOverview() =>
        ApiResponse<NetiqOverviewDto>.Ok(_netiq.GetOverview());

    [HttpPost("netiq/hosts")]
    public ApiResponse<HostDto> AddNetiqHost([FromBody] AddNetiqHostRequest request) =>
        ApiResponse<HostDto>.Ok(_netiq.AddHost(request));

    [HttpPost("netiq/hosts/bulk")]
    public ApiResponse<BulkAddResultDto> BulkAddNetiqHosts([FromBody] BulkAddNetiqHostsRequest request) =>
        ApiResponse<BulkAddResultDto>.Ok(_netiq.BulkAddHosts(request));

    [HttpPut("hosts/{hostId:long}/active")]
    public ApiResponse<HostDto> SetHostActive(long hostId, [FromBody] SetHostActiveRequest request) =>
        ApiResponse<HostDto>.Ok(_netiq.SetActive(hostId, request.Active));

    // ── 主機群組與授權矩陣 ────────────────────────────────────────────────────

    [HttpGet("host-groups")]
    public ApiResponse<List<HostGroupDto>> GetHostGroups() =>
        ApiResponse<List<HostGroupDto>>.Ok(_groups.GetHostGroups());

    [HttpPost("host-groups")]
    public ApiResponse<HostGroupDto> SaveHostGroup([FromBody] SaveHostGroupRequest request) =>
        ApiResponse<HostGroupDto>.Ok(_groups.SaveHostGroup(request));

    [HttpDelete("host-groups/{groupId:long}")]
    public ApiResponse DeleteHostGroup(long groupId)
    {
        _groups.DeleteHostGroup(groupId);
        return ApiResponse.Ok();
    }

    [HttpGet("access")]
    public ApiResponse<AccessMatrixDto> GetAccessMatrix() =>
        ApiResponse<AccessMatrixDto>.Ok(_groups.GetAccessMatrix());

    [HttpPut("access/{userGroupId:long}")]
    public ApiResponse SetAccess(long userGroupId, [FromBody] SetAccessRequest request)
    {
        _groups.SetAccess(userGroupId, request.HostGroupIds);
        return ApiResponse.Ok();
    }
}

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
    private readonly INetiqDiscoveryService _discovery;
    private readonly IGroupAdminService _groups;
    private readonly ISentinelAdminService _sentinels;

    public AdminController(
        IUserAdminService users,
        IHostAdminService hosts,
        INetiqHostService netiq,
        INetiqDiscoveryService discovery,
        IGroupAdminService groups,
        ISentinelAdminService sentinels)
    {
        _users = users;
        _hosts = hosts;
        _netiq = netiq;
        _discovery = discovery;
        _groups = groups;
        _sentinels = sentinels;
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
    public ApiResponse<PagedResult<HostDto>> GetHosts(
        [FromQuery] string? query,
        [FromQuery] string status = "",
        [FromQuery] string? sentinel = null,
        [FromQuery] string? groupIds = null,
        [FromQuery] string sort = "name",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var request = new HostSearchRequest
        {
            Query = query,
            Status = status,
            Sentinel = sentinel,
            GroupIds = string.IsNullOrWhiteSpace(groupIds)
                ? null
                : groupIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
                    .Where(id => id.HasValue).Select(id => id!.Value).ToList(),
            Sort = sort,
            Page = page,
            PageSize = pageSize
        };

        return ApiResponse<PagedResult<HostDto>>.Ok(_hosts.GetHosts(request));
    }

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

    // ── NetIQ 主動探索匯入（docs/SCALE-2000-PLAN.md §1）──────────────────────────

    [HttpPost("netiq/scan")]
    public async Task<ApiResponse<NetiqScanResultDto>> Scan([FromBody] NetiqScanRequest request, CancellationToken ct) =>
        ApiResponse<NetiqScanResultDto>.Ok(await _discovery.ScanAsync(request.Server, ct));

    /// <summary>新增 Sentinel 精靈步驟 1：以尚未存檔的帳密掃描，成功才建立 Sentinel（定案 6）</summary>
    [HttpPost("netiq/create-and-scan")]
    public async Task<ApiResponse<NetiqScanResultDto>> CreateAndScan([FromBody] CreateAndScanSentinelRequest request, CancellationToken ct) =>
        ApiResponse<NetiqScanResultDto>.Ok(await _discovery.CreateAndScanAsync(request, ct));

    [HttpPost("netiq/import")]
    public ApiResponse<NetiqImportResultDto> ImportNetiq([FromBody] NetiqImportRequest request) =>
        ApiResponse<NetiqImportResultDto>.Ok(_discovery.Import(request));

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

    /// <summary>批次加入成員的預覽（網段／關鍵字命中主機，不寫入）</summary>
    [HttpPost("host-groups/{groupId:long}/members/preview")]
    public ApiResponse<HostGroupMemberPreviewDto> PreviewMembers(
        long groupId, [FromBody] HostGroupMemberQueryRequest request) =>
        ApiResponse<HostGroupMemberPreviewDto>.Ok(_groups.PreviewMembers(groupId, request));

    /// <summary>把選定主機加入群組（可選同時移出原群組）</summary>
    [HttpPost("host-groups/{groupId:long}/members")]
    public ApiResponse<HostGroupMemberPreviewDto> AddMembers(
        long groupId, [FromBody] AddHostGroupMembersRequest request) =>
        ApiResponse<HostGroupMemberPreviewDto>.Ok(_groups.AddMembers(groupId, request));

    /// <summary>「目前成員」頁籤：本群組現有成員清單</summary>
    [HttpGet("host-groups/{groupId:long}/members")]
    public ApiResponse<List<HostGroupMemberDto>> GetMembers(long groupId) =>
        ApiResponse<List<HostGroupMemberDto>>.Ok(_groups.GetMembers(groupId));

    /// <summary>把選定主機移出群組</summary>
    [HttpPost("host-groups/{groupId:long}/members/remove")]
    public ApiResponse RemoveMembers(long groupId, [FromBody] RemoveHostGroupMembersRequest request)
    {
        _groups.RemoveMembers(groupId, request.HostIds);
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

    // ── Sentinel ─────────────────────────────────────────────────────────────

    [HttpGet("sentinels")]
    public ApiResponse<List<SentinelDto>> GetSentinels() =>
        ApiResponse<List<SentinelDto>>.Ok(_sentinels.GetSentinels());

    [HttpPost("sentinels")]
    public ApiResponse<SentinelDto> SaveSentinel([FromBody] SaveSentinelRequest request) =>
        ApiResponse<SentinelDto>.Ok(_sentinels.SaveSentinel(request));

    [HttpDelete("sentinels/{sentinelId:long}")]
    public ApiResponse DeleteSentinel(long sentinelId)
    {
        _sentinels.DeleteSentinel(sentinelId);
        return ApiResponse.Ok();
    }

    [HttpPut("sentinels/{sentinelId:long}/active")]
    public ApiResponse<SentinelDto> SetSentinelActive(long sentinelId, [FromBody] SetSentinelActiveRequest request) =>
        ApiResponse<SentinelDto>.Ok(_sentinels.SetActive(sentinelId, request.Active));
}

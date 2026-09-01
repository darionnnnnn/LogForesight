using LogForesight.Core.Analysis;
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
    private readonly UserAdminService _users;
    private readonly HostAdminService _hosts;
    private readonly INetiqHostService _netiq;
    private readonly NetiqDiscoveryService _discovery;
    private readonly GroupAdminService _groups;
    private readonly SentinelAdminService _sentinels;
    private readonly NetiqOptionsService _netiqOptions;
    private readonly NetiqProbeService _probe;
    private readonly IAuditService _audit;
    private readonly IUserStore _userStore;
    private readonly IWebHostEnvironment _env;
    private readonly ISystemSettingsStore _settingsStore;
    private readonly IUserDisplayNameService _userDisplayNames;

    public AdminController(
        UserAdminService users,
        HostAdminService hosts,
        INetiqHostService netiq,
        NetiqDiscoveryService discovery,
        GroupAdminService groups,
        SentinelAdminService sentinels,
        NetiqOptionsService netiqOptions,
        NetiqProbeService probe,
        IAuditService audit,
        IUserStore userStore,
        IWebHostEnvironment env,
        ISystemSettingsStore settingsStore,
        IUserDisplayNameService userDisplayNames)
    {
        _users = users;
        _hosts = hosts;
        _netiq = netiq;
        _discovery = discovery;
        _groups = groups;
        _sentinels = sentinels;
        _netiqOptions = netiqOptions;
        _probe = probe;
        _audit = audit;
        _userStore = userStore;
        _env = env;
        _settingsStore = settingsStore;
        _userDisplayNames = userDisplayNames;
    }

    // ── 使用者 ───────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public ApiResponse<List<UserDto>> GetUsers() =>
        ApiResponse<List<UserDto>>.Ok(_users.GetUsers());

    /// <summary>使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）：可見主機、能力、被指派歷程</summary>
    [HttpGet("users/{userId:long}/detail")]
    public ApiResponse<UserDetailDto> GetUserDetail(long userId) =>
        ApiResponse<UserDetailDto>.Ok(_users.GetUserDetail(userId));

    [HttpPost("users")]
    public ApiResponse<UserDto> SaveUser([FromBody] SaveUserRequest request) =>
        ApiResponse<UserDto>.Ok(_users.SaveUser(request));

    /// <summary>一次新增多個帳號（docs/archive/HISTORY.md #7）</summary>
    [HttpPost("users/batch")]
    public ApiResponse<BatchCreateUsersResultDto> BatchCreateUsers([FromBody] BatchCreateUsersRequest request) =>
        ApiResponse<BatchCreateUsersResultDto>.Ok(_users.BatchCreateUsers(request));

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
        [FromQuery] string? os = null,
        [FromQuery] string? prtgMap = null,
        [FromQuery] string sort = "name",
        [FromQuery] string dir = "asc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var request = new HostSearchRequest
        {
            Query = query,
            Status = status,
            Sentinel = sentinel,
            GroupIds = QueryStringParsing.ParseLongs(groupIds),
            Os = os,
            PrtgMap = prtgMap,
            Sort = sort,
            Dir = dir,
            Page = page,
            PageSize = pageSize
        };

        return ApiResponse<PagedResult<HostDto>>.Ok(_hosts.GetHosts(request));
    }

    /// <summary>
    /// 「全選符合目前篩選的主機」（體檢 X3）：與清單頁共用同一組篩選參數與同一份篩選實作，
    /// 因此畫面說「符合 N 台」與實際選到的必然是同一批。回 id 清單讓寫入仍走既有的
    /// 批次改群組端點（同一份逐台檢查與同一份彙總稽核）。
    /// </summary>
    [HttpGet("hosts/ids")]
    public ApiResponse<HostIdListDto> GetMatchingHostIds(
        [FromQuery] string? query,
        [FromQuery] string status = "",
        [FromQuery] string? sentinel = null,
        [FromQuery] string? groupIds = null,
        [FromQuery] string? os = null,
        [FromQuery] string? prtgMap = null,
        [FromQuery] string sort = "name",
        [FromQuery] string dir = "asc")
    {
        var request = new HostSearchRequest
        {
            Query = query,
            Status = status,
            Sentinel = sentinel,
            GroupIds = QueryStringParsing.ParseLongs(groupIds),
            Os = os,
            PrtgMap = prtgMap,
            Sort = sort,
            Dir = dir
        };

        return ApiResponse<HostIdListDto>.Ok(_hosts.MatchingHostIds(request));
    }

    [HttpPost("hosts")]
    public ApiResponse<HostDto> SaveHost([FromBody] SaveHostRequest request) =>
        ApiResponse<HostDto>.Ok(_hosts.SaveHost(request));

    [HttpPut("hosts/{hostId:long}/groups")]
    public ApiResponse<HostDto> SetHostGroups(long hostId, [FromBody] SetIdsRequest request) =>
        ApiResponse<HostDto>.Ok(_hosts.SetHostGroups(hostId, request.Ids));

    /// <summary>批次改群組（docs/archive/FEEDBACK-5-PLAN.md §8）</summary>
    [HttpPut("hosts/groups/batch")]
    public ApiResponse<HostGroupsBatchResultDto> SetGroupsBatch([FromBody] SetGroupsBatchRequest request) =>
        ApiResponse<HostGroupsBatchResultDto>.Ok(_hosts.SetGroupsBatch(request.HostIds, request.GroupIds, request.Mode));

    /// <summary>批次設定分級（回饋十九輪批次G）</summary>
    [HttpPut("hosts/tier/batch")]
    public ApiResponse<HostTierBatchResultDto> SetTierBatch([FromBody] SetTierBatchRequest request) =>
        ApiResponse<HostTierBatchResultDto>.Ok(_hosts.SetTierBatch(request.HostIds, request.Tier));

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

    // ── NetIQ 主動探索匯入（docs/archive/HISTORY.md §1）──────────────────────────

    [HttpPost("netiq/scan")]
    public ApiResponse<NetiqScanJobDto> StartScan([FromBody] NetiqScanRequest request)
    {
        var granularity = SentinelQueryBuilder.ParseGranularity(request.Granularity);
        var jobId = _discovery.StartScan(request.Server, request.SubnetPrefix, granularity, request.Concurrency ?? 1);
        return ApiResponse<NetiqScanJobDto>.Ok(new NetiqScanJobDto
        {
            JobId = jobId,
            Status = "running"
        });
    }

    [HttpGet("netiq/scan/{jobId}")]
    public ApiResponse<NetiqScanJobDto> GetScanStatus(string jobId) =>
        ApiResponse<NetiqScanJobDto>.Ok(_discovery.GetScanStatus(jobId));

    [HttpPost("netiq/scan/{jobId}/cancel")]
    public ApiResponse<bool> CancelScan(string jobId)
    {
        _discovery.CancelScan(jobId);
        return ApiResponse<bool>.Ok(true);
    }

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

    /// <summary>測試連線（項目 6）：驗證網址／帳密是否正確，不落地任何東西</summary>
    [HttpPost("sentinels/test-connection")]
    public async Task<ApiResponse<TestSentinelConnectionResultDto>> TestSentinelConnection(
        [FromBody] TestSentinelConnectionRequest request, CancellationToken ct) =>
        ApiResponse<TestSentinelConnectionResultDto>.Ok(
            await _sentinels.TestConnectionAsync(request, _netiqOptions.Get(), ct));

    // ── NetIQ 連線與節流參數（「系統管理 > NetIQ 維護」頁）────────────────────

    [HttpGet("netiq/options")]
    public ApiResponse<NetiqOptionsDto> GetNetiqOptions() =>
        ApiResponse<NetiqOptionsDto>.Ok(ToNetiqDto(_netiqOptions.Get()));

    [HttpPut("netiq/options")]
    public ApiResponse<NetiqOptionsDto> UpdateNetiqOptions([FromBody] UpdateNetiqOptionsRequest request) =>
        ApiResponse<NetiqOptionsDto>.Ok(ToNetiqDto(_netiqOptions.Update(request)));

    /// <summary>NetiqOptions → 顯示 DTO：補上更新者顯示名稱（§9，即時解析）</summary>
    private NetiqOptionsDto ToNetiqDto(NetiqOptions o) => new()
    {
        QueryDelayMs = o.QueryDelayMs,
        PageSize = o.PageSize,
        MaxResultsPerJob = o.MaxResultsPerJob,
        TimeoutSeconds = o.TimeoutSeconds,
        RetryCount = o.RetryCount,
        AllowInvalidCertificates = o.AllowInvalidCertificates,
        BackfillDays = o.BackfillDays,
        MaxParallelServers = o.MaxParallelServers,
        MaxParallelQueriesPerServer = o.MaxParallelQueriesPerServer,
        ChatLiveFetchEnabled = o.ChatLiveFetchEnabled,
        UseOfflineDemoData = o.UseOfflineDemoData,
        CanUseOfflineDemo = !_env.IsProduction(),
        UpdatedAt = o.UpdatedAt,
        UpdatedByAccount = o.UpdatedByAccount,
        UpdatedByDisplayName = string.IsNullOrEmpty(o.UpdatedByAccount)
            ? null
            : (_userStore.FindByAccount(o.UpdatedByAccount) is { } u ? _userDisplayNames.Of(u.DisplayName) : null),
        MaxBackfillDays = NetiqOptions.GetEffectiveBackfillDaysLimit(_settingsStore.Get().RetentionDays)
    };

    // ── NetIQ API 診斷（probe，「診斷」分頁，docs/archive/WEB-SCHEDULER-PLAN.md §1.4.11）──────────

    [HttpGet("netiq/probe/status")]
    public ApiResponse<NetiqProbeStatusDto> GetNetiqProbeStatus() =>
        ApiResponse<NetiqProbeStatusDto>.Ok(_probe.GetStatus());

    [HttpPost("netiq/probe/start")]
    public ApiResponse<StartNetiqProbeResultDto> StartNetiqProbe([FromBody] StartNetiqProbeRequest request)
    {
        if (!_probe.TryStart(request.SentinelId, request.SampleIp, request.SampleLinuxIp, out var sentinel, out var error))
            throw DomainException.Validation(error ?? "無法啟動診斷。");

        _audit.Record(
            action: AuditActions.NetiqProbeRun,
            summary: $"對 Sentinel「{sentinel!.Name}」執行 API 診斷",
            targetKind: "sentinel",
            targetId: sentinel.SentinelId.ToString(),
            detail: new { sentinel.Name, request.SampleIp, request.SampleLinuxIp });

        return ApiResponse<StartNetiqProbeResultDto>.Ok(new StartNetiqProbeResultDto { Started = true });
    }
}

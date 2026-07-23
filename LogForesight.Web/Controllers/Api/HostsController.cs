using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 目前登入者可見的主機——各查詢頁主篩選列的主機選單來源。
///
/// **沒有 [Permission] 標註是刻意的**：任何已登入者都可以問「我看得到哪些主機」，
/// 答案本身就是依授權過濾的結果（IVisibilityService，三層授權的第 3 層）。
/// 這正是那一層存在的意義——不靠能力標註，靠資料範圍過濾。
/// </summary>
[ApiController]
[Route("api/hosts")]
public class HostsController : ControllerBase
{
    private readonly IVisibilityService _visibility;
    private readonly IHostGroupStore _hostGroups;

    public HostsController(IVisibilityService visibility, IHostGroupStore hostGroups)
    {
        _visibility = visibility;
        _hostGroups = hostGroups;
    }

    /// <summary>
    /// query：搜尋式 autocomplete 用（§5.4 D-4），伺服器端前綴/包含比對、上限 20 筆——
    /// 兩千台規模下問題查詢頁的主機篩選不能再一次把全部主機灌進一個 &lt;select&gt;。
    /// ids：依主機 Id 精確取回（不受 20 筆上限），供已選主機（例如網址帶入的 hostIds）
    /// 解析回顯示名稱使用。兩者皆未提供時回全部（既有行為，向下相容）。
    /// </summary>
    [HttpGet]
    public ApiResponse<List<VisibleHostDto>> GetVisibleHosts([FromQuery] string? query = null, [FromQuery] string? ids = null)
    {
        var hosts = _visibility.GetVisibleHosts().Where(h => h.Active);

        if (!string.IsNullOrWhiteSpace(ids))
        {
            var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet();
            hosts = hosts.Where(h => wanted.Contains(h.HostId));
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            hosts = hosts.Where(h => h.HostName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        IEnumerable<WebHost> ordered = hosts.OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(query)) ordered = ordered.Take(20);

        var result = ordered
            .Select(h => new VisibleHostDto
            {
                HostId = h.HostId,
                HostName = h.HostName,
                RoleDesc = h.RoleDesc,
                LastReportAt = h.LastReportAt
            })
            .ToList();

        return ApiResponse<List<VisibleHostDto>>.Ok(result);
    }

    /// <summary>
    /// 目前登入者看得到的主機所屬的群組（§5.4 D-4，問題查詢頁的群組篩選 chip 來源）。
    /// 只列出「至少有一台可見主機」的群組——不洩漏使用者看不到的部門結構。
    /// </summary>
    [HttpGet("groups")]
    public ApiResponse<List<HostGroupOptionDto>> GetVisibleHostGroups()
    {
        var visibleGroupIds = _visibility.GetVisibleHosts()
            .Where(h => h.Active)
            .SelectMany(h => h.GroupIds)
            .ToHashSet();

        var result = _hostGroups.GetAll()
            .Where(g => visibleGroupIds.Contains(g.GroupId))
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HostGroupOptionDto { GroupId = g.GroupId, GroupName = g.GroupName })
            .ToList();

        return ApiResponse<List<HostGroupOptionDto>>.Ok(result);
    }
}

using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers;

/// <summary>
/// 頁面殼（docs/WEB-SPEC.md §8.5）。**每個 Action 只回傳 View，不帶任何資料**——
/// 資料一律由前端 fetch 呼叫 API 取得。
///
/// 這不是為了時髦：View 沒有資料，就不存在「同一份資料有 Razor 與 API 兩個來源」
/// 的維護問題，頁面行為也全部可以從 API 層測試。
/// </summary>
[Authorize]
public class PagesController : Controller
{
    [HttpGet("/login")]
    [AllowAnonymous]
    public IActionResult Login() => View();

    [HttpGet("/")]
    public IActionResult Dashboard() => View();

    [HttpGet("/records")]
    public IActionResult Records() => View();

    /// <summary>風險日詳情。資源識別為 {hostId}/{date} 複合鍵（§7.2）</summary>
    [HttpGet("/records/{hostId:long}/{date}")]
    public IActionResult RecordDetail(long hostId, string date)
    {
        ViewData["HostId"] = hostId;
        ViewData["Date"] = date;
        return View();
    }

    [HttpGet("/hosts/{hostId:long}")]
    public IActionResult HostDetail(long hostId)
    {
        ViewData["HostId"] = hostId;
        return View();
    }

    [HttpGet("/reports")]
    public IActionResult Reports() => View();

    [HttpGet("/permission-changes")]
    [Permission(Capability.ConfirmPermission)]
    public IActionResult PermissionChanges() => View();

    [HttpGet("/audit")]
    [Permission(Capability.ViewAudit)]
    public IActionResult Audit() => View();

    [HttpGet("/runs")]
    [Permission(Capability.DevMonitor)]
    public IActionResult Runs() => View();

    [HttpGet("/admin/rules")]
    [Permission(Capability.Maintain)]
    public IActionResult Rules() => View();

    [HttpGet("/admin/users")]
    [Permission(Capability.Maintain)]
    public IActionResult Users() => View();

    [HttpGet("/admin/hosts")]
    [Permission(Capability.Maintain)]
    public IActionResult Hosts() => View();

    [HttpGet("/admin/groups")]
    [Permission(Capability.Maintain)]
    public IActionResult Groups() => View();

    [HttpGet("/admin/imports")]
    [Permission(Capability.Maintain)]
    public IActionResult Imports() => View();

    [HttpGet("/access-denied")]
    public IActionResult AccessDenied() => View();

    [HttpGet("/error")]
    [AllowAnonymous]
    public IActionResult Error() => View();
}

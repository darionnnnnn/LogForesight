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

    /// <summary>處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）：全登入角色可查看任何人，
    /// 資料以檢視者的可見範圍過濾，不需要額外能力標註</summary>
    [HttpGet("/handlers/{userId:long}")]
    public IActionResult HandlerDetail(long userId)
    {
        ViewData["UserId"] = userId;
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

    /// <summary>排程作業（docs/archive/FEEDBACK-6-PLAN.md §2）：DevMonitor 或 Maintain 任一即可進入——
    /// 排程設定與手動觸發屬 Maintain，執行紀錄唯讀屬 DevMonitor 既有範圍，serverAdmin 因此
    /// 也能看見執行紀錄佐證排程活著（頁內仍依能力分層顯示可編輯區塊）</summary>
    [HttpGet("/runs")]
    [Permission(Capability.DevMonitor, Capability.Maintain)]
    public IActionResult Runs() => View();

    [HttpGet("/admin/rules")]
    [Permission(Capability.Maintain)]
    public IActionResult Rules() => View();

    [HttpGet("/admin/users")]
    [Permission(Capability.Maintain)]
    public IActionResult Users() => View();

    /// <summary>
    /// 使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）：管理視角的單一使用者全貌——
    /// 可見主機（含「為什麼看得到」）、上次登入、處理中／已處理項目、被指派歷程。
    /// 與 <see cref="HandlerDetail"/> 刻意分開：那頁是全角色的工作頁、資料以**檢視者**
    /// 可見範圍過濾；這頁以**被查看者**為準，是 Maintain 專屬的管理資訊。
    /// </summary>
    [HttpGet("/admin/users/{userId:long}")]
    [Permission(Capability.Maintain)]
    public IActionResult UserDetail(long userId)
    {
        ViewData["UserId"] = userId;
        return View();
    }

    [HttpGet("/admin/hosts")]
    [Permission(Capability.Maintain)]
    public IActionResult Hosts() => View();

    [HttpGet("/admin/groups")]
    [Permission(Capability.Maintain)]
    public IActionResult Groups() => View();

    [HttpGet("/admin/imports")]
    [Permission(Capability.Maintain)]
    public IActionResult Imports() => View();

    [HttpGet("/admin/netiq")]
    [Permission(Capability.Maintain)]
    public IActionResult Netiq() => View();

    [HttpGet("/admin/settings")]
    [Permission(Capability.Maintain)]
    public IActionResult Settings() => View();

    /// <summary>操作說明書（docs/archive/FEEDBACK-15-PLAN.md 批次E）：僅 Maintain 顯示，
    /// 側欄選單顯示與此頁面級標註雙閘，比照既有 admin 頁慣例</summary>
    [HttpGet("/help/manual")]
    [Permission(Capability.Maintain)]
    public IActionResult HelpManual() => View();

    [HttpGet("/access-denied")]
    public IActionResult AccessDenied() => View();

    [HttpGet("/error")]
    [AllowAnonymous]
    public IActionResult Error() => View();
}

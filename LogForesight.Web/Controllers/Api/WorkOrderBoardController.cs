using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 負載看板與待派清單：能力＝<c>Assign</c> 或 <c>ViewAll</c>（同一標註內多個能力＝任一）。
///
/// 路由不會被 <see cref="WorkOrderViewController"/> 的 <c>{id:long}</c> 吃掉：那邊的參數帶 long 約束，
/// 「load-board」「gaps」不是整數、約束不成立；且字面段的路由優先序本來就高於參數段。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign, Capability.ViewAll)]
public class WorkOrderBoardController : ControllerBase
{
    private readonly WorkOrderBoardService _service;
    private readonly DismissedIssueService _dismissed;
    private readonly AdminTodoService _todo;

    public WorkOrderBoardController(WorkOrderBoardService service, DismissedIssueService dismissed, AdminTodoService todo)
    {
        _service = service;
        _dismissed = dismissed;
        _todo = todo;
    }

    [HttpGet("load-board")]
    public ApiResponse<LoadBoardDto> LoadBoard([FromQuery] long? groupId) =>
        ApiResponse<LoadBoardDto>.Ok(_service.GetLoadBoard(groupId));

    /// <summary>待派清單；<c>section</c>（dispatchable／unassignable／notDispatched）只列該段的問題，各段統計不隨之變</summary>
    [HttpGet("gaps")]
    public ApiResponse<GapsDto> Gaps([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1,
        [FromQuery] string? section = null) =>
        ApiResponse<GapsDto>.Ok(_service.GetGaps(from, to, page, section));

    /// <summary>
    /// 管理者提醒與待辦的筆數與明細（上報逾期未處理、今天新上報、本趟派不出去、上報沒有收件人、反覆復發；
    /// 具 Assign 的人另有昨晚取數結果、可派未派、處理人已停用或暫停仍有單、未分組主機）；與夜間的管理者摘要信、儀表板「需要你處理」列
    /// 同一個彙整函式（<see cref="AdminReminderService.Compute"/>），郵件總開關關閉時交辦總覽靠這支端點顯示。
    /// </summary>
    [SkipDataVersionBump]
    [HttpGet("admin-reminders")]
    public ApiResponse<AdminReminderDto> AdminReminders() =>
        ApiResponse<AdminReminderDto>.Ok(_todo.GetForCurrentUser());

    /// <summary>不再自動派工的輕量計數（N 個問題、M 台）：清單預設收合，展開前只需要這個數字</summary>
    [HttpGet("dismissed/count")]
    public ApiResponse<DismissedCountDto> DismissedCount() =>
        ApiResponse<DismissedCountDto>.Ok(_dismissed.GetCount());

    /// <summary>被「不再打擾」擋住、不再自動派工的項目（依問題分組、分頁）；可見範圍同待派頁</summary>
    [HttpGet("dismissed")]
    public ApiResponse<DismissedListDto> Dismissed([FromQuery] int page = 1) =>
        ApiResponse<DismissedListDto>.Ok(_dismissed.GetList(page));
}

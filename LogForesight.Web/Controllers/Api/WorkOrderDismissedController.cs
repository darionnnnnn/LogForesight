using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 恢復「不再自動派工」的項目：能力＝<c>Assign</c>（指派權限）。
/// 不用 <c>Maintain</c>：救援帳號 serverAdmin 有 Maintain 卻沒有任何業務資料檢視，不該能改派工判定。
/// 清單讀取在 <see cref="WorkOrderBoardController"/>（Assign 或 ViewAll）。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign)]
public class WorkOrderDismissedController : ControllerBase
{
    private readonly DismissedIssueService _dismissed;

    public WorkOrderDismissedController(DismissedIssueService dismissed) => _dismissed = dismissed;

    /// <summary>恢復單一（主機, 問題）的自動派工</summary>
    [HttpPost("dismissed/restore")]
    public ApiResponse<DismissedRestoreResultDto> RestoreDismissed([FromBody] RestoreDismissedRequest request) =>
        ApiResponse<DismissedRestoreResultDto>.Ok(_dismissed.Restore(request.HostName, request.IssueKey));

    /// <summary>恢復整個問題（該問題所有被擋住的主機）的自動派工</summary>
    [HttpPost("dismissed/restore-issue")]
    public ApiResponse<DismissedRestoreResultDto> RestoreDismissedIssue([FromBody] RestoreDismissedIssueRequest request) =>
        ApiResponse<DismissedRestoreResultDto>.Ok(_dismissed.RestoreIssue(request.Source, request.EventId));
}

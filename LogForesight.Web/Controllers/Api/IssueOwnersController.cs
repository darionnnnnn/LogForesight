using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 問題負責人維護 API（回饋十八輪批次F，「問題負責人」頁）。整個 Controller 需要 Maintain
/// 能力——與主機／群組管理同一組（admin 與 serverAdmin 持有）。
/// </summary>
[ApiController]
[Route("api/admin/issue-owners")]
[Permission(Capability.Maintain)]
public class IssueOwnersController : ControllerBase
{
    private readonly IssueOwnerAdminService _service;

    public IssueOwnersController(IssueOwnerAdminService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<List<IssueOwnerDto>> List() => ApiResponse<List<IssueOwnerDto>>.Ok(_service.List());

    /// <summary>問題選擇器候選項：近期出現過的問題，供新增規則時挑選（也允許前端手動輸入不在清單內的值）</summary>
    [HttpGet("recent-issues")]
    public ApiResponse<List<RecentIssueOptionDto>> RecentIssues() => ApiResponse<List<RecentIssueOptionDto>>.Ok(_service.RecentIssues());

    [HttpPut]
    public ApiResponse<IssueOwnerDto> Upsert([FromBody] SaveIssueOwnerRequest request) =>
        ApiResponse<IssueOwnerDto>.Ok(_service.Upsert(request));

    [HttpDelete("{source}/{eventId:int}")]
    public ApiResponse Delete(string source, int eventId)
    {
        _service.Delete(source, eventId);
        return ApiResponse.Ok();
    }

    /// <summary>設定機房結論（回饋十九輪批次F）——問題檔案頁的「設定機房結論」入口，
    /// 與依問題視角統一標記勾選「之後自動套用」共用同一個服務方法。</summary>
    [HttpPut("{source}/{eventId:int}/conclusion")]
    public ApiResponse<IssueOwnerDto> SetConclusion(string source, int eventId, [FromBody] SetIssueConclusionRequest request) =>
        ApiResponse<IssueOwnerDto>.Ok(_service.SetConclusion(source, eventId, request));

    /// <summary>解除機房結論（§2 決策一：只清結論欄位，已套用的歷史列不回滾）</summary>
    [HttpDelete("{source}/{eventId:int}/conclusion")]
    public ApiResponse<IssueOwnerDto> ClearConclusion(string source, int eventId) =>
        ApiResponse<IssueOwnerDto>.Ok(_service.ClearConclusion(source, eventId));
}

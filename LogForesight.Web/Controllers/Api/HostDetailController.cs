using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>主機詳情／時間軸（§9.4）</summary>
[ApiController]
[Route("api/host-detail")]
public class HostDetailController : ControllerBase
{
    private readonly RecordDetailQueryService _service;
    private readonly HostPrtgDetailService _prtgDetail;

    public HostDetailController(RecordDetailQueryService service, HostPrtgDetailService prtgDetail)
    {
        _service = service;
        _prtgDetail = prtgDetail;
    }

    [HttpGet("{hostId:long}")]
    public ApiResponse<HostDetailDto> Get(long hostId, [FromQuery] int days = 30) =>
        ApiResponse<HostDetailDto>.Ok(_service.GetHostDetail(hostId, Math.Clamp(days, 7, 90)));

    /// <summary>某個問題（Source+EventId）的逐日發生明細（docs/archive/FEEDBACK-4-PLAN.md §3）：
    /// 重點問題彙總表某一列展開時查詢，days 與外層時間軸期間一致</summary>
    [HttpGet("{hostId:long}/issues")]
    public ApiResponse<HostIssueOccurrenceDto> Issues(
        long hostId, [FromQuery] string source, [FromQuery] int eventId, [FromQuery] int days = 30) =>
        ApiResponse<HostIssueOccurrenceDto>.Ok(_service.GetHostIssueOccurrences(hostId, source, eventId, Math.Clamp(days, 7, 90)));

    /// <summary>取得指定主機目前對應的 PRTG device 與 sensor 清單（可見性檢查在 Service 內、早於任何資料讀取）</summary>
    [HttpGet("{hostId:long}/prtg")]
    public ApiResponse<HostPrtgMappingDto> Prtg(long hostId) =>
        ApiResponse<HostPrtgMappingDto>.Ok(_prtgDetail.GetForHost(hostId));
}

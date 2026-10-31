using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 問題查詢與風險日詳情（docs/WEB-SPEC.md §9.2、§9.3）。
///
/// 資源識別採 <c>{hostId}/{date}</c> 複合鍵（§7.2）：JSONL 後端的紀錄天然以
/// （主機,日期）為鍵、沒有代理數字 id，SQL 的 record_id 只是內部主鍵不對外。
/// 兩後端因此共用同一套路由。
///
/// 沒有 [Permission] 標註是刻意的——所有查詢都經 IVisibilityService 過濾，
/// 使用者只會拿到授權範圍內的資料（三層授權的第 3 層）。
/// </summary>
[ApiController]
[Route("api/records")]
public class RecordsController : ControllerBase
{
    private readonly RecordListQueryService _list;
    private readonly RecordDetailQueryService _detail;

    public RecordsController(RecordListQueryService list, RecordDetailQueryService detail)
    {
        _list = list;
        _detail = detail;
    }

    [HttpGet]
    public ApiResponse<PagedResult<RecordListItemDto>> Search([FromQuery] RecordQuery query) =>
        ApiResponse<PagedResult<RecordListItemDto>>.Ok(_list.Search(BuildRequest(query, RecordView.Detail)));

    /// <summary>依主機彙總（日期合併）。不支援處理狀態類篩選（<see cref="RecordQuerySupport"/>）</summary>
    [HttpGet("by-host")]
    public ApiResponse<PagedResult<RecordHostGroupDto>> ByHost([FromQuery] RecordQuery query) =>
        ApiResponse<PagedResult<RecordHostGroupDto>>.Ok(_list.SearchByHost(BuildRequest(query, RecordView.ByHost)));

    /// <summary>依日期彙總（主機合併）。不支援處理狀態類篩選（<see cref="RecordQuerySupport"/>）</summary>
    [HttpGet("by-date")]
    public ApiResponse<PagedResult<RecordDateGroupDto>> ByDate([FromQuery] RecordQuery query) =>
        ApiResponse<PagedResult<RecordDateGroupDto>>.Ok(_list.SearchByDate(BuildRequest(query, RecordView.ByDate)));

    /// <summary>依問題彙總（主機與日期都合併，docs/archive/FEEDBACK-4-PLAN.md §4）。
    /// §10：支援處理狀態（處理概況三態）與未指派過濾，不支援逾期（<see cref="RecordQuerySupport"/>）。
    ///
    /// 回傳型別必須寫 <see cref="IssueSearchResultDto"/> 而不是父型別 <c>PagedResult</c>：
    /// JSON 依**宣告型別**序列化，寫父型別會把子型別獨有的欄位（去重主機數、靜音未列出數）
    /// 靜默吊掉——服務層回傳值正確、單元測試也綠，只有實際打 API 才看得出來。</summary>
    [HttpGet("by-issue")]
    public ApiResponse<IssueSearchResultDto> ByIssue([FromQuery] RecordQuery query) =>
        ApiResponse<IssueSearchResultDto>.Ok(_list.SearchByIssue(BuildRequest(query, RecordView.ByIssue)));

    /// <summary>
    /// 依視角的支援矩陣把查詢參數組成請求：不支援的處理狀態類條件即使 URL 帶了也不傳給服務
    /// （前端同樣不送、不顯示該條件，見 records.js 的 viewSupports）。
    /// </summary>
    internal static RecordSearchRequest BuildRequest(RecordQuery query, RecordView view)
    {
        var (parsedFrom, parsedTo) = ParseDateRange(query.From, query.To);
        var request = new RecordSearchRequest
        {
            HostIds = ParseIds(query.HostIds),
            GroupIds = ParseIds(query.GroupIds),
            From = parsedFrom,
            To = parsedTo,
            RiskLevels = ParseStrings(query.RiskLevels),
            Categories = ParseStrings(query.Categories),
            Severity = query.Severity,
            EventId = query.EventId,
            Source = query.Source,
            SortKey = query.Sort,
            Ascending = query.Dir == "asc",
            Page = query.Page ?? 1,
            PageSize = query.PageSize ?? 50
        };

        if (RecordQuerySupport.Supports(view, RecordQuerySupport.Statuses))
            request.Statuses = ParseStrings(query.Statuses);
        if (RecordQuerySupport.Supports(view, RecordQuerySupport.Overdue))
            request.Overdue = query.Overdue;
        if (RecordQuerySupport.Supports(view, RecordQuerySupport.Unassigned))
            request.Unassigned = query.Unassigned == true;
        return request;
    }

    /// <summary>
    /// 主機／群組 id 清單：沒給（空白）回 null＝不限；給了卻一個合法數字都沒有就擋下，
    /// 否則「篩選失效、悄悄變成全部可見主機」使用者完全看不出來。
    /// </summary>
    private static List<long>? ParseIds(string? raw)
    {
        var ids = ParseLongs(raw);
        if (ids is { Count: 0 })
            throw DomainException.Validation("主機／群組參數格式不正確。");
        return ids;
    }

    /// <summary>
    /// 「下一筆未處理」捷徑（回饋四十五輪 B2）：回傳下一筆的主機與日期，沒有下一筆時回 null
    /// ——呼叫端要的只是一個連結，不需要整份清單 DTO（改版前是把 200 筆完整清單拉到前端自己找）。
    ///
    /// 授權：<see cref="RecordListQueryService.FindNextUnhandled"/> 自己解析可見範圍
    /// （不因為「內部呼叫既有服務」就假設已經擋過），可見範圍為空時回 null。
    /// </summary>
    [HttpGet("next-unhandled")]
    public ApiResponse<NextUnhandledDto?> NextUnhandled([FromQuery] long hostId, [FromQuery] string? date) =>
        ApiResponse<NextUnhandledDto?>.Ok(_list.FindNextUnhandled(hostId, ParseRequiredDate(date)));

    [HttpGet("{hostId:long}/{date}")]
    public ApiResponse<RecordDetailDto> GetDetail(long hostId, string date) =>
        ApiResponse<RecordDetailDto>.Ok(_detail.GetDetail(hostId, ParseRequiredDate(date)));

    /// <summary>
    /// 報告全文（前端以等寬字型原樣呈現）。<c>kind</c> 預設風險報告，
    /// <c>weekly_checkup</c> 取該日紀錄所掛的體檢報告。查無回 null，不是錯誤。
    /// </summary>
    [HttpGet("{hostId:long}/{date}/report")]
    public ApiResponse<ReportViewDto?> GetReport(long hostId, string date, [FromQuery] string? kind = null)
    {
        var reportKind = kind switch
        {
            null or "" or ReportKinds.DailyRisk => ReportKinds.DailyRisk,
            ReportKinds.WeeklyCheckup => ReportKinds.WeeklyCheckup,
            // 權限異動報告不掛在分析紀錄上（走 /api/permission-changes/report），
            // 未知種類一律擋掉而不是安靜回空，否則打錯字會被當成「這天沒報告」
            _ => throw DomainException.Validation("不支援的報告種類。")
        };
        return ApiResponse<ReportViewDto?>.Ok(_detail.GetReportView(hostId, ParseRequiredDate(date), reportKind));
    }

    /// <summary>
    /// 單一問題簽章的先前處理歷史（docs/archive/FEEDBACK-5-PLAN.md §4）。issueKey 含 <c>|</c>，
    /// 走 query string 不進路由（路由樣板對 <c>|</c> 的處理因 routing 設定而異，query string 天生無此問題）。
    /// </summary>
    [HttpGet("{hostId:long}/{date}/handling/issue-history")]
    public ApiResponse<IssueHistoryDto> GetIssueHistory(long hostId, string date, [FromQuery] string issueKey) =>
        ApiResponse<IssueHistoryDto>.Ok(_detail.GetIssueHistory(hostId, ParseRequiredDate(date), issueKey));
}

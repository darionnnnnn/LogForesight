using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// AI 加值層（docs/HISTORY.md §6）。純加值：AI 不可用或失敗時一律回 data:null，
/// 前端據此隱藏對應 UI，其餘功能不受影響。所有查詢都經既有 Service，繼承其可見範圍過濾。
/// </summary>
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly AiInsightService _ai;
    private readonly DashboardService _dashboard;
    private readonly RecordQueryService _records;
    private readonly WebAppSettings _settings;
    private readonly IRiskyEventLookup _eventLookup;
    private readonly IHostStore _hosts;

    public AiController(
        AiInsightService ai,
        DashboardService dashboard,
        RecordQueryService records,
        WebAppSettings settings,
        IRiskyEventLookup eventLookup,
        IHostStore hosts)
    {
        _ai = ai;
        _dashboard = dashboard;
        _records = records;
        _settings = settings;
        _eventLookup = eventLookup;
        _hosts = hosts;
    }

    /// <summary>AI 是否可用——前端在渲染前先問一次，避免對每個功能各發一次註定失敗的請求</summary>
    [HttpGet("status")]
    public ApiResponse<AiStatusDto> Status() =>
        ApiResponse<AiStatusDto>.Ok(new AiStatusDto { Available = _ai.Available });

    /// <summary>儀表板今日焦點（W1-1）</summary>
    [HttpGet("today-focus")]
    public async Task<ApiResponse<AiFocusDto?>> TodayFocus([FromQuery] int? days)
    {
        if (!_ai.Available) return ApiResponse<AiFocusDto?>.Ok(null);
        var dashboard = _dashboard.GetSummary(days ?? _settings.Ui.DashboardDefaultDays);
        return ApiResponse<AiFocusDto?>.Ok(await _ai.TodayFocusAsync(dashboard));
    }

    /// <summary>查詢結果 AI 歸納（W1-2）：跨主機同簽章聚類 → AI 講白話</summary>
    [HttpGet("query-summary")]
    public async Task<ApiResponse<AiTextDto?>> QuerySummary(
        [FromQuery] string? hostIds, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? riskLevels, [FromQuery] string? categories)
    {
        if (!_ai.Available) return ApiResponse<AiTextDto?>.Ok(null);

        var request = new RecordSearchRequest
        {
            HostIds = ParseLongs(hostIds),
            From = ParseDate(from),
            To = ParseDate(to),
            RiskLevels = ParseStrings(riskLevels),
            Categories = ParseStrings(categories)
        };
        var clusters = _records.ClusterSignatures(request);
        var salt = $"{from}|{to}|{riskLevels}|{categories}|{hostIds}";
        return ApiResponse<AiTextDto?>.Ok(await _ai.SummarizeQueryAsync(clusters, salt));
    }

    /// <summary>詳情頁單一問題的 AI 判讀（W2）</summary>
    [HttpGet("interpret-issue")]
    public async Task<ApiResponse<AiTextDto?>> InterpretIssue(
        [FromQuery] long hostId, [FromQuery] string date, [FromQuery] string issueKey)
    {
        if (!_ai.Available) return ApiResponse<AiTextDto?>.Ok(null);

        var parsedDate = ParseRequiredDate(date);
        var detail = _records.GetDetail(hostId, parsedDate);
        var issue = detail.TopIssues.FirstOrDefault(i => i.IssueKey == issueKey);
        if (issue == null) return ApiResponse<AiTextDto?>.Ok(null);

        return ApiResponse<AiTextDto?>.Ok(await _ai.InterpretIssueAsync(issue, detail.HostName, detail.Date));
    }

    /// <summary>
    /// 詳情頁對話（R7 精簡版）：單一問題為範圍，不持久化，client 每輪送完整歷史。
    /// 授權與 context 完全複用 GetDetail（同 interpret-issue），不信任 client 帶來的內容以外欄位——
    /// 輪數、角色交錯、單則長度一律伺服器端強制，不只是前端 UX 限制。
    /// </summary>
    [HttpPost("chat")]
    public async Task<ApiResponse<AiTextDto?>> Chat([FromBody] ChatRequest request)
    {
        if (!_ai.Available) return ApiResponse<AiTextDto?>.Ok(null);

        var parsedDate = ParseRequiredDate(request.Date);
        var detail = _records.GetDetail(request.HostId, parsedDate);
        var issue = detail.TopIssues.FirstOrDefault(i => i.IssueKey == request.IssueKey);
        if (issue == null) return ApiResponse<AiTextDto?>.Ok(null);

        ValidateChatMessages(request.Messages);

        // 當日分析報告全文一併餵給 AI（docs/HISTORY.md #11）：與「報告全文」卡同一條路、
        // 同一套授權（GetReport 內部同樣先 GetOne 驗證可見範圍）；無報告（低風險日）時為 null，
        // ChatAsync 略過即可，不影響既有問答流程
        var report = _records.GetReport(request.HostId, parsedDate);

        // 詢問 AI 現場事件取得（docs/WEB-SCHEDULER-PLAN.md §2.2.4）：只在第一輪（使用者剛選定這個
        // 問題、還沒有對話歷史）取一次，後續輪次沿用同一份 context，不必每輪重查。
        // IRiskyEventLookup 內部先查風險 log 暫存（本機與 NetIQ 主機皆有）、查無才 fallback 既有的
        // Sentinel 即時查詢（僅 NetIQ 主機、開關/節流語意不變）。任何失敗／不符資格一律回 null，靜默降級。
        LiveEventFetchResult? liveEvents = null;
        if (request.Messages.Count == 1)
        {
            var host = _hosts.Get(request.HostId);
            if (host != null) liveEvents = await _eventLookup.FindAsync(host, parsedDate, issue.Source, issue.EventId);
        }

        return ApiResponse<AiTextDto?>.Ok(await _ai.ChatAsync(issue, detail.HostName, detail.Date, request.Messages, report, liveEvents));
    }

    private const int MaxChatTurns = 10;
    private const int MaxUserMessageChars = 500;
    private const int MaxAssistantMessageChars = 1500;

    /// <summary>
    /// 對話輪數上限、角色交錯、單則長度一律在這裡強制——client 端的限制只是 UX，
    /// 真正的防線在這裡（例如用 curl 直接打這支 API 跳過前端）。
    /// </summary>
    private static void ValidateChatMessages(List<ChatMessageDto> messages)
    {
        if (messages.Count == 0) throw DomainException.Validation("對話內容不可為空。");

        var userTurns = messages.Count(m => m.Role == "user");
        if (userTurns > MaxChatTurns) throw DomainException.Validation("對話已達 10 輪上限，請清除重來。");

        if (messages[^1].Role != "user") throw DomainException.Validation("對話格式錯誤：最後一則必須是使用者訊息。");

        for (int i = 0; i < messages.Count; i++)
        {
            var expectedRole = i % 2 == 0 ? "user" : "assistant";
            if (messages[i].Role != expectedRole)
                throw DomainException.Validation("對話格式錯誤：角色必須依使用者／AI 交替。");

            var maxLen = messages[i].Role == "user" ? MaxUserMessageChars : MaxAssistantMessageChars;
            if (string.IsNullOrWhiteSpace(messages[i].Content) || messages[i].Content.Length > maxLen)
                throw DomainException.Validation($"對話格式錯誤：訊息內容不可為空，且不可超過 {maxLen} 字。");
        }
    }

}

public class AiStatusDto
{
    public bool Available { get; set; }
}

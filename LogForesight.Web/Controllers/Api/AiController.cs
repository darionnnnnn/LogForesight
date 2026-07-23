using System.Globalization;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// AI 加值層（docs/SCALE-2000-PLAN.md §6）。純加值：AI 不可用或失敗時一律回 data:null，
/// 前端據此隱藏對應 UI，其餘功能不受影響。所有查詢都經既有 Service，繼承其可見範圍過濾。
/// </summary>
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IAiInsightService _ai;
    private readonly IDashboardService _dashboard;
    private readonly IRecordQueryService _records;
    private readonly WebAppSettings _settings;

    public AiController(
        IAiInsightService ai,
        IDashboardService dashboard,
        IRecordQueryService records,
        WebAppSettings settings)
    {
        _ai = ai;
        _dashboard = dashboard;
        _records = records;
        _settings = settings;
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

        var parsedDate = ParseDate(date) ?? throw DomainException.Validation("日期格式必須為 yyyy-MM-dd。");
        var detail = _records.GetDetail(hostId, parsedDate);
        var issue = detail.TopIssues.FirstOrDefault(i => i.IssueKey == issueKey);
        if (issue == null) return ApiResponse<AiTextDto?>.Ok(null);

        return ApiResponse<AiTextDto?>.Ok(await _ai.InterpretIssueAsync(issue, detail.HostName, detail.Date));
    }

    private static List<long>? ParseLongs(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var v) ? v : (long?)null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();

    private static List<string>? ParseStrings(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}

public class AiStatusDto
{
    public bool Available { get; set; }
}

using System.Text;
using System.Text.Json.Serialization;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

// ── 回傳前端的 DTO（AI 加值層，缺 AI 時為 null，前端據此隱藏）──────────────────

public class AiFocusItemDto
{
    public string Text { get; set; } = string.Empty;
    /// <summary>驗證過的下鑽網址；AI 給的參數沒過白名單時為 null（只顯示文字）</summary>
    public string? Link { get; set; }
}

public class AiFocusDto
{
    public List<AiFocusItemDto> Items { get; set; } = new();
}

public class AiTextDto
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// AI 加值功能（docs/SCALE-2000-PLAN.md §6 W1＋W2）。
/// 每個功能：把**已彙總的結構化統計**餵給 AI，要一段短輸出。程式先算好確定性的部分，
/// AI 只做「講白話、排順序」。AI 不可用時一律回 null（呼叫端隱藏對應 UI）。
/// </summary>
public interface IAiInsightService
{
    bool Available { get; }

    Task<AiFocusDto?> TodayFocusAsync(DashboardDto dashboard);
    Task<AiTextDto?> SummarizeQueryAsync(List<IssueClusterDto> clusters, string cacheSalt);
    Task<AiTextDto?> InterpretIssueAsync(IssueDto issue, string hostName, string date);
}

public class AiInsightService : IAiInsightService
{
    private readonly IWebAiService _ai;

    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
    { "Storage", "Hardware", "Security", "Service", "Backup", "Config", "Resource", "Other" };
    private static readonly HashSet<string> KnownRisks = new() { "高", "中", "低" };

    public AiInsightService(IWebAiService ai) => _ai = ai;

    public bool Available => _ai.Available;

    // ── W1-1 儀表板「今日焦點」──────────────────────────────────────────────

    public async Task<AiFocusDto?> TodayFocusAsync(DashboardDto dashboard)
    {
        // 全期無風險就不必勞煩 AI——沒有可排序的東西
        if (dashboard.HighRiskDays == 0 && dashboard.MediumRiskDays == 0) return null;

        var input = BuildFocusInput(dashboard);
        var cacheKey = $"focus|{dashboard.From}|{dashboard.To}|{WebAiService.HashInput(input)}";

        const string system =
            "你是資深維運分析師。以下是一段期間的風險彙總統計。請挑出最值得優先處理的最多三件事，" +
            "每件用一句不超過 40 字的白話說明，並可附一個下鑽篩選（categories 用英文類別、riskLevels 用 高/中/低）。" +
            "只根據提供的統計，不要臆測。" + PromptGuidelines.Language +
            "只輸出 JSON：{\"items\":[{\"text\":\"...\",\"categories\":\"Storage\",\"riskLevels\":\"高,中\"}]}，" +
            "categories/riskLevels 可省略。第一個字元必須是 {。";

        var result = await _ai.GenerateAsync<FocusResult>(cacheKey, system, input);
        if (result?.Items == null || result.Items.Count == 0) return null;

        var dto = new AiFocusDto();
        foreach (var item in result.Items.Take(3))
        {
            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            dto.Items.Add(new AiFocusItemDto
            {
                Text = item.Text.Trim(),
                Link = BuildValidatedLink(item.Categories, item.RiskLevels, dashboard.From, dashboard.To)
            });
        }

        return dto.Items.Count > 0 ? dto : null;
    }

    private static string BuildFocusInput(DashboardDto d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"期間 {d.From}～{d.To}：高風險日 {d.HighRiskDays}、中風險日 {d.MediumRiskDays}、涵蓋率缺口 {d.CoverageGapDays} 天。");
        if (d.Categories.Count > 0)
        {
            sb.AppendLine("風險類別（問題數／受影響主機）：");
            foreach (var c in d.Categories.Take(8))
                sb.AppendLine($"  - {c.Category}：{c.IssueCount} 項、{c.AffectedHosts} 台，最高嚴重度 {c.MaxSeverity}");
        }
        if (d.HostRanking.Count > 0)
        {
            sb.AppendLine("風險主機排行：");
            foreach (var h in d.HostRanking.Take(5))
                sb.AppendLine($"  - {h.HostName}：高風險 {h.HighRiskDays} 日、關聯訊號 {h.CorrelationDays} 日");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 白名單驗證 AI 回傳的下鑽參數，通過才組連結。AI 產出不可信任——
    /// 未過驗證只丟連結、保留文字（不讓惡意/亂填的參數變成頁面上的網址）。
    /// </summary>
    private static string? BuildValidatedLink(string? categories, string? riskLevels, string from, string to)
    {
        var cats = SplitValid(categories, KnownCategories);
        var risks = SplitValid(riskLevels, KnownRisks);
        if (cats.Count == 0 && risks.Count == 0) return null;

        var parts = new List<string>();
        if (cats.Count > 0) parts.Add("categories=" + Uri.EscapeDataString(string.Join(",", cats)));
        if (risks.Count > 0) parts.Add("riskLevels=" + Uri.EscapeDataString(string.Join(",", risks)));
        parts.Add("from=" + from);
        parts.Add("to=" + to);
        return "/records?" + string.Join("&", parts);
    }

    private static List<string> SplitValid(string? csv, HashSet<string> allowed) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(allowed.Contains)
                .Distinct()
                .ToList();

    // ── W1-2 查詢結果 AI 歸納（聚類由程式先算，AI 只講白話）─────────────────

    public async Task<AiTextDto?> SummarizeQueryAsync(List<IssueClusterDto> clusters, string cacheSalt)
    {
        if (clusters.Count == 0) return null;

        var sb = new StringBuilder("以下是查詢結果中跨主機出現的相同事件（程式已聚合）：\n");
        foreach (var c in clusters.Take(5))
            sb.AppendLine($"  - {c.Source} (EventId {c.EventId})：{c.HostCount} 台主機、合計 {c.TotalCount} 次");

        var cacheKey = $"summary|{WebAiService.HashInput(cacheSalt + sb)}";
        const string system =
            "你是資深維運分析師。以下是查詢結果中跨主機的相同事件聚合。請用不超過三句白話點出可能的共通成因或值得注意處。" +
            "只根據提供的聚合資料，不要臆測。" + PromptGuidelines.Language +
            "只輸出 JSON：{\"text\":\"...\"}。第一個字元必須是 {。";

        var result = await _ai.GenerateAsync<TextResult>(cacheKey, system, sb.ToString());
        return string.IsNullOrWhiteSpace(result?.Text) ? null : new AiTextDto { Text = result!.Text.Trim() };
    }

    // ── W2 詳情頁快速判讀（主要服務未命中規則的「其他」類別）──────────────

    public async Task<AiTextDto?> InterpretIssueAsync(IssueDto issue, string hostName, string date)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"主機 {hostName}，日期 {date}。");
        sb.AppendLine($"事件：{issue.Source} (EventId {issue.EventId})，記錄檔 {issue.LogName}，嚴重度 {issue.Severity}，今日 {issue.Count} 次。");
        if (!string.IsNullOrWhiteSpace(issue.TrendText)) sb.AppendLine($"趨勢：{issue.TrendText}");
        if (!string.IsNullOrWhiteSpace(issue.KeyDetails)) sb.AppendLine($"關鍵細節：{issue.KeyDetails}");
        if (issue.SampleMessages?.Count > 0)
            sb.AppendLine("範例訊息：" + string.Join(" / ", issue.SampleMessages.Take(2)).Truncate(500));

        var cacheKey = $"interpret|{hostName}|{date}|{issue.IssueKey}";
        const string system =
            "你是資深 Windows Server 維運分析師。針對以下單一事件，用兩句話回答：這件事要不要緊、以及該先做什麼。" +
            "只根據提供的資料，不要臆測。" + PromptGuidelines.Language +
            "只輸出 JSON：{\"text\":\"...\"}。第一個字元必須是 {。";

        var result = await _ai.GenerateAsync<TextResult>(cacheKey, system, sb.ToString());
        return string.IsNullOrWhiteSpace(result?.Text) ? null : new AiTextDto { Text = result!.Text.Trim() };
    }

    // ── AI 回應契約（內部）──────────────────────────────────────────────────

    private class FocusResult
    {
        [JsonPropertyName("items")] public List<FocusItem> Items { get; set; } = new();
    }

    private class FocusItem
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("categories")] public string? Categories { get; set; }
        [JsonPropertyName("riskLevels")] public string? RiskLevels { get; set; }
    }

    private class TextResult
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}

/// <summary>跨主機同簽章聚類（程式確定性計算，供 AI 歸納）</summary>
public class IssueClusterDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int HostCount { get; set; }
    public int TotalCount { get; set; }
}

internal static class StringTruncateExtensions
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max];
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LogForesight.Tests.Scenarios;

// ── 結構化結果：每天一份快照，供測試斷言；ToMarkdown 再把同一份資料排成表格 ───────────

/// <summary>一台主機當天的主機日風險</summary>
public sealed record HostDayView(
    string Host, bool HasRecord, string RiskLevel, string RiskBasis, int ErrorCount, int WarningCount, bool DataIncomplete);

/// <summary>問題清單的一列（主機日分析結果的 TopIssues）</summary>
public sealed record IssueView(
    string Host, string Label, string Severity, int Count, bool ElevatesDayRisk, bool Suppressed, string Trend);

/// <summary>主機日的告警文字（趨勢／關聯）；<see cref="Suppressed"/>＝被抑制而未計入風險</summary>
public sealed record AlertView(string Host, string Kind, string Text, bool Suppressed);

/// <summary>
/// 夜間派工對某主機日某個問題的處置結果。
/// <see cref="Outcome"/>：delivered（掛進案件或派入交辦單）／skipped（略過，原因在 <see cref="Reason"/>）／
/// deferred（延後，原因同）／failed／untracked（派工沒有登記這個問題，例如沒進派工流程）。
/// </summary>
public sealed record DispatchDecisionView(string Host, string Issue, string Outcome, string? Reason, long? WorkOrderId);

/// <summary>一趟夜間派工的彙總（<c>NightlyDispatchSummary</c>）</summary>
public sealed record DispatchSummaryView(
    int CreatedOrders, int AttachedMembers, IReadOnlyDictionary<string, int> SkipCounts,
    IReadOnlyList<string> PerHandlerLines);

/// <summary>交辦單成員（一台主機的一個問題案件）</summary>
public sealed record OrderMemberView(string Host, string Issue, string Status, string Handler);

/// <summary>交辦單快照。<see cref="IsNew"/>＝這張單是當天才出現的</summary>
public sealed record OrderView(
    long OrderId, string Issue, string Handler, string Origin, string State, bool IsNew,
    IReadOnlyList<OrderMemberView> Members);

/// <summary><see cref="Phase"/>：分析（夜間分析與派工階段）／動作（人為動作階段）</summary>
public sealed record OutboxItemView(string Phase, string Kind, string Recipient, long WorkOrderId, int MemberDelta, string Actor,
    string? Reason = null, string? Status = null, DateTime? Until = null, bool BlocksRedispatch = false,
    string? Detail = null, int Escalated = 0, int Unassignable = 0, int NoRecipient = 0);

public sealed record AuditItemView(string Phase, string Account, string Action, string Result, string Summary);

public sealed record ActionResultView(string Actor, string Description, int StatusCode, string? ErrorCode, string? Message, string? Body = null)
{
    /// <summary>2xx＝成功；0＝對象解析不到而沒有送出</summary>
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

/// <summary>某一天的完整快照</summary>
public sealed class DayResult
{
    public int Day { get; init; }

    public int HostDaysAnalyzed { get; init; }

    public int HostDaysFailed { get; init; }

    /// <summary>分析流程的警示行（⚠／✗ 開頭的 console 行），沒有就是空</summary>
    public IReadOnlyList<string> AnalysisWarnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<HostDayView> Hosts { get; init; } = Array.Empty<HostDayView>();

    public IReadOnlyList<IssueView> Issues { get; init; } = Array.Empty<IssueView>();

    public IReadOnlyList<AlertView> Alerts { get; init; } = Array.Empty<AlertView>();

    public IReadOnlyList<DispatchDecisionView> Decisions { get; init; } = Array.Empty<DispatchDecisionView>();

    public DispatchSummaryView Dispatch { get; init; } = new(0, 0, new Dictionary<string, int>(), Array.Empty<string>());

    /// <summary>當天結束時全部交辦單（含已結案）的狀態</summary>
    public IReadOnlyList<OrderView> Orders { get; init; } = Array.Empty<OrderView>();

    public IReadOnlyList<OutboxItemView> OutboxAdded { get; init; } = Array.Empty<OutboxItemView>();

    public IReadOnlyList<AuditItemView> AuditAdded { get; init; } = Array.Empty<AuditItemView>();

    public IReadOnlyList<ActionResultView> Actions { get; init; } = Array.Empty<ActionResultView>();

    /// <summary>當天結束時的讀取探針結果（<c>DayPlan.Probe</c>），依宣告順序</summary>
    public IReadOnlyList<ProbeView> Probes { get; init; } = Array.Empty<ProbeView>();

    /// <summary>當天站台寄出的信（替身記錄；含即時寄出的上報信，不含摘要排程——背景服務在測試站台移除）</summary>
    public IReadOnlyList<SentMailRecord> MailsSent { get; init; } = Array.Empty<SentMailRecord>();

    public ProbeView Probe(string name) =>
        Probes.FirstOrDefault(p => p.Name == name) ?? throw new KeyNotFoundException($"第 {Day} 天沒有探針「{name}」");

    public HostDayView Host(string name) =>
        Hosts.FirstOrDefault(h => string.Equals(h.Host, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"第 {Day} 天沒有主機「{name}」的快照");

    public IEnumerable<OrderView> NewOrders => Orders.Where(o => o.IsNew);

    public OrderView Order(long id) =>
        Orders.FirstOrDefault(o => o.OrderId == id) ?? throw new KeyNotFoundException($"第 {Day} 天沒有單#{id}");
}

/// <summary>整個情境的結果</summary>
public sealed class ScenarioResult
{
    internal ScenarioResult(string name, IReadOnlyList<DayResult> days, IReadOnlyDictionary<DateTime, int> dateToDay)
    {
        Name = name;
        Days = days;
        _dateToDay = dateToDay;
    }

    internal void SetVerdict(ScenarioVerdict verdict) => Verdict = verdict;

    /// <summary>用另一份預期／已知差異清單重新比對同一份結果（不必重跑情境）。</summary>
    public ScenarioVerdict Evaluate(Scenario scenario) => ScenarioVerdict.Evaluate(scenario, this);

    private readonly IReadOnlyDictionary<DateTime, int> _dateToDay;

    public string Name { get; }

    public IReadOnlyList<DayResult> Days { get; }

    /// <summary>預期與已知差異的比對結果（沒有宣告任何預期時是空的、視為通過）</summary>
    public ScenarioVerdict Verdict { get; private set; } = null!;

    public DayResult Day(int day) =>
        Days.FirstOrDefault(d => d.Day == day) ?? throw new KeyNotFoundException($"情境沒有第 {day} 天");

    /// <summary>預期比對有問題就擲 <see cref="ScenarioAssertionException"/>（訊息列出預期與實際）</summary>
    public void AssertExpectations() => Verdict.AssertOk();

    // ── 輸出 ──────────────────────────────────────────────────────────────

    private static readonly Regex DateText = new(@"(?<![\d])(\d{4})[-/](\d{2})[-/](\d{2})(?![\d])", RegexOptions.Compiled);

    /// <summary>
    /// 把文字裡的真實日期（yyyy-MM-dd）換成「第 N 天」。不在情境範圍內的日期換成「錨點±k」，
    /// 這樣同一情境在不同日子跑，輸出逐字相同。
    /// </summary>
    internal string NormalizeDates(string text) =>
        DateText.Replace(text, m =>
        {
            if (!DateTime.TryParse($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return m.Value;
            if (_dateToDay.TryGetValue(date.Date, out var day)) return $"第{day}天";
            var firstDate = _dateToDay.Where(p => p.Value == 1).Select(p => p.Key).First();
            var offset = (date.Date - firstDate).Days;
            return offset >= 0 ? $"第{1 + offset}天" : $"第{offset}天";
        });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 情境：{Name}");
        foreach (var day in Days)
        {
            sb.AppendLine();
            AppendDay(sb, day);
        }

        return NormalizeDates(sb.ToString()).TrimEnd() + "\n";
    }

    private static void AppendDay(StringBuilder sb, DayResult d)
    {
        sb.AppendLine($"## 第 {d.Day} 天");
        sb.AppendLine();
        sb.AppendLine($"分析：主機日 {d.HostDaysAnalyzed} 個完成、{d.HostDaysFailed} 個失敗" +
                      (d.AnalysisWarnings.Count > 0 ? $"；警示 {d.AnalysisWarnings.Count} 行" : ""));
        foreach (var warning in d.AnalysisWarnings) sb.AppendLine($"- {warning.Trim()}");

        sb.AppendLine();
        sb.AppendLine("### 主機日風險");
        Table(sb, new[] { "主機", "風險", "判定依據", "錯誤", "警告", "資料不完整" },
            d.Hosts.Select(h => new[]
            {
                h.Host, h.HasRecord ? h.RiskLevel : "（無紀錄）", h.RiskBasis, h.ErrorCount.ToString(CultureInfo.InvariantCulture),
                h.WarningCount.ToString(CultureInfo.InvariantCulture), h.DataIncomplete ? "是" : "否"
            }));

        sb.AppendLine();
        sb.AppendLine("### 問題清單");
        Table(sb, new[] { "主機", "問題（來源 事件編號）", "嚴重度", "次數", "重大", "被抑制", "趨勢" },
            d.Issues.Select(i => new[]
            {
                i.Host, i.Label, i.Severity, i.Count.ToString(CultureInfo.InvariantCulture),
                i.ElevatesDayRisk ? "是" : "否", i.Suppressed ? "是" : "否", i.Trend
            }));

        sb.AppendLine();
        sb.AppendLine("### 趨勢告警與關聯發現");
        Table(sb, new[] { "主機", "類型", "內容", "被抑制" },
            d.Alerts.Select(a => new[] { a.Host, a.Kind, a.Text, a.Suppressed ? "是" : "否" }));

        sb.AppendLine();
        sb.AppendLine("### 夜間派工決策");
        sb.AppendLine($"彙總：建單 {d.Dispatch.CreatedOrders}、掛入成員 {d.Dispatch.AttachedMembers}" +
                      (d.Dispatch.SkipCounts.Count > 0
                          ? "；略過 " + string.Join("、", d.Dispatch.SkipCounts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}×{p.Value}"))
                          : ""));
        foreach (var line in d.Dispatch.PerHandlerLines) sb.AppendLine($"- {line}");
        sb.AppendLine();
        Table(sb, new[] { "主機", "問題", "結果", "原因代碼", "單號" },
            d.Decisions.Select(x => new[]
            {
                x.Host, x.Issue, x.Outcome, x.Reason ?? "", x.WorkOrderId is { } id ? $"#{id}" : ""
            }));

        sb.AppendLine();
        sb.AppendLine("### 交辦單（當天結束時）");
        Table(sb, new[] { "單號", "問題", "處理人", "來源", "狀態", "新", "成員（主機：狀態）" },
            d.Orders.Select(o => new[]
            {
                $"#{o.OrderId}", o.Issue, o.Handler, o.Origin, o.State, o.IsNew ? "新" : "",
                string.Join("；", o.Members.Select(m => $"{m.Host}:{m.Status}"))
            }));

        sb.AppendLine();
        sb.AppendLine("### 通知佇列（新增）");
        Table(sb, new[] { "階段", "種類", "收件人", "單號", "成員變化", "操作者" },
            d.OutboxAdded.Select(o => new[]
            {
                o.Phase, o.Kind, o.Recipient, $"#{o.WorkOrderId}", o.MemberDelta.ToString(CultureInfo.InvariantCulture), o.Actor
            }));

        sb.AppendLine();
        sb.AppendLine("### 稽核紀錄（新增）");
        Table(sb, new[] { "階段", "帳號", "動作", "結果", "摘要" },
            d.AuditAdded.Select(a => new[] { a.Phase, a.Account, a.Action, a.Result, a.Summary }));

        sb.AppendLine();
        sb.AppendLine("### 人為動作");
        Table(sb, new[] { "帳號", "動作", "HTTP", "錯誤碼", "訊息" },
            d.Actions.Select(a => new[]
            {
                a.Actor, a.Description, a.StatusCode == 0 ? "未送出" : a.StatusCode.ToString(CultureInfo.InvariantCulture),
                a.ErrorCode ?? "", a.Message ?? ""
            }));
    }

    private static void Table(StringBuilder sb, string[] header, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            sb.AppendLine("（無）");
            return;
        }

        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("|" + string.Concat(header.Select(_ => " --- |")));
        foreach (var row in list)
            sb.AppendLine("| " + string.Join(" | ", row.Select(Cell)) + " |");
    }

    private static string Cell(string? value) =>
        string.IsNullOrEmpty(value) ? " " : value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
}

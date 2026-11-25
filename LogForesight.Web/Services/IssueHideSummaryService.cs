using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題負責與靜音頁的「不顯示設定總覽」（第 81 輪批次 N，F16-4）：每個問題一列，彙總目前生效的四種設定——
/// 靜音（迄日）、機房結論、抑制（幾條、範圍）、不再打擾（幾台、其中幾台會恢復）。四個來源各一次批次查詢：
/// 問題檔案（靜音與結論）、抑制清單、規則表、被「不再自動派工」擋住的案件（<see cref="DismissedIssueService.GetIssueSummaries"/>）。
///
/// **判定都重用既有的**，這裡不寫第二份：
/// 靜音＝<see cref="IssueProfile.CurrentMute"/>（以今天判定，與「靜音中」頁籤同一函式）；
/// 不再打擾＝<see cref="DismissedIssueService"/> 的共用閘門（台數與待派頁清單每組相同）；
/// 抑制生效＝未設到期或到期時間在現在之後（<see cref="SuppressionFilter"/> 的同一口徑），規則型以
/// <see cref="KnownIssueCatalog.FindRuleByAggregateKey"/> 把問題對到規則（問題查詢列的 ruleId 同一出口）。
///
/// 抑制是掛在規則上的，沒有「問題清單」可直接列：規則型抑制要反查有哪些問題，只能看近 <see cref="RecentIssueWindowDays"/> 天出現過的問題
/// （多一次聚合查詢，且只在確實有生效中的規則型抑制時才查）；簽章型抑制的簽章鍵本身就含來源與事件編號，不必反查。
/// 因此「近期沒出現過、也沒有其他三種設定」的規則抑制不會出現在這份總覽，到規則維護的「告警抑制」頁籤才看得到完整清單。
/// </summary>
public sealed class IssueHideSummaryService
{
    /// <summary>反查規則型抑制的問題時回看的天數（與問題選擇器的近期窗口同值）</summary>
    internal const int RecentIssueWindowDays = 30;

    private readonly IIssueOwnerStore _owners;
    private readonly ISuppressionStore _suppressions;
    private readonly IKnownIssueRuleStore _rules;
    private readonly IIssueAggregateQuery _aggregates;
    private readonly DismissedIssueService _dismissed;
    private readonly TimeProvider _clock;

    public IssueHideSummaryService(
        IIssueOwnerStore owners, ISuppressionStore suppressions, IKnownIssueRuleStore rules,
        IIssueAggregateQuery aggregates, DismissedIssueService dismissed, TimeProvider? clock = null)
    {
        _owners = owners;
        _suppressions = suppressions;
        _rules = rules;
        _aggregates = aggregates;
        _dismissed = dismissed;
        _clock = clock ?? TimeProvider.System;
    }

    private sealed class Row
    {
        public string Source = string.Empty;
        public int EventId;
        public IssueHideMuteDto? Mute;
        public IssueHideConclusionDto? Conclusion;
        public IssueHideDismissedDto? Dismissed;
        public int Site, Group, Host;
        public string? RuleId;
    }

    public List<IssueHideSummaryDto> GetSummaries()
    {
        var today = _clock.LocalToday();
        var now = _clock.LocalNow();
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var rows = new Dictionary<(string SourceUpper, int EventId), Row>();

        Row RowFor(string source, int eventId)
        {
            var key = IssueProfile.KeyOf(source, eventId);
            if (!rows.TryGetValue(key, out var row))
                rows[key] = row = new Row { Source = source, EventId = eventId };
            return row;
        }

        // 1. 問題檔案：靜音與機房結論
        foreach (var p in _owners.GetAll())
        {
            var mute = IssueProfile.CurrentMute(p, today);
            if (mute == null && p.ConclusionStatus == null) continue;
            var row = RowFor(p.SourceName, p.EventId);
            if (mute != null)
                row.Mute = new IssueHideMuteDto { To = mute.To.Date, Reason = mute.Reason, ByAccount = mute.ByAccount };
            if (p.ConclusionStatus != null)
                row.Conclusion = new IssueHideConclusionDto
                {
                    Status = p.ConclusionStatus,
                    StatusText = HandlingTextHelpers.IssueStatusText(p.ConclusionStatus),
                    AutoApply = p.AutoApply
                };
        }

        // 2. 不再打擾（資料庫端候選＋共用閘門）
        foreach (var d in _dismissed.GetIssueSummaries())
            RowFor(d.Source, d.EventId).Dismissed = new IssueHideDismissedDto
            {
                Hosts = d.Hosts, ReleasingHosts = d.ReleasingHosts, NextReleaseInDays = d.NextReleaseInDays
            };

        // 3. 抑制：生效中的規則型與簽章型
        var active = _suppressions.LoadAll()
            .Where(s => (s.TargetType == SuppressionTargetTypes.Rule || s.TargetType == SuppressionTargetTypes.Signature)
                        && (s.ExpiresAt == null || s.ExpiresAt.Value > now))
            .ToList();
        var ruleSuppressions = active.Where(s => s.TargetType == SuppressionTargetTypes.Rule)
            .GroupBy(s => s.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        void Count(Row row, RuleSuppression s)
        {
            if (s.Scope == SuppressionScopes.Site) row.Site++;
            else if (s.Scope == SuppressionScopes.Group) row.Group++;
            else row.Host++;
        }

        foreach (var s in active.Where(s => s.TargetType == SuppressionTargetTypes.Signature))
        {
            if (string.IsNullOrWhiteSpace(s.SignatureKey) || IssueSignatureKey.TryParseFull(s.SignatureKey) is not { } parsed) continue;
            var row = RowFor(parsed.Source, parsed.EventId);
            Count(row, s);
            row.RuleId ??= KnownIssueCatalog.FindRuleByAggregateKey(rules, parsed.Source, parsed.EventId)?.Id;
        }

        if (ruleSuppressions.Count > 0)
        {
            // 規則型：以近期出現過的問題反查。沒有生效中的規則型抑制就不查
            var to = today;
            var from = to.AddDays(-(RecentIssueWindowDays - 1));
            foreach (var a in _aggregates.Aggregate(IssueExclusion.None, from, to, null))
            {
                var rule = KnownIssueCatalog.FindRuleByAggregateKey(rules, a.Source, a.EventId);
                if (rule == null || !ruleSuppressions.TryGetValue(rule.Id, out var list)) continue;
                var row = RowFor(a.Source, a.EventId);
                row.RuleId = rule.Id;
                foreach (var s in list) Count(row, s);
            }
        }

        return rows.Values
            .Where(r => r.Mute != null || r.Conclusion != null || r.Dismissed != null || r.Site + r.Group + r.Host > 0)
            .OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.EventId)
            .Select(r => new IssueHideSummaryDto
            {
                SourceName = r.Source,
                EventId = r.EventId,
                DisplayLabel = IssueNames.ForRules(rules, r.Source, r.EventId),
                Mute = r.Mute,
                Conclusion = r.Conclusion,
                Dismissed = r.Dismissed,
                Suppression = r.Site + r.Group + r.Host == 0 ? null : new IssueHideSuppressionDto
                {
                    Total = r.Site + r.Group + r.Host, SiteCount = r.Site, GroupCount = r.Group, HostCount = r.Host,
                    RuleId = r.RuleId
                }
            })
            .ToList();
    }
}

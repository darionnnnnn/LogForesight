using LogForesight.Core.Models;
using LogForesight.Core.Service;

namespace LogForesight.Core.Analysis;

/// <summary>歷史弱佐證修訂；只在可還原獨立下限時降級，不推測已遺失的 AI 或關聯證據。</summary>
public static class PrtgHistoricalRiskReview
{
    public const int Version = 1;
    public static readonly string[] WeakPatterns =
    [CorrelationPatternIds.PrtgStorageCorroborated, CorrelationPatternIds.PrtgCapacityCorroborated,
        CorrelationPatternIds.PrtgOutageCorroborated];

    public static bool IsWeakPattern(string? id) => id != null && WeakPatterns.Contains(id);
    private static readonly (string Old, string Current)[] Prefixes =
    [("【儲存故障雙重確認】", "【儲存異常同日訊號】"),
        ("【磁碟容量雙重確認】", "【容量異常同日訊號】"),
        ("【失聯獲 PRTG 證實】", "【關機與監測異常同日訊號】")];

    public static bool Apply(DailyAnalysisRecord record)
    {
        if (record.RiskReview?.Version >= Version) return false;
        var oldTexts = record.CorrelationAlerts.Concat(record.SuppressedCorrelationAlerts).ToArray();
        if (!oldTexts.Any(t => Prefixes.Any(p => t.StartsWith(p.Old, StringComparison.Ordinal))) &&
            !IsWeakPattern(record.RiskBasis)) return false;

        var original = new HistoricalRiskReview
        {
            Version = Version, OriginalRiskLevel = record.RiskLevel,
            OriginalRiskBasis = record.RiskBasis, OriginalHeadline = record.Headline,
            OriginalSummary = record.Summary, ReviewedAtUtc = DateTime.UtcNow
        };
        string Replace(string text)
        {
            foreach (var prefix in Prefixes)
                if (text.StartsWith(prefix.Old, StringComparison.Ordinal))
                    return prefix.Current + "歷史兩來源同日出現訊號；缺乏同資源及同時段證據，不能視為雙重確認。";
            return text;
        }
        record.CorrelationAlerts = record.CorrelationAlerts.Select(Replace).ToList();
        record.SuppressedCorrelationAlerts = record.SuppressedCorrelationAlerts.Select(Replace).ToList();
        foreach (var reference in record.CorrelationAlertRefs) reference.Text = Replace(reference.Text);

        var strongIndependent = record.TopIssues.Any(i => !i.Suppressed && i.ElevatesDayRisk);
        var hasOtherCorrelation = record.CorrelationAlertRefs.Any(r => !IsWeakPattern(r.PatternId)) ||
            record.CorrelationAlerts.Any(t => !record.CorrelationAlertRefs.Any(r => r.Text == t && IsWeakPattern(r.PatternId)));
        // RiskBasis 指向弱配對且無其他關聯／AI時，保存的規則旗標與趨勢足以還原下限。
        var canRecalculate = IsWeakPattern(record.RiskBasis) && record.TopIssues.Count > 0 && !record.AiAnalyzed && !hasOtherCorrelation;
        if (strongIndependent)
        {
            record.RiskLevel = RiskLevels.High;
            record.RiskBasis = "independent-rule";
            original.Status = "revised";
            original.Reason = "同日配對改列弱佐證；獨立重大規則仍支持高風險。";
        }
        else if (canRecalculate)
        {
            record.RiskLevel = record.TrendAlerts.Count > 0 ||
                record.TopIssues.Any(i => !i.Suppressed && i.Severity == IssueSeverity.High)
                ? RiskLevels.Medium : RiskLevels.Low;
            record.RiskBasis = "historical-independent-evidence";
            original.Status = "revised";
            original.Reason = "移除缺時間與資源證據的配對加權；有效風險依保存的獨立規則及趨勢重算。";
        }
        else
        {
            original.Status = "pending";
            original.Reason = "歷史風險待重評：保存內容不足以還原 AI／其他關聯的獨立風險，不自動降低原等級。";
        }
        // 舊敘事保存在修訂區；現行 AI／郵件不可繼續把原文當有效佐證。
        record.Headline = original.Status == "pending" ? "歷史風險待重評" : "歷史佐證已修訂";
        record.Summary = original.Reason;
        record.TrendAssessment = "請依保存的規則、趨勢與來源證據查證。";
        record.Action = "同日訊號不代表同一故障；請核對資源與時間，必要時重新分析。";
        record.AiAnalyzed = false;
        record.AiPending = false; // 歷史遷移不自動重寄或觸發新的 AI 告警。
        record.WeeklyCheckup = null; // 原週報仍在原始快照；不把舊「雙重確認」敘事當有效報告。
        record.RiskReview = original;
        return true;
    }
}

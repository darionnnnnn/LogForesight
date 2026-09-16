using System.Diagnostics;
using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// 把 PRTG finding 映射成問題簽章（EventId=0 + EventKey，同 Linux 規則模式）。
/// 分類與嚴重度由命中的規則物件給定，刻意不走通用規則分類表
/// （<c>KnownIssueCatalog.Classify</c> 只認 windows／linux 平台，PRTG 走它會讓 EventKey
/// 被清空、finding 脫離處理狀態鏈）。
/// </summary>
public static class PrtgFindingMapper
{
    public const string PrtgLogName = "PRTG";

    /// <summary>判定簽章是否源自 PRTG 規則（依 LogName == "PRTG"，不分大小寫）</summary>
    public static bool IsPrtg(LogIssueSignature? signature) =>
        signature != null && string.Equals(signature.LogName, PrtgLogName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一組 PRTG finding 推導出的日風險等級（docs/PRTG-SPEC.md §9）。
    /// 判定與 <c>LogAnalysisService.ComputeRuleBasedRisk</c> 的 issues 部分同語意：
    /// 任一未被抑制的 finding 帶 <c>ElevatesDayRisk</c> → 高；任一為 High → 中；否則低。
    ///
    /// 這是**單向上調**的輸入：呼叫端一律取 <c>RiskLevels.MoreSevere(既有, 這個)</c>，
    /// 絕不用它壓低既有等級——PRTG 只是輔助訊號，看不到事件層的證據。
    /// </summary>
    public static string RiskFromFindings(IReadOnlyList<LogIssueSignature> findings)
    {
        if (findings.Any(f => PrtgFindingMapper.IsPrtg(f) && !f.Suppressed && f.ElevatesDayRisk)) return RiskLevels.High;
        if (findings.Any(f => PrtgFindingMapper.IsPrtg(f) && !f.Suppressed && f.Severity == IssueSeverity.High)) return RiskLevels.Medium;
        return RiskLevels.Low;
    }

    /// <summary>
    /// 風險依據代碼（純顯示）：<c>prtg:{規則代碼}</c>。規則代碼取自 EventKey 的
    /// <c>prtg:{code}:{objid}</c>格式，取不到時退回 <c>prtg</c>。
    /// 沒有這個代碼，畫面會出現「高風險」卻說不出是哪個訊號拉上去的。
    /// </summary>
    public static string RiskBasisFrom(IReadOnlyList<LogIssueSignature> findings)
    {
        var decisive = findings.FirstOrDefault(f => PrtgFindingMapper.IsPrtg(f) && !f.Suppressed && f.ElevatesDayRisk)
                       ?? findings.FirstOrDefault(f => PrtgFindingMapper.IsPrtg(f) && !f.Suppressed && f.Severity == IssueSeverity.High);
        if (decisive == null) return "prtg";

        var parts = decisive.EventKey.Split(':');
        return parts.Length >= 2 && parts[0] == "prtg" ? $"prtg:{parts[1]}" : "prtg";
    }

    public static LogIssueSignature ToSignature(PrtgFinding finding, DateTime day)
    {
        var targetObjid = finding.SensorObjid ?? finding.DeviceObjid;

        return new LogIssueSignature
        {
            LogName = PrtgLogName,
            Source = $"PRTG:{finding.RuleCode}",
            EventId = 0,
            EntryType = EventLogEntryType.Warning,
            EventKey = $"prtg:{finding.RuleCode}:{targetObjid}",
            Count = 1,
            FirstSeen = "00:00",
            LastSeen = "23:59",
            SampleMessages = new List<string> { finding.Detail },
            DistinctMessageCount = 1,
            Category = finding.Rule.Category,
            Severity = finding.Rule.Severity,
            ElevatesDayRisk = finding.Rule.ElevatesDayRisk && !finding.Acknowledged,
            KnownIssue = finding.Rule.Description,
            RuleId = finding.Rule.Id
        };
    }
}

using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;

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
    /// <summary>舊案件的顯示文字可讀化；不改案件鍵、儲存內容或原始稽核。</summary>
    public static string DisplayStoredLabel(string label)
    {
        if (!label.StartsWith("PRTG:", StringComparison.OrdinalIgnoreCase)) return label;
        var start = label.IndexOf("（prtg:", StringComparison.Ordinal);
        if (start < 0 || !label.EndsWith('）')) return label;
        var key = label[(start + 1)..^1];
        if (key.Split(':') is not { Length: >= 5 } parts || !long.TryParse(parts[2], out var sensor) || sensor <= 0) return label;
        return new LogIssueSignature { LogName = PrtgLogName, EventKey = key }.SourceEventLabel;
    }

    public static string Fingerprint(IEnumerable<LogIssueSignature> issues) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(issues.Where(IsPrtg).OrderBy(i => i.EventKey).ToArray()))));

    /// <summary>判定簽章是否源自 PRTG 規則（依 LogName == "PRTG"，不分大小寫）</summary>
    public static bool IsPrtg(LogIssueSignature? signature) =>
        signature != null && string.Equals(signature.LogName, PrtgLogName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 從簽章 Source（<c>PRTG:{規則代碼}</c>，前綴不分大小寫）解出規則代碼。
    /// 前綴不符或代碼為空白時回 false。白話說明與 prompt 共用這一份解析，不各寫字串切割。
    /// </summary>
    public static bool TryGetRuleCode(string? source, out string code)
    {
        code = string.Empty;
        const string prefix = PrtgLogName + ":";
        if (source == null || !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var candidate = source.Substring(prefix.Length).Trim();
        if (candidate.Length == 0) return false;

        code = candidate;
        return true;
    }

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

    public static LogIssueSignature ToSignature(PrtgFinding finding, DateTime day, long? verifiedHostId = null)
    {
        var targetObjid = finding.SensorObjid ?? finding.DeviceObjid;
        var governedDiskAlias = IsGovernedDiskIdentityAlias(finding);
        var governedPressure = finding.RuleCode is PrtgRuleEvaluator.RuleResourceCpuPressure or
            PrtgRuleEvaluator.RuleResourceMemoryPressure or PrtgRuleEvaluator.RuleResourceDiskPressure &&
            finding.Rule.PrtgRuleCode == finding.RuleCode;
        var identityRuleCode = governedDiskAlias
            ? PrtgRuleEvaluator.RuleDiskFreeTrend : finding.RuleCode;

        var observations = SourceEvidence.BoundObservations(finding.EvidenceWindows
            .Where(window => window.StartUtc.Offset == TimeSpan.Zero && window.EndUtc.Offset == TimeSpan.Zero &&
                window.EndUtc > window.StartUtc)
            .Take(65).Select(window => new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.Prtg,
                ExactHostKey = verifiedHostId is > 0 ? $"host-id:{verifiedHostId}" : null,
                // The native sensor's monitored volume/endpoint and addressable evidence reference
                // have not been proved by objid, category, or our internal hash.
                ResourceScope = SourceResourceScope.Unknown,
                SourceReferenceQuality = SourceReferenceQuality.Unknown,
                WindowStartUtc = window.StartUtc, WindowEndUtc = window.EndUtc,
                WindowResolution = finding.EvidenceWindowResolution,
                ProjectionFingerprint = SourceEvidence.Fingerprint(finding.SourceGeneration,
                    finding.ResourceGeneration, targetObjid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    finding.RuleCode, window.StartUtc.ToString("O"), window.EndUtc.ToString("O"))
            }), out var truncated);
        return new LogIssueSignature
        {
            LogName = PrtgLogName,
            Source = $"PRTG:{identityRuleCode}",
            EventId = 0,
            EntryType = EventLogEntryType.Warning,
            EventKey = $"prtg:{identityRuleCode}:{targetObjid}" +
                (finding.SourceGeneration != null && finding.ResourceGeneration != null
                    ? $":{finding.SourceGeneration}:{finding.ResourceGeneration}" : ""),
            PrtgSourceGeneration = finding.SourceGeneration,
            PrtgResourceGeneration = finding.ResourceGeneration,
            PrtgIncidentStartedAt = finding.IncidentStartedAt,
            PrtgPresenceSourceDay = finding.PresenceSourceDay,
            PrtgPresenceSourceAsOf = finding.PresenceSourceAsOf,
            PrtgPresenceDeviceStatusAsOf = finding.PresenceDeviceStatusAsOf,
            PrtgPresenceSourceAuthorityFingerprint = finding.PresenceSourceAuthorityFingerprint,
            PrtgPresenceMappingFingerprint = finding.PresenceMappingFingerprint,
            PrtgPresenceInventoryFingerprint = finding.PresenceInventoryFingerprint,
            SourceObservations = observations,
            SourceObservationsTruncated = truncated,
            Count = 1,
            FirstSeen = "00:00",
            LastSeen = "23:59",
            SampleMessages = new List<string> { finding.Detail },
            DistinctMessageCount = 1,
            Category = finding.Rule.Category,
            Severity = finding.Rule.Severity,
            // PRTG 已確認只表示有人知悉；故障是否仍存在由狀態證據判定，
            // 通知重複與靜音另由處置層處理，不因此降低日風險。
            ElevatesDayRisk = finding.Rule.ElevatesDayRisk,
            KnownIssue = finding.Rule.Description,
            RuleId = finding.Rule.Id,
            PrtgDisplayLabel = governedDiskAlias ? finding.DisplayLabel : null,
            PrtgResourceReasonCodes = governedDiskAlias && finding.ResourceReasonCodes is { Count: > 0 and <= 2 } reasons &&
                reasons.All(code => code is "disk-two-hour-low-water" or "disk-seven-day-low-water-trend")
                ? reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() : null,
            PrtgRuleAdmissionFingerprint = governedPressure && finding.RuleAdmissionFingerprint is { Length: 64 } fingerprint &&
                fingerprint.All(Uri.IsHexDigit) ? fingerprint : null,
            PrtgTrendSourceRuleId = governedDiskAlias ? finding.TrendSourceRuleId : null,
            PrtgTrendSourceRuleFingerprint = governedDiskAlias && finding.TrendSourceRuleFingerprint is { Length: 64 } trendFingerprint &&
                trendFingerprint.All(Uri.IsHexDigit) ? trendFingerprint : null,
            PrtgChannelGeneration = governedPressure ? finding.ChannelGeneration : null,
            // 跨來源佐證（PrtgCorroboration）靠它分辨 sensor 類型；silent（device 層）為 null
            PrtgSensorCategory = finding.SensorCategory
        };
    }

    private static bool IsGovernedDiskIdentityAlias(PrtgFinding finding) =>
        finding.EventIdentityRuleCode == PrtgRuleEvaluator.RuleDiskFreeTrend &&
        finding.RuleCode == PrtgRuleEvaluator.RuleResourceDiskPressure &&
        finding.Rule.Id == "builtin-prtg-resource-disk-pressure" &&
        finding.SensorCategory == PrtgSensorCategories.Disk &&
        !string.IsNullOrWhiteSpace(finding.DisplayLabel) &&
        finding.RuleAdmissionFingerprint is { Length: 64 } admissionFingerprint && admissionFingerprint.All(Uri.IsHexDigit) &&
        finding.ResourceReasonCodes is { Count: > 0 and <= 2 } reasons &&
        reasons.All(code => code is "disk-two-hour-low-water" or "disk-seven-day-low-water-trend");
}

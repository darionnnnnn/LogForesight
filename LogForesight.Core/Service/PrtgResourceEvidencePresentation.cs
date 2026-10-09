using System.Security.Cryptography;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>Safe, bounded presentation of resource evidence already persisted on a formal PRTG finding.</summary>
public sealed record PrtgResourceEvidencePresentation(
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> ReasonText,
    string EvidenceVersionReference,
    string RuleAdmissionVersionReference)
{
    private static readonly IReadOnlyDictionary<string, string> ReasonDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["disk-two-hour-low-water"] = "兩個完整小時皆處於磁碟低水位",
            ["disk-seven-day-low-water-trend"] = "七日趨勢預估將到達低水位"
        };

    public static PrtgResourceEvidencePresentation? From(LogIssueSignature issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        if (!IsFormalPrtgResourcePressureIssue(issue)) return null;

        var reasons = issue.PrtgResourceReasonCodes is { Count: > 0 and <= 2 } codes &&
                      codes.All(ReasonDescriptions.ContainsKey)
            ? codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var sourceValid = IsBoundedStoredVersion(issue.PrtgSourceGeneration);
        var resourceValid = IsBoundedStoredVersion(issue.PrtgResourceGeneration);
        var channelValid = IsBoundedStoredVersion(issue.PrtgChannelGeneration);
        var admissionValid = IsSha256(issue.PrtgRuleAdmissionFingerprint);

        return new PrtgResourceEvidencePresentation(
            reasons,
            reasons.Select(code => ReasonDescriptions[code]).ToArray(),
            sourceValid && resourceValid && channelValid && admissionValid
                ? StableReference("prtg-resource-evidence-v1", issue.PrtgSourceGeneration!, issue.PrtgResourceGeneration!,
                    issue.PrtgChannelGeneration!, issue.PrtgRuleAdmissionFingerprint!)
                : "unknown",
            admissionValid
                ? StableReference("prtg-rule-admission-v1", issue.PrtgRuleAdmissionFingerprint!)
                : "unknown");
    }

    public static bool IsFormalPrtgResourcePressureIssue(LogIssueSignature issue)
    {
        if (!PrtgFindingMapper.IsPrtg(issue) || !PrtgFindingMapper.TryGetRuleCode(issue.Source, out var code)) return false;
        return issue.RuleId switch
        {
            "builtin-prtg-resource-cpu-pressure" => code == PrtgRuleEvaluator.RuleResourceCpuPressure,
            "builtin-prtg-resource-memory-pressure" => code == PrtgRuleEvaluator.RuleResourceMemoryPressure,
            "builtin-prtg-resource-disk-pressure" => code == PrtgRuleEvaluator.RuleDiskFreeTrend,
            _ => false
        };
    }

    private static bool IsBoundedStoredVersion(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => !char.IsControl(character));

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string StableReference(params string[] components) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(components)));
}

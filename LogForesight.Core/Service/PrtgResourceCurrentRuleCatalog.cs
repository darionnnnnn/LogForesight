using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>Current enabled PRTG rule chosen by the stored catalog, with its exact reviewable version.</summary>
public sealed record PrtgResourceCurrentRule(KnownIssueRule Rule, string Fingerprint);

public sealed class PrtgResourceCurrentRuleCatalog
{
    private readonly IReadOnlyDictionary<PrtgResourceFamily, PrtgResourceCurrentRule?> _rules;
    private readonly string _diskTrendFingerprint;

    private PrtgResourceCurrentRuleCatalog(
        IReadOnlyDictionary<PrtgResourceFamily, PrtgResourceCurrentRule?> rules, bool diskTrendEnabled,
        string diskTrendFingerprint)
    { _rules = rules; DiskTrendEnabled = diskTrendEnabled; _diskTrendFingerprint = diskTrendFingerprint; DiskTrendRuleFingerprint = diskTrendEnabled ? diskTrendFingerprint : ""; }

    public bool DiskTrendEnabled { get; }
    public string? DiskTrendRuleId { get; private init; }
    public string DiskTrendRuleFingerprint { get; }

    public PrtgResourceCurrentRule? For(PrtgResourceFamily family) => _rules.GetValueOrDefault(family);

    public string AdmissionFingerprintFor(PrtgResourceFamily family)
    {
        var selected = For(family)?.Fingerprint ?? "disabled";
        return family == PrtgResourceFamily.Disk
            ? Hash(selected + "\u001f" + _diskTrendFingerprint)
            : selected;
    }

    public static PrtgResourceCurrentRuleCatalog Load(StorageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return Load(new KnownIssueRuleStore(backend.Blob("rules")));
    }

    public static PrtgResourceCurrentRuleCatalog Load(KnownIssueRuleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var loaded = store.Exists ? store.Load() : null;
        var rules = loaded is { Success: true, Content.Rules: not null }
            ? RuleValidator.Validate(loaded.Content.Rules).ValidRules : new List<KnownIssueRule>();
        var diskTrendRules = rules.Where(rule => rule.Enabled &&
            string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.PrtgRuleCode, PrtgRuleEvaluator.RuleDiskFreeTrend, StringComparison.Ordinal) &&
            string.Equals(rule.PrtgSensorCategory, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgDiskTrendThresholds is not null).ToArray();
        var selected = new Dictionary<PrtgResourceFamily, PrtgResourceCurrentRule?>
        {
            [PrtgResourceFamily.Cpu] = Select(rules, PrtgRuleEvaluator.RuleResourceCpuPressure,
                PrtgSensorCategories.Cpu),
            [PrtgResourceFamily.Memory] = Select(rules, PrtgRuleEvaluator.RuleResourceMemoryPressure,
                PrtgSensorCategories.Memory),
            [PrtgResourceFamily.Disk] = Select(rules, PrtgRuleEvaluator.RuleResourceDiskPressure,
                PrtgSensorCategories.Disk)
        };
        var diskTrendFingerprint = diskTrendRules.Length == 1
            ? ComputeRuleFingerprint(diskTrendRules[0])
            : diskTrendRules.Length == 0 ? "disabled" : "ambiguous:" + Hash(JsonSerializer.Serialize(
                diskTrendRules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray()));
        return new(selected, diskTrendRules.Length == 1, diskTrendFingerprint)
        { DiskTrendRuleId = diskTrendRules.Length == 1 ? diskTrendRules[0].Id : null };
    }

    private static PrtgResourceCurrentRule? Select(IReadOnlyList<KnownIssueRule> rules,
        string code, string category)
    {
        var matching = rules.Where(rule => rule.Enabled &&
            string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.PrtgRuleCode, code, StringComparison.Ordinal) &&
            string.Equals(rule.PrtgSensorCategory, category, StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgDiskTrendThresholds is null).ToArray();
        if (matching.Length != 1) return null;
        return new(matching[0], ComputeRuleFingerprint(matching[0]));
    }

    public static string ComputeRuleFingerprint(KnownIssueRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rule)));
    }

    private static string Hash(string material) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
}

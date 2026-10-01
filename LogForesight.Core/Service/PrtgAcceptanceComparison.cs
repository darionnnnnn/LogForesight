namespace LogForesight.Core.Service;

/// <summary>人工查證的獨立事故基準。空時間代表未知，不把未知當未發現。</summary>
public sealed class PrtgAcceptanceIncident
{
    public string IncidentId { get; set; } = "";
    public long HostId { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public string Outcome { get; set; } = "occurred";
    public DateTimeOffset? PredictedImpactAt { get; set; }
    public string ActionDetails { get; set; } = "";
    public string BeforeMeasurement { get; set; } = "";
    public string AfterMeasurement { get; set; } = "";
    public bool? ConfirmedPositive { get; set; }
    public DateTimeOffset? NetiqActionableAt { get; set; }
    public DateTimeOffset? NativePrtgActionableAt { get; set; }
    public DateTimeOffset? SimpleUnionActionableAt { get; set; }
    public DateTimeOffset? CombinedActionableAt { get; set; }
    public DateTimeOffset? CombinedEvidenceAvailableAt { get; set; }
    public DateTimeOffset? DispositionAt { get; set; }
    public double? NetiqVerificationMinutes { get; set; }
    public double? NativePrtgVerificationMinutes { get; set; }
    public double? SimpleUnionVerificationMinutes { get; set; }
    public double? CombinedVerificationMinutes { get; set; }
    public string EvidenceReference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Segment { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public DateTimeOffset ReviewedAt { get; set; }
}

public static class PrtgAcceptanceComparison
{
    public static object TimingSummary(IReadOnlyList<PrtgAcceptanceIncident> incidents)
    {
        var qualified = incidents.Where(i => i.ConfirmedPositive == true && i.Outcome == "occurred" &&
            i.OccurredAt.HasValue && i.CombinedActionableAt.HasValue && i.CombinedEvidenceAvailableAt.HasValue &&
            i.CombinedEvidenceAvailableAt <= i.CombinedActionableAt && i.EvidenceReference.Length > 0).ToArray();
        PrtgTimingDistribution Leads(Func<PrtgAcceptanceIncident, DateTimeOffset?> baseline) => Distribution(qualified
            .Where(i => baseline(i).HasValue).Select(i => (baseline(i)!.Value - i.CombinedActionableAt!.Value).TotalMinutes));
        PrtgTimingDistribution Costs(Func<PrtgAcceptanceIncident, double?> baseline) => Distribution(incidents
            .Where(i => i.ConfirmedPositive.HasValue && baseline(i).HasValue && i.CombinedVerificationMinutes.HasValue)
            .Select(i => baseline(i)!.Value - i.CombinedVerificationMinutes!.Value));
        return new { ObservedIncidentLead = Leads(i => i.OccurredAt), NetiqLead = Leads(i => i.NetiqActionableAt),
            NativePrtgLead = Leads(i => i.NativePrtgActionableAt), SimpleUnionLead = Leads(i => i.SimpleUnionActionableAt),
            NetiqSavedVerification = Costs(i => i.NetiqVerificationMinutes),
            NativePrtgSavedVerification = Costs(i => i.NativePrtgVerificationMinutes),
            SimpleUnionSavedVerification = Costs(i => i.SimpleUnionVerificationMinutes) };
    }
    public static PrtgTimingDistribution Distribution(IEnumerable<double> minutes)
    {
        var sorted = minutes.Where(double.IsFinite).Order().ToArray();
        return sorted.Length == 0 ? new(0, null, null) : new(sorted.Length,
            sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2,
            sorted[0]);
    }
    public static object[] Timings(IReadOnlyList<PrtgAcceptanceIncident> incidents) => incidents.Select(i => {
        var qualified = i.ConfirmedPositive == true && i.CombinedActionableAt.HasValue &&
            i.CombinedEvidenceAvailableAt.HasValue && i.CombinedEvidenceAvailableAt <= i.CombinedActionableAt && i.EvidenceReference.Length > 0;
        double? Lead(DateTimeOffset? baseline) => qualified && baseline.HasValue
            ? (baseline.Value - i.CombinedActionableAt!.Value).TotalMinutes : null;
        double? Saved(double? baseline) => baseline.HasValue && i.CombinedVerificationMinutes.HasValue
            ? baseline - i.CombinedVerificationMinutes : null;
        return (object)new { i.IncidentId, i.HostId, i.Segment, EvidenceQualified = qualified,
            ObservedOutcome = i.Outcome, IncidentLeadMinutes = i.Outcome == "occurred" ? Lead(i.OccurredAt) : null,
            PredictedLeadMinutes = Lead(i.PredictedImpactAt), NetiqLeadMinutes = Lead(i.NetiqActionableAt),
            NativePrtgLeadMinutes = Lead(i.NativePrtgActionableAt), SimpleUnionLeadMinutes = Lead(i.SimpleUnionActionableAt),
            i.NetiqVerificationMinutes, i.NativePrtgVerificationMinutes, i.SimpleUnionVerificationMinutes, i.CombinedVerificationMinutes,
            NetiqSavedVerificationMinutes = Saved(i.NetiqVerificationMinutes),
            NativePrtgSavedVerificationMinutes = Saved(i.NativePrtgVerificationMinutes),
            SimpleUnionSavedVerificationMinutes = Saved(i.SimpleUnionVerificationMinutes) };
    }).ToArray();
    public static PrtgAcceptanceComparisonResult Evaluate(IReadOnlyList<PrtgAcceptanceIncident> incidents)
    {
        var positives = incidents.Where(i => i.ConfirmedPositive == true).ToArray();
        var valid = positives.Where(i => i.EvidenceReference.Length > 0 && i.CombinedActionableAt.HasValue &&
            i.CombinedEvidenceAvailableAt.HasValue && i.CombinedEvidenceAvailableAt <= i.CombinedActionableAt).ToArray();
        var compared = valid.Where(i => i.Outcome == "occurred" && i.OccurredAt.HasValue && i.NetiqActionableAt.HasValue && i.NativePrtgActionableAt.HasValue &&
            i.SimpleUnionActionableAt.HasValue).ToArray();
        var early = valid.Count(i => i.Outcome == "occurred" && i.OccurredAt.HasValue && i.CombinedActionableAt < i.OccurredAt);
        var incremental = compared.Count(i => i.CombinedActionableAt < i.NetiqActionableAt &&
            i.CombinedActionableAt < i.NativePrtgActionableAt && i.CombinedActionableAt < i.SimpleUnionActionableAt);
        return new(incidents.Count, incidents.Count(i => i.ConfirmedPositive == null), positives.Length,
            incidents.Count(i => i.ConfirmedPositive == false), valid.Length, compared.Length, early, incremental,
            positives.Length - valid.Length,
            incidents.Count(i => i.Outcome == "prevented"), incidents.Count(i => i.Outcome == "unknown"),
            "人工事故標籤與三種基準缺一即不證明合併增益；同日弱佐證與事後回填不能計為提前預警。未設定現場驗收門檻，不自動宣稱實用程度通過。");
    }
}
public sealed record PrtgAcceptanceComparisonResult(int Incidents, int Unreviewed, int ConfirmedPositive,
    int ConfirmedFalsePositive, int EvidenceQualified, int FullyCompared, int BeforeIncident,
    int IncrementalBeforeAllBaselines, int ExcludedMissingOrFutureEvidence, int Prevented, int OutcomeUnknown, string Limitations);

public sealed record PrtgTimingDistribution(int Samples, double? MedianMinutes, double? WorstMinutes);

using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;
public sealed class PrtgAcceptanceComparisonTests
{
    [Fact]
    public void 缺基準不能把原生Prtg早已發現算為合併增益()
    {
        var at = DateTimeOffset.UtcNow;
        var label = new PrtgAcceptanceIncident { IncidentId = "a", ConfirmedPositive = true, OccurredAt = at,
            CombinedActionableAt = at.AddHours(-1), CombinedEvidenceAvailableAt = at.AddHours(-2), EvidenceReference = "incident",
            NativePrtgActionableAt = at.AddHours(-3), NetiqActionableAt = at.AddHours(1) };
        var result = PrtgAcceptanceComparison.Evaluate([label]);
        Assert.Equal(1, result.BeforeIncident); Assert.Equal(0, result.FullyCompared);
        Assert.Equal(0, result.IncrementalBeforeAllBaselines);
        label.SimpleUnionActionableAt = at.AddHours(-3);
        Assert.Equal(0, PrtgAcceptanceComparison.Evaluate([label]).IncrementalBeforeAllBaselines);
    }
    [Fact]
    public void 事後資料與未查證不得計入提前預警分子()
    {
        var at = DateTimeOffset.UtcNow;
        var label = new PrtgAcceptanceIncident { ConfirmedPositive = true, OccurredAt = at,
            CombinedActionableAt = at.AddHours(-1), CombinedEvidenceAvailableAt = at.AddHours(1), EvidenceReference = "late" };
        var result = PrtgAcceptanceComparison.Evaluate([label, new()]);
        Assert.Equal(1, result.Unreviewed); Assert.Equal(1, result.ExcludedMissingOrFutureEvidence);
        Assert.Equal(0, result.BeforeIncident);
    }
    [Fact]
    public void 完整事故查證且比三種基準早才列增益()
    {
        var at = DateTimeOffset.UtcNow;
        var label = new PrtgAcceptanceIncident { ConfirmedPositive = true, OccurredAt = at,
            CombinedActionableAt = at.AddHours(-2), CombinedEvidenceAvailableAt = at.AddHours(-3), EvidenceReference = "incident",
            NetiqActionableAt = at, NativePrtgActionableAt = at.AddHours(-1), SimpleUnionActionableAt = at.AddHours(-1) };
        Assert.Equal(1, PrtgAcceptanceComparison.Evaluate([label]).IncrementalBeforeAllBaselines);
    }
    [Fact]
    public void 提前分鐘保留負值_未知不當零_查證耗時分開計算()
    {
        var at = DateTimeOffset.UtcNow;
        var label = new PrtgAcceptanceIncident { ConfirmedPositive = true, OccurredAt = at,
            CombinedActionableAt = at.AddMinutes(10), CombinedEvidenceAvailableAt = at, EvidenceReference = "verified",
            NativePrtgActionableAt = at.AddMinutes(-10), NetiqVerificationMinutes = 30, CombinedVerificationMinutes = 10 };
        var json = System.Text.Json.JsonSerializer.SerializeToElement(PrtgAcceptanceComparison.Timings([label])[0]);
        Assert.Equal(-10, json.GetProperty("IncidentLeadMinutes").GetDouble());
        Assert.Equal(-20, json.GetProperty("NativePrtgLeadMinutes").GetDouble());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("NetiqLeadMinutes").ValueKind);
        Assert.Equal(20, json.GetProperty("NetiqSavedVerificationMinutes").GetDouble());
        label.CombinedEvidenceAvailableAt = at.AddHours(1);
        json = System.Text.Json.JsonSerializer.SerializeToElement(PrtgAcceptanceComparison.Timings([label])[0]);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("IncidentLeadMinutes").ValueKind);
    }
    [Fact]
    public void 預防案例不虛構實際提前量_樣本中位數最差值與未知分開()
    {
        var at = DateTimeOffset.UtcNow;
        var label = new PrtgAcceptanceIncident { Outcome = "prevented", ConfirmedPositive = true,
            CombinedActionableAt = at.AddHours(-2), CombinedEvidenceAvailableAt = at.AddHours(-3), EvidenceReference = "intervention",
            PredictedImpactAt = at.AddHours(1), NetiqActionableAt = at, NativePrtgActionableAt = at, SimpleUnionActionableAt = at };
        var comparison = PrtgAcceptanceComparison.Evaluate([label]);
        Assert.Equal(1, comparison.Prevented); Assert.Equal(0, comparison.BeforeIncident); Assert.Equal(0, comparison.IncrementalBeforeAllBaselines);
        var row = System.Text.Json.JsonSerializer.SerializeToElement(PrtgAcceptanceComparison.Timings([label])[0]);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("IncidentLeadMinutes").ValueKind);
        Assert.Equal(180, row.GetProperty("PredictedLeadMinutes").GetDouble());
        Assert.Equal(new PrtgTimingDistribution(3, 10, -20), PrtgAcceptanceComparison.Distribution([30, -20, 10]));
        Assert.Equal(new PrtgTimingDistribution(2, 5, 0), PrtgAcceptanceComparison.Distribution([0, 10]));
        Assert.Equal(new PrtgTimingDistribution(0, null, null), PrtgAcceptanceComparison.Distribution([]));
    }
}

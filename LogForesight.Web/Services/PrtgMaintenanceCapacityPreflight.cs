using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

public sealed record PrtgMaintenanceCapacityDecision(bool Admitted, string Reason,
    int TableRequestCount, double MinimumPacingSeconds, double AvailablePacingSeconds,
    string? AdmissionPlanFingerprint);

/// <summary>
/// Rejects maintenance calls only when the current purpose lane's mandatory pacing alone cannot
/// fit inside the caller deadline with its safety margin. HTTP latency and shared-window waits are
/// intentionally left to the caller's existing hard deadline and cancellation behavior.
/// </summary>
public static class PrtgMaintenanceCapacityPreflight
{
    public const int CallerDeadlineSeconds = 30;
    public const int SafetyMarginSeconds = 5;

    public static int ExpectedOrdinaryProbeTableRequests(int sensorCount)
    {
        if (sensorCount is < 1 or > PrtgTrustedSamplingProbeService.MaxSensorIds)
            throw new ArgumentOutOfRangeException(nameof(sensorCount));
        return checked(2 + 2 * sensorCount);
    }

    public static PrtgMaintenanceCapacityDecision Evaluate(PrtgRequestBudget budget,
        PrtgCapacityAdmissionPlan? currentPlan, PrtgRequestPurpose purpose, int tableRequestCount,
        TimeSpan? deadline = null, TimeSpan? safetyMargin = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var boundedDeadline = deadline ?? TimeSpan.FromSeconds(CallerDeadlineSeconds);
        var margin = safetyMargin ?? TimeSpan.FromSeconds(SafetyMarginSeconds);
        var available = boundedDeadline - margin;
        if (tableRequestCount < 1 || boundedDeadline <= TimeSpan.Zero || margin < TimeSpan.Zero ||
            available <= TimeSpan.Zero)
            return new(false, "maintenance-capacity-input-invalid", tableRequestCount, 0,
                Math.Max(0, available.TotalSeconds), currentPlan?.Fingerprint);
        if (currentPlan is null || currentPlan.LeaseUntilUtc <= budget.Clock.UtcNow)
            return new(false, "maintenance-capacity-plan-missing-or-expired", tableRequestCount, 0,
                available.TotalSeconds, null);
        if (!budget.TryGetMinimumTablePacingWait(purpose, tableRequestCount,
                currentPlan.Fingerprint, out var minimumWait))
            return new(false, "maintenance-capacity-plan-not-current", tableRequestCount, 0,
                available.TotalSeconds, currentPlan.Fingerprint);
        if (minimumWait >= currentPlan.LeaseUntilUtc - budget.Clock.UtcNow)
            return new(false, "maintenance-capacity-plan-expires-before-minimum-send", tableRequestCount,
                minimumWait.TotalSeconds, available.TotalSeconds, currentPlan.Fingerprint);

        var admitted = minimumWait < available;
        return new(admitted,
            admitted ? "minimum-table-pacing-fits-deadline" : "minimum-table-pacing-exceeds-deadline-margin",
            tableRequestCount, minimumWait.TotalSeconds, available.TotalSeconds, currentPlan.Fingerprint);
    }
}

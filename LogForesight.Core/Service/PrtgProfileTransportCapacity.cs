using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

public sealed record PrtgProfileTransportSample(
    string SourceFingerprint,
    string ScopeFingerprint,
    string StrategyFingerprint,
    string RequestShapeFingerprint,
    DateTimeOffset CompletedAtUtc,
    long ElapsedMilliseconds,
    int SensorCount,
    int RequestsAttempted,
    int RequestsSent,
    string Outcome,
    string? FailureCode,
    string VersionFingerprint);

public sealed record PrtgProfileTransportEstimate(
    PrtgSnapshotCapacityStatus Status,
    int TargetSensorCount,
    int FreshSuccessfulSamples,
    DateTimeOffset? OldestSampleAtUtc,
    double? P95SensorSeconds,
    double? EstimatedSeconds,
    double CompletionWindowSeconds,
    double RequiredHeadroomFraction,
    string Reason);

/// <summary>Bounded, opaque evidence for the two table reads used by profile refresh.</summary>
public sealed class PrtgProfileTransportCapacityStore(EfJsonBlobStore blob)
{
    public const string BlobKey = "prtg_profile_transport_capacity_v1";
    public const int MaximumSamples = 128;
    public const int MaximumSerializedBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<PrtgProfileTransportSample> Read()
    {
        var (content, _, length) = blob.ReadBoundedWithVersion(MaximumSerializedBytes);
        if (length > MaximumSerializedBytes || content is not null && Encoding.UTF8.GetByteCount(content) > MaximumSerializedBytes)
            return Array.Empty<PrtgProfileTransportSample>();
        try { return string.IsNullOrWhiteSpace(content) ? [] : JsonSerializer.Deserialize<List<PrtgProfileTransportSample>>(content, JsonOptions)?.TakeLast(MaximumSamples).ToArray() ?? []; }
        catch (JsonException) { return []; }
    }

    public void Record(PrtgProfileTransportSample sample)
    {
        if (!Hash(sample.SourceFingerprint) || !Hash(sample.ScopeFingerprint) || !Hash(sample.StrategyFingerprint) ||
            !Hash(sample.RequestShapeFingerprint) || !Hash(sample.VersionFingerprint) || sample.SensorCount is < 1 or > 5 ||
            sample.RequestsAttempted is < 0 or > 20 || sample.RequestsSent is < 0 or > 20 ||
            sample.ElapsedMilliseconds < 0 || sample.Outcome is not ("success" or "failed" or "timeout"))
            throw new ArgumentException("Invalid bounded profile transport sample.", nameof(sample));
        if (sample.Outcome == "success" &&
            (sample.RequestsAttempted != sample.SensorCount * 4 || sample.RequestsSent != sample.SensorCount * 4))
            throw new ArgumentException("A successful profile transport sample must contain every attempted and sent GET.", nameof(sample));

        blob.MutateWithContext((_, current) =>
        {
            var rows = Parse(current);
            var sameContract = rows.Where(x => MatchesContract(x, sample)).ToArray();
            if (sample.Outcome != "success") rows.RemoveAll(x => MatchesContract(x, sample));
            var hour = sameContract.Where(x => x.Outcome == "success" && x.CompletedAtUtc.UtcDateTime.Date == sample.CompletedAtUtc.UtcDateTime.Date &&
                x.CompletedAtUtc.UtcDateTime.Hour == sample.CompletedAtUtc.UtcDateTime.Hour).ToArray();
            if (sample.Outcome == "success" && hour.Length >= PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
            {
                var keep = hour.Append(sample).OrderByDescending(x => x.ElapsedMilliseconds)
                    .ThenByDescending(x => x.CompletedAtUtc).Take(PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples).ToHashSet();
                rows.RemoveAll(x => MatchesContract(x, sample) && x.Outcome == "success" &&
                    x.CompletedAtUtc.UtcDateTime.Date == sample.CompletedAtUtc.UtcDateTime.Date &&
                    x.CompletedAtUtc.UtcDateTime.Hour == sample.CompletedAtUtc.UtcDateTime.Hour);
                rows.AddRange(keep);
            }
            else rows.Add(sample);
            if (rows.Count > MaximumSamples) rows.RemoveRange(0, rows.Count - MaximumSamples);
            var json = JsonSerializer.Serialize(rows, JsonOptions);
            while (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes && rows.Count > 1)
            { rows.RemoveAt(0); json = JsonSerializer.Serialize(rows, JsonOptions); }
            if (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes) throw new InvalidDataException("profile-transport-capacity-evidence-cap-exceeded");
            return (json, true);
        }, MaximumSerializedBytes);
    }

    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Hash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool MatchesContract(PrtgProfileTransportSample x, PrtgProfileTransportSample y) =>
        x.SourceFingerprint == y.SourceFingerprint && x.ScopeFingerprint == y.ScopeFingerprint &&
        x.StrategyFingerprint == y.StrategyFingerprint && x.RequestShapeFingerprint == y.RequestShapeFingerprint &&
        x.VersionFingerprint == y.VersionFingerprint && x.SensorCount == y.SensorCount;
    private static List<PrtgProfileTransportSample> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        if (Encoding.UTF8.GetByteCount(content) > MaximumSerializedBytes) throw new InvalidDataException("profile-transport-capacity-evidence-cap-exceeded");
        return JsonSerializer.Deserialize<List<PrtgProfileTransportSample>>(content, JsonOptions)?.TakeLast(MaximumSamples).ToList() ?? [];
    }
}

/// <summary>One current capacity plan lease; release is compare-and-swap and never restores an old plan.</summary>
public sealed class PrtgCapacityReservationStore(EfJsonBlobStore blob)
{
    public const string BlobKey = "prtg_capacity_reservation_v1";
    private const int MaximumBytes = 4096;
    private sealed class State
    {
        public string PlanFingerprint { get; set; } = "";
        public string Owner { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset? LeaseUntilUtc { get; set; }
    }

    public bool TryAcquire(string planFingerprint, string owner, DateTimeOffset nowUtc,
        TimeSpan duration, out long version)
    {
        if (planFingerprint.Length != 64 || owner.Length is < 1 or > 128 || duration <= TimeSpan.Zero)
            throw new ArgumentException("Invalid profile capacity reservation.");
        var acquiredVersion = 0L;
        var acquired = blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            if (state.PlanFingerprint == planFingerprint && state.LeaseUntilUtc > nowUtc)
                return (JsonSerializer.Serialize(state), false);
            state.PlanFingerprint = planFingerprint;
            state.Owner = owner;
            state.Version++;
            state.LeaseUntilUtc = nowUtc + duration;
            acquiredVersion = state.Version;
            return (Serialize(state), true);
        }, MaximumBytes);
        version = acquiredVersion;
        return acquired;
    }

    public bool Renew(string planFingerprint, string owner, long version, DateTimeOffset untilUtc)
    {
        var renewed = false;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            if (state.PlanFingerprint != planFingerprint || state.Owner != owner || state.Version != version ||
                state.LeaseUntilUtc <= DateTimeOffset.UtcNow)
                return (JsonSerializer.Serialize(state), false);
            state.LeaseUntilUtc = untilUtc;
            renewed = true;
            return (Serialize(state), true);
        }, MaximumBytes);
        return renewed;
    }

    public void Release(string planFingerprint, string owner, long version)
    {
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            if (state.PlanFingerprint == planFingerprint && state.Owner == owner && state.Version == version)
            { state.Owner = ""; state.LeaseUntilUtc = null; }
            return (Serialize(state), true);
        }, MaximumBytes);
    }

    private static State Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        if (Encoding.UTF8.GetByteCount(raw) > MaximumBytes) throw new InvalidDataException("profile-capacity-reservation-cap-exceeded");
        return JsonSerializer.Deserialize<State>(raw) ?? new();
    }
    private static string Serialize(State value)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("profile-capacity-reservation-cap-exceeded");
        return json;
    }
}

/// <summary>Conservative completion estimate: four sequential Table-budget requests per sensor.</summary>
public static class PrtgProfileTransportCapacityEvaluator
{
    public const double RequiredHeadroomFraction = .25;
    public static readonly TimeSpan EvidenceFreshness = TimeSpan.FromHours(24);
    public static readonly TimeSpan ProfileRefreshWindow = TimeSpan.FromHours(23 * .75);

    public static PrtgProfileTransportEstimate Evaluate(int targetSensorCount, string sourceFingerprint,
        string scopeFingerprint, string strategyFingerprint, string requestShapeFingerprint,
        string versionFingerprint, IEnumerable<PrtgProfileTransportSample> evidence, DateTimeOffset nowUtc,
        double tableRequestsAvailableAfterSharedTraffic = PrtgSnapshotCapacityEvaluator.MaximumTableRequestsPerSecond,
        int requiredFreshSuccessfulSamples = PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
    {
        if (targetSensorCount < 0) throw new ArgumentOutOfRangeException(nameof(targetSensorCount));
        var matching = evidence.Where(s => s.SourceFingerprint == sourceFingerprint && s.ScopeFingerprint == scopeFingerprint &&
            s.StrategyFingerprint == strategyFingerprint && s.RequestShapeFingerprint == requestShapeFingerprint &&
            s.VersionFingerprint == versionFingerprint && s.SensorCount == Math.Min(targetSensorCount, 5) &&
            s.CompletedAtUtc <= nowUtc && nowUtc - s.CompletedAtUtc <= EvidenceFreshness)
            .OrderBy(s => s.CompletedAtUtc).ToArray();
        var lastInvalidIndex = Array.FindLastIndex(matching, x => x.Outcome != "success" ||
            x.RequestsAttempted != x.SensorCount * 4 || x.RequestsSent != x.SensorCount * 4);
        var latest = matching.LastOrDefault();
        var successful = matching.Skip(lastInvalidIndex + 1).Where(x => x.Outcome == "success" &&
            x.RequestsAttempted == x.SensorCount * 4 && x.RequestsSent == x.SensorCount * 4).ToArray();
        var requiredSamples = lastInvalidIndex >= 0
            ? PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples : requiredFreshSuccessfulSamples;
        var oldest = successful.FirstOrDefault()?.CompletedAtUtc;
        PrtgProfileTransportEstimate Result(PrtgSnapshotCapacityStatus status, double? p95, double? seconds, string reason) =>
            new(status, targetSensorCount, successful.Length, oldest, p95, seconds,
                ProfileRefreshWindow.TotalSeconds, RequiredHeadroomFraction, reason);
        if (targetSensorCount == 0) return Result(PrtgSnapshotCapacityStatus.CapacityQualified, 0, 0, "empty_scope");
        if (requiredFreshSuccessfulSamples is < 1 or > PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
            throw new ArgumentOutOfRangeException(nameof(requiredFreshSuccessfulSamples));
        if (latest is not null && lastInvalidIndex == matching.Length - 1)
            return Result(PrtgSnapshotCapacityStatus.CapacityUnverified, null, null, "latest_profile_transport_sample_failed_or_timed_out");
        if (successful.Length < requiredSamples)
            return Result(PrtgSnapshotCapacityStatus.CapacityUnverified, null, null, lastInvalidIndex >= 0
                ? "recent_profile_transport_failure_or_timeout" : "insufficient_fresh_matching_profile_samples");
        if (tableRequestsAvailableAfterSharedTraffic <= 0)
            return Result(PrtgSnapshotCapacityStatus.CapacityUnverified, null, null, "shared_table_budget_unavailable");
        var perSensorSeconds = successful.Select(x => x.ElapsedMilliseconds / 1000d / x.SensorCount).Order().ToArray();
        var p95 = perSensorSeconds[Math.Clamp((int)Math.Ceiling(perSensorSeconds.Length * .95) - 1, 0, perSensorSeconds.Length - 1)];
        // The probe performs four sequential Table-budget GETs per sensor. The shared rate model includes current
        // work before assigning the remaining Table2/s tokens to this serialized refresh lane.
        var requestRateFloor = Math.Ceiling(targetSensorCount * 4d / tableRequestsAvailableAfterSharedTraffic);
        var sequentialFloor = Math.Ceiling(targetSensorCount * p95);
        var estimate = Math.Max(requestRateFloor, sequentialFloor);
        if (!double.IsFinite(estimate) || estimate > ProfileRefreshWindow.TotalSeconds)
            return Result(PrtgSnapshotCapacityStatus.CapacityExceeded, p95, estimate, "profile_refresh_window_or_headroom_exceeded");
        return Result(PrtgSnapshotCapacityStatus.CapacityQualified, p95, estimate, "within_profile_refresh_window_with_headroom");
    }
}

public sealed record PrtgJointCapacityEstimate(
    PrtgSnapshotCapacityStatus Status,
    double SharedTableRequestsPerSecond,
    double SnapshotTableRequestsPerSecond,
    double ProfileTableRequestsPerSecond,
    double GeneralResidualRequestsPerSecond,
    double SnapshotEstimatedSeconds,
    double ProfileEstimatedSeconds,
    double ReclaimDelaySeconds,
    double SnapshotWindowSeconds,
    double ProfileWindowSeconds,
    string Reason);

/// <summary>
/// A temporary admission result for one small recovery request. It is not a full-scope capacity
/// qualification and must never be persisted as a replacement admission plan.
/// </summary>
public sealed record PrtgBoundedSnapshotRecoveryEstimate(
    bool Admitted,
    int TargetCount,
    double TimeoutBoundSeconds,
    double SnapshotEstimatedSeconds,
    double ProfileEstimatedSeconds,
    string Reason);

/// <summary>Conservative shared Table2/s admission for snapshot and serialized profile work.</summary>
public static class PrtgJointCapacityEvaluator
{
    public const double SharedTableRequestsPerSecond = 2;
    public const double RequiredHeadroomFraction = .25;
    public const double ProfileRefreshSliceSeconds = 600;
    public const double ProfileRefreshInterSliceDelaySeconds = 60;

    /// <summary>
    /// The hosted refresh loop sleeps for one minute between ten-minute slices. Reserve the
    /// worst number of those pauses that can occur inside the full-scope completion window.
    /// </summary>
    public static double ProfileRefreshInterSliceDelayBudget(double completionWindowSeconds)
    {
        if (!double.IsFinite(completionWindowSeconds) || completionWindowSeconds <= 0) return double.PositiveInfinity;
        var slices = Math.Ceiling(completionWindowSeconds / ProfileRefreshSliceSeconds);
        return Math.Max(0, slices - 1) * ProfileRefreshInterSliceDelaySeconds;
    }

    public static PrtgJointCapacityEstimate Evaluate(PrtgSnapshotCapacityEstimate snapshot,
        PrtgProfileTransportEstimate profile, PrtgRequestBudgetUsage usage, int httpTimeoutSeconds)
        => EvaluateCore(snapshot, profile, usage, httpTimeoutSeconds, null, null);

    /// <summary>
    /// Re-evaluates fresh workload evidence against an already published lane allocation. Runtime
    /// samples may refresh latency evidence without silently changing the immutable plan rates.
    /// </summary>
    public static PrtgJointCapacityEstimate EvaluateAgainstReservedRates(PrtgSnapshotCapacityEstimate snapshot,
        PrtgProfileTransportEstimate profile, PrtgRequestBudgetUsage usage, int httpTimeoutSeconds,
        double snapshotTableRequestsPerSecond, double profileTableRequestsPerSecond)
        => EvaluateCore(snapshot, profile, usage, httpTimeoutSeconds,
            snapshotTableRequestsPerSecond, profileTableRequestsPerSecond);

    /// <summary>
    /// Bounds a single <=50-ID snapshot recovery request by the configured HTTP timeout while
    /// reusing the existing plan's reserved rates and the currently qualified profile evidence.
    /// This temporary envelope does not create or replace full-scope five-sample evidence.
    /// </summary>
    public static PrtgBoundedSnapshotRecoveryEstimate EvaluateBoundedSingleBatchRecovery(
        int targetCount, string strategy, PrtgProfileTransportEstimate profile,
        PrtgRequestBudgetUsage usage, int httpTimeoutSeconds,
        double snapshotTableRequestsPerSecond, double profileTableRequestsPerSecond)
    {
        PrtgBoundedSnapshotRecoveryEstimate Denied(string reason) =>
            new(false, targetCount, Math.Max(0, httpTimeoutSeconds), double.PositiveInfinity,
                profile.EstimatedSeconds ?? double.PositiveInfinity, reason);

        if (targetCount is < 1 or > PrtgSnapshotCapacityEvaluator.BatchSize)
            return Denied("recovery_scope_not_one_bounded_batch");
        if (httpTimeoutSeconds < 1)
            return Denied("recovery_http_timeout_invalid");
        if (profile.Status != PrtgSnapshotCapacityStatus.CapacityQualified)
            return Denied("recovery_profile_evidence_not_qualified");
        if (!double.IsFinite(snapshotTableRequestsPerSecond) || snapshotTableRequestsPerSecond <= 0 ||
            !double.IsFinite(profileTableRequestsPerSecond) || profileTableRequestsPerSecond <= 0 ||
            snapshotTableRequestsPerSecond + profileTableRequestsPerSecond >=
                SharedTableRequestsPerSecond * (1 - RequiredHeadroomFraction))
            return Denied("recovery_reserved_rates_do_not_preserve_headroom");

        var windowSeconds = string.Equals(strategy, "aggressive", StringComparison.OrdinalIgnoreCase)
            ? PrtgSnapshotCapacityEvaluator.AggressiveWindow.TotalSeconds
            : PrtgSnapshotCapacityEvaluator.ConservativeWindow.TotalSeconds;
        var timeoutBound = (double)httpTimeoutSeconds;
        var singleBatch = new PrtgSnapshotCapacityEstimate(
            PrtgSnapshotCapacityStatus.CapacityQualified, targetCount, 1, 0, null,
            timeoutBound, timeoutBound, windowSeconds, RequiredHeadroomFraction,
            "single_batch_http_timeout_envelope_not_full_capacity_evidence");
        var joint = EvaluateAgainstReservedRates(singleBatch, profile, usage, httpTimeoutSeconds,
            snapshotTableRequestsPerSecond, profileTableRequestsPerSecond);
        return new PrtgBoundedSnapshotRecoveryEstimate(
            joint.Status == PrtgSnapshotCapacityStatus.CapacityQualified,
            targetCount, timeoutBound, joint.SnapshotEstimatedSeconds, joint.ProfileEstimatedSeconds,
            joint.Status == PrtgSnapshotCapacityStatus.CapacityQualified
                ? "bounded_single_batch_recovery_timeout_bound_fit"
                : "bounded_recovery_" + joint.Reason);
    }

    private static PrtgJointCapacityEstimate EvaluateCore(PrtgSnapshotCapacityEstimate snapshot,
        PrtgProfileTransportEstimate profile, PrtgRequestBudgetUsage usage, int httpTimeoutSeconds,
        double? reservedSnapshotRate, double? reservedProfileRate)
    {
        var snapshotWindow = snapshot.CompletionWindowSeconds * (1 - RequiredHeadroomFraction);
        var profileWindow = profile.CompletionWindowSeconds;
        // Allocate rates from a deterministic reclaim bound, not instantaneous pool usage. This
        // keeps the published plan fingerprint stable across idle/busy runtime checks. The bound
        // covers the longest possible rolling window, a full HTTP timeout for an in-flight slot,
        // and one second for lane/token collision. Current usage is still checked against it below.
        var reclaim = 60 + Math.Max(1, httpTimeoutSeconds) + 1;
        var tableWait = usage.UntilNextTableToken?.TotalSeconds ?? 0;
        var historicWait = usage.HistoricRequestsInLastMinute >= 5
            ? usage.UntilNextHistoricToken?.TotalSeconds ?? 60
            : 0;
        var inFlightWait = usage.InFlight > 0 ? Math.Max(1, httpTimeoutSeconds) : 0;
        var observedReclaim = Math.Max(tableWait, Math.Max(historicWait, inFlightWait));
        var snapshotRateWindow = snapshotWindow - reclaim - 1;
        var profileRequests = profile.TargetSensorCount * 4d;
        var profileP95 = profile.P95SensorSeconds ?? double.PositiveInfinity;
        var profileSensorWorkSeconds = profile.TargetSensorCount * profileP95;
        var profileInterSliceBudget = ProfileRefreshInterSliceDelayBudget(profileWindow);
        // The durable worker stops after ten minutes and ExecuteAsync waits sixty seconds before
        // opening the next slice. Reserve that scheduler time and the measured serial sensor
        // response work before allocating pacing tokens, so the published lane can finish inside
        // its actual 17.25-hour deadline rather than treating the whole window as transport time.
        var profileRateWindow = profileWindow - reclaim - profileSensorWorkSeconds - profileInterSliceBudget - 1;
        var measuredChunkSize = Math.Min(profile.TargetSensorCount, 5);
        var measuredChunkSeconds = measuredChunkSize * (profile.P95SensorSeconds ?? double.PositiveInfinity);
        // ProfileProbeService has one 30s deadline per group of up to five sensors. A rate
        // allocated only from the 23h full-scope window would strand tiny scopes for hours
        // between GETs, so reserve enough throughput for one measured group to finish inside
        // that request deadline as well as the full-scope completion window.
        var groupDeadlineSeconds = TimeSpan.FromSeconds(30).TotalSeconds;
        var groupRequestGaps = Math.Max(0, measuredChunkSize * 4d - 1);
        const double profileGroupDeadlineMarginSeconds = 5;
        // Keep the floor independent of observed elapsed-time samples so renewing a plan does not
        // silently change its lane fingerprint. Slow measurements still fail the deadline check.
        const double minimumProfileRate = 1.0;
        var profileRate = reservedProfileRate ?? (profileRateWindow > 0
            ? Math.Max(profileRequests / profileRateWindow, minimumProfileRate)
            : double.PositiveInfinity);
        var profileGroupEstimatedSeconds = measuredChunkSeconds + groupRequestGaps / profileRate +
            profileGroupDeadlineMarginSeconds;
        var profileGroupDeadlineFailed = profile.Status == PrtgSnapshotCapacityStatus.CapacityQualified &&
            (!double.IsFinite(profileGroupEstimatedSeconds) || profileGroupEstimatedSeconds >= groupDeadlineSeconds);
        var snapshotRate = reservedSnapshotRate ?? (snapshotRateWindow > 0
            ? snapshot.BatchCount / snapshotRateWindow : double.PositiveInfinity);
        var combinedRate = profileRate + snapshotRate;
        var plannedCapacity = SharedTableRequestsPerSecond * (1 - RequiredHeadroomFraction);
        var remainingRate = plannedCapacity - combinedRate;
        var snapshotP95 = snapshot.P95BatchSeconds ?? double.PositiveInfinity;
        var effectiveSnapshotRate = snapshotRate;
        var effectiveProfileRate = profileRate;
        var snapshotSeconds = Math.Max(effectiveSnapshotRate > 0 && snapshotRateWindow > 0
                ? Math.Ceiling(snapshot.BatchCount / effectiveSnapshotRate) : double.PositiveInfinity,
            Math.Ceiling(snapshot.BatchCount / 3d) * snapshotP95) + reclaim;
        var profilePacedCompletion = effectiveProfileRate > 0
            ? Math.Max(0, profileRequests - 1d) / effectiveProfileRate : double.PositiveInfinity;
        // Each sensor performs four serial GETs; the pilot's per-sensor p95 covers response time,
        // while the plan rate adds the gap between each actual send. A max() would hide this
        // sequential cost and understate large-scope completion time.
        var profileTransportSeconds = profilePacedCompletion + profileSensorWorkSeconds + reclaim;
        var profileSeconds = profileTransportSeconds + ProfileRefreshInterSliceDelayBudget(profileTransportSeconds);
        var status = observedReclaim > reclaim ||
            snapshot.Status == PrtgSnapshotCapacityStatus.CapacityExceeded ||
            profile.Status == PrtgSnapshotCapacityStatus.CapacityExceeded || combinedRate >= plannedCapacity || profileGroupDeadlineFailed
            ? PrtgSnapshotCapacityStatus.CapacityExceeded
            : snapshot.Status != PrtgSnapshotCapacityStatus.CapacityQualified ||
              profile.Status != PrtgSnapshotCapacityStatus.CapacityQualified
                ? PrtgSnapshotCapacityStatus.CapacityUnverified
                : snapshotSeconds > snapshotWindow || profileSeconds > profileWindow
                    ? PrtgSnapshotCapacityStatus.CapacityExceeded
                    : PrtgSnapshotCapacityStatus.CapacityQualified;
        var reason = status switch
        {
            PrtgSnapshotCapacityStatus.CapacityExceeded when observedReclaim > reclaim => "current_shared_pool_reclaim_exceeds_plan_bound",
            PrtgSnapshotCapacityStatus.CapacityExceeded when profileGroupDeadlineFailed => "profile_probe_group_deadline_exceeded",
            PrtgSnapshotCapacityStatus.CapacityExceeded when combinedRate >= plannedCapacity => "joint_table_quota_or_traffic_exceeded",
            PrtgSnapshotCapacityStatus.CapacityExceeded => "joint_completion_window_or_reclaim_delay_exceeded",
            PrtgSnapshotCapacityStatus.CapacityUnverified => snapshot.Status != PrtgSnapshotCapacityStatus.CapacityQualified
                ? $"snapshot_{snapshot.Reason}" : $"profile_{profile.Reason}",
            _ => "joint_table_quota_with_reclaim_headroom_qualified"
        };
        return new(status, SharedTableRequestsPerSecond, snapshotRate, profileRate, Math.Max(0, remainingRate),
            snapshotSeconds, profileSeconds, reclaim,
            snapshotWindow, profileWindow, reason);
    }

    public static PrtgCapacityAdmissionPlan CreatePlan(PrtgJointCapacityEstimate estimate,
        string sourceFingerprint, string snapshotScopeFingerprint, string profileScopeFingerprint,
        string strategyFingerprint, string snapshotRequestShapeFingerprint,
        string requestShapeFingerprint, string runtimeVersionFingerprint,
        DateTimeOffset nowUtc, string settingsRevision = "", string policyRevision = "")
    {
        if (!IsHash(sourceFingerprint) || !IsHash(snapshotScopeFingerprint) || !IsHash(profileScopeFingerprint) ||
            !IsHash(strategyFingerprint) || !IsHash(snapshotRequestShapeFingerprint) ||
            !IsHash(requestShapeFingerprint) || !IsHash(runtimeVersionFingerprint))
            throw new InvalidOperationException("joint-capacity-contract-fingerprint-invalid");
        if (estimate.Status != PrtgSnapshotCapacityStatus.CapacityQualified ||
            estimate.SnapshotTableRequestsPerSecond <= 0 || estimate.ProfileTableRequestsPerSecond <= 0 ||
            estimate.GeneralResidualRequestsPerSecond <= 0 ||
            estimate.SnapshotTableRequestsPerSecond + estimate.ProfileTableRequestsPerSecond >
                SharedTableRequestsPerSecond * (1 - RequiredHeadroomFraction) + 1e-9)
            throw new InvalidOperationException("joint-capacity-plan-not-qualified");
        var fingerprint = ComputePlanFingerprint(sourceFingerprint, snapshotScopeFingerprint, profileScopeFingerprint,
            strategyFingerprint, snapshotRequestShapeFingerprint, requestShapeFingerprint, runtimeVersionFingerprint,
            estimate.SnapshotTableRequestsPerSecond, estimate.ProfileTableRequestsPerSecond,
            estimate.GeneralResidualRequestsPerSecond);
        return new PrtgCapacityAdmissionPlan(fingerprint, sourceFingerprint,
            snapshotScopeFingerprint, profileScopeFingerprint, strategyFingerprint, snapshotRequestShapeFingerprint,
            requestShapeFingerprint,
            runtimeVersionFingerprint, settingsRevision, policyRevision,
            estimate.SnapshotTableRequestsPerSecond, estimate.ProfileTableRequestsPerSecond,
            estimate.GeneralResidualRequestsPerSecond, nowUtc, nowUtc, "pending", 0);
    }

    public static bool HasCurrentPlanFingerprint(PrtgCapacityAdmissionPlan plan) =>
        plan.Fingerprint == ComputePlanFingerprint(plan.SourceFingerprint, plan.SnapshotScopeFingerprint,
            plan.ProfileScopeFingerprint, plan.StrategyFingerprint, plan.SnapshotRequestShapeFingerprint,
            plan.RequestShapeFingerprint, plan.RuntimeVersionFingerprint, plan.SnapshotTableRequestsPerSecond,
            plan.ProfileTableRequestsPerSecond, plan.GeneralResidualRequestsPerSecond);

    private static string ComputePlanFingerprint(string sourceFingerprint, string snapshotScopeFingerprint,
        string profileScopeFingerprint, string strategyFingerprint, string snapshotRequestShapeFingerprint,
        string requestShapeFingerprint, string runtimeVersionFingerprint, double snapshotRate,
        double profileRate, double residualRate)
    {
        var key = string.Join("|", sourceFingerprint, snapshotScopeFingerprint, profileScopeFingerprint,
            strategyFingerprint, snapshotRequestShapeFingerprint, requestShapeFingerprint, runtimeVersionFingerprint,
            snapshotRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profileRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            residualRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return PrtgProfileTransportCapacityStore.Fingerprint(key);
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

public enum PrtgSnapshotCapacityStatus
{
    CapacityUnverified,
    CapacityQualified,
    CapacityExceeded
}

public sealed record PrtgSnapshotCapacitySample(
    string ScopeFingerprint,
    string EndpointFingerprint,
    string RequestShapeFingerprint,
    DateTimeOffset CompletedAtUtc,
    long ElapsedMilliseconds,
    int RequestedSensorCount,
    string Outcome);

public sealed record PrtgSnapshotCapacityEstimate(
    PrtgSnapshotCapacityStatus Status,
    int TargetCount,
    int BatchCount,
    int FreshMatchingFullBatchSamples,
    DateTimeOffset? OldestSampleAtUtc,
    double? P95BatchSeconds,
    double? EstimatedSeconds,
    double CompletionWindowSeconds,
    double RequiredHeadroomFraction,
    string Reason);

/// <summary>Bounded evidence of real snapshot-table requests. It stores opaque hashes and timings only.</summary>
public sealed class PrtgSnapshotCapacityStore(EfJsonBlobStore blob)
{
    public const string BlobKey = "prtg_snapshot_capacity_evidence";
    public const int MaximumSerializedBytes = 64 * 1024;
    public const int MaximumSamples = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<PrtgSnapshotCapacitySample> Read()
    {
        var (prefix, _, length) = blob.ReadBoundedWithVersion(MaximumSerializedBytes);
        if (prefix == null) return Array.Empty<PrtgSnapshotCapacitySample>();
        if (length > MaximumSerializedBytes || Encoding.UTF8.GetByteCount(prefix) > MaximumSerializedBytes)
            return Array.Empty<PrtgSnapshotCapacitySample>();
        try
        {
            return JsonSerializer.Deserialize<List<PrtgSnapshotCapacitySample>>(prefix, JsonOptions)
                ?.TakeLast(MaximumSamples).ToArray() ?? Array.Empty<PrtgSnapshotCapacitySample>();
        }
        catch (JsonException) { return Array.Empty<PrtgSnapshotCapacitySample>(); }
    }

    public void Record(PrtgSnapshotCapacitySample sample)
    {
        if (sample.ScopeFingerprint.Length != 64 || sample.EndpointFingerprint.Length != 64 ||
            sample.RequestShapeFingerprint.Length != 64 || sample.RequestedSensorCount is < 1 or > 50 ||
            sample.ElapsedMilliseconds < 0 || sample.Outcome is not ("success" or "failed" or "timeout"))
            throw new ArgumentException("Invalid bounded snapshot capacity sample.", nameof(sample));

        blob.MutateWithContext((_, current) =>
        {
            var samples = ParseBounded(current);
            if (sample.Outcome != "success")
            {
                // A live timeout/failure invalidates prior clean evidence for this exact contract;
                // the next five successful full batches must re-establish the estimate.
                samples.RemoveAll(x => x.ScopeFingerprint == sample.ScopeFingerprint &&
                    x.EndpointFingerprint == sample.EndpointFingerprint &&
                    x.RequestShapeFingerprint == sample.RequestShapeFingerprint);
            }
            else
            {
                // Recovery starts with a new clean run; do not keep an old failed sample in
                // the quality window once successful retries begin.
                samples.RemoveAll(x => x.ScopeFingerprint == sample.ScopeFingerprint &&
                    x.EndpointFingerprint == sample.EndpointFingerprint &&
                    x.RequestShapeFingerprint == sample.RequestShapeFingerprint && x.Outcome != "success");
            }
            var sameHour = samples.Where(x => x.ScopeFingerprint == sample.ScopeFingerprint &&
                x.EndpointFingerprint == sample.EndpointFingerprint &&
                x.RequestShapeFingerprint == sample.RequestShapeFingerprint &&
                x.Outcome == "success" &&
                x.CompletedAtUtc.UtcDateTime.Date == sample.CompletedAtUtc.UtcDateTime.Date &&
                x.CompletedAtUtc.UtcDateTime.Hour == sample.CompletedAtUtc.UtcDateTime.Hour).ToArray();
            // Keep the five slowest successes in each UTC hour. This bounds writes while ensuring
            // a later slower request can never be discarded in favor of an optimistic sample.
            if (sample.Outcome == "success" && sameHour.Length >= PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
            {
                var retained = sameHour.Append(sample).OrderByDescending(x => x.ElapsedMilliseconds)
                    .ThenByDescending(x => x.CompletedAtUtc)
                    .Take(PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples).ToHashSet();
                samples.RemoveAll(x => x.ScopeFingerprint == sample.ScopeFingerprint &&
                    x.EndpointFingerprint == sample.EndpointFingerprint &&
                    x.RequestShapeFingerprint == sample.RequestShapeFingerprint &&
                    x.Outcome == "success" &&
                    x.CompletedAtUtc.UtcDateTime.Date == sample.CompletedAtUtc.UtcDateTime.Date &&
                    x.CompletedAtUtc.UtcDateTime.Hour == sample.CompletedAtUtc.UtcDateTime.Hour);
                samples.AddRange(retained);
            }
            else samples.Add(sample);
            if (samples.Count > MaximumSamples) samples.RemoveRange(0, samples.Count - MaximumSamples);
            var json = JsonSerializer.Serialize(samples, JsonOptions);
            while (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes && samples.Count > 1)
            {
                samples.RemoveAt(0);
                json = JsonSerializer.Serialize(samples, JsonOptions);
            }
            if (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes)
                throw new InvalidDataException("Capacity evidence sample exceeds storage bound.");
            return (json, true);
        }, maxCurrentCharacters: MaximumSerializedBytes);
    }

    public static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static List<PrtgSnapshotCapacitySample> ParseBounded(string? current)
    {
        if (string.IsNullOrEmpty(current)) return [];
        if (Encoding.UTF8.GetByteCount(current) > MaximumSerializedBytes)
            throw new InvalidDataException("Capacity evidence blob exceeds storage bound.");
        return JsonSerializer.Deserialize<List<PrtgSnapshotCapacitySample>>(current, JsonOptions)
            ?.TakeLast(MaximumSamples).ToList() ?? [];
    }
}

/// <summary>Fixed admission model: 2 table requests/s, 4 shared in-flight, 25% capacity headroom.</summary>
public static class PrtgSnapshotCapacityEvaluator
{
    public const int MaximumTableRequestsPerSecond = 2;
    public const int MaximumSharedInFlight = 4;
    public const int RequiredFullBatchSamples = 5;
    public const int BatchSize = 50;
    public const double RequiredHeadroomFraction = 0.25;
    public static readonly TimeSpan EvidenceFreshness = TimeSpan.FromHours(24);
    public static readonly TimeSpan ConservativeWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AggressiveWindow = TimeSpan.FromMinutes(3);

    public static PrtgSnapshotCapacityEstimate Evaluate(int targetCount, string strategy,
        string scopeFingerprint, string endpointFingerprint, string requestShapeFingerprint,
        IEnumerable<PrtgSnapshotCapacitySample> evidence, DateTimeOffset nowUtc)
    {
        if (targetCount < 0) throw new ArgumentOutOfRangeException(nameof(targetCount));
        var window = string.Equals(strategy, "aggressive", StringComparison.OrdinalIgnoreCase)
            ? AggressiveWindow.TotalSeconds : ConservativeWindow.TotalSeconds;
        var batches = (targetCount + BatchSize - 1) / BatchSize;
        var expectedBatchSize = Math.Min(BatchSize, targetCount);
        var matching = evidence.Where(s => s.ScopeFingerprint == scopeFingerprint &&
            s.EndpointFingerprint == endpointFingerprint && s.RequestShapeFingerprint == requestShapeFingerprint &&
            s.RequestedSensorCount == expectedBatchSize && s.CompletedAtUtc <= nowUtc &&
            nowUtc - s.CompletedAtUtc <= EvidenceFreshness).OrderBy(s => s.CompletedAtUtc).ToArray();
        var fullSamples = matching.Length;
        var oldest = matching.FirstOrDefault()?.CompletedAtUtc;

        PrtgSnapshotCapacityEstimate Result(PrtgSnapshotCapacityStatus status, double? p95, double? seconds, string reason) =>
            new(status, targetCount, batches, fullSamples, oldest, p95, seconds, window,
                RequiredHeadroomFraction, reason);

        if (batches == 0) return Result(PrtgSnapshotCapacityStatus.CapacityQualified, 0, 0, "empty_scope");
        if (fullSamples < RequiredFullBatchSamples)
            return Result(PrtgSnapshotCapacityStatus.CapacityUnverified, null, null, "insufficient_fresh_full_batch_samples");
        if (matching.Any(s => s.Outcome != "success"))
            return Result(PrtgSnapshotCapacityStatus.CapacityUnverified, null, null, "recent_full_batch_failure_or_timeout");

        var timings = matching.Select(s => s.ElapsedMilliseconds / 1000d).Order().ToArray();
        var p95 = timings[Math.Clamp((int)Math.Ceiling(timings.Length * 0.95) - 1, 0, timings.Length - 1)];
        var rateFloor = Math.Ceiling(batches / (double)MaximumTableRequestsPerSecond);
        // The last paced request can be sent only after all preceding batch slots. Its response
        // then needs a full measured p95; keep both terms because pacing and HTTP overlap only
        // across the three worker slots, while the final response cannot overlap itself.
        var pacedCompletion = Math.Max(0, batches - 1d) / MaximumTableRequestsPerSecond + p95;
        var concurrencyCompletion = Math.Ceiling(batches / (double)(MaximumSharedInFlight - 1)) * p95;
        var estimate = Math.Max(Math.Max(rateFloor, concurrencyCompletion), pacedCompletion);
        if (!double.IsFinite(estimate) || estimate > window * (1 - RequiredHeadroomFraction))
            return Result(PrtgSnapshotCapacityStatus.CapacityExceeded, p95, estimate, "completion_window_or_headroom_exceeded");
        return Result(PrtgSnapshotCapacityStatus.CapacityQualified, p95, estimate, "within_fixed_window_with_headroom");
    }
}

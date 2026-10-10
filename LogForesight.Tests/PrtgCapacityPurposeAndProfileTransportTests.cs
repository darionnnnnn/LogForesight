using LogForesight.Core.Service;
using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgCapacityPurposeAndProfileTransportTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    [InlineData(4, 10)]
    [InlineData(5, 12)]
    public void Grouped_refresh_request_count_is_shared_by_contract(int sensors, int expectedRequests) =>
        Assert.Equal(expectedRequests, PrtgProfileTransportCapacityEvaluator.ExpectedRequests(sensors));

    [Fact]
    public void Partial_runtime_samples_keep_bootstrap_strict_and_require_exact_group_shape()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new string('A', 64); var scope = new string('B', 64);
        var strategy = new string('C', 64); var shape = new string('D', 64); var version = new string('E', 64);
        var sample = new PrtgProfileTransportSample(source, scope, strategy, shape, now.AddSeconds(-1),
            2_000, 2, 6, 6, "success", null, version, 2_000);
        var strict = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, scope, strategy,
            shape, version, [sample], now);
        var runtime = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, scope, strategy,
            shape, version, [sample], now, tableRequestsAvailableAfterSharedTraffic: 1.5,
            requiredFreshSuccessfulSamples: 1, allowPartialRuntimeSamples: true);
        var mismatch = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, scope, strategy,
            shape, version, [sample with { RequestsSent = 8 }], now,
            tableRequestsAvailableAfterSharedTraffic: 1.5, requiredFreshSuccessfulSamples: 1,
            allowPartialRuntimeSamples: true);
        var stale = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, scope, strategy,
            shape, version, [sample with { CompletedAtUtc = now.AddHours(-25) }], now,
            tableRequestsAvailableAfterSharedTraffic: 1.5, requiredFreshSuccessfulSamples: 1,
            allowPartialRuntimeSamples: true);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, strict.Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, runtime.Status);
        Assert.Equal(24_000d, runtime.EstimatedSeconds);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, mismatch.Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, stale.Status);
    }

    [Fact]
    public void Partial_runtime_store_preserves_actual_failure_counters_and_invalidates_old_success()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-prtg-partial-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgProfileTransportCapacityStore(backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            PrtgProfileTransportSample Make(DateTimeOffset at, string outcome, int attempted, int sent) => new(
                new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
                at, 2_000, 2, attempted, sent, outcome, outcome == "success" ? null : "request-shape-mismatch",
                new string('E', 64), 2_000);

            store.Record(Make(now, "success", 6, 6));
            Assert.Contains(store.Read(), sample => sample.SensorCount == 2 &&
                sample.RequestsAttempted == 6 && sample.RequestsSent == 6 && sample.Outcome == "success");
            Assert.Throws<ArgumentException>(() => store.Record(Make(now.AddSeconds(1), "success", 8, 8)));

            store.Record(Make(now.AddSeconds(2), "failed", 20, 20));
            store.Record(Make(now.AddSeconds(3), "success", 6, 6));
            var recovered = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000,
                new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
                new string('E', 64), store.Read(), now.AddSeconds(4),
                tableRequestsAvailableAfterSharedTraffic: 1.5, requiredFreshSuccessfulSamples: 1,
                allowPartialRuntimeSamples: true);
            Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, recovered.Status);

            store.Record(Make(now.AddSeconds(4), "success", 12, 12) with { SensorCount = 5 });
            store.Record(Make(now.AddSeconds(5), "failed", 20, 20));
            var result = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000,
                new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
                new string('E', 64), store.Read(), now.AddSeconds(6),
                tableRequestsAvailableAfterSharedTraffic: 1.5, requiredFreshSuccessfulSamples: 1,
                allowPartialRuntimeSamples: true);
            var strictResult = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000,
                new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
                new string('E', 64), store.Read(), now.AddSeconds(6), tableRequestsAvailableAfterSharedTraffic: 1.5);

            Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, result.Status);
            Assert.Equal("latest_profile_transport_sample_failed_or_timed_out", result.Reason);
            Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, strictResult.Status);
            Assert.Equal("latest_profile_transport_sample_failed_or_timed_out", strictResult.Reason);
            Assert.Contains(store.Read(), sample => sample.Outcome == "failed" &&
                sample.RequestsAttempted == 20 && sample.RequestsSent == 20);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Fixed_refresh_groups_align_due_sensor_with_only_fresh_proven_eligible_neighbors()
    {
        var page = Enumerable.Range(1, 12).Select(value => (long)value).ToArray();
        var groups = PrtgTrustedSamplingProfileRefreshHostedService.BuildRefreshGroups(page,
            [1, 6, 12], [2, 3, 4, 5, 7, 8, 9, 10, 11]);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, groups[0]);
        Assert.Equal(new long[] { 6, 7, 8, 9, 10 }, groups[1]);
        Assert.Equal(new long[] { 11, 12 }, groups[2]);
        Assert.Equal(30, groups.Sum(group => PrtgProfileTransportCapacityEvaluator.ExpectedRequests(group.Length)));

        var oneDueSensor = PrtgTrustedSamplingProfileRefreshHostedService.BuildRefreshGroups(page,
            [1], [2, 3, 4, 5]);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Assert.Single(oneDueSensor));
        Assert.Equal(12, PrtgProfileTransportCapacityEvaluator.ExpectedRequests(Assert.Single(oneDueSensor).Length));

        // Failed/cooldown and pending-proof/no-proof sensors are deliberately absent from the
        // fresh-proven-eligible set, so a due neighbor cannot pull them into an early retry.
        var cooldownAndPendingExcluded = PrtgTrustedSamplingProfileRefreshHostedService.BuildRefreshGroups(page,
            [1], [2]);
        Assert.Equal(new long[] { 1, 2 }, Assert.Single(cooldownAndPendingExcluded));
    }

    [Fact]
    public void Profile_work_timing_excludes_only_measured_admission_and_legacy_keeps_full_wall_time()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 5).Select(i => new PrtgProfileTransportSample(
            "source", "scope", "strategy", "shape", now.AddSeconds(-i), 12_000, 5, 12, 12,
            "success", null, "version", NonAdmissionElapsedMilliseconds: 1_000)).ToArray();
        var measured = PrtgProfileTransportCapacityEvaluator.Evaluate(5, "source", "scope", "strategy",
            "shape", "version", samples, now);
        var legacy = PrtgProfileTransportCapacityEvaluator.Evaluate(5, "source", "scope", "strategy",
            "shape", "version", samples.Select(sample => sample with { NonAdmissionElapsedMilliseconds = null }), now);
        Assert.Equal(.2, measured.P95SensorSeconds);
        Assert.Equal(2.4, legacy.P95SensorSeconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5_001)]
    public void Invalid_work_timing_invalidates_current_success_evidence(long workMilliseconds)
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 5).Select(i => new PrtgProfileTransportSample(
            "source", "scope", "strategy", "shape", now.AddSeconds(-i), 5_000, 5, 12, 12,
            "success", null, "version", NonAdmissionElapsedMilliseconds: 1_000)).ToArray();
        var invalid = samples[0] with { CompletedAtUtc = now.AddSeconds(1), NonAdmissionElapsedMilliseconds = workMilliseconds };
        var result = PrtgProfileTransportCapacityEvaluator.Evaluate(5, "source", "scope", "strategy",
            "shape", "version", samples.Append(invalid), now.AddSeconds(2));
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, result.Status);
        Assert.Null(result.P95SensorSeconds);
    }

    [Fact]
    public async Task Snapshot_lane_allows_three_in_flight_and_leaves_the_fourth_slot_to_profile()
    {
        var budget = new PrtgRequestBudget();
        var snapshots = new List<PrtgBudgetLease>();
        try
        {
            for (var i = 0; i < 3; i++)
                snapshots.Add(await budget.AcquireAsync(PrtgEndpointCategory.Other, default, PrtgRequestPurpose.Snapshot));
            var profile = await budget.AcquireAsync(PrtgEndpointCategory.Other, default, PrtgRequestPurpose.ProfileRefresh);

            Assert.Equal(4, budget.InFlightCount);
            var secondProfile = budget.AcquireAsync(PrtgEndpointCategory.Other, default, PrtgRequestPurpose.ProfileRefresh);
            Assert.Equal(1, budget.WaiterCount);
            profile.Dispose();
            using var secondProfileLease = await secondProfile;
            Assert.Equal(4, budget.InFlightCount);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                budget.AcquireAsync(PrtgEndpointCategory.Other, cancelled.Token, PrtgRequestPurpose.Snapshot));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                budget.AcquireAsync(PrtgEndpointCategory.Other, cancelled.Token, PrtgRequestPurpose.CapacityPilot));
        }
        finally
        {
            foreach (var lease in snapshots) lease.Dispose();
        }
        Assert.Equal(0, budget.InFlightCount);
    }

    [Fact]
    public async Task General_lane_keeps_legacy_four_slot_limit_when_profile_lane_is_idle()
    {
        var budget = new PrtgRequestBudget();
        var leases = new List<PrtgBudgetLease>();
        try
        {
            for (var i = 0; i < 4; i++)
                leases.Add(await budget.AcquireAsync(PrtgEndpointCategory.Other));
            Assert.Equal(4, budget.InFlightCount);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public void Profile_transport_estimate_requires_five_fresh_exact_contract_samples()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new string('A', 64);
        var scope = new string('B', 64);
        var strategy = new string('C', 64);
        var shape = new string('D', 64);
        var version = new string('E', 64);
        var samples = Enumerable.Range(0, 5).Select(i => new PrtgProfileTransportSample(
            source, scope, strategy, shape, now.AddMinutes(-i), 5000, 5, 12, 12, "success", null, version)).ToArray();

        var qualified = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, source, scope, strategy,
            shape, version, samples, now);
        var otherSource = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, new string('F', 64), scope,
            strategy, shape, version, samples, now);
        var failed = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, source, scope, strategy, shape,
            version, samples.Append(samples[0] with { Outcome = "timeout", RequestsAttempted = 1, RequestsSent = 0 }), now);
        var olderAfterFailure = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, source, scope, strategy, shape,
            version, samples.Append(samples[^1] with { CompletedAtUtc = now.AddSeconds(1), Outcome = "failed",
                RequestsAttempted = 20, RequestsSent = 20 }), now.AddSeconds(2));
        var failure = samples[^1] with { CompletedAtUtc = now.AddSeconds(1), Outcome = "timeout",
            RequestsAttempted = 1, RequestsSent = 0 };
        var oneRecovery = samples.Append(failure).Append(samples[0] with { CompletedAtUtc = now.AddSeconds(2) });
        var oneRecoveryResult = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, source, scope, strategy,
            shape, version, oneRecovery, now.AddSeconds(3));
        var fullRecovery = oneRecovery.Concat(Enumerable.Range(1, 4).Select(i => samples[0] with
            { CompletedAtUtc = now.AddSeconds(2 + i) }));
        var fullRecoveryResult = PrtgProfileTransportCapacityEvaluator.Evaluate(1000, source, scope,
            strategy, shape, version, fullRecovery, now.AddSeconds(7));

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, qualified.Status);
        Assert.Equal(1200d, qualified.EstimatedSeconds);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, otherSource.Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, failed.Status);
        Assert.Equal("latest_profile_transport_sample_failed_or_timed_out", olderAfterFailure.Reason);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, oneRecoveryResult.Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, fullRecoveryResult.Status);
    }

    [Fact]
    public void Stale_reservation_owner_cannot_release_or_restore_a_newer_plan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-prtg-capacity-reservation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var reservations = new PrtgCapacityReservationStore(backend.Blob(PrtgCapacityReservationStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var oldPlan = new string('A', 64);
            var newPlan = new string('B', 64);
            Assert.True(reservations.TryAcquire(oldPlan, "old-owner", now, TimeSpan.FromMinutes(1), out var oldVersion));
            Assert.True(reservations.TryAcquire(newPlan, "new-owner", now, TimeSpan.FromMinutes(1), out var newVersion));

            reservations.Release(oldPlan, "old-owner", oldVersion);

            Assert.False(reservations.TryAcquire(newPlan, "third-owner", now, TimeSpan.FromMinutes(1), out _));
            reservations.Release(newPlan, "new-owner", newVersion);
            Assert.True(reservations.TryAcquire(newPlan, "third-owner", now, TimeSpan.FromMinutes(1), out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Missing_or_corrupt_plan_shape_and_runtime_fingerprints_fail_closed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-prtg-admission-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var blob = backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey);
            var store = new PrtgCapacityAdmissionPlanStore(blob);
            var now = DateTimeOffset.UtcNow;
            var plan = TestPlan(new string('A', 64), 1);
            var legacy = new
            {
                plan.Fingerprint, plan.SourceFingerprint, plan.SnapshotScopeFingerprint,
                plan.ProfileScopeFingerprint, plan.StrategyFingerprint, plan.SettingsRevision,
                plan.PolicyRevision, plan.SnapshotTableRequestsPerSecond, plan.ProfileTableRequestsPerSecond,
                plan.GeneralResidualRequestsPerSecond, CreatedAtUtc = now, LeaseUntilUtc = now.AddHours(1),
                plan.Owner, plan.Version
            };
            blob.Mutate(_ => (JsonSerializer.Serialize(legacy), true));
            Assert.Null(store.ReadCurrent(now));

            var missingShape = JsonSerializer.Serialize(plan with { SnapshotRequestShapeFingerprint = null! });
            blob.Mutate(_ => (missingShape, true));
            Assert.Null(store.ReadCurrent(now));

            var missingRuntime = JsonSerializer.Serialize(plan with { RuntimeVersionFingerprint = null! });
            blob.Mutate(_ => (missingRuntime, true));
            Assert.Null(store.ReadCurrent(now));

            blob.Mutate(_ => ("{invalid-json", true));
            Assert.Null(store.ReadCurrent(now));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Joint_plan_allocates_positive_rates_with_room_for_reclaim_and_general_traffic()
    {
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            50, 1, 5, DateTimeOffset.UtcNow, 1, 1, 600, .25, "qualified");
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            100, 5, DateTimeOffset.UtcNow, .2, 100, 62100, .25, "qualified");
        var usage = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, usage, 30);
        var plan = PrtgJointCapacityEvaluator.CreatePlan(joint, new string('A', 64),
            new string('B', 64), new string('C', 64), new string('D', 64), new string('E', 64), new string('F', 64), new string('7', 64), DateTimeOffset.UtcNow,
            "settings-r1", "policy-r1");

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, joint.Status);
        Assert.True(plan.SnapshotTableRequestsPerSecond > 0);
        Assert.True(plan.ProfileTableRequestsPerSecond > 0);
        Assert.True(plan.GeneralResidualRequestsPerSecond > 0);
        Assert.True(plan.SnapshotTableRequestsPerSecond + plan.ProfileTableRequestsPerSecond <= 1.5);
    }

    [Fact]
    public void Joint_plan_rate_fingerprint_is_stable_across_idle_and_busy_shared_pool_states()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            150, 3, 5, now, 2, 6, 600, .25, "qualified");
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            1000, 5, now, .2, 2000, 62100, .25, "qualified");
        var idle = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
        var busy = new PrtgRequestBudgetUsage(1, 1, 1, 2, 5, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(59));

        var idleEstimate = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, idle, 30);
        var busyEstimate = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, busy, 30);
        var slowerRecentSampleEstimate = PrtgJointCapacityEvaluator.Evaluate(snapshot,
            profile with { P95SensorSeconds = .3 }, idle, 30);
        var idlePlan = PrtgJointCapacityEvaluator.CreatePlan(idleEstimate, new string('A', 64),
            new string('B', 64), new string('C', 64), new string('D', 64), new string('E', 64), new string('F', 64), new string('7', 64), now, "settings-r1", "policy-r1");
        var busyPlan = PrtgJointCapacityEvaluator.CreatePlan(busyEstimate, new string('A', 64),
            new string('B', 64), new string('C', 64), new string('D', 64), new string('E', 64), new string('F', 64), new string('7', 64), now, "settings-r1", "policy-r1");
        var slowerRecentSamplePlan = PrtgJointCapacityEvaluator.CreatePlan(slowerRecentSampleEstimate,
            new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64), new string('E', 64), new string('F', 64), new string('7', 64), now,
            "settings-r1", "policy-r1");

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, idleEstimate.Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, busyEstimate.Status);
        Assert.Equal(idlePlan.Fingerprint, busyPlan.Fingerprint);
        Assert.Equal(idlePlan.Fingerprint, slowerRecentSamplePlan.Fingerprint);
        Assert.Equal(idlePlan.SnapshotTableRequestsPerSecond, busyPlan.SnapshotTableRequestsPerSecond);
        Assert.Equal(idlePlan.ProfileTableRequestsPerSecond, busyPlan.ProfileTableRequestsPerSecond);
        Assert.True(idleEstimate.ReclaimDelaySeconds >= 60);
    }

    [Fact]
    public void Small_profile_scope_keeps_one_five_sensor_probe_group_inside_its_deadline()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            50, 1, 5, now, 1, 1, 600, .25, "qualified");
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            5, 5, now, 1, 5, 62100, .25, "qualified");
        var usage = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, usage, 30);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, joint.Status);
        Assert.True(joint.ProfileTableRequestsPerSecond >= 11d / 20d);
        Assert.True(11d / joint.ProfileTableRequestsPerSecond + 10d < 30d);

        var slowProfile = profile with { P95SensorSeconds = 3 };
        var slowJoint = PrtgJointCapacityEvaluator.Evaluate(snapshot, slowProfile, usage, 30);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityExceeded, slowJoint.Status);
        Assert.Equal("profile_probe_group_deadline_exceeded", slowJoint.Reason);
    }

    [Fact]
    public void Full_profile_scope_estimate_includes_the_refresh_workers_inter_slice_waits()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            150, 3, 5, now, 2, 6, 600, .25, "qualified");
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            15_000, 5, now, .2, 3_000, 62_100, .25, "qualified");
        var usage = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(0, PrtgJointCapacityEvaluator.ProfileRefreshInterSliceDelayBudget(600));
        Assert.Equal(60, PrtgJointCapacityEvaluator.ProfileRefreshInterSliceDelayBudget(600.01));
        Assert.Equal(60, PrtgJointCapacityEvaluator.ProfileRefreshInterSliceDelayBudget(1_200));
        Assert.Equal(120, PrtgJointCapacityEvaluator.ProfileRefreshInterSliceDelayBudget(1_200.01));

        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, usage, 30);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, joint.Status);
        Assert.True(joint.ProfileEstimatedSeconds < profile.CompletionWindowSeconds);
        Assert.True(joint.ProfileEstimatedSeconds > joint.ReclaimDelaySeconds + profile.TargetSensorCount * .2);
    }

    [Fact]
    public void Runtime_profile_cost_refresh_keeps_reserved_rates_when_safe_and_fails_closed_when_over_budget()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new string('A', 64);
        var snapshotScope = new string('B', 64);
        var profileScope = new string('C', 64);
        var strategy = new string('D', 64);
        var snapshotShape = new string('E', 64);
        var profileShape = new string('F', 64);
        var runtimeVersion = new string('7', 64);
        var initialSamples = Enumerable.Range(0, 5).Select(i => new PrtgProfileTransportSample(source,
            profileScope, strategy, profileShape, now.AddMinutes(-i), 1_000, 5, 12, 12,
            "success", null, runtimeVersion)).ToArray();
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            150, 3, 5, now, 2, 6, 600, .25, "qualified");
        var usage = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

        var initialProfile = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, profileScope,
            strategy, profileShape, runtimeVersion, initialSamples, now);
        var initialJoint = PrtgJointCapacityEvaluator.Evaluate(snapshot, initialProfile, usage, 30);
        var published = PrtgJointCapacityEvaluator.CreatePlan(initialJoint, source, snapshotScope,
            profileScope, strategy, snapshotShape, profileShape, runtimeVersion, now, "settings-r1", "policy-r1");

        var oneFreshSafeSample = new PrtgProfileTransportSample(source, profileScope, strategy, profileShape,
            now, 1_050, 5, 12, 12, "success", null, runtimeVersion);
        var agedSamplesAndRefresh = initialSamples.Select((sample, i) => sample with
            { CompletedAtUtc = now.AddHours(-25).AddMinutes(i) }).Append(oneFreshSafeSample);
        var refreshedProfile = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, profileScope,
            strategy, profileShape, runtimeVersion, agedSamplesAndRefresh, now,
            requiredFreshSuccessfulSamples: 1);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, refreshedProfile.Status);
        Assert.Equal(1, refreshedProfile.FreshSuccessfulSamples);

        var refreshedJoint = PrtgJointCapacityEvaluator.EvaluateAgainstReservedRates(snapshot,
            refreshedProfile, usage, 30, published.SnapshotTableRequestsPerSecond,
            published.ProfileTableRequestsPerSecond);
        var refreshedPlan = PrtgJointCapacityEvaluator.CreatePlan(refreshedJoint, source, snapshotScope,
            profileScope, strategy, snapshotShape, profileShape, runtimeVersion, now.AddMinutes(1),
            "settings-r1", "policy-r1");
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, refreshedJoint.Status);
        Assert.Equal(published.Fingerprint, refreshedPlan.Fingerprint);

        var slowSample = oneFreshSafeSample with { ElapsedMilliseconds = 7_500 };
        var slowProfile = PrtgProfileTransportCapacityEvaluator.Evaluate(15_000, source, profileScope,
            strategy, profileShape, runtimeVersion,
            initialSamples.Select((sample, i) => sample with { CompletedAtUtc = now.AddHours(-25).AddMinutes(i) })
                .Append(slowSample), now, requiredFreshSuccessfulSamples: 1);
        var overBudget = PrtgJointCapacityEvaluator.EvaluateAgainstReservedRates(snapshot, slowProfile,
            usage, 30, published.SnapshotTableRequestsPerSecond, published.ProfileTableRequestsPerSecond);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityExceeded, overBudget.Status);
    }

    [Fact]
    public void Bounded_snapshot_recovery_uses_timeout_envelope_and_keeps_full_capacity_unqualified()
    {
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            1, 1, DateTimeOffset.UtcNow.AddMinutes(-1), .1, 1,
            PrtgProfileTransportCapacityEvaluator.ProfileRefreshWindow.TotalSeconds, .25, "qualified");
        var idle = new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

        var safe = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
            1, "conservative", profile, idle, 30, .4, .5);
        Assert.True(safe.Admitted, safe.Reason);
        Assert.Equal(1, safe.TargetCount);
        Assert.Equal(30, safe.TimeoutBoundSeconds);
        Assert.Equal("bounded_single_batch_recovery_timeout_bound_fit", safe.Reason);

        var oversized = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
            51, "conservative", profile, idle, 30, .4, .5);
        Assert.False(oversized.Admitted);
        Assert.Equal("recovery_scope_not_one_bounded_batch", oversized.Reason);

        var slow = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
            1, "conservative", profile, idle, 300, .4, .5);
        Assert.False(slow.Admitted);
        Assert.StartsWith("bounded_recovery_", slow.Reason);

        var noHeadroom = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
            1, "conservative", profile, idle, 30, 1, .5);
        Assert.False(noHeadroom.Admitted);
        Assert.Equal("recovery_reserved_rates_do_not_preserve_headroom", noHeadroom.Reason);

        var staleProfile = profile with { Status = PrtgSnapshotCapacityStatus.CapacityUnverified };
        var profileBlocked = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
            1, "conservative", staleProfile, idle, 30, .4, .5);
        Assert.False(profileBlocked.Admitted);
        Assert.Equal("recovery_profile_evidence_not_qualified", profileBlocked.Reason);
    }

    [Fact]
    public async Task Purpose_rate_wait_happens_before_global_in_flight_permit_is_acquired()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var budget = new PrtgRequestBudget(clock);
        var fingerprint = new string('A', 64);
        budget.SetAdmissionPlan(TestPlan(fingerprint, 1));
        using (var first = await budget.AcquireAsync(PrtgEndpointCategory.Table, default,
            PrtgRequestPurpose.ProfileRefresh, fingerprint))
        {
            await first.MarkRequestSentAsync();
        }

        var queued = budget.AcquireAsync(PrtgEndpointCategory.Table, default,
            PrtgRequestPurpose.ProfileRefresh, fingerprint);
        await clock.WaitForTimerAsync();
        Assert.Equal(0, budget.InFlightCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        using var second = await queued;
        Assert.Equal(1, budget.InFlightCount);
    }

    [Fact]
    public async Task Mixed_snapshot_profile_and_general_table_lanes_progress_under_shared_rolling_quota()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var budget = new PrtgRequestBudget(clock);
        var fingerprint = new string('A', 64);
        budget.SetAdmissionPlan(TestPlan(fingerprint, 1));
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = new Dictionary<PrtgRequestPurpose, int>
        {
            [PrtgRequestPurpose.Snapshot] = 0,
            [PrtgRequestPurpose.ProfileRefresh] = 0,
            [PrtgRequestPurpose.General] = 0
        };
        var observations = new List<(PrtgRequestPurpose Purpose, PrtgEndpointCategory Category,
            TimeSpan At, PrtgRequestBudgetUsage Usage)>();
        var workers = new List<Task>();

        async Task SendAsync(PrtgEndpointCategory category, PrtgRequestPurpose purpose)
        {
            using var lease = await budget.AcquireAsync(category, watchdog.Token, purpose, fingerprint);
            await lease.MarkRequestSentAsync(watchdog.Token);
            var usage = budget.ReadUsage();
            lock (observations)
            {
                observations.Add((purpose, category, clock.Elapsed, usage));
                if (category == PrtgEndpointCategory.Table) sent[purpose]++;
            }
        }

        try
        {
            for (var round = 0; round < 3; round++)
            {
                var pending = new[]
                {
                    SendAsync(PrtgEndpointCategory.Table, PrtgRequestPurpose.Snapshot),
                    SendAsync(PrtgEndpointCategory.Table, PrtgRequestPurpose.ProfileRefresh),
                    SendAsync(PrtgEndpointCategory.Table, PrtgRequestPurpose.General)
                };
                workers.AddRange(pending);
                await clock.AdvanceUntilCompleteAsync(Task.WhenAll(pending), watchdog.Token);
            }

            for (var i = 0; i < 5; i++)
            {
                var history = SendAsync(PrtgEndpointCategory.HistoricData, PrtgRequestPurpose.General);
                workers.Add(history);
                await clock.AdvanceUntilCompleteAsync(history, watchdog.Token);
            }
            var sixthHistory = SendAsync(PrtgEndpointCategory.HistoricData, PrtgRequestPurpose.General);
            workers.Add(sixthHistory);
            Assert.False(sixthHistory.IsCompleted);
            Assert.InRange(budget.InFlightCount, 0, 4);
            await clock.AdvanceUntilCompleteAsync(sixthHistory, watchdog.Token);
            Assert.Equal(1, budget.ReadUsage().HistoricRequestsInLastMinute);

            Assert.All(sent.Values, count => Assert.Equal(3, count));
            Assert.Equal(0, budget.InFlightCount);
            Assert.Equal(15, observations.Count);
            Assert.All(observations.Where(item => item.Category == PrtgEndpointCategory.Table), item =>
            {
                Assert.InRange(item.Usage.TableRequestsInLastSecond, 1, 2);
                Assert.InRange(item.Usage.InFlight, 1, 4);
                Assert.InRange(item.Usage.ProfileInFlight, 0, 1);
            });
            Assert.All(observations.Where(item => item.Category == PrtgEndpointCategory.HistoricData), item =>
            {
                Assert.InRange(item.Usage.HistoricRequestsInLastMinute, 1, 5);
                Assert.InRange(item.Usage.InFlight, 1, 4);
            });
            Assert.Equal(1.5, budget.CurrentAdmissionPlan!.SnapshotTableRequestsPerSecond +
                budget.CurrentAdmissionPlan.ProfileTableRequestsPerSecond +
                budget.CurrentAdmissionPlan.GeneralResidualRequestsPerSecond, 6);
        }
        finally
        {
            if (workers.Any(worker => !worker.IsCompleted)) watchdog.Cancel();
            try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception) { /* The original test failure is preserved; all workers are bounded and joined. */ }
        }
    }

    [Fact]
    public async Task Replacing_admission_plan_fails_queued_old_lane_before_it_can_be_granted()
    {
        var budget = new PrtgRequestBudget();
        budget.SetAdmissionPlan(TestPlan(new string('A', 64), 1));
        var blockers = new List<PrtgBudgetLease>();
        try
        {
            for (var i = 0; i < 4; i++) blockers.Add(await budget.AcquireAsync(PrtgEndpointCategory.Other));
            var queued = budget.AcquireAsync(PrtgEndpointCategory.Table, default,
                PrtgRequestPurpose.ProfileRefresh, new string('A', 64));
            Assert.Equal(1, budget.WaiterCount);

            budget.SetAdmissionPlan(TestPlan(new string('B', 64), 2));

            await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        }
        finally
        {
            foreach (var blocker in blockers) blocker.Dispose();
        }
    }

    private static PrtgCapacityAdmissionPlan TestPlan(string fingerprint, long version) =>
        new(fingerprint, new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64),
            new string('5', 64), new string('6', 64), new string('7', 64),
            "settings", "policy", .5, .5, .5, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1), "owner", version);

    private sealed class TestClock(DateTimeOffset initial) : IPrtgClock
    {
        private readonly object _lock = new();
        private DateTimeOffset _utcNow = initial;
        private TimeSpan _elapsed;
        private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Completion)> _timers = [];
        private readonly SemaphoreSlim _timerAdded = new(0);

        public DateTimeOffset UtcNow { get { lock (_lock) return _utcNow; } }
        public TimeSpan Elapsed { get { lock (_lock) return _elapsed; } }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var due = UtcNow + delay;
            lock (_lock) _timers.Add((due, completion));
            _timerAdded.Release();
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public async Task AdvanceUntilCompleteAsync(Task operation, CancellationToken cancellationToken)
        {
            while (!operation.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AdvanceToNextTimerAsync(operation, cancellationToken);
            }
            await operation;
        }

        private async Task AdvanceToNextTimerAsync(Task operation, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation.IsCompleted) return;
                List<TaskCompletionSource<bool>> ready;
                var hasTimer = false;
                lock (_lock)
                {
                    _timers.RemoveAll(timer => timer.Completion.Task.IsCompleted);
                    if (_timers.Count > 0)
                    {
                        var due = _timers.Min(timer => timer.Due);
                        if (due > _utcNow)
                        {
                            var delta = due - _utcNow;
                            _utcNow += delta;
                            _elapsed += delta;
                        }
                        ready = _timers.Where(timer => timer.Due <= _utcNow)
                            .Select(timer => timer.Completion).ToList();
                        _timers.RemoveAll(timer => timer.Due <= _utcNow);
                        hasTimer = true;
                    }
                    else ready = [];
                }

                if (operation.IsCompleted) return;
                if (!hasTimer)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var timerRegistration = _timerAdded.WaitAsync(waitCts.Token);
                    var winner = await Task.WhenAny(operation, timerRegistration);
                    if (winner == operation)
                    {
                        waitCts.Cancel();
                        try { await timerRegistration; }
                        catch (OperationCanceledException) when (waitCts.IsCancellationRequested) { }
                        return;
                    }
                    await timerRegistration;
                    if (operation.IsCompleted) return;
                    continue;
                }
                foreach (var completion in ready) completion.TrySetResult(true);
                // Let the continuation register its next wait or complete the operation before
                // advancing another fake-time interval. The real watchdog bounds this handoff.
                await Task.Delay(1, cancellationToken);
                return;
            }
        }

        public async Task WaitForTimerAsync()
        {
            for (var i = 0; i < 100; i++)
            {
                lock (_lock) if (_timers.Count > 0) return;
                await Task.Delay(2);
            }
            throw new TimeoutException("Purpose lane did not schedule its rate wait.");
        }

        public void Advance(TimeSpan duration)
        {
            List<TaskCompletionSource<bool>> ready;
            lock (_lock)
            {
                _utcNow += duration;
                _elapsed += duration;
                ready = _timers.Where(timer => timer.Due <= _utcNow).Select(timer => timer.Completion).ToList();
                _timers.RemoveAll(timer => timer.Due <= _utcNow);
            }
            foreach (var completion in ready) completion.TrySetResult(true);
        }
    }
}

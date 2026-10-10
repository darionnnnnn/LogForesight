using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgMaintenanceCapacityPreflightTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(5, 12)]
    public void Ordinary_probe_shape_is_the_exact_batch_contract(int sensors, int expectedRequests)
    {
        Assert.Equal(expectedRequests,
            PrtgMaintenanceCapacityPreflight.ExpectedOrdinaryProbeTableRequests(sensors));
    }

    [Fact]
    public void General_lane_refuses_when_its_mandatory_spacing_alone_breaks_30_seconds()
    {
        var clock = new TestClock();
        var budget = new PrtgRequestBudget(clock);
        var plan = TestPlan(generalRate: 11d / 400d);
        budget.SetAdmissionPlan(plan);

        var result = PrtgMaintenanceCapacityPreflight.Evaluate(budget, plan,
            PrtgRequestPurpose.General, PrtgMaintenanceCapacityPreflight.ExpectedOrdinaryProbeTableRequests(5));

        Assert.False(result.Admitted);
        Assert.Equal("minimum-table-pacing-exceeds-deadline-margin", result.Reason);
        Assert.Equal(12, result.TableRequestCount);
        Assert.True(result.MinimumPacingSeconds > 25);
    }

    [Fact]
    public async Task General_lane_admits_only_current_plan_and_accounts_for_an_existing_reserved_send()
    {
        var clock = new TestClock();
        var budget = new PrtgRequestBudget(clock);
        var plan = TestPlan(generalRate: .2);
        budget.SetAdmissionPlan(plan);
        using (var first = await budget.AcquireAsync(PrtgEndpointCategory.Table, default,
                   PrtgRequestPurpose.General, plan.Fingerprint))
            await first.MarkRequestSentAsync();

        var current = PrtgMaintenanceCapacityPreflight.Evaluate(budget, plan,
            PrtgRequestPurpose.General, 4);
        var stale = PrtgMaintenanceCapacityPreflight.Evaluate(budget,
            plan with { Fingerprint = new string('B', 64) }, PrtgRequestPurpose.General, 4);

        Assert.True(current.Admitted);
        Assert.Equal(20, current.MinimumPacingSeconds, precision: 6);
        Assert.Equal("maintenance-capacity-plan-not-current", stale.Reason);
        Assert.False(stale.Admitted);
    }

    [Fact]
    public async Task General_table_send_with_a_plan_fingerprint_is_fenced_by_the_live_budget()
    {
        var budget = new PrtgRequestBudget(new TestClock());
        var plan = TestPlan(generalRate: .5);
        budget.SetAdmissionPlan(plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            budget.AcquireAsync(PrtgEndpointCategory.Table, default, PrtgRequestPurpose.General,
                new string('B', 64)));
    }

    [Fact]
    public void Stale_or_expired_plan_fails_closed()
    {
        var clock = new TestClock();
        var budget = new PrtgRequestBudget(clock);
        var plan = TestPlan(generalRate: .5, leaseSeconds: 1);
        budget.SetAdmissionPlan(plan);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = PrtgMaintenanceCapacityPreflight.Evaluate(budget, plan,
            PrtgRequestPurpose.General, 4);

        Assert.False(result.Admitted);
        Assert.Equal("maintenance-capacity-plan-missing-or-expired", result.Reason);
    }

    [Fact]
    public void Current_plan_that_expires_before_its_first_paced_send_is_not_admitted()
    {
        var clock = new TestClock();
        var budget = new PrtgRequestBudget(clock);
        var plan = TestPlan(generalRate: .2, leaseSeconds: 10);
        budget.SetAdmissionPlan(plan);

        var result = PrtgMaintenanceCapacityPreflight.Evaluate(budget, plan,
            PrtgRequestPurpose.General, 4);

        Assert.False(result.Admitted);
        Assert.Equal("maintenance-capacity-plan-expires-before-minimum-send", result.Reason);
        Assert.Equal(15, result.MinimumPacingSeconds, precision: 6);
    }

    [Theory]
    [InlineData(1e-320)]
    [InlineData(5e-324)]
    public void Extremely_small_positive_general_rate_is_reported_as_uncomputable_without_overflow(double rate)
    {
        var budget = new PrtgRequestBudget(new TestClock());
        var plan = TestPlan(generalRate: rate);
        budget.SetAdmissionPlan(plan);

        Assert.False(budget.TryGetMinimumTablePacingWait(PrtgRequestPurpose.General, 12,
            plan.Fingerprint, out var wait));
        Assert.Equal(TimeSpan.Zero, wait);
    }

    private static PrtgCapacityAdmissionPlan TestPlan(double generalRate, int leaseSeconds = 3600)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = new string('A', 64);
        return new PrtgCapacityAdmissionPlan(hash, hash, hash, hash, hash, hash, hash, hash,
            "settings", "policy", .6, .6, generalRate, now, now.AddSeconds(leaseSeconds), "test", 1);
    }

    private sealed class TestClock : IPrtgClock
    {
        private DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        private TimeSpan elapsed;
        public DateTimeOffset UtcNow => utcNow;
        public TimeSpan Elapsed => elapsed;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Advance(delay);
            return Task.CompletedTask;
        }
        public void Advance(TimeSpan duration)
        {
            utcNow += duration;
            elapsed += duration;
        }
    }
}

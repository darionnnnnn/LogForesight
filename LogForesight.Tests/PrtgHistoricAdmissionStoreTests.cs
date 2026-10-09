using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgHistoricAdmissionStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-qual-admission-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend firstBackend;
    private readonly StorageBackend secondBackend;

    public PrtgHistoricAdmissionStoreTests()
    {
        var settings = new StorageSettings { Type = "Sqlite" };
        firstBackend = new StorageBackend(settings, root);
        secondBackend = new StorageBackend(settings, root);
    }

    [Fact]
    public void SeparateBackendInstancesShareAtomicFivePerMinuteWindow()
    {
        var first = new PrtgHistoricAdmissionStore(firstBackend);
        var second = new PrtgHistoricAdmissionStore(secondBackend);
        var now = DateTimeOffset.UtcNow;
        var outcomes = new[]
        {
            first.TryRecordSend(now, false, out _),
            second.TryRecordSend(now, false, out _),
            first.TryRecordSend(now, false, out _),
            second.TryRecordSend(now, false, out _),
            first.TryRecordSend(now, false, out _),
            second.TryRecordSend(now, false, out _)
        };

        Assert.Equal(5, outcomes.Count(value => value));
        Assert.Equal(1, outcomes.Count(value => !value));
        Assert.Equal(5, second.ReadDecision(now, false).SentInWindow);
    }

    [Fact]
    public void BackwardClockKeepsFutureTokensAndCannotGrantExtraHistoricSend()
    {
        var sendTime = DateTimeOffset.UtcNow;
        var clockNow = sendTime;
        var admission = new PrtgHistoricAdmissionStore(firstBackend, () => clockNow);
        for (var i = 0; i < 5; i++) Assert.True(admission.TryRecordSend(sendTime, false, out _));

        var earlierClock = sendTime.AddSeconds(-30);
        clockNow = earlierClock;
        Assert.False(admission.TryRecordSend(earlierClock, false, out var blocked));
        Assert.Equal(5, blocked.SentInWindow);
        Assert.Equal(5, admission.ReadDecision(earlierClock, false).SentInWindow);
        clockNow = sendTime.AddSeconds(61);
        Assert.True(admission.TryRecordSend(sendTime.AddSeconds(61), false, out _));
    }

    [Fact]
    public void MalformedFutureTokenBeyondBoundFailsClosedInsteadOfBeingTrimmed()
    {
        var sendTime = DateTimeOffset.UtcNow;
        var clockNow = sendTime;
        var admission = new PrtgHistoricAdmissionStore(firstBackend, () => clockNow);
        Assert.True(admission.TryRecordSend(sendTime, false, out _));

        clockNow = sendTime.AddMinutes(-2);
        Assert.Throws<InvalidDataException>(() => admission.TryRecordSend(sendTime.AddMinutes(-2), false, out _));
    }

    [Fact]
    public void AggregateSentAndPendingAboveFiveFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new PrtgHistoricAdmissionStore.State
        {
            Sent = Enumerable.Range(0, 4).Select(_ => new PrtgHistoricAdmissionStore.SentToken
                { SentAtUtc = now.AddSeconds(-1) }).ToList(),
            Pending = Enumerable.Range(0, 4).Select(i => new PrtgHistoricAdmissionStore.PendingToken
            {
                Id = "pending-" + i, JobId = "job-1", Owner = "owner-1", LeaseVersion = 1,
                ExpiresUtc = now.AddSeconds(20)
            }).ToList()
        };
        firstBackend.Blob(PrtgHistoricAdmissionStore.BlobKey).Mutate(_ => (JsonSerializer.Serialize(state), true));
        var admission = new PrtgHistoricAdmissionStore(secondBackend);

        Assert.Throws<InvalidDataException>(() => admission.Read());
        Assert.Throws<InvalidDataException>(() => admission.TryRecordSend(now, false, out _));
    }

    [Fact]
    public void SqlMutationResamplesClockAfterRollingWindowWaitInsteadOfUsingCallerTimestamp()
    {
        var callerTime = DateTimeOffset.UtcNow;
        var transactionTime = callerTime;
        var admission = new PrtgHistoricAdmissionStore(firstBackend, () => transactionTime);
        for (var i = 0; i < 5; i++) Assert.True(admission.TryRecordSend(callerTime, false, out _));

        transactionTime = callerTime.AddSeconds(61);
        Assert.True(admission.TryRecordSend(callerTime, false, out var decision));
        Assert.Equal(1, decision.SentInWindow);
    }

    [Fact]
    public void ReservationConsumptionRejectsLeaseExpiredWhileSqlMutationWasWaiting()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 72);
        var transactionTime = now;
        var admission = new PrtgHistoricAdmissionStore(firstBackend, () => transactionTime);
        Assert.True(admission.TryReserveQualification(job.JobId, job.Owner, job.Version, now,
            out var ticket, out _));

        transactionTime = job.LeaseUntilUtc.AddTicks(1);
        Assert.Throws<InvalidOperationException>(() => admission.ConsumeReservation(ticket, now.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => admission.TryReserveQualification(job.JobId,
            job.Owner, job.Version, now.AddSeconds(1), out _, out _));
    }

    [Fact]
    public async Task GlobalHistoricWaitDoesNotOccupyLocalInFlightPermit()
    {
        var coordinator = new BlockingCoordinator();
        var budget = new PrtgRequestBudget();
        budget.SetHistoricCoordinator(coordinator);
        var acquire = budget.AcquireAsync(PrtgEndpointCategory.HistoricData);

        await Task.Yield();
        Assert.Equal(0, budget.InFlightCount);
        coordinator.AllowAdmission();
        var lease = await acquire;
        Assert.Equal(1, budget.InFlightCount);
        lease.Dispose();
        Assert.Equal(0, budget.InFlightCount);
        Assert.True(coordinator.Released);
    }

    [Fact]
    public void ActiveJobReservesOneQualificationAndFourOtherHistoricSlots()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 72);
        var admission = new PrtgHistoricAdmissionStore(secondBackend);
        for (var i = 0; i < 4; i++) Assert.True(admission.TryRecordSend(now, false, out _));

        Assert.True(admission.TryReserveQualification(job.JobId, job.Owner, job.Version, now,
            out var ticket, out _));
        Assert.False(admission.TryRecordSend(now, false, out _));
        Assert.False(admission.TryReserveQualification(job.JobId,
            job.Owner, job.Version, now, out _, out _));
        admission.ConsumeReservation(ticket, now);

        Assert.Equal(5, admission.ReadDecision(now, false).SentInWindow);
        Assert.False(admission.TryRecordSend(now, true, out _));
    }

    [Fact]
    public void CancelReleasesPendingReservationAndClearsActiveSplitButKeepsSentTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 72);
        var admission = new PrtgHistoricAdmissionStore(firstBackend);
        Assert.True(admission.TryReserveQualification(job.JobId, job.Owner, job.Version, now,
            out var ticket, out _));

        jobs.Cancel(job.JobId, "maintainer-1", job.Version, now);

        Assert.Null(jobs.ReadOverview().ActiveQualification);
        Assert.Empty(admission.Read().Pending);
        Assert.Throws<InvalidOperationException>(() => admission.ConsumeReservation(ticket, now));
        var afterWindow = now.AddSeconds(61);
        var afterWindowAdmission = new PrtgHistoricAdmissionStore(firstBackend, () => afterWindow);
        for (var i = 0; i < 5; i++) Assert.True(afterWindowAdmission.TryRecordSend(afterWindow, false, out _));
    }

    [Fact]
    public void ExpiredWorkerLeaseCannotReserveQualificationHistoricRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 72);
        jobs.ReleaseLease(job.JobId, job.Owner, job.Version, now);

        var admission = new PrtgHistoricAdmissionStore(secondBackend);
        Assert.Throws<InvalidOperationException>(() => admission.TryReserveQualification(
            job.JobId, job.Owner, job.Version, now.AddMilliseconds(1), out _, out _));
    }

    [Fact]
    public void ReleasedHttpLeaseKeepsValidQuotaLedgerAndOnlyReacquiredOwnerCanReserveQualification()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 72);
        jobs.ReleaseLease(job.JobId, job.Owner, job.Version, now);

        var admission = new PrtgHistoricAdmissionStore(secondBackend);
        var parked = admission.Read();
        Assert.NotNull(parked.ActiveQualification);
        Assert.Equal(job.Owner, parked.ActiveQualification!.Owner);
        Assert.Equal(now, parked.ActiveQualification.LeaseUntilUtc);
        Assert.Throws<InvalidOperationException>(() => admission.TryReserveQualification(
            job.JobId, job.Owner, job.Version, now.AddSeconds(1), out _, out _));
        for (var i = 0; i < 4; i++)
        {
            Assert.True(admission.TryReserveOther(now, out var otherTicket, out _));
            admission.ConsumeReservation(otherTicket, now);
        }
        Assert.False(admission.TryReserveOther(now, out _, out _));

        Assert.True(jobs.TryAcquireLease("worker-reacquired", now.AddSeconds(1), TimeSpan.FromMinutes(3),
            out var reacquired));
        var activeJob = Assert.IsType<PrtgQualificationJobStateStore.Job>(reacquired);
        Assert.True(admission.TryReserveQualification(activeJob.JobId, activeJob.Owner, activeJob.Version,
            now.AddSeconds(1), out var ticket, out _));
        admission.ConsumeReservation(ticket, now.AddSeconds(1));
        Assert.Equal(5, admission.ReadDecision(now.AddSeconds(1), false).SentInWindow);
    }

    [Fact]
    public void RestartReadsSameDeadlineAndExpiredDeadlineRestoresUnsplitWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(firstBackend);
        var job = Start(jobs, now, duration: 1);
        var reopened = new PrtgQualificationJobStateStore(secondBackend).ReadCurrent()!;
        Assert.Equal(job.DeadlineUtc, reopened.DeadlineUtc);
        Assert.Equal(job.JobId, reopened.JobId);

        var afterDeadline = job.DeadlineUtc.AddSeconds(1);
        var admission = new PrtgHistoricAdmissionStore(secondBackend, () => afterDeadline);
        for (var i = 0; i < 5; i++) Assert.True(admission.TryRecordSend(afterDeadline, false, out _));
    }

    private static PrtgQualificationJobStateStore.Job Start(PrtgQualificationJobStateStore jobs,
        DateTimeOffset now, int duration) => jobs.Start(Guid.NewGuid().ToString("N"), "owner-a",
        new string('A', 64), "source-r1", "settings-r1", "policy-r1", new string('B', 64),
        1, 1, duration, now, 3);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class BlockingCoordinator : IPrtgHistoricRequestCoordinator
    {
        private readonly TaskCompletionSource<PrtgHistoricReservation> admission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get; private set; }
        public Task WaitAndRecordSendAsync(bool qualification, CancellationToken cancellationToken,
            string? reservationId = null) => Task.CompletedTask;
        public Task<PrtgHistoricReservation> ReserveOtherAsync(CancellationToken cancellationToken) =>
            admission.Task.WaitAsync(cancellationToken);
        public Task<PrtgHistoricReservation> ReserveQualificationAsync(string jobId, string owner,
            long leaseVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void ReleaseReservation(string reservationId) => Released = true;
        public void AllowAdmission() => admission.TrySetResult(new PrtgHistoricReservation(this, "test-ticket"));
    }
}

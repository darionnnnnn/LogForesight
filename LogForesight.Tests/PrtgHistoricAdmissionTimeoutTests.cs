using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgHistoricAdmissionTimeoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-historic-timeout-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;

    public PrtgHistoricAdmissionTimeoutTests() =>
        backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);

    [Fact]
    public async Task OtherReservationTimesOutWithoutCallerCancellationAndLeavesNoPendingTicket()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new PrtgHistoricAdmissionStore(backend);
        FillOrdinaryWindow(store, now);
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromMilliseconds(150));
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAsync<TimeoutException>(() => coordinator.ReserveOtherAsync(cancellation.Token));

        Assert.Equal("prtg-historic-admission-wait-timeout", error.Message);
        Assert.False(cancellation.IsCancellationRequested);
        var state = store.Read();
        Assert.Equal(5, state.Sent.Count);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task UnreservedSendTimesOutWithoutRecordingAnAdditionalSend()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new PrtgHistoricAdmissionStore(backend);
        FillOrdinaryWindow(store, now);
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => coordinator.WaitAndRecordSendAsync(
            qualification: false, CancellationToken.None));

        Assert.Equal("prtg-historic-admission-wait-timeout", error.Message);
        var state = store.Read();
        Assert.Equal(5, state.Sent.Count);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task QualificationReservationTimesOutWhenItsReservedLaneIsFull()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(backend);
        var job = StartJob(jobs, now);
        var store = new PrtgHistoricAdmissionStore(backend);
        Assert.True(store.TryReserveQualification(job.JobId, job.Owner, job.Version, now,
            out var occupiedTicket, out _));
        store.ConsumeReservation(occupiedTicket, now);
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => coordinator.ReserveQualificationAsync(
            job.JobId, job.Owner, job.Version, CancellationToken.None));

        Assert.Equal("prtg-historic-admission-wait-timeout", error.Message);
        var state = store.Read();
        Assert.Single(state.Sent);
        Assert.True(state.Sent[0].Qualification);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task CallerCancellationWinsOverAdmissionTimeoutAndCleansPendingReservation()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new PrtgHistoricAdmissionStore(backend);
        FillOrdinaryWindow(store, now);
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ReserveOtherAsync(cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(5, store.Read().Sent.Count);
        Assert.Empty(store.Read().Pending);
    }

    [Fact]
    public async Task CancelledCallerDoesNotConsumeAnAlreadyIssuedReservation()
    {
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromSeconds(5));
        var store = new PrtgHistoricAdmissionStore(backend);
        using var reservation = await coordinator.ReserveOtherAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.WaitAndRecordSendAsync(
            qualification: false, cancellation.Token, reservation.Id));

        coordinator.ReleaseReservation(reservation.Id);
        var state = store.Read();
        Assert.Empty(state.Sent);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task ValidQualificationAndOrdinaryReservationsStillConsumeNormally()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new PrtgQualificationJobStateStore(backend);
        var job = StartJob(jobs, now);
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromSeconds(2));
        var store = new PrtgHistoricAdmissionStore(backend);

        using (var qualification = await coordinator.ReserveQualificationAsync(
                   job.JobId, job.Owner, job.Version, CancellationToken.None))
            await coordinator.WaitAndRecordSendAsync(false, CancellationToken.None, qualification.Id);
        using (var other = await coordinator.ReserveOtherAsync(CancellationToken.None))
            await coordinator.WaitAndRecordSendAsync(false, CancellationToken.None, other.Id);

        var state = store.Read();
        Assert.Equal(2, state.Sent.Count);
        Assert.Single(state.Sent, token => token.Qualification);
        Assert.Single(state.Sent, token => !token.Qualification);
        Assert.Empty(state.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineAfterSuccessfulSqlReservationReleasesTheFreshUnsentTicket(bool qualification)
    {
        var now = DateTimeOffset.UtcNow;
        var job = qualification ? StartJob(new PrtgQualificationJobStateStore(backend), now) : null;
        var coordinator = new SqlPrtgHistoricRequestCoordinator(backend, TimeSpan.FromTicks(1));
        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            using var reservation = qualification
                ? await coordinator.ReserveQualificationAsync(job!.JobId, job.Owner, job.Version, CancellationToken.None)
                : await coordinator.ReserveOtherAsync(CancellationToken.None);
        });
        Assert.Equal("prtg-historic-admission-wait-timeout", error.Message);
        var state = new PrtgHistoricAdmissionStore(backend).Read();
        Assert.Empty(state.Sent);
        Assert.Empty(state.Pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    public void InternalWaitOverrideMustBePositiveAndNoLongerThanTwoMinutes(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlPrtgHistoricRequestCoordinator(
            backend, TimeSpan.FromSeconds(seconds)));
    }

    private static void FillOrdinaryWindow(PrtgHistoricAdmissionStore store, DateTimeOffset now)
    {
        for (var i = 0; i < PrtgHistoricAdmissionStore.WindowCapacity; i++)
            Assert.True(store.TryRecordSend(now, qualification: false, out _));
    }

    private static PrtgQualificationJobStateStore.Job StartJob(PrtgQualificationJobStateStore jobs,
        DateTimeOffset now) => jobs.Start(Guid.NewGuid().ToString("N"), "owner-a", new string('A', 64),
        "source-r1", "settings-r1", "policy-r1", new string('B', 64), 1, 1, 1, now, 3);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

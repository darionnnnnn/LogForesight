using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Small SQLite contract fixtures for durable scope-purge boundaries and replay.</summary>
public sealed class PrtgScopePurgeStoreTests : IDisposable
{
    private sealed class TestConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-scope-purge-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly PrtgScopePurgeStore _jobs;
    private readonly EfPrtgStore _prtg;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "scope-generation-1";
    private static readonly HashSet<long> Keep = new() { 1 };

    public PrtgScopePurgeStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _jobs = _backend.PrtgStore().ScopePurgeJobs();
        _prtg = _backend.PrtgStore();
        _prtg.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "kept", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "purged", Ip = "10.0.0.2" }
        }, Now.LocalDateTime);
        _prtg.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 11, DeviceObjid = 1, Name = "kept-sensor" },
            new PrtgSensorRow { Objid = 22, DeviceObjid = 2, Name = "purged-sensor" }
        }, Now.LocalDateTime);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void SeedRows(int values = 2, int states = 1)
    {
        using var context = _backend.CreateContext();
        for (var i = 0; i < values; i++)
            context.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = i == 0 ? 11 : 22,
                PeriodStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).AddHours(i),
                AvgValue = i, Quality = "good", CreatedAt = Now.LocalDateTime
            });
        for (var i = 0; i < states; i++)
            context.PrtgStateChanges.Add(new PrtgStateChangeRow
            {
                SensorObjid = 22, ChangedAt = Now.LocalDateTime.AddMinutes(i), Status = "down",
                Quality = "good", CreatedAt = Now.LocalDateTime
            });
        context.SaveChanges();
    }

    private PrtgScopePurgeJobRow Preview(long actor = 7, long permissionRevision = 3, string? authFingerprint = null,
        DateTimeOffset? now = null) =>
        _jobs.CreatePreview(actor, permissionRevision, authFingerprint, Fingerprint, Keep, 1, now ?? Now);

    private PrtgScopePurgeJobRow Accept(PrtgScopePurgeJobRow preview, long actor = 7, long permissionRevision = 3,
        string? authFingerprint = null, DateTimeOffset? now = null) =>
        _jobs.Accept(preview.JobId, actor, permissionRevision, authFingerprint, Fingerprint, now ?? Now.AddMinutes(1));

    [Fact]
    public void Preview_accept_batch_commit_unknown_retry_freezes_candidate_watermark()
    {
        SeedRows();
        var preview = Preview();
        Assert.Equal(1, preview.CandidateValues);
        Assert.Equal(1, preview.CandidateStateChanges);
        var accepted = Accept(preview);

        // This value is after the durable acceptance watermark and must remain outside this job.
        using (var context = _backend.CreateContext())
        {
            context.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 22, PeriodStart = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Local),
                AvgValue = 99, Quality = "good", CreatedAt = Now.AddMinutes(2).LocalDateTime
            });
            context.SaveChanges();
        }

        var committed = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        Assert.Equal("complete", committed.Status);
        Assert.Equal(1, committed.DeletedValues);
        Assert.Equal(1, committed.DeletedStateChanges);
        Assert.Equal(0, committed.RemainingValues);
        Assert.Equal(0, committed.RemainingStateChanges);

        // Model a commit whose acknowledgement was lost: the original revision is an idempotent replay key.
        var replay = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(3));
        Assert.Equal(committed.Revision, replay.Revision);
        Assert.Equal(committed.DeletedValues, replay.DeletedValues);
        Assert.Equal(committed.DeletedStateChanges, replay.DeletedStateChanges);
        using var verify = _backend.CreateContext();
        Assert.Equal(new long[] { 11, 22 }, verify.PrtgValues.OrderBy(x => x.Id).Select(x => x.SensorObjid).ToArray());
        Assert.Empty(verify.PrtgStateChanges);
    }

    [Fact]
    public void Preview_details_project_the_frozen_candidate_set_and_filter_visibility_before_top_twenty()
    {
        SeedRows();
        var preview = Preview();
        var boundary = _jobs.GetPreviewBoundary(preview.JobId, actorId: 7);
        Assert.Equal(new long[] { 22 }, boundary.CandidateSensorIds.OrderBy(id => id));

        // A new source sensor after preview must not leak into that preview's detail projection.
        _prtg.UpsertSensors(new[] { new PrtgSensorRow { Objid = 33, DeviceObjid = 2, Name = "late-sensor" } }, Now.AddMinutes(1).LocalDateTime);
        var details = _prtg.PreviewOutOfScopeCandidateDetails(boundary.CandidateSensorIds,
            topDevices: 20, visibleDeviceObjids: new HashSet<long>());

        Assert.Equal(1, details.AffectedDevices);
        Assert.Equal(0, details.VisibleAffectedDevices);
        Assert.Empty(details.TopDeviceNames);
        Assert.Equal(0, details.UnknownSensors);
    }

    [Fact]
    public void Candidate_removed_by_other_retention_is_counted_separately()
    {
        SeedRows();
        var preview = Preview();
        var accepted = Accept(preview);
        using (var context = _backend.CreateContext())
        {
            var candidate = context.PrtgValues.Single(x => x.SensorObjid == 22);
            context.PrtgValues.Remove(candidate);
            context.SaveChanges();
        }

        var next = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        var complete = next.Status == "complete" ? next : _jobs.RunBatch(next.JobId, next.Revision, Fingerprint, Now.AddMinutes(3));
        Assert.Equal("complete", complete.Status);
        Assert.Equal(0, complete.DeletedValues);
        Assert.Equal(1, complete.OtherwiseRemovedValues);
        Assert.Equal(0, complete.RemainingValues);
        Assert.Equal(1, complete.DeletedStateChanges);
    }

    [Fact]
    public void Scope_change_stops_before_any_delete_and_mutex_rejects_second_entrance()
    {
        SeedRows();
        var first = Accept(Preview());
        var second = Preview();
        Assert.Throws<InvalidOperationException>(() => Accept(second));

        var stopped = _jobs.RunBatch(first.JobId, first.Revision, "changed-generation", Now.AddMinutes(2));
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("scope-changed", stopped.StopReason);
        using (var context = _backend.CreateContext())
        {
            Assert.Equal(2, context.PrtgValues.Count());
            Assert.Single(context.PrtgStateChanges);
        }

        // The safely stopped job released its slot so a fresh preview can be accepted.
        Assert.Equal("accepted", Accept(second).Status);
    }

    [Fact]
    public void Automatic_parent_and_manual_confirmation_share_one_durable_slot()
    {
        SeedRows();
        var automatic = _jobs.CreateAutomatic("nightly-parent-42", Fingerprint, Keep, 1, Now);
        var replay = _jobs.CreateAutomatic("nightly-parent-42", Fingerprint, Keep, 1, Now.AddMinutes(1));
        Assert.Equal(automatic.JobId, replay.JobId);
        Assert.Throws<InvalidOperationException>(() => Accept(Preview()));
        var completed = _jobs.RunBatch(automatic.JobId, automatic.Revision, Fingerprint, Now.AddMinutes(2),
            ownershipCheck: () => true);
        Assert.Equal("complete", completed.Status);
        Assert.Equal("accepted", Accept(Preview()).Status);
    }

    [Fact]
    public void Automatic_batch_without_parent_verifier_pauses_without_deleting()
    {
        SeedRows();
        var automatic = _jobs.CreateAutomatic("nightly-parent-unverifiable", Fingerprint, Keep, 1, Now);

        var paused = _jobs.RunBatch(automatic.JobId, automatic.Revision, Fingerprint, Now.AddMinutes(1));

        Assert.Equal("paused", paused.Status);
        Assert.Equal("parent-ownership-unverifiable", paused.StopReason);
        Assert.Equal(0, paused.DeletedValues);
        using var context = _backend.CreateContext();
        Assert.Equal(2, context.PrtgValues.Count());
    }

    [Fact]
    public void Parent_ownership_is_rechecked_inside_batch_transaction_before_delete()
    {
        SeedRows(values: 2, states: 1);
        var automatic = _jobs.CreateAutomatic("nightly-parent-cancel-race", Fingerprint, Keep, 1, Now);

        var stopped = _jobs.RunBatch(automatic.JobId, automatic.Revision, Fingerprint, Now.AddMinutes(1),
            ownershipCheck: () => false);

        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("parent-ownership-lost", stopped.StopReason);
        Assert.Equal(0, stopped.DeletedValues);
        using var verify = _backend.CreateContext();
        Assert.Equal(2, verify.PrtgValues.Count());
        Assert.Single(verify.PrtgStateChanges);
    }

    [Fact]
    public void Preview_is_bound_to_actor_permission_revision_and_five_minute_expiry()
    {
        SeedRows();
        var wrongActor = Preview();
        Assert.Throws<InvalidOperationException>(() => Accept(wrongActor, actor: 8));
        Assert.Throws<InvalidOperationException>(() => Accept(wrongActor, permissionRevision: 4));
        Assert.Throws<InvalidOperationException>(() => _jobs.Accept(wrongActor.JobId, 7, 3, null, Fingerprint, Now.AddMinutes(6)));

        var fresh = Preview();
        Assert.Equal("accepted", Accept(fresh).Status);
    }

    [Fact]
    public void Accepted_preview_retry_after_expiry_recovers_same_job_after_unknown_response()
    {
        var preview = Preview();
        var accepted = _jobs.Accept(preview.JobId, 7, 3, null, Fingerprint, Now.AddMinutes(4));

        // The first confirm committed, but its response was lost. A retry beyond preview expiry
        // must resolve to that accepted job; expiry only rejects a first acceptance.
        var recovered = _jobs.Accept(preview.JobId, 7, 3, null, Fingerprint, Now.AddMinutes(8));

        Assert.Equal(accepted.JobId, recovered.JobId);
        Assert.Equal("accepted", recovered.Status);
        Assert.Equal(accepted.Revision, recovered.Revision);
    }

    [Fact]
    public void Scope_writers_are_fenced_while_purge_owns_slot_and_scope_revision_drift_stops_next_batch()
    {
        SeedRows(values: 2, states: 1);
        var computedFence = _jobs.CaptureScopeFenceJson();
        using (var context = _backend.CreateContext())
        {
            var revision = context.Blobs.Single(x => x.BlobKey == EfPrtgStore.CatalogueDataRevisionBlobKey);
            revision.Version++;
            context.SaveChanges();
        }
        Assert.Throws<InvalidOperationException>(() => _jobs.CreatePreview(7, 3, null, Fingerprint,
            Keep, 1, Now, expectedScopeFenceJson: computedFence));

        var stalePreview = Preview();
        using (var context = _backend.CreateContext())
        {
            var revision = context.Blobs.SingleOrDefault(x => x.BlobKey == EfPrtgStore.CatalogueDataRevisionBlobKey);
            Assert.NotNull(revision);
            revision!.Version++;
            context.SaveChanges();
        }
        Assert.Throws<InvalidOperationException>(() => Accept(stalePreview));

        var now = DateTimeOffset.UtcNow;
        var preview = _jobs.CreatePreview(7, 3, null, Fingerprint, Keep, 1, now);
        var accepted = _jobs.Accept(preview.JobId, 7, 3, null, Fingerprint, now.AddMinutes(1));

        // The host-map writer's slot check and mutation share one serializable transaction.
        Assert.Throws<InvalidOperationException>(() => _prtg.ReplaceHostMapForDate(DateTime.Today,
            new[] { new PrtgHostMapRow { DeviceObjid = 2, HostId = 77, HostName = "changed" } }));
        Assert.Throws<InvalidOperationException>(() => _prtg.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 2, HostId = 77, Note = "changed"
        }));
        var hosts = new HostStore(_backend.Blob("hosts"));
        Assert.Throws<InvalidOperationException>(() => hosts.Upsert(new WebHost { HostName = "scope-race" }));

        // Model a scope source committed by an older/out-of-process writer that did not use the slot guard.
        // The per-batch version read and delete share one transaction, so no newly unsafe candidate is deleted.
        using (var context = _backend.CreateContext())
        {
            var revision = context.Blobs.SingleOrDefault(x => x.BlobKey == EfPrtgStore.CatalogueDataRevisionBlobKey);
            if (revision == null)
                context.Blobs.Add(new BlobRow { BlobKey = EfPrtgStore.CatalogueDataRevisionBlobKey, Content = "{}", Version = 1 });
            else revision.Version++;
            context.SaveChanges();
        }

        var stopped = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("scope-revision-changed", stopped.StopReason);
        Assert.Equal(0, stopped.DeletedValues);
        using var verify = _backend.CreateContext();
        Assert.Equal(2, verify.PrtgValues.Count());
    }

    [Fact]
    public void Stopped_partial_counts_and_link_survive_store_restart_and_new_preview()
    {
        SeedRows(values: 252, states: 0);
        var firstPreview = Preview();
        Assert.Equal(251, firstPreview.CandidateValues);
        var accepted = Accept(firstPreview);
        var firstBatch = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        Assert.Equal("running", firstBatch.Status);
        Assert.Equal(249, firstBatch.DeletedValues);
        Assert.Equal(2, firstBatch.RemainingValues);

        var stopped = _jobs.RunBatch(firstBatch.JobId, firstBatch.Revision, "new-scope-generation", Now.AddMinutes(3));
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal(249, stopped.DeletedValues);
        Assert.Equal(2, stopped.RemainingValues);

        // A new store object models a restarted Web process reading the durable database.
        var restartedJobs = _backend.PrtgStore().ScopePurgeJobs();
        var recoveredPartial = Assert.IsType<PrtgScopePurgeJobRow>(restartedJobs.GetLatestStoppedPartial(actorId: 7));
        Assert.Equal(stopped.JobId, recoveredPartial.JobId);
        Assert.Equal(249, recoveredPartial.DeletedValues);
        Assert.Equal(2, recoveredPartial.RemainingValues);
        Assert.Null(restartedJobs.GetLatestStoppedPartial(actorId: 8));

        var nextPreview = restartedJobs.CreatePreview(7, 3, null, "new-scope-generation", Keep,
            1, Now.AddMinutes(4), recoveredPartial.JobId);
        var durableLink = restartedJobs.Get(nextPreview.JobId);
        Assert.Equal(stopped.JobId, durableLink!.PreviousJobId);
    }

    [Fact]
    public void Each_batch_uses_monotonic_estimate_and_reconciles_other_retention_only_at_safe_stop()
    {
        SeedRows(values: 252, states: 0);
        var preview = Preview();
        Assert.Equal(251, preview.CandidateValues);
        var accepted = Accept(preview);

        // Simulate an independent retention deleting a row from the frozen set between pages.
        using (var context = _backend.CreateContext())
        {
            var externallyRemoved = context.PrtgValues.OrderBy(x => x.Id).Skip(249).First();
            context.PrtgValues.Remove(externallyRemoved);
            context.SaveChanges();
        }

        var batch = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2));
        Assert.Equal("running", batch.Status);
        Assert.False(batch.CountsReconciled);
        Assert.Equal(249, batch.DeletedValues);
        Assert.Equal(2, batch.RemainingValues); // Explicitly an estimate while another retention may be concurrent.

        var stopped = _jobs.RunBatch(batch.JobId, batch.Revision, "changed-scope", Now.AddMinutes(3));
        Assert.Equal("stopped", stopped.Status);
        Assert.True(stopped.CountsReconciled);
        Assert.Equal(1, stopped.OtherwiseRemovedValues);
        Assert.Equal(1, stopped.RemainingValues);
        Assert.Equal(249, stopped.DeletedValues);
    }

    [Fact]
    public void Automatic_parent_ownership_loss_pauses_after_committed_batch()
    {
        // Keep the 251-row fixture below the development limit while making every row a candidate.
        _prtg.UpsertSensors(new[] { new PrtgSensorRow { Objid = 11, DeviceObjid = 2, Name = "purged-sensor" } }, Now.LocalDateTime);
        SeedRows(values: 251, states: 0);
        _prtg.ScopeBaseline().Update(b => { b.DeviceCount = 1; b.At = Now.LocalDateTime; });
        var scope = new PrtgScopeResult(new HashSet<long> { 1 }, 1, 0, 0, 0)
        {
            SourceGeneration = "verified-generation",
            ScopeFenceJson = _jobs.CaptureScopeFenceJson()
        };
        var ownershipChecks = 0;
        var console = new TestConsole();

        var completed = PrtgScopePurge.RunAfterStructureSync(_prtg, scope, devicesRefreshed: true,
            verifiedParentJobId: "verified-parent-77", console: console, cancellationToken: CancellationToken.None,
            parentOwnsRun: () => ++ownershipChecks <= 3);

        Assert.False(completed);
        using var context = _backend.CreateContext();
        var job = context.PrtgScopePurgeJobs.Single(x => x.ParentJobId == "verified-parent-77");
        Assert.Equal("paused", job!.Status);
        Assert.Equal("parent-ownership-lost", job.StopReason);
        Assert.Equal(250, job.DeletedValues);
        Assert.Equal(1, job.RemainingValues);
        Assert.Null(job.ActiveOwner);
    }

    [Fact]
    public void Expired_owner_lease_pauses_and_releases_slot_without_deleting()
    {
        SeedRows();
        var first = Accept(Preview());
        Assert.True(_jobs.HasActiveOwner(Now.AddMinutes(5).AddSeconds(59)));

        Assert.False(_jobs.HasActiveOwner(Now.AddMinutes(6)));
        var expired = _jobs.Get(first.JobId)!;
        Assert.Equal("paused", expired.Status);
        Assert.Equal("owner-lease-expired", expired.StopReason);
        Assert.Null(expired.ActiveOwner);
        Assert.Null(expired.LeaseExpiresAtUtc);
        Assert.Equal(0, expired.DeletedValues + expired.DeletedStateChanges);
        using (var source = _backend.CreateContext()) Assert.Equal(2, source.PrtgValues.Count());

        var competingPreview = Preview(now: Now.AddMinutes(6));
        var second = Accept(competingPreview, now: Now.AddMinutes(6).AddSeconds(1));
        Assert.Equal("accepted", second.Status);
        Assert.Equal(second.JobId, _jobs.Get(second.JobId)!.JobId);
    }

    [Fact]
    public void Purge_counts_use_sensor_id_seek_index()
    {
        SeedRows();
        using var context = _backend.CreateContext();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT COUNT(*) FROM lf_prtg_values WHERE sensor_objid IN (22) AND id <= 1000";
        if (command.Connection!.State != System.Data.ConnectionState.Open) command.Connection.Open();
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("IX_lf_prtg_values_purge_sensor_id", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("SCAN lf_prtg_values", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Server_admin_fingerprint_is_bound_and_cancellation_before_batch_keeps_acceptance_intact()
    {
        SeedRows();
        var preview = Preview(actor: 0, authFingerprint: "admin-config-1");
        Assert.Throws<InvalidOperationException>(() => Accept(preview, actor: 0, authFingerprint: "admin-config-2"));
        var accepted = Accept(preview, actor: 0, authFingerprint: "admin-config-1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => _jobs.RunBatch(accepted.JobId, accepted.Revision,
            Fingerprint, Now.AddMinutes(2), cancelled.Token));
        var stillAccepted = _jobs.Get(accepted.JobId)!;
        Assert.Equal(accepted.Revision, stillAccepted.Revision);
        Assert.Equal("accepted", stillAccepted.Status);
        var resumed = _jobs.RunBatch(stillAccepted.JobId, stillAccepted.Revision, Fingerprint, Now.AddMinutes(3));
        Assert.Equal("complete", resumed.Status);
    }

    [Fact]
    public void Live_authorization_reloads_active_user_group_and_persistent_permission_revision()
    {
        var users = new UserStore(_backend.Blob("purge-auth-users"), _backend.Blob("purge-auth-login"));
        var groups = new UserGroupStore(_backend.Blob("purge-auth-groups"));
        var adminGroup = groups.Upsert(new UserGroup { GroupName = "purge-admin", Role = UserRole.Admin, Active = true });
        var user = users.Upsert(new WebUser { Account = "purge-admin", DisplayName = "Purge Admin", Active = true });
        users.SetGroups(user.UserId, new[] { adminGroup.GroupId });
        var identity = FakeCurrentUser.ForUser(user.UserId); // Deliberately carries no Maintain claim.
        var authorization = new PrtgScopePurgeAuthorizationService(users,
            new UserCapabilityResolver(groups, new HostStore(_backend.Blob("purge-auth-hosts"))),
            identity, new WebAppSettings(), _backend);
        var permissionWriter = new PermissionVersionStamp(_backend.Blob(PermissionVersionStamp.BlobKey));

        var initial = authorization.Check(user.UserId);
        Assert.True(initial.Allowed);
        Assert.Contains(Capability.Maintain, authorization.CurrentCapabilities());
        permissionWriter.Bump(); // Separate stamp instance models a permission change from another process.
        Assert.Equal(initial.PermissionRevision + 1, authorization.Check(user.UserId).PermissionRevision);

        groups.Upsert(new UserGroup { GroupId = adminGroup.GroupId, GroupName = adminGroup.GroupName, Role = UserRole.Admin, Active = false });
        Assert.False(authorization.Check(user.UserId).Allowed);
        users.Upsert(new WebUser { UserId = user.UserId, Account = user.Account, DisplayName = user.DisplayName, Active = false });
        Assert.False(authorization.Check(user.UserId).Allowed);
    }

    [Fact]
    public void Permission_change_after_http_check_is_fenced_inside_batch_transaction_before_delete()
    {
        SeedRows();
        var users = new UserStore(_backend.Blob("users"), _backend.Blob("user_last_login"));
        var groups = new UserGroupStore(_backend.Blob("user_groups"));
        var adminGroup = groups.Upsert(new UserGroup { GroupName = "purge-batch-admin", Role = UserRole.Admin, Active = true });
        var user = users.Upsert(new WebUser { Account = "purge-batch-admin", DisplayName = "Batch Admin", Active = true });
        users.SetGroups(user.UserId, new[] { adminGroup.GroupId });
        var authorization = new PrtgScopePurgeAuthorizationService(users,
            new UserCapabilityResolver(groups, new HostStore(_backend.Blob("purge-batch-hosts"))),
            FakeCurrentUser.ForUser(user.UserId), new WebAppSettings(), _backend);
        var checkedAuthorization = authorization.Check(user.UserId);
        Assert.True(checkedAuthorization.Allowed);

        var preview = _jobs.CreatePreview(user.UserId, checkedAuthorization.PermissionRevision,
            checkedAuthorization.AuthFingerprint, Fingerprint, Keep, 1, Now);
        var accepted = _jobs.Accept(preview.JobId, user.UserId, checkedAuthorization.PermissionRevision,
            checkedAuthorization.AuthFingerprint, Fingerprint, Now.AddMinutes(1));

        // Simulate permission revocation after the controller check and before the page transaction.
        groups.Upsert(new UserGroup { GroupId = adminGroup.GroupId, GroupName = adminGroup.GroupName, Role = UserRole.Admin, Active = false });
        new PermissionVersionStamp(_backend.Blob(PermissionVersionStamp.BlobKey)).Bump();
        var fenced = _jobs.RunBatch(accepted.JobId, accepted.Revision, Fingerprint, Now.AddMinutes(2),
            authorizationFence: checkedAuthorization.ToBatchFence());

        Assert.Equal("paused", fenced.Status);
        Assert.Equal("authorization-changed", fenced.StopReason);
        Assert.Equal(0, fenced.DeletedValues + fenced.DeletedStateChanges);
        Assert.True(fenced.CountsReconciled);
        using var source = _backend.CreateContext();
        Assert.Equal(2, source.PrtgValues.Count());
        Assert.Equal(1, source.PrtgStateChanges.Count());
    }
}

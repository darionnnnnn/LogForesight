using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourceIdentityStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    [Fact]
    public void ManualOnlyHostMapping_HostIdentityAtoBtoAInvalidatesOnlyItsSensor()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var hosts = new HostStore(_fx.Blob("hosts"));
        hosts.Upsert(new WebHost { HostName = "host-a", IpAddress = "10.0.0.1", Source = "netiq" });
        hosts.Upsert(new WebHost { HostName = "host-b", IpAddress = "10.0.0.2", Source = "netiq" });
        var hostA = hosts.FindByName("host-a")!;
        var hostB = hosts.FindByName("host-b")!;
        new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(p =>
            {
                p.SourceGeneration = "source-v1";
                p.HostIds = [hostA.HostId, hostB.HostId];
                p.SensorIds = [10001, 20002];
            });

        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = 10001, DeviceObjid = 101, SensorType = "ping", Category = "availability" },
                new PrtgSensorRow { Objid = 20002, DeviceObjid = 202, SensorType = "ping", Category = "availability" });
            ctx.PrtgManualMaps.AddRange(
                new PrtgManualMapRow { DeviceObjid = 101, HostId = hostA.HostId },
                new PrtgManualMapRow { DeviceObjid = 202, HostId = hostB.HostId });
            PrtgResourceIdentityStore.Set(ctx, 10001, "source-v1", 101, hostA.HostId,
                "resource-a", "ping|availability|auto", "channel-a", true, DateTimeOffset.UtcNow);
            PrtgResourceIdentityStore.Set(ctx, 20002, "source-v1", 202, hostB.HostId,
                "resource-b", "ping|availability|auto", "channel-b", true, DateTimeOffset.UtcNow);
            ctx.SaveChanges();
        }

        Assert.Empty(store.GetLatestHostMapWithDate().Rows);
        Assert.Equal(new long[] { 10001 }, store.GetSensorIdsMappedToHosts([hostA.HostId]));
        bool IsCurrent(long sensorId, string generation, long deviceId, long hostId) =>
            new PrtgResourceIdentityStore(_fx.Blob(PrtgResourceIdentityStore.Prefix + sensorId))
                .IsCurrent(sensorId, "source-v1", generation, deviceId, hostId);
        var originalA = store.GetResourceIdentity(10001);
        var originalB = store.GetResourceIdentity(20002);
        Assert.True(IsCurrent(10001, originalA.Generation, 101, hostA.HostId));

        hosts.Upsert(new WebHost { HostName = "host-a", IpAddress = "10.0.0.9", Source = "netiq" });
        var changedToB = store.GetResourceIdentity(10001);
        var unrelatedAfterB = store.GetResourceIdentity(20002);
        Assert.True(changedToB.PendingReconciliation);
        Assert.True(changedToB.Epoch > originalA.Epoch);
        Assert.False(IsCurrent(10001, originalA.Generation, 101, hostA.HostId));
        Assert.Equal(originalB.Epoch, unrelatedAfterB.Epoch);
        Assert.Equal(originalB.Generation, unrelatedAfterB.Generation);

        // 僅重整鏡像不會清除 pending；必須以最新來源身分完成重新確認。
        store.ReplaceHostMapForDate(DateTime.Today, Array.Empty<PrtgHostMapRow>());
        var reconciled = store.GetResourceIdentity(10001);
        Assert.True(reconciled.PendingReconciliation);
        var confirmedA = store.BindObservedResource(10001, hostA.HostId, "source-v1", "resource-a-after-ip-change");
        Assert.False(confirmedA.PendingReconciliation);
        Assert.Equal(reconciled.Epoch, confirmedA.Epoch);
        Assert.True(IsCurrent(10001, confirmedA.Generation, 101, hostA.HostId));

        hosts.Upsert(new WebHost { HostName = "host-a", IpAddress = "10.0.0.1", Source = "netiq" });
        var changedBackToA = store.GetResourceIdentity(10001);
        Assert.True(changedBackToA.PendingReconciliation);
        Assert.True(changedBackToA.Epoch > confirmedA.Epoch);
        Assert.False(IsCurrent(10001, confirmedA.Generation, 101, hostA.HostId));
        store.MarkResourceEpochsPendingForHosts([hostA.HostId]);
        Assert.True(store.GetResourceIdentity(10001).Epoch > changedBackToA.Epoch);
        var unrelatedAfterAba = store.GetResourceIdentity(20002);
        Assert.Equal(originalB.Epoch, unrelatedAfterAba.Epoch);
        Assert.Equal(originalB.Generation, unrelatedAfterAba.Generation);
    }

    [Fact]
    public void ResourceIdentityLedger_OverEightKiB_FailsClosedOnReadAndWrite()
    {
        const long sensorId = 90001;
        var blob = _fx.Blob(PrtgResourceIdentityStore.Prefix + sensorId);
        blob.Mutate(_ => (new string('x', PrtgResourceIdentityStore.MaxLedgerBytes + 1), true));

        Assert.Throws<InvalidDataException>(() => new PrtgResourceIdentityStore(blob).Get(sensorId));
        Assert.Throws<InvalidDataException>(() => new EfPrtgStore(_fx.NewContext).GetResourceIdentity(sensorId));
        using var ctx = _fx.NewContext();
        Assert.Throws<InvalidDataException>(() => PrtgResourceIdentityStore.Set(ctx, sensorId, "source-v1",
            1, 1, new string('r', PrtgResourceIdentityStore.MaxLedgerBytes), "inventory", "channel", true,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HostAuthorityRevision_BaselineIsIdempotentAndTrackedIncrementsReuseOneRow()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        store.EnsureResourceAuthorityRevisions([31, 31]);
        Assert.Equal(1, store.ReadResourceAuthorityRevision(31));

        using (var ctx = _fx.NewContext())
        {
            var loadedRows = PrtgResourceIdentityStore.CaptureLoadedAuthorityRevisionRows(ctx);
            PrtgResourceIdentityStore.LoadAuthorityRevisionRows(ctx, [31, 32], loadedRows);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, 31, DateTimeOffset.UtcNow, loadedRows);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, 31, DateTimeOffset.UtcNow, loadedRows);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, 32, DateTimeOffset.UtcNow, loadedRows);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, 32, DateTimeOffset.UtcNow, loadedRows);
            ctx.SaveChanges();
        }

        Assert.Equal(3, store.ReadResourceAuthorityRevision(31));
        Assert.Equal(3, store.ReadResourceObservationRevisions([31])[31]);
        Assert.Equal(2, store.ReadResourceAuthorityRevision(32));
        Assert.Equal(2, store.ReadResourceObservationRevisions([32])[32]);
        using var verify = _fx.NewContext();
        Assert.Equal(1, verify.Blobs.Count(blob => blob.BlobKey == PrtgResourceIdentityStore.AuthorityRevisionKey(31)));
        Assert.Equal(1, verify.Blobs.Count(blob => blob.BlobKey == PrtgResourceIdentityStore.AuthorityRevisionKey(32)));
    }

    [Fact]
    public void BulkHostMapRefresh_ChangesOldAndNewHostAndTombstonesRemovedResourceWithoutRotatingUnchangedHost()
    {
        const string sourceGeneration = "bulk-map-revision-source-v1";
        const long deviceA = 7101, deviceRemoved = 7102, deviceUnchanged = 7103;
        const long sensorA = 71001, sensorRemoved = 71002, sensorUnchanged = 71003;
        const long hostA = 71, hostRemoved = 72, hostUnchanged = 73, hostB = 81;
        var mapDate = DateTime.Today;
        new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.SourceGeneration = sourceGeneration);
        using (var seed = _fx.NewContext())
        {
            seed.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = deviceA, Name = "device-a" },
                new PrtgDeviceRow { Objid = deviceRemoved, Name = "device-removed" },
                new PrtgDeviceRow { Objid = deviceUnchanged, Name = "device-unchanged" });
            seed.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = sensorA, DeviceObjid = deviceA, SensorType = "ping", Category = "availability" },
                new PrtgSensorRow { Objid = sensorRemoved, DeviceObjid = deviceRemoved, SensorType = "ping", Category = "availability" },
                new PrtgSensorRow { Objid = sensorUnchanged, DeviceObjid = deviceUnchanged, SensorType = "ping", Category = "availability" });
            seed.SaveChanges();
        }

        var store = new EfPrtgStore(_fx.NewContext);
        store.ReplaceHostMapForDate(mapDate,
        [
            new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceA, HostId = hostA, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceRemoved, HostId = hostRemoved, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceUnchanged, HostId = hostUnchanged, MapStatus = PrtgMapStatus.Ok }
        ]);
        var beforeA = store.GetResourceIdentity(sensorA);
        var beforeRemoved = store.GetResourceIdentity(sensorRemoved);
        var beforeUnchanged = store.GetResourceIdentity(sensorUnchanged);
        var authorityBefore = store.ReadResourceAuthorityRevisions([hostA, hostRemoved, hostUnchanged, hostB]);
        var observationBefore = store.ReadResourceObservationRevisions([hostA, hostRemoved, hostUnchanged, hostB]);
        var hostMapRevisionBefore = store.ReadHostMapDataRevision();

        store.ReplaceHostMapForDate(mapDate,
        [
            new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceA, HostId = hostB, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceUnchanged, HostId = hostUnchanged, MapStatus = PrtgMapStatus.Ok }
        ]);

        var afterA = store.GetResourceIdentity(sensorA);
        var afterRemoved = store.GetResourceIdentity(sensorRemoved);
        var afterUnchanged = store.GetResourceIdentity(sensorUnchanged);
        Assert.Equal(hostB, afterA.HostId);
        Assert.True(afterA.Active);
        Assert.Equal(beforeA.Epoch + 1, afterA.Epoch);
        Assert.NotEqual(beforeA.Generation, afterA.Generation);
        Assert.Equal(0, afterRemoved.HostId);
        Assert.False(afterRemoved.Active);
        Assert.Equal(beforeRemoved.Epoch + 1, afterRemoved.Epoch);
        Assert.NotEqual(beforeRemoved.Generation, afterRemoved.Generation);
        Assert.Equal(beforeUnchanged.Epoch, afterUnchanged.Epoch);
        Assert.Equal(beforeUnchanged.Generation, afterUnchanged.Generation);

        var authorityAfter = store.ReadResourceAuthorityRevisions([hostA, hostRemoved, hostUnchanged, hostB]);
        var observationAfter = store.ReadResourceObservationRevisions([hostA, hostRemoved, hostUnchanged, hostB]);
        Assert.Equal(authorityBefore[hostA] + 1, authorityAfter[hostA]);
        Assert.Equal(authorityBefore[hostRemoved] + 1, authorityAfter[hostRemoved]);
        Assert.Equal(authorityBefore[hostB] + 1, authorityAfter[hostB]);
        Assert.Equal(authorityBefore[hostUnchanged], authorityAfter[hostUnchanged]);
        Assert.Equal(observationBefore[hostA] + 1, observationAfter[hostA]);
        Assert.Equal(observationBefore[hostRemoved] + 1, observationAfter[hostRemoved]);
        Assert.Equal(observationBefore[hostB] + 1, observationAfter[hostB]);
        Assert.Equal(observationBefore[hostUnchanged], observationAfter[hostUnchanged]);
        Assert.Equal(hostMapRevisionBefore + 1, store.ReadHostMapDataRevision());
    }

    [Fact]
    public void BulkHostMapRefresh_FailedRevisionWriteRollsBackMappingIdentityAndBothHostRevisions()
    {
        const string sourceGeneration = "bulk-map-rollback-source-v1";
        const long deviceId = 8201, sensorId = 82001, oldHostId = 82, newHostId = 83;
        var mapDate = DateTime.Today;
        new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.SourceGeneration = sourceGeneration);
        using (var seed = _fx.NewContext())
        {
            seed.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId, Name = "rollback-device" });
            seed.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = sensorId, DeviceObjid = deviceId, SensorType = "ping", Category = "availability"
            });
            seed.SaveChanges();
        }
        var store = new EfPrtgStore(_fx.NewContext);
        store.ReplaceHostMapForDate(mapDate,
            [new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceId, HostId = oldHostId, MapStatus = PrtgMapStatus.Ok }]);
        var identityBefore = store.GetResourceIdentity(sensorId);
        var revisionsBefore = store.ReadResourceAuthorityRevisions([oldHostId, newHostId]);
        var observationsBefore = store.ReadResourceObservationRevisions([oldHostId, newHostId]);
        var hostMapRevisionBefore = store.ReadHostMapDataRevision();
        using (var trigger = _fx.NewContext())
        {
            var revisionKey = PrtgResourceIdentityStore.AuthorityRevisionKey(oldHostId);
            trigger.Database.ExecuteSqlRaw($"CREATE TRIGGER fail_bulk_map_revision BEFORE UPDATE ON lf_blobs " +
                $"WHEN NEW.blob_key = '{revisionKey}' BEGIN SELECT RAISE(ABORT, 'simulated revision write failure'); END;");
        }

        Assert.Throws<DbUpdateException>(() => store.ReplaceHostMapForDate(mapDate,
            [new PrtgHostMapRow { MapDate = mapDate, DeviceObjid = deviceId, HostId = newHostId, MapStatus = PrtgMapStatus.Ok }]));

        using (var verify = _fx.NewContext())
        {
            Assert.Equal(oldHostId, Assert.Single(verify.PrtgHostMaps.Where(row => row.MapDate == mapDate)).HostId);
            var identityAfter = PrtgResourceIdentityStore.Read(verify, sensorId);
            Assert.Equal(identityBefore.Epoch, identityAfter.Epoch);
            Assert.Equal(identityBefore.Generation, identityAfter.Generation);
            Assert.Equal(identityBefore.HostId, identityAfter.HostId);
            Assert.Equal(revisionsBefore[oldHostId], verify.Blobs.AsNoTracking()
                .Where(row => row.BlobKey == PrtgResourceIdentityStore.AuthorityRevisionKey(oldHostId))
                .Select(row => row.Version).Single());
            Assert.Equal(observationsBefore[oldHostId], verify.Blobs.AsNoTracking()
                .Where(row => row.BlobKey == PrtgResourceIdentityStore.ObservationRevisionKey(oldHostId))
                .Select(row => row.Version).Single());
            Assert.False(verify.Blobs.Any(row => row.BlobKey == PrtgResourceIdentityStore.AuthorityRevisionKey(newHostId)));
            Assert.False(verify.Blobs.Any(row => row.BlobKey == PrtgResourceIdentityStore.ObservationRevisionKey(newHostId)));
        }
        Assert.Equal(hostMapRevisionBefore, store.ReadHostMapDataRevision());
    }

    [Fact]
    public void WholeEvidenceProfileFreshness_FailsClosedAfterTrustedMetadataExpires()
    {
        const long hostId = 51;
        const long deviceId = 501;
        const long sensorId = 5001;
        const string sourceGeneration = "profile-freshness-source-v1";
        var store = new EfPrtgStore(_fx.NewContext);
        new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.SourceGeneration = sourceGeneration;
            policy.HostIds = [hostId];
            policy.SensorIds = [sensorId];
        });
        store.UpsertDevices([new PrtgDeviceRow { Objid = deviceId, Name = "profile-host" }], DateTime.Now);
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = sensorId, DeviceObjid = deviceId, SensorType = "CPU", Category = PrtgSensorCategories.Cpu
        }], DateTime.Now);
        store.ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = deviceId, MapDate = DateTime.Today, HostId = hostId, MapStatus = PrtgMapStatus.Ok }]);
        var identity = store.BindObservedResource(sensorId, hostId, sourceGeneration, "profile-resource-v1");
        identity = store.SetObservedChannel(sensorId, sourceGeneration, "profile-channel-v1", identity.Generation);

        PrtgTrustedSamplingProfile MakeProfile(DateTimeOffset observedAtUtc) => PrtgTrustedSamplingProfile.FromProbe(
            sensorId, identity, "CPU", "primary", "CPU utilization", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "cpu-semantic-v1", new string('a', 64), 15,
            DateTime.SpecifyKind(new DateTime(2026, 10, 1, 0, 0, 0), DateTimeKind.Utc),
            TimeSpan.FromMinutes(5), "minutes", "UTC", "UTC", "UTC", observedAtUtc,
            "typed-metadata-reference", "same-physical-sample-reference", true, 50, 50,
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).ToOADate(),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).ToOADate());

        store.RecordTrustedSamplingProfile(MakeProfile(DateTimeOffset.UtcNow.AddHours(-25)));
        var dailyMap = new Dictionary<long, long> { [deviceId] = hostId };
        var selectedSensorIds = new HashSet<long> { sensorId };
        var valueSensorsByHost = store.GetResourceSensorIdsByHost(dailyMap, [hostId], selectedSensorIds,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PrtgSensorCategories.Cpu });
        var sensorToHost = valueSensorsByHost.SelectMany(pair => pair.Value
                .Select(id => (SensorId: id, HostId: pair.Key)))
            .ToDictionary(pair => pair.SensorId, pair => pair.HostId);
        Assert.Contains(hostId, store.HostsWithInvalidTrustedProfiles(sensorToHost, DateTimeOffset.UtcNow));
        var stateOnlySensorsByHost = store.GetResourceSensorIdsByHost(dailyMap, [hostId], selectedSensorIds,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(stateOnlySensorsByHost[hostId]);
        Assert.DoesNotContain(hostId, store.HostsWithInvalidTrustedProfiles(new Dictionary<long, long>(), DateTimeOffset.UtcNow));

        store.RecordTrustedSamplingProfile(MakeProfile(DateTimeOffset.UtcNow));
        Assert.DoesNotContain(hostId, store.HostsWithInvalidTrustedProfiles(sensorToHost, DateTimeOffset.UtcNow));
        using (var ctx = _fx.NewContext())
        {
            var profileRow = Assert.Single(ctx.Blobs.Where(blob =>
                blob.BlobKey == PrtgTrustedSamplingProfile.StorePrefix + sensorId));
            ctx.Blobs.Remove(profileRow);
            ctx.SaveChanges();
        }
        Assert.Contains(hostId, store.HostsWithInvalidTrustedProfiles(sensorToHost, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void WholeEvidenceProfileSelection_BoundsChosenSensorsAndNeverLoadsIrrelevantCategoryText()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new IdentityReadCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var ctx = NewContext()) ctx.Database.EnsureCreated();

        const long deviceId = 501;
        const long hostId = 51;
        const long selectedCpuSensor = 900_001;
        const long selectedUnsupportedSensor = 900_002;
        var now = DateTime.UtcNow;
        using (var ctx = NewContext())
        {
            // The mapped device has more than the old 30,000 all-inventory cap, although only two
            // sensors belong to the current policy selection.
            ctx.PrtgSensors.AddRange(Enumerable.Range(1, 30_001).Select(index => new PrtgSensorRow
            {
                Objid = index,
                DeviceObjid = deviceId,
                Name = $"unselected-{index}",
                SensorType = "CPU",
                Category = PrtgSensorCategories.Cpu,
                SyncedAt = now,
                CreatedAt = now
            }));
            ctx.PrtgSensors.AddRange(
                new PrtgSensorRow
                {
                    Objid = selectedCpuSensor,
                    DeviceObjid = deviceId,
                    Name = "selected-paused-cpu",
                    SensorType = "CPU",
                    Category = PrtgSensorCategories.Cpu,
                    Paused = true,
                    SyncedAt = now,
                    CreatedAt = now
                },
                new PrtgSensorRow
                {
                    Objid = selectedUnsupportedSensor,
                    DeviceObjid = deviceId,
                    Name = "selected-unsupported",
                    SensorType = "other",
                    Category = new string('x', 100_000),
                    SyncedAt = now,
                    CreatedAt = now
                });
            ctx.SaveChanges();
        }

        var store = new EfPrtgStore(NewContext);
        recorder.Clear();
        var result = store.GetResourceSensorIdsByHost(
            new Dictionary<long, long> { [deviceId] = hostId }, [hostId],
            new HashSet<long> { selectedCpuSensor, selectedUnsupportedSensor },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PrtgSensorCategories.Cpu });

        Assert.Equal(new[] { selectedCpuSensor }, result[hostId]);
        var sensorRead = Assert.Single(recorder.Commands.Where(sql =>
            sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase)));
        var selectList = sensorRead[..sensorRead.IndexOf("FROM", StringComparison.OrdinalIgnoreCase)];
        Assert.DoesNotContain("category", selectList, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchIdentityRead_BoundsSqlProjectionAndRejectsOversizedOrMalformedLedgers()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new IdentityReadCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var ctx = NewContext()) ctx.Database.EnsureCreated();

        const long sensorId = 90002;
        var key = PrtgResourceIdentityStore.Prefix + sensorId;
        var blob = new EfJsonBlobStore(NewContext, key);
        var store = new EfPrtgStore(NewContext);
        var payloads = new[]
        {
            new string('x', 2 * 1024 * 1024),
            new string('雪', 3_000), // fewer than 8 Ki characters, more than 8 Ki UTF-8 bytes
            "{"
        };

        foreach (var payload in payloads)
        {
            blob.Mutate(_ => (payload, true));
            recorder.Clear();

            Assert.Throws<InvalidDataException>(() => store.GetResourceIdentities([sensorId]));

            var read = Assert.Single(recorder.Commands.Where(sql =>
                sql.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase) &&
                sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("substr", read, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("length", read, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(501, 2)]
    [InlineData(5001, 11)]
    public void SensorUpsert_LoadsIdentityRowsOncePer500SensorBatch_AndPreservesIdentityGenerations(
        int sensorCount, int expectedIdentityBatchReads)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new IdentityReadCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var ctx = NewContext()) ctx.Database.EnsureCreated();

        const long deviceId = 710;
        const long hostId = 71;
        const string sourceGeneration = "identity-batch-source-v1";
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(NewContext, PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.SourceGeneration = sourceGeneration);
        using (var ctx = NewContext())
        {
            SeedExistingResourceIdentityRows(ctx, sensorCount, deviceId, hostId, sourceGeneration, active: true);
            ctx.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = deviceId, HostId = hostId });
            ctx.SaveChanges();
        }

        var store = new EfPrtgStore(NewContext);
        var sensors = Enumerable.Range(1, sensorCount).Select(id => BatchSensor(id, deviceId, "ping", "before")).ToArray();
        var targetId = sensors[0].Objid;
        var before = store.GetResourceIdentity(targetId);
        Assert.True(before.Active);
        Assert.True(new PrtgResourceIdentityStore(new EfJsonBlobStore(NewContext,
            PrtgResourceIdentityStore.Prefix + targetId)).IsCurrent(targetId, sourceGeneration,
            before.Generation, deviceId, hostId));

        recorder.Clear();
        Assert.Equal(sensorCount, store.UpsertSensors(sensors.Select(sensor => BatchSensor(
            sensor.Objid, deviceId, "snmp", "before")).ToArray(), DateTime.Now, requireAtomic: true));
        AssertQueryCount(expectedIdentityBatchReads, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        var revisionReads = GetRevisionMarkerReads(recorder.CapturedCommands).ToArray();
        AssertQueryCount(expectedIdentityBatchReads, revisionReads.Length, recorder.CapturedCommands);
        var revisionKeyCounts = revisionReads.Select(CountRevisionKeys).ToArray();
        Assert.True(revisionKeyCounts.All(count => count is >= 1 and <= 500),
            $"Revision key counts were [{string.Join(", ", revisionKeyCounts)}].\n{DescribeBlobQueries(recorder.CapturedCommands)}");
        var afterFirstIdentityChange = store.GetResourceIdentity(targetId);
        Assert.Equal(before.Epoch + 1, afterFirstIdentityChange.Epoch);
        Assert.NotEqual(before.Generation, afterFirstIdentityChange.Generation);
        Assert.True(new PrtgResourceIdentityStore(new EfJsonBlobStore(NewContext,
            PrtgResourceIdentityStore.Prefix + targetId)).IsCurrent(targetId, sourceGeneration,
            afterFirstIdentityChange.Generation, deviceId, hostId));

        recorder.Clear();
        Assert.Equal(sensorCount, store.UpsertSensors(sensors.Select(sensor => BatchSensor(
            sensor.Objid, deviceId, "snmp", "renamed")).ToArray(), DateTime.Now, requireAtomic: true));
        AssertQueryCount(0, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        Assert.False(GetRevisionMarkerReads(recorder.CapturedCommands).Any(),
            DescribeBlobQueries(recorder.CapturedCommands));
        var afterNameOnly = store.GetResourceIdentity(targetId);
        Assert.Equal(afterFirstIdentityChange.Epoch, afterNameOnly.Epoch);
        Assert.Equal(afterFirstIdentityChange.Generation, afterNameOnly.Generation);

        recorder.Clear();
        store.UpsertSensors([BatchSensor(targetId, deviceId, "http", "renamed")], DateTime.Now, requireAtomic: true);
        AssertQueryCount(1, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(1, GetRevisionMarkerReads(recorder.CapturedCommands).Count(), recorder.CapturedCommands);
        var afterIdentityChange = store.GetResourceIdentity(targetId);
        Assert.Equal(afterFirstIdentityChange.Epoch + 1, afterIdentityChange.Epoch);
        Assert.NotEqual(afterFirstIdentityChange.Generation, afterIdentityChange.Generation);
        var identityStore = new PrtgResourceIdentityStore(new EfJsonBlobStore(NewContext,
            PrtgResourceIdentityStore.Prefix + targetId));
        Assert.False(identityStore.IsCurrent(targetId, sourceGeneration, afterFirstIdentityChange.Generation, deviceId, hostId));
        Assert.True(identityStore.IsCurrent(targetId, sourceGeneration, afterIdentityChange.Generation, deviceId, hostId));
    }

    [Fact]
    public void SensorUpsert_BoundsAuthorityAndObservationMarkerPreloadTo500Keys()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new IdentityReadCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var ctx = NewContext()) ctx.Database.EnsureCreated();

        const int sensorCount = 251;
        const string sourceGeneration = "identity-revision-batch-source-v1";
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(NewContext, PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.SourceGeneration = sourceGeneration);
        var changedAt = DateTimeOffset.UtcNow;
        using (var ctx = NewContext())
        {
            for (var index = 0; index < sensorCount; index++)
            {
                var sensorId = 800_001L + index;
                var deviceId = 900_001L + index;
                var hostId = 100_001L + index;
                ctx.PrtgSensors.Add(BatchSensor(sensorId, deviceId, "ping", "before"));
                ctx.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = deviceId, HostId = hostId });
                var identity = new PrtgResourceIdentity
                {
                    SensorId = sensorId, Epoch = 1, Generation = $"revision-generation-{sensorId}",
                    SourceGeneration = sourceGeneration, DeviceId = deviceId, HostId = hostId,
                    ResourceFingerprint = $"resource-{sensorId}", InventoryFingerprint = "ping||",
                    ChannelFingerprint = $"channel-{sensorId}", ChannelGeneration = $"channel-generation-{sensorId}",
                    Active = true, ChangedAtUtc = changedAt
                };
                ctx.Blobs.Add(new BlobRow
                {
                    BlobKey = PrtgResourceIdentityStore.Prefix + sensorId,
                    Content = JsonSerializer.Serialize(identity, LfJsonOptions.Pretty),
                    Version = 1, UpdatedAt = changedAt.LocalDateTime
                });
            }
            ctx.SaveChanges();
        }

        var store = new EfPrtgStore(NewContext);
        recorder.Clear();
        Assert.Equal(sensorCount, store.UpsertSensors(Enumerable.Range(0, sensorCount)
            .Select(index => BatchSensor(800_001L + index, 900_001L + index, "snmp", "after")).ToArray(),
            DateTime.Now, requireAtomic: true));

        AssertQueryCount(1, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        var revisionReads = GetRevisionMarkerReads(recorder.CapturedCommands).ToArray();
        var actualRevisionKeyCounts = revisionReads.Select(CountRevisionKeys).OrderByDescending(count => count).ToArray();
        Assert.True(new[] { 500, 2 }.SequenceEqual(actualRevisionKeyCounts),
            $"Expected revision key counts [500, 2], got [{string.Join(", ", actualRevisionKeyCounts)}].\n{DescribeBlobQueries(recorder.CapturedCommands)}");
        Assert.True(revisionReads.Select(CountRevisionKeys).All(count => count is >= 1 and <= 500),
            DescribeBlobQueries(recorder.CapturedCommands));
    }

    [Fact]
    public void SensorUpsert_IdentityRowsInSecondBatchFailureRollsBackSensorsAndLedgers()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options;
        using (var setup = new LfDbContext(options)) setup.Database.EnsureCreated();
        const string sourceGeneration = "identity-rollback-source-v1";
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(() => new LfDbContext(options), PrtgMonitoringPolicyStore.BlobKey))
            .Update(policy => policy.SourceGeneration = sourceGeneration);
        var before = Enumerable.Range(1, 501).Select(id => new PrtgResourceIdentity
        {
            SensorId = id, Epoch = 1, Generation = $"generation-{id}", SourceGeneration = sourceGeneration,
            DeviceId = 710, ResourceFingerprint = $"resource-{id}",
            InventoryFingerprint = "ping||", ChannelFingerprint = "channel", ChannelGeneration = $"channel-{id}",
            Active = false, ChangedAtUtc = DateTimeOffset.UtcNow
        }).ToArray();
        using (var seed = new LfDbContext(options))
        {
            seed.PrtgSensors.AddRange(Enumerable.Range(1, 501).Select(id => BatchSensor(id, 710, "ping", "S")));
            seed.Blobs.AddRange(before.Select(identity => new BlobRow
            {
                BlobKey = PrtgResourceIdentityStore.Prefix + identity.SensorId,
                Content = JsonSerializer.Serialize(identity, LfJsonOptions.Pretty),
                Version = 1,
                UpdatedAt = DateTime.Now
            }));
            seed.SaveChanges();
        }
        var fail = true;
        var store = new EfPrtgStore(() => new FailOnSecondSaveContext(options, () => fail));
        var sensors = Enumerable.Range(1, 501).Select(id => BatchSensor(id, 710, "snmp", "S")).ToArray();

        Assert.Throws<InvalidOperationException>(() => store.UpsertSensors(sensors, DateTime.Now, requireAtomic: true));
        using (var check = new LfDbContext(options))
        {
            Assert.All(check.PrtgSensors, sensor => Assert.Equal("ping", sensor.SensorType));
            Assert.Equal(501, check.Blobs.Count(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.Prefix)));
            var persisted = PrtgResourceIdentityStore.Read(check, 1);
            Assert.Equal(before[0].Generation, persisted.Generation);
            Assert.Equal(before[0].Epoch, persisted.Epoch);
        }

        fail = false;
        Assert.Equal(501, store.UpsertSensors(sensors, DateTime.Now, requireAtomic: true));
        using var committed = new LfDbContext(options);
        Assert.Equal(501, committed.PrtgSensors.Count());
        Assert.Equal(501, committed.Blobs.Count(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.Prefix)));
        Assert.Equal("snmp", committed.PrtgSensors.Single(sensor => sensor.Objid == 1).SensorType);
        var committedIdentity = PrtgResourceIdentityStore.Read(committed, 1);
        Assert.Equal(before[0].Epoch + 1, committedIdentity.Epoch);
        Assert.NotEqual(before[0].Generation, committedIdentity.Generation);
    }

    [Fact]
    public void NewSensorHasNoCurrentResourceIdentityUntilNativeBindingAndReconciliation()
    {
        const long sensorId = 90210;
        const long deviceId = 902;
        const long hostId = 92;
        const string sourceGeneration = "new-sensor-source-v1";
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = deviceId, HostId = hostId });
        var policy = new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var initialPolicy = policy.Update(p =>
        {
            p.Revision = "new-sensor-revision-1";
            p.SourceGeneration = sourceGeneration;
            p.HostIds = [hostId];
            p.SensorIds = [sensorId];
        });

        store.UpsertSensors([BatchSensor(sensorId, deviceId, "ping", "new")], DateTime.Now);
        var unobserved = store.GetResourceIdentity(sensorId);
        Assert.Equal(0, unobserved.Epoch);
        Assert.Empty(unobserved.Generation);
        Assert.Empty(unobserved.SourceGeneration);
        Assert.Empty(unobserved.ResourceFingerprint);
        Assert.False(unobserved.Active);
        Assert.False(unobserved.PendingReconciliation);
        var identityStore = new PrtgResourceIdentityStore(_fx.Blob(PrtgResourceIdentityStore.Prefix + sensorId));
        Assert.False(identityStore.IsCurrent(sensorId, sourceGeneration, "", deviceId, hostId));

        var pending = policy.UpdateWithResourceEpochs(initialPolicy.Revision, [sensorId], p =>
            p.Revision = "new-sensor-revision-2");
        var awaitingObservation = store.GetResourceIdentity(sensorId);
        Assert.Equal(1, awaitingObservation.Epoch);
        Assert.False(string.IsNullOrWhiteSpace(awaitingObservation.Generation));
        Assert.Equal(sourceGeneration, awaitingObservation.SourceGeneration);
        Assert.Empty(awaitingObservation.ResourceFingerprint);
        Assert.False(awaitingObservation.Active);
        Assert.True(awaitingObservation.PendingReconciliation);
        Assert.False(identityStore.IsCurrent(sensorId, sourceGeneration, awaitingObservation.Generation, deviceId, hostId));

        var observed = store.BindObservedResource(sensorId, hostId, pending.SourceGeneration, "native-observed-resource");
        Assert.Equal(awaitingObservation.Epoch, observed.Epoch);
        Assert.Equal(awaitingObservation.Generation, observed.Generation);
        Assert.Equal("native-observed-resource", observed.ResourceFingerprint);
        Assert.True(observed.Active);
        Assert.False(observed.PendingReconciliation);
        Assert.True(identityStore.IsCurrent(sensorId, pending.SourceGeneration, observed.Generation, deviceId, hostId));
    }

    private static PrtgSensorRow BatchSensor(long sensorId, long deviceId, string sensorType, string name) => new()
    {
        Objid = sensorId,
        DeviceObjid = deviceId,
        Name = name,
        SensorType = sensorType,
        Category = PrtgSensorCategories.Availability,
        Status = "Up"
    };

    private static void SeedExistingResourceIdentityRows(LfDbContext ctx, int sensorCount, long deviceId,
        long hostId, string sourceGeneration, bool active)
    {
        var changedAt = DateTimeOffset.UtcNow;
        var sensors = Enumerable.Range(1, sensorCount).Select(id => BatchSensor(id, deviceId, "ping", "before")).ToArray();
        ctx.PrtgSensors.AddRange(sensors);
        ctx.Blobs.AddRange(sensors.Select(sensor => new PrtgResourceIdentity
        {
            SensorId = sensor.Objid,
            Epoch = 1,
            Generation = $"generation-{sensor.Objid}",
            SourceGeneration = sourceGeneration,
            DeviceId = deviceId,
            HostId = hostId,
            ResourceFingerprint = $"resource-{sensor.Objid}",
            InventoryFingerprint = $"ping|{sensor.Category}|{sensor.CategorySource}",
            ChannelFingerprint = $"channel-{sensor.Objid}",
            ChannelGeneration = $"channel-generation-{sensor.Objid}",
            Active = active,
            ChangedAtUtc = changedAt
        }).Select(identity => new BlobRow
        {
            BlobKey = PrtgResourceIdentityStore.Prefix + identity.SensorId,
            Content = JsonSerializer.Serialize(identity, LfJsonOptions.Pretty),
            Version = 1,
            UpdatedAt = changedAt.LocalDateTime
        }));
    }

    private static int CountIdentityPrefixBatchReads(IEnumerable<RecordedCommand> commands) => commands.Count(command =>
        IsBlobSelect(command) && IsBatchBlobLookup(command) &&
        GetReferencedBlobKeys(command).Any(value => value.StartsWith(PrtgResourceIdentityStore.Prefix,
            StringComparison.Ordinal)));

    private static int CountPerResourceIdentityReads(IEnumerable<RecordedCommand> commands) => commands.Count(command =>
        IsBlobSelect(command) && GetReferencedBlobKeys(command).Any(value => value.StartsWith(
            PrtgResourceIdentityStore.Prefix, StringComparison.Ordinal)) &&
        !IsBatchBlobLookup(command));

    private static IEnumerable<RecordedCommand> GetRevisionMarkerReads(IEnumerable<RecordedCommand> commands) =>
        commands.Where(command => IsBlobSelect(command) &&
            GetReferencedBlobKeys(command).Any(value =>
                value.StartsWith(PrtgResourceIdentityStore.AuthorityRevisionPrefix, StringComparison.Ordinal) ||
                value.StartsWith(PrtgResourceIdentityStore.ObservationRevisionPrefix, StringComparison.Ordinal)));

    private static int CountRevisionKeys(RecordedCommand command) =>
        GetReferencedBlobKeys(command).Distinct(StringComparer.Ordinal).Count(value =>
            value.StartsWith(PrtgResourceIdentityStore.AuthorityRevisionPrefix, StringComparison.Ordinal) ||
            value.StartsWith(PrtgResourceIdentityStore.ObservationRevisionPrefix, StringComparison.Ordinal));

    private static bool IsBlobSelect(RecordedCommand command) =>
        command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("blob_key", StringComparison.OrdinalIgnoreCase);

    private static bool IsBatchBlobLookup(RecordedCommand command) =>
        command.CommandText.Contains(" IN ", StringComparison.OrdinalIgnoreCase) ||
        // EF simplifies a one-key Contains batch to equality without LIMIT. Single-row
        // identity reads use SingleOrDefault/FirstOrDefault and retain their LIMIT guard.
        (GetReferencedBlobKeys(command).Count == 1 &&
         command.CommandText.Contains("AS \"Length\"", StringComparison.Ordinal) &&
         !Regex.IsMatch(command.CommandText, @"\bLIMIT\b", RegexOptions.IgnoreCase));

    private static IReadOnlyList<string> GetReferencedBlobKeys(RecordedCommand command)
    {
        var keys = new HashSet<string>(GetParameterValues(command).Where(IsResourceOrRevisionKey),
            StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(command.CommandText,
                     @"prtg_resource_(?:identity|authority_host|observation_host)_\d+",
                     RegexOptions.CultureInvariant))
            keys.Add(match.Value);
        return keys.ToArray();
    }

    private static bool IsResourceOrRevisionKey(string value) =>
        Regex.IsMatch(value, @"^prtg_resource_(?:identity|authority_host|observation_host)_\d+$",
            RegexOptions.CultureInvariant);

    private static string DescribeBlobQueries(IEnumerable<RecordedCommand> commands)
    {
        var selected = commands.Where(IsBlobSelect).Select(command =>
            $"SQL: {command.CommandText}{Environment.NewLine}Parameters: " +
            string.Join(" | ", command.ParameterValues.Select(value => value ?? "<null>")));
        return string.Join(Environment.NewLine + "---" + Environment.NewLine, selected);
    }

    private static void AssertQueryCount(int expected, int actual, IEnumerable<RecordedCommand> commands) =>
        Assert.True(expected == actual,
            $"Expected {expected} query matches, got {actual}.{Environment.NewLine}{DescribeBlobQueries(commands)}");

    private static IReadOnlyList<string> GetParameterValues(RecordedCommand command)
    {
        var values = new List<string>();
        foreach (var parameter in command.ParameterValues)
        {
            if (string.IsNullOrWhiteSpace(parameter)) continue;
            if (parameter.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var items = JsonSerializer.Deserialize<string[]>(parameter);
                    if (items is not null) { values.AddRange(items); continue; }
                }
                catch (JsonException) { }
            }
            values.Add(parameter);
        }
        return values;
    }

    [Fact]
    public void PolicyRevisionCasAndAffectedResourceEpochs_AreAtomicAndAdvanceExactlyOnce()
    {
        const long affectedSensor = 30001;
        const long unrelatedSensor = 40002;
        var policy = new PrtgMonitoringPolicyStore(_fx.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => { p.Revision = "revision-1"; p.SourceGeneration = "source-v1"; p.HostIds = [31]; p.SensorIds = [affectedSensor]; });
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = affectedSensor, DeviceObjid = 301, SensorType = "ping", Category = "availability" },
                new PrtgSensorRow { Objid = unrelatedSensor, DeviceObjid = 402, SensorType = "ping", Category = "availability" });
            ctx.PrtgHostMaps.AddRange(
                new PrtgHostMapRow { DeviceObjid = 301, MapDate = DateTime.Today, HostId = 31, MapStatus = PrtgMapStatus.Ok },
                new PrtgHostMapRow { DeviceObjid = 402, MapDate = DateTime.Today, HostId = 42, MapStatus = PrtgMapStatus.Ok });
            PrtgResourceIdentityStore.Set(ctx, affectedSensor, "source-v1", 301, 31,
                "resource-a", "ping|availability|auto", "channel-a", true, DateTimeOffset.UtcNow);
            PrtgResourceIdentityStore.Set(ctx, unrelatedSensor, "source-v1", 402, 42,
                "resource-b", "ping|availability|auto", "channel-b", true, DateTimeOffset.UtcNow);
            ctx.SaveChanges();
        }

        var store = new EfPrtgStore(_fx.NewContext);
        var beforeAffected = store.GetResourceIdentity(affectedSensor);
        var beforeUnrelated = store.GetResourceIdentity(unrelatedSensor);
        Assert.Throws<InvalidOperationException>(() => policy.UpdateWithResourceEpochs("stale-revision",
            [affectedSensor], p => { p.Revision = "must-not-publish"; p.SourceGeneration = "source-stale"; }));

        Assert.Equal("revision-1", policy.Get().Revision);
        Assert.Equal("source-v1", policy.Get().SourceGeneration);
        var afterFailedCas = store.GetResourceIdentity(affectedSensor);
        Assert.Equal(beforeAffected.Epoch, afterFailedCas.Epoch);
        Assert.Equal(beforeAffected.Generation, afterFailedCas.Generation);

        var updated = policy.UpdateWithResourceEpochs("revision-1", [affectedSensor], p =>
        {
            p.Revision = "revision-2";
            p.SourceGeneration = "source-v2";
        });
        var afterAffected = store.GetResourceIdentity(affectedSensor);
        var afterUnrelated = store.GetResourceIdentity(unrelatedSensor);
        Assert.Equal("revision-2", updated.Revision);
        Assert.Equal("source-v2", updated.SourceGeneration);
        Assert.Equal(beforeAffected.Epoch + 1, afterAffected.Epoch);
        Assert.NotEqual(beforeAffected.Generation, afterAffected.Generation);
        Assert.Equal("source-v2", afterAffected.SourceGeneration);
        Assert.True(afterAffected.PendingReconciliation);
        Assert.Equal(beforeUnrelated.Epoch, afterUnrelated.Epoch);
        Assert.Equal(beforeUnrelated.Generation, afterUnrelated.Generation);
        Assert.Equal(beforeUnrelated.SourceGeneration, afterUnrelated.SourceGeneration);

        store.ReplaceHostMapForDate(DateTime.Today,
        [
            new PrtgHostMapRow { DeviceObjid = 301, MapDate = DateTime.Today, HostId = 31, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 402, MapDate = DateTime.Today, HostId = 42, MapStatus = PrtgMapStatus.Ok }
        ]);
        var afterMirrorRefresh = store.GetResourceIdentity(affectedSensor);
        Assert.True(afterMirrorRefresh.PendingReconciliation);
        var reconciled = store.BindObservedResource(affectedSensor, 31, "source-v2", "resource-a-v2");
        Assert.Equal(afterAffected.Epoch, reconciled.Epoch);
        Assert.Equal(afterAffected.Generation, reconciled.Generation);
        Assert.Equal("source-v2", reconciled.SourceGeneration);
        Assert.False(reconciled.PendingReconciliation);
        var stillUnrelated = store.GetResourceIdentity(unrelatedSensor);
        Assert.Equal(beforeUnrelated.Epoch, stillUnrelated.Epoch);
        Assert.Equal(beforeUnrelated.Generation, stillUnrelated.Generation);
    }

    [Fact]
    public void PolicyEpochUpdate_Loads500IdentityBatchesAndPreservesPerSensorAndPerHostRevisionCounts()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new IdentityReadCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var setup = NewContext()) setup.Database.EnsureCreated();

        const int sensorCount = 501;
        const long hostId = 77;
        const string oldSource = "policy-epoch-source-v1";
        const string newSource = "policy-epoch-source-v2";
        var policy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(NewContext,
            PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "revision-before"; value.SourceGeneration = oldSource; });
        using (var seed = NewContext())
        {
            SeedExistingResourceIdentityRows(seed, sensorCount, 710, hostId, oldSource, active: true);
            seed.SaveChanges();
        }
        recorder.Clear();

        var updated = policy.UpdateWithResourceEpochs("revision-before", Enumerable.Range(1, sensorCount).Select(id => (long)id), value =>
        {
            value.Revision = "revision-after";
            value.SourceGeneration = newSource;
        });

        Assert.Equal("revision-after", updated.Revision);
        AssertQueryCount(2, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        var revisionReads = GetRevisionMarkerReads(recorder.CapturedCommands).ToArray();
        AssertQueryCount(1, revisionReads.Length, recorder.CapturedCommands);
        AssertQueryCount(2, CountRevisionKeys(revisionReads[0]), recorder.CapturedCommands);
        Assert.True(revisionReads.Select(CountRevisionKeys).All(count => count is >= 1 and <= 500),
            DescribeBlobQueries(recorder.CapturedCommands));

        var store = new EfPrtgStore(NewContext);
        for (var sensorId = 1; sensorId <= sensorCount; sensorId++)
        {
            var identity = store.GetResourceIdentity(sensorId);
            Assert.Equal(2, identity.Epoch);
            Assert.Equal(newSource, identity.SourceGeneration);
            Assert.True(identity.PendingReconciliation);
            Assert.NotEqual($"generation-{sensorId}", identity.Generation);
        }
        Assert.Equal((long)sensorCount, store.ReadResourceAuthorityRevisions([hostId])[hostId]);
        Assert.Equal((long)sensorCount, store.ReadResourceObservationRevisions([hostId])[hostId]);
    }

    [Fact]
    public void PolicyEpochUpdate_ProcessesDisjointOldAndNew15000SelectionsInOne30000IdentityTransaction()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var recorder = new BlobSelectCommandRecorder();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);
        using (var setup = NewContext()) setup.Database.EnsureCreated();

        const int oldSelectionCount = 15_000;
        const int newSelectionCount = 15_000;
        const int affectedCount = oldSelectionCount + newSelectionCount;
        const long hostId = 81;
        const long unrelatedHostId = 82;
        const string oldSource = "policy-epoch-disjoint-v1";
        const string newSource = "policy-epoch-disjoint-v2";
        var policy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(NewContext,
            PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "disjoint-before"; value.SourceGeneration = oldSource; });
        using (var seed = NewContext())
        {
            SeedExistingResourceIdentityRows(seed, affectedCount, 810, hostId, oldSource, active: true);
            var unrelated = BatchSensor(40_001, 820, "ping", "unrelated");
            seed.PrtgSensors.Add(unrelated);
            var unrelatedIdentity = new PrtgResourceIdentity
            {
                SensorId = unrelated.Objid,
                Epoch = 1,
                Generation = "generation-unrelated",
                SourceGeneration = oldSource,
                DeviceId = unrelated.DeviceObjid,
                HostId = unrelatedHostId,
                ResourceFingerprint = "resource-unrelated",
                InventoryFingerprint = $"{unrelated.SensorType}|{unrelated.Category}|{unrelated.CategorySource}",
                ChannelFingerprint = "channel-unrelated",
                ChannelGeneration = "channel-generation-unrelated",
                Active = true,
                ChangedAtUtc = DateTimeOffset.UtcNow
            };
            seed.Blobs.Add(new BlobRow
            {
                BlobKey = PrtgResourceIdentityStore.Prefix + unrelatedIdentity.SensorId,
                Content = JsonSerializer.Serialize(unrelatedIdentity, LfJsonOptions.Pretty),
                Version = 1,
                UpdatedAt = DateTime.Now
            });
            seed.SaveChanges();
        }
        recorder.Clear();

        // Mirrors the controller's old-minus-new plus new-minus-old affected-sensor union.
        var oldSelection = Enumerable.Range(1, oldSelectionCount).Select(id => (long)id).ToArray();
        var newSelection = Enumerable.Range(oldSelectionCount + 1, newSelectionCount).Select(id => (long)id).ToArray();
        var affectedResources = oldSelection.Except(newSelection).Concat(newSelection.Except(oldSelection)).ToArray();
        Assert.Equal(affectedCount, affectedResources.Length);
        var updated = policy.UpdateWithResourceEpochs("disjoint-before", affectedResources, value =>
        {
            value.Revision = "disjoint-after";
            value.SourceGeneration = newSource;
        });

        Assert.Equal("disjoint-after", updated.Revision);
        Assert.Equal(newSource, updated.SourceGeneration);
        AssertQueryCount(60, CountIdentityPrefixBatchReads(recorder.CapturedCommands), recorder.CapturedCommands);
        AssertQueryCount(0, CountPerResourceIdentityReads(recorder.CapturedCommands), recorder.CapturedCommands);
        var revisionReads = GetRevisionMarkerReads(recorder.CapturedCommands).ToArray();
        AssertQueryCount(1, revisionReads.Length, recorder.CapturedCommands);
        AssertQueryCount(2, CountRevisionKeys(revisionReads[0]), recorder.CapturedCommands);
        recorder.Clear();

        using var verify = NewContext();
        var identities = verify.Blobs.AsNoTracking()
            .Where(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.Prefix))
            .Select(row => new { row.BlobKey, row.Content, row.Version })
            .ToList()
            .Where(row => !row.BlobKey.StartsWith(PrtgResourceIdentityStore.AuthorityRevisionPrefix) &&
                          !row.BlobKey.StartsWith(PrtgResourceIdentityStore.ObservationRevisionPrefix))
            .ToDictionary(row => row.BlobKey, row => (Identity: JsonSerializer.Deserialize<PrtgResourceIdentity>(
                row.Content, LfJsonOptions.Pretty)!, row.Version), StringComparer.Ordinal);
        Assert.Equal(affectedCount + 1, identities.Count);
        for (var sensorId = 1; sensorId <= affectedCount; sensorId++)
        {
            var row = identities[PrtgResourceIdentityStore.Prefix + sensorId];
            Assert.Equal(2, row.Identity.Epoch);
            Assert.NotEqual($"generation-{sensorId}", row.Identity.Generation);
            Assert.Equal(newSource, row.Identity.SourceGeneration);
            Assert.True(row.Identity.PendingReconciliation);
            Assert.True(row.Identity.Active);
            Assert.Equal(2, row.Version);
        }
        var unchanged = identities[PrtgResourceIdentityStore.Prefix + 40_001];
        Assert.Equal(1, unchanged.Identity.Epoch);
        Assert.Equal("generation-unrelated", unchanged.Identity.Generation);
        Assert.Equal(oldSource, unchanged.Identity.SourceGeneration);
        Assert.False(unchanged.Identity.PendingReconciliation);
        Assert.Equal(1, unchanged.Version);

        var store = new EfPrtgStore(NewContext);
        Assert.Equal((long)affectedCount, store.ReadResourceAuthorityRevisions([hostId])[hostId]);
        Assert.Equal((long)affectedCount, store.ReadResourceObservationRevisions([hostId])[hostId]);
        Assert.Equal(0, store.ReadResourceAuthorityRevisions([unrelatedHostId])[unrelatedHostId]);
        Assert.Equal(0, store.ReadResourceObservationRevisions([unrelatedHostId])[unrelatedHostId]);
    }

    [Fact]
    public void PolicyEpochUpdate_FailureAfterFirst500RowsRollsBackIdentityRevisionsAndPolicy()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options;
        using (var setup = new LfDbContext(options)) setup.Database.EnsureCreated();

        const int sensorCount = 501;
        const long hostId = 78;
        const string oldSource = "policy-epoch-rollback-v1";
        const string newSource = "policy-epoch-rollback-v2";
        var plainPolicy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(() => new LfDbContext(options),
            PrtgMonitoringPolicyStore.BlobKey));
        plainPolicy.Update(value => { value.Revision = "rollback-before"; value.SourceGeneration = oldSource; });
        using (var seed = new LfDbContext(options))
        {
            SeedExistingResourceIdentityRows(seed, sensorCount, 711, hostId, oldSource, active: true);
            seed.SaveChanges();
        }
        var initial = new EfPrtgStore(() => new LfDbContext(options));
        var firstBefore = initial.GetResourceIdentity(1);
        var lastBefore = initial.GetResourceIdentity(sensorCount);
        var failingPolicy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(
            () => new FailOnSecondSaveContext(options, () => true), PrtgMonitoringPolicyStore.BlobKey));

        Assert.Throws<InvalidOperationException>(() => failingPolicy.UpdateWithResourceEpochs("rollback-before",
            Enumerable.Range(1, sensorCount).Select(id => (long)id), value =>
            {
                value.Revision = "rollback-after";
                value.SourceGeneration = newSource;
            }));

        Assert.Equal("rollback-before", plainPolicy.Get().Revision);
        Assert.Equal(oldSource, plainPolicy.Get().SourceGeneration);
        var after = new EfPrtgStore(() => new LfDbContext(options));
        Assert.Equal(firstBefore.Generation, after.GetResourceIdentity(1).Generation);
        Assert.Equal(firstBefore.Epoch, after.GetResourceIdentity(1).Epoch);
        Assert.False(after.GetResourceIdentity(1).PendingReconciliation);
        Assert.Equal(lastBefore.Generation, after.GetResourceIdentity(sensorCount).Generation);
        Assert.Equal(lastBefore.Epoch, after.GetResourceIdentity(sensorCount).Epoch);
        Assert.False(after.GetResourceIdentity(sensorCount).PendingReconciliation);
        Assert.Equal(0, after.ReadResourceAuthorityRevisions([hostId])[hostId]);
        Assert.Equal(0, after.ReadResourceObservationRevisions([hostId])[hostId]);
    }

    [Theory]
    [InlineData(15_001)]
    [InlineData(30_000)]
    public void PolicyEpochUpdate_AllowsTheFullOldPlusNewSelectionUnionBeforeOpeningTheStore(int sensorCount)
    {
        var contextFactoryCalls = 0;
        var policy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(() =>
        {
            contextFactoryCalls++;
            throw new InvalidOperationException("Reached storage boundary after passing the scope cap.");
        }, PrtgMonitoringPolicyStore.BlobKey));

        var error = Assert.Throws<InvalidOperationException>(() => policy.UpdateWithResourceEpochs("revision",
            Enumerable.Range(1, sensorCount).Select(id => (long)id), _ => { }));

        Assert.Equal("Reached storage boundary after passing the scope cap.", error.Message);
        Assert.Equal(1, contextFactoryCalls);
    }

    [Fact]
    public void PolicyEpochUpdate_RejectsMoreThan30000BeforeOpeningStorageOrChangingPolicy()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options);
        using (var setup = NewContext()) setup.Database.EnsureCreated();
        var policy = new PrtgMonitoringPolicyStore(new EfJsonBlobStore(NewContext,
            PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "before-too-large"; value.SourceGeneration = "source-before"; });

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.UpdateWithResourceEpochs("before-too-large",
            Enumerable.Range(1, 30_001).Select(id => (long)id), value =>
            {
                value.Revision = "must-not-publish";
                value.SourceGeneration = "source-after";
            }));

        Assert.Equal("before-too-large", policy.Get().Revision);
        Assert.Equal("source-before", policy.Get().SourceGeneration);
        using var verify = NewContext();
        Assert.Empty(verify.Blobs.Where(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.Prefix)));
        Assert.Empty(verify.Blobs.Where(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.AuthorityRevisionPrefix)));
        Assert.Empty(verify.Blobs.Where(row => row.BlobKey.StartsWith(PrtgResourceIdentityStore.ObservationRevisionPrefix)));
    }

    public void Dispose() => _fx.Dispose();

    private sealed class IdentityReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<RecordedCommand> CapturedCommands { get; } = [];

        public void Clear()
        {
            Commands.Clear();
            CapturedCommands.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            CapturedCommands.Add(new RecordedCommand(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(parameter => parameter.Value is null or DBNull ? null : parameter.Value.ToString()).ToArray()));
            return result;
        }
    }

    private sealed class BlobSelectCommandRecorder : DbCommandInterceptor
    {
        public List<RecordedCommand> CapturedCommands { get; } = [];

        public void Clear() => CapturedCommands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("blob_key", StringComparison.OrdinalIgnoreCase))
                CapturedCommands.Add(new RecordedCommand(command.CommandText,
                    command.Parameters.Cast<DbParameter>()
                        .Select(parameter => parameter.Value is null or DBNull ? null : parameter.Value.ToString())
                        .ToArray()));
            return result;
        }
    }

    private sealed record RecordedCommand(string CommandText, IReadOnlyList<string?> ParameterValues);

    private sealed class FailOnSecondSaveContext(DbContextOptions<LfDbContext> options, Func<bool> shouldFail)
        : LfDbContext(options)
    {
        private int _saveCount;

        public override int SaveChanges()
        {
            if (++_saveCount == 2 && shouldFail())
                throw new InvalidOperationException("後一批模擬寫入失敗");
            return base.SaveChanges();
        }
    }
}

using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-journal-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    public PrtgSnapshotJournalTests() => _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);

    [Fact]
    public void 來源與資料庫不同_拒絕重播且不修改檔案()
    {
        var journal = new PrtgSnapshotJournal(_backend);
        var now = DateTime.Today;
        journal.Save("source-a", new[] { new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12) },
            Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        var original = File.ReadAllBytes(journal.FilePath);
        Assert.Throws<InvalidDataException>(() => journal.Load("source-b", now));
        _backend.Blob("prtg_snapshot_database_id").Mutate(_ => ("\"different-database\"", 0));
        Assert.Throws<InvalidDataException>(() => new PrtgSnapshotJournal(_backend).Load("source-a", now));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    [Fact]
    public void 可信slotProof經journalcheckpoint與pendingbatch重播保留且舊列仍為版本零()
    {
        var hour = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        var input = new PrtgTrustedSample(8, 42, "source", "resource", "channel", "epoch", "semantic", "strategy", 15,
            hour, hour.AddMinutes(15), hour.AddMinutes(15).AddSeconds(2), TimeSpan.FromMinutes(1),
            PrtgTrustedSampleQuality.Good, "physical-1", "UTC", "UTC");
        var accumulator = new PrtgSnapshotAccumulator();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(input, input.ReceivedAt.AddSeconds(1)));
        var trustedRow = Assert.Single(accumulator.PreviewDrainAll(4, hour.AddMinutes(30)));
        var oldRow = new PrtgValueRow { SensorObjid = 9, PeriodStart = hour, Quality = PrtgDataQuality.Sampled };
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Save("source", accumulator.Capture(), [new PrtgSnapshotJournal.Batch(Guid.NewGuid().ToString("N"), [trustedRow, oldRow])], hour.AddMinutes(30));
        var loaded = journal.Load("source", hour.AddMinutes(30))!;
        Assert.Equal(1, Assert.Single(loaded.Accumulator).Trusted!.Slots.Count);
        Assert.Equal(1, Assert.Single(loaded.Pending).Rows[0].TrustVersion);
        Assert.Null(loaded.Pending.Single().Rows[1].TrustedProof);
        Assert.Equal(0, loaded.Pending.Single().Rows[1].TrustVersion);
    }

    [Fact]
    public void 空V2checkpoint可轉換binding並以新binding重新載入()
    {
        var now = DateTime.Today;
        using var seed = new PrtgSnapshotJournal(_backend);
        seed.Save("source-a", [], [], now);
        var prior = seed.Load("source-a", now)!;
        var original = File.ReadAllBytes(seed.FilePath);
        using var migration = new PrtgSnapshotJournal(_backend);
        Assert.Equal("source-a", migration.Load("source-b", now)!.SourceEndpoint);

        migration.EnableIncremental("source-b", now);

        Assert.False(File.Exists(seed.FilePath));
        Assert.True(File.Exists(seed.ManifestFilePath));
        using var recovered = new PrtgSnapshotJournal(_backend);
        var state = recovered.Load("source-b", now)!;
        Assert.Equal(2, state.Version);
        Assert.Equal("source-b", state.SourceEndpoint);
        Assert.Equal(prior.DatabaseId, state.DatabaseId);
        Assert.Empty(state.Accumulator);
        Assert.Empty(state.Pending);

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(recovered.ManifestFilePath));
        var generation = manifest.RootElement.GetProperty("Generation").GetString()!;
        var segmentPath = Path.Combine(Path.GetDirectoryName(recovered.FilePath)!, generation, "segments", "00000000000000000001.json");
        using var segment = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(segmentPath));
        Assert.Equal("source-b", segment.RootElement.GetProperty("SourceEndpoint").GetString());
        Assert.Equal(prior.DatabaseId, segment.RootElement.GetProperty("DatabaseId").GetString());
        var snapshot = segment.RootElement.GetProperty("Snapshot");
        Assert.Equal("source-b", snapshot.GetProperty("SourceEndpoint").GetString());
        Assert.Equal(prior.DatabaseId, snapshot.GetProperty("DatabaseId").GetString());
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(segmentPath))));
    }

    [Fact]
    public void 非空V2checkpoint不能轉換binding且保留原檔()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Save("source-a", [new(now, 1, 12, 1, 12, 12)], [], now);
        journal.Load("source-a", now);
        var original = File.ReadAllBytes(journal.FilePath);
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original));

        Assert.Throws<InvalidDataException>(() => journal.EnableIncremental("source-b", now));

        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
        Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(journal.FilePath))));
        Assert.False(File.Exists(journal.ManifestFilePath));
    }

    [Fact]
    public void 滿額與超過安全重播期_保留原檔()
    {
        var now = DateTime.Today;
        var journal = new PrtgSnapshotJournal(_backend);
        var row = new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12);
        journal.Save("source", new[] { row }, Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        var original = File.ReadAllBytes(journal.FilePath);
        Assert.Throws<InvalidDataException>(() => journal.Save("source",
            Enumerable.Repeat(row, PrtgSnapshotJournal.MaxRows + 1).ToArray(), Array.Empty<PrtgSnapshotJournal.Batch>(), now));
        Assert.Throws<InvalidDataException>(() => journal.Load("source", now.AddDays(31)));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    [Fact]
    public void 未完成暫存檔不影響上一份已提交樣本()
    {
        var journal = new PrtgSnapshotJournal(_backend);
        var now = DateTime.Today;
        journal.Save("source", new[] { new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12) },
            Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        File.WriteAllText(journal.FilePath + ".tmp", "incomplete");
        var state = journal.Load("source", now)!;
        Assert.Equal(12, Assert.Single(state.Accumulator).Sum);
    }

    [Fact]
    public void 合法JSON樣本遭修改與舊版無checksum_拒絕重播且保留原檔()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Save("source", [new(now, 1, 12, 1, 12, 12)], [], now);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal.FilePath))!;
        node["Accumulator"]![0]!["Sum"] = 13;
        var changed = node.ToJsonString(); File.WriteAllText(journal.FilePath, changed);
        Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => journal.Load("source", now)).Message);
        Assert.Equal(changed, File.ReadAllText(journal.FilePath));
        node["Version"] = 1; changed = node.ToJsonString(); File.WriteAllText(journal.FilePath, changed);
        Assert.Contains("舊版", Assert.Throws<InvalidDataException>(() => journal.Load("source", now)).Message);
        Assert.Equal(changed, File.ReadAllText(journal.FilePath));
    }

    [Fact]
    public void 過期寫入者不能覆蓋已保存樣本_持有工作者時第二個不能接管()
    {
        var now = DateTime.Today;
        using var first = new PrtgSnapshotJournal(_backend);
        first.Save("source", [new(now, 1, 12, 1, 12, 12)], [], now);
        using var second = new PrtgSnapshotJournal(_backend);
        var stale = second.Load("source", now)!;
        first.Save("source", [..stale.Accumulator, new(now, 2, 20, 1, 20, 20)], [], now);
        Assert.Throws<InvalidDataException>(() => second.Save("source", [..stale.Accumulator, new(now, 3, 30, 1, 30, 30)], [], now));
        Assert.Contains(first.Load("source", now)!.Accumulator, r => r.SensorObjid == 2);
        first.AcquireOwnership();
        Assert.Throws<IOException>(() => second.AcquireOwnership());
        first.Dispose();
        second.AcquireOwnership();
        second.Load("source", now);
        second.Save("source", [new(now, 2, 20, 1, 20, 20)], [], now);
    }

    [Fact]
    public void 持久來源binding明列endpoint與source且不隨資源修訂改變()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-a");
        var journal = new PrtgSnapshotJournal(_backend); var now = DateTime.Today;
        var binding = PrtgSnapshotJournal.Binding(_backend, "https://fixture.example");
        var bindingParts = binding.Split('|');
        Assert.Equal("v3", bindingParts[0]);
        Assert.Equal(PrtgSnapshotJournal.Endpoint("https://fixture.example"), bindingParts[1]);
        Assert.Equal("source-a", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bindingParts[2])));
        journal.Save(binding,
            [new(now, 1, 12, 1, 12, 12)], [], now);
        var original = File.ReadAllBytes(journal.FilePath);
        _backend.Blob("prtg_resource_generation_revision").Mutate(_ => ("1", 0));
        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("1", 0));
        Assert.Equal(binding, PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"));
        Assert.Single(new PrtgSnapshotJournal(_backend).Load(binding, now)!.Accumulator);
        policy.Update(p => p.SourceGeneration = "source-b");
        Assert.NotEqual(binding, PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"));
        Assert.Throws<InvalidDataException>(() => journal.Load(PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"), now));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    [Fact]
    public void BindingPair以同一份policySource建立current與legacy值()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => value.SourceGeneration = "source-pair-a");

        var captured = PrtgSnapshotJournal.CaptureBindingPair(_backend, "https://fixture.example");
        policy.Update(value => value.SourceGeneration = "source-pair-b");

        Assert.Equal("source-pair-a", captured.SourceGeneration);
        Assert.Equal("source-pair-a", System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(captured.Current.Split('|')[2])));
        Assert.Equal(64, captured.Legacy.Length);
        Assert.All(captured.Legacy, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.NotEqual(captured.Current, PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"));
        Assert.NotEqual(captured.Legacy, PrtgSnapshotJournal.LegacyBinding(_backend, "https://fixture.example"));
    }

    [Fact]
    public void ExactLegacyV2Binding_先驗checksum後原子遷移且保留trustedProof與legacy列()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-v2");
        var url = "https://fixture.example";
        var legacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        var currentBinding = PrtgSnapshotJournal.Binding(_backend, url);
        var now = DateTime.Today;
        var hour = DateTime.SpecifyKind(now.AddHours(10), DateTimeKind.Unspecified);
        var effectiveUtc = DateTime.SpecifyKind(hour, DateTimeKind.Utc);
        var accumulator = new PrtgSnapshotAccumulator();
        var trusted = new PrtgTrustedSample(8, 42, "source-v2", "resource-8", "channel-8", "epoch-8",
            "semantic-v1", "strategy-v1", 15, effectiveUtc, effectiveUtc.AddMinutes(15),
            effectiveUtc.AddMinutes(15).AddSeconds(2), TimeSpan.FromMinutes(1), PrtgTrustedSampleQuality.Good,
            "measurement-8", "UTC", "UTC");
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            accumulator.AddTrusted(trusted, trusted.ReceivedAt.AddSeconds(1)));
        var legacyRow = new PrtgValueRow
        {
            SensorObjid = 9, PeriodStart = hour, Quality = PrtgDataQuality.Sampled, CreatedAt = hour,
            TrustVersion = 0, TrustedProof = null
        };
        using var writer = new PrtgSnapshotJournal(_backend);
        writer.Save(legacyBinding, accumulator.Capture(),
            [new PrtgSnapshotJournal.Batch(Guid.NewGuid().ToString("N"), [legacyRow])], now.AddDays(1));
        var original = File.ReadAllBytes(writer.FilePath);

        using var migration = new PrtgSnapshotJournal(_backend);
        var loaded = migration.Load(currentBinding, now.AddDays(1), legacyBinding)!;
        Assert.Equal(legacyBinding, loaded.SourceEndpoint);
        Assert.Equal(original, File.ReadAllBytes(migration.FilePath));
        migration.EnableIncremental(currentBinding, now.AddDays(1), legacyBinding);

        Assert.False(File.Exists(migration.FilePath));
        Assert.True(File.Exists(migration.ManifestFilePath));
        using var recovered = new PrtgSnapshotJournal(_backend);
        var state = recovered.Load(currentBinding, now.AddDays(1))!;
        var checkpoint = Assert.Single(state.Accumulator);
        Assert.Equal(trusted.ResourceGeneration, checkpoint.Trusted!.ResourceGeneration);
        Assert.Equal(trusted.ResourceEpoch, checkpoint.Trusted.ResourceEpoch);
        var pendingRow = Assert.Single(Assert.Single(state.Pending).Rows);
        Assert.Equal(0, pendingRow.TrustVersion);
        Assert.Null(pendingRow.TrustedProof);
    }

    [Fact]
    public void LegacyV2binding不匹配_保留原字節且不重標為新來源()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-v2");
        var url = "https://fixture.example";
        var legacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        using var writer = new PrtgSnapshotJournal(_backend);
        var now = DateTime.Today;
        writer.Save(legacyBinding, [new(now, 8, 42, 1, 42, 42)], [], now);
        var original = File.ReadAllBytes(writer.FilePath);
        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("1", 0));
        var currentBinding = PrtgSnapshotJournal.Binding(_backend, url);
        var changedLegacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        Assert.NotEqual(legacyBinding, changedLegacyBinding);

        using var restart = new PrtgSnapshotJournal(_backend);
        var error = Assert.Throws<InvalidDataException>(() => restart.Load(currentBinding, now, changedLegacyBinding));
        Assert.Contains("舊版來源 binding", error.Message);
        Assert.Equal(original, File.ReadAllBytes(restart.FilePath));
        Assert.False(File.Exists(restart.ManifestFilePath));
    }

    [Fact]
    public void LegacyV2遷移交換manifest前故障_原checkpoint仍為權威且可重試()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-v2");
        var url = "https://fixture.example";
        var legacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        var currentBinding = PrtgSnapshotJournal.Binding(_backend, url);
        var now = DateTime.Today;
        using var writer = new PrtgSnapshotJournal(_backend);
        writer.Save(legacyBinding, [new(now, 8, 42, 1, 42, 42)], [], now);
        var original = File.ReadAllBytes(writer.FilePath);

        using var migration = new PrtgSnapshotJournal(_backend);
        migration.Load(currentBinding, now, legacyBinding);
        migration.FaultPoint = point =>
        {
            if (point == "before-generation-switch") throw new IOException("simulated migration failure");
        };
        Assert.Throws<IOException>(() => migration.EnableIncremental(currentBinding, now, legacyBinding));
        Assert.Equal(original, File.ReadAllBytes(migration.FilePath));
        Assert.False(File.Exists(migration.ManifestFilePath));

        migration.FaultPoint = null;
        migration.EnableIncremental(currentBinding, now, legacyBinding);
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(42, Assert.Single(recovered.Load(currentBinding, now)!.Accumulator).Sum);
    }

    [Fact]
    public void Scopechange重啟保留A與B樣本_新epoch只使A失去正式readiness()
    {
        var source = "source-r03-proof";
        var url = "https://fixture.example";
        var policy = new PrtgMonitoringPolicy
        {
            Revision = "policy-r03",
            SourceGeneration = source,
            EndpointHint = PrtgSnapshotJournal.Endpoint(url),
            SourceTimeZoneId = "UTC",
            SourceCultureName = "en-US",
            RawTimestampTimeZoneId = "UTC",
            AnalysisTimeZoneId = "UTC",
            TimeBasisEvidenceReference = "time-basis-r03",
            HostIds = [81, 82],
            SensorIds = [8, 9]
        };
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(value =>
        {
            value.Revision = policy.Revision;
            value.SourceGeneration = policy.SourceGeneration;
        });
        var endpoint = PrtgSnapshotJournal.Binding(_backend, url);
        var now = DateTimeOffset.UtcNow;
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var effective = currentHour.AddHours(-1); // The evidence hour is complete at restart.
        var observedAt = now.AddSeconds(-2); // Keep the source metadata probe fresh.
        var profileMeasured = observedAt.UtcDateTime;
        var accumulator = new PrtgSnapshotAccumulator();
        var fixtures = new List<(PrtgResourceIdentity Identity, PrtgTrustedSamplingProfile Profile,
            PrtgTrustedSamplingProfileResolution Resolution, PrtgTrustedSample Sample)>();
        foreach (var pair in new[] { (Sensor: 8L, Host: 81L, Resource: "resource-a", Channel: "channel-a"),
                     (Sensor: 9L, Host: 82L, Resource: "resource-b", Channel: "channel-b") })
        {
            var identity = new PrtgResourceIdentity
            {
                SensorId = pair.Sensor, Epoch = 1, Generation = pair.Resource, SourceGeneration = source,
                DeviceId = pair.Host + 100, HostId = pair.Host, ResourceFingerprint = $"resource-fp-{pair.Sensor}",
                InventoryFingerprint = $"inventory-fp-{pair.Sensor}",
                ChannelFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"synthetic-channel-fingerprint-{pair.Sensor}"))),
                ChannelGeneration = pair.Channel, Active = true
            };
            var strategy = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(source, "UTC", "UTC", "UTC",
                PrtgFetchStrategy.Conservative, 15);
            var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(pair.Sensor, identity, "CPU", pair.Channel,
                "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "cpu-percent-v1",
                strategy, 15, effective, TimeSpan.FromMinutes(1), "seconds", "UTC", "UTC", "UTC",
                observedAt, $"metadata-{pair.Sensor}", $"physical-{pair.Sensor}", true, 10, 10,
                profileMeasured.ToOADate(), profileMeasured.ToOADate());
            var profile = PrtgConsumerProfileFixtureClosure.CreateProfileOnly(identity, policy,
                "synthetic-journal-settings", PrtgFetchStrategy.Conservative, sourceProfile);
            var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, pair.Sensor,
                "CPU", PrtgFetchStrategy.Conservative, 15, effective, now.UtcDateTime, now.UtcDateTime,
                now.UtcDateTime);
            Assert.True(resolution.Ready, resolution.RejectionReason);
            PrtgTrustedSample? lastSample = null;
            for (var slot = 0; slot < 3; slot++)
            {
                var measured = effective.AddMinutes(slot * 15).AddSeconds(1);
                var received = measured.AddSeconds(2);
                var sample = new PrtgTrustedSample(pair.Sensor, 10 + slot, source, identity.Generation,
                    identity.ChannelGeneration, identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
                    profile.StrategyEffectiveFromHourUtc, measured, received, profile.ConfirmedScanInterval,
                    PrtgTrustedSampleQuality.Good, $"measurement-{pair.Sensor}-{slot}", "UTC", "UTC");
                Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(sample, received));
                lastSample = sample;
            }
            fixtures.Add((identity, profile, resolution, lastSample!));
        }
        using (var seed = new PrtgSnapshotJournal(_backend))
            seed.Save(endpoint, accumulator.Capture(), [], now.LocalDateTime);

        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("1", 0));
        _backend.Blob("prtg_resource_generation_revision").Mutate(_ => ("1", 0));
        Assert.Equal(endpoint, PrtgSnapshotJournal.Binding(_backend, url));
        using var restarted = new PrtgSnapshotJournal(_backend);
        var restored = restarted.Load(PrtgSnapshotJournal.Binding(_backend, url), now.LocalDateTime)!
            .Accumulator.ToDictionary(row => row.SensorObjid);
        Assert.Equal(2, restored.Count);

        foreach (var fixture in fixtures)
        {
            var checkpoint = restored[fixture.Identity.SensorId];
            Assert.Equal(fixture.Sample.ResourceEpoch, checkpoint.Trusted!.ResourceEpoch);
            var proofJson = PrtgTrustedSampleProof.Serialize(checkpoint.Trusted);
            var readinessRow = new PrtgDiskReadinessProofProjection(0, fixture.Identity.SensorId,
                checkpoint.Hour, checkpoint.Trusted.Slots.Average(slot => slot.Value),
                checkpoint.Trusted.Slots.Min(slot => slot.Value), checkpoint.Trusted.Slots.Max(slot => slot.Value),
                checkpoint.Trusted.Slots.Count * 100d / (60 / checkpoint.Trusted.StrategyMinutes),
                PrtgDataQuality.Sampled, 1, proofJson, proofJson.Length);
            Assert.Equal(75d, readinessRow.Coverage);
            Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(readinessRow, fixture.Resolution));
            if (fixture.Identity.SensorId == 8)
            {
                var changedIdentity = new PrtgResourceIdentity
                {
                    SensorId = fixture.Identity.SensorId, Epoch = 2, Generation = "resource-a-new",
                    SourceGeneration = fixture.Identity.SourceGeneration, DeviceId = fixture.Identity.DeviceId,
                    HostId = fixture.Identity.HostId, ResourceFingerprint = fixture.Identity.ResourceFingerprint,
                    InventoryFingerprint = fixture.Identity.InventoryFingerprint,
                    ChannelFingerprint = fixture.Identity.ChannelFingerprint,
                    ChannelGeneration = fixture.Identity.ChannelGeneration, Active = true,
                    PendingReconciliation = false, ChangedAtUtc = now
                };
                var staleResolution = PrtgTrustedSamplingProfileResolver.Resolve(fixture.Profile, changedIdentity,
                    policy, fixture.Identity.SensorId, "CPU", PrtgFetchStrategy.Conservative, 15, effective,
                    now.UtcDateTime, now.UtcDateTime, now.UtcDateTime);
                Assert.False(staleResolution.Ready);
                Assert.False(PrtgDiskTrustedProofValidator.IsTrusted(readinessRow, staleResolution));
            }
            else
            {
                Assert.True(PrtgDiskTrustedProofValidator.IsTrusted(readinessRow, fixture.Resolution));
            }
        }
    }

    [Fact]
    public void ExactLegacySegmentbinding_原子遷移且交換manifest後故障仍可由新格式恢復()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-segment-v1");
        var url = "https://fixture.example";
        var legacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        var currentBinding = PrtgSnapshotJournal.Binding(_backend, url);
        var now = DateTime.Today;
        var hour = DateTime.SpecifyKind(now.AddHours(10), DateTimeKind.Unspecified);
        var effectiveUtc = DateTime.SpecifyKind(hour, DateTimeKind.Utc);
        var trustedAccumulator = new PrtgSnapshotAccumulator();
        var trustedInput = new PrtgTrustedSample(8, 42, "source-segment-v1", "resource-8", "channel-8",
            "epoch-8", "semantic-v1", "strategy-v1", 15, effectiveUtc, effectiveUtc.AddMinutes(15),
            effectiveUtc.AddMinutes(15).AddSeconds(2), TimeSpan.FromMinutes(1), PrtgTrustedSampleQuality.Good,
            "measurement-8", "UTC", "UTC");
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            trustedAccumulator.AddTrusted(trustedInput, trustedInput.ReceivedAt.AddSeconds(1)));
        var legacyPending = new PrtgValueRow
        {
            SensorObjid = 9, PeriodStart = hour, Quality = PrtgDataQuality.Sampled,
            CreatedAt = hour, TrustVersion = 0, TrustedProof = null
        };
        using (var seed = new PrtgSnapshotJournal(_backend))
        {
            seed.Load(legacyBinding, now);
            seed.EnableIncremental(legacyBinding, now);
            seed.AppendDelta(legacyBinding,
                new PrtgSnapshotAccumulator.CheckpointDelta(trustedAccumulator.Capture(), []),
                [new PrtgSnapshotJournal.Batch(Guid.NewGuid().ToString("N"), [legacyPending])], null, now);
        }
        var root = Path.Combine(_backend.DataRoot, "pending", "prtg-snapshot");
        var oldGeneration = Directory.GetDirectories(root, "checkpoint.g.*").Single();
        var oldSegmentHashes = Directory.GetFiles(Path.Combine(oldGeneration, "segments"), "*.json")
            .ToDictionary(path => Path.GetFileName(path), path => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));

        using var migration = new PrtgSnapshotJournal(_backend);
        var oldState = migration.Load(currentBinding, now, legacyBinding)!;
        Assert.Equal(42, Assert.Single(oldState.Accumulator).Sum);
        migration.FaultPoint = point =>
        {
            if (point == "after-generation-switch") throw new IOException("simulated crash after manifest publication");
        };
        Assert.Throws<IOException>(() => migration.EnableIncremental(currentBinding, now, legacyBinding));
        var unchangedOldHashes = Directory.GetFiles(Path.Combine(oldGeneration, "segments"), "*.json")
            .ToDictionary(path => Path.GetFileName(path), path => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(oldSegmentHashes, unchangedOldHashes);
        Assert.True(File.Exists(migration.ManifestFilePath));
        using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(migration.ManifestFilePath)))
        {
            var newGeneration = manifest.RootElement.GetProperty("Generation").GetString()!;
            Assert.NotEqual(Path.GetFileName(oldGeneration), newGeneration);
            var newSnapshot = Path.Combine(root, newGeneration, "segments", "00000000000000000001.json");
            Assert.True(File.Exists(newSnapshot));
        }

        // Simulate process restart after the durable pointer switch. The new generation
        // is authoritative and contains the exact validated legacy data.
        using var recovered = new PrtgSnapshotJournal(_backend);
        var recoveredState = recovered.Load(currentBinding, now)!;
        Assert.Equal(trustedInput.ResourceEpoch, Assert.Single(recoveredState.Accumulator).Trusted!.ResourceEpoch);
        var pendingRow = Assert.Single(Assert.Single(recoveredState.Pending).Rows);
        Assert.Equal(0, pendingRow.TrustVersion);
        Assert.Null(pendingRow.TrustedProof);
    }

    [Fact]
    public void LegacySegmentbinding不匹配_整代checksum文件保持原樣並拒絕重標()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(value => value.SourceGeneration = "source-segment-old");
        var url = "https://fixture.example";
        var legacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        var now = DateTime.Today;
        using (var writer = new PrtgSnapshotJournal(_backend))
        {
            writer.Load(legacyBinding, now);
            writer.EnableIncremental(legacyBinding, now);
            writer.AppendDelta(legacyBinding,
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 8, 42, 1, 42, 42)], []), null, null, now);
        }
        var root = Path.Combine(_backend.DataRoot, "pending", "prtg-snapshot");
        static Dictionary<string, string> Hashes(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(path, file), file => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        var before = Hashes(root);
        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("1", 0));
        var currentLegacyBinding = PrtgSnapshotJournal.LegacyBinding(_backend, url);
        Assert.NotEqual(legacyBinding, currentLegacyBinding);

        using var restart = new PrtgSnapshotJournal(_backend);
        Assert.Contains("舊版來源 binding", Assert.Throws<InvalidDataException>(() =>
            restart.Load(PrtgSnapshotJournal.Binding(_backend, url), now, currentLegacyBinding)).Message);
        Assert.Equal(before, Hashes(root));
    }

    [Fact]
    public void 增量append_1與10筆既有backlog寫入成本相同且舊segment不變()
    {
        static (long Bytes, int FilesRead, int FilesWritten, bool Stable, bool QuotaAccurate) Run(string root, int backlog)
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
            var now = DateTime.Today;
            using (var seed = new PrtgSnapshotJournal(backend))
            {
                seed.Load("source", now);
                seed.EnableIncremental("source", now);
                for (var i = 0; i < backlog; i++)
                    seed.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                        Enumerable.Range(1, 1_000).Select(offset =>
                            new PrtgSnapshotAccumulator.CheckpointRow(now, i * 1_000L + offset, offset, 1, offset, offset)).ToArray(), []), null, null, now);
            }

            using var writer = new PrtgSnapshotJournal(backend);
            writer.Load("source", now);
            var segmentDir = Directory.GetDirectories(Path.GetDirectoryName(writer.FilePath)!, "checkpoint.g.*")
                .Select(path => Path.Combine(path, "segments")).Single();
            var oldHashes = Directory.GetFiles(segmentDir).ToDictionary(path => Path.GetFileName(path)!,
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            var readBase = writer.BytesRead;
            var writeBase = writer.BytesWritten;
            var filesReadBase = writer.FilesRead;
            var filesWrittenBase = writer.FilesWritten;
            var added = Enumerable.Range(1, 1_000).Select(offset =>
                new PrtgSnapshotAccumulator.CheckpointRow(now, 100_000 + offset, offset, 1, offset, offset)).ToArray();
            writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(added, []), null, null, now);
            var batchId = Guid.NewGuid().ToString("N");
            var batchRows = Enumerable.Range(1, 1_000).Select(offset => new PrtgValueRow
            {
                SensorObjid = 100_000 + offset,
                PeriodStart = now,
                AvgValue = offset,
                MinValue = offset,
                MaxValue = offset,
                Coverage = 10,
                Quality = PrtgDataQuality.Sampled,
                CreatedAt = now
            }).ToArray();
            writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta([],
                added.Select(row => new PrtgSnapshotAccumulator.CheckpointKey(row.Hour, row.SensorObjid)).ToArray()),
                [new(batchId, batchRows)], null, now);
            writer.AppendAck("source", batchId, now);
            var stable = oldHashes.All(pair =>
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(segmentDir, pair.Key!))));
                return actual == pair.Value;
            });
            var physicalBytes = Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            return (writer.BytesWritten - writeBase, writer.FilesRead - filesReadBase,
                writer.FilesWritten - filesWrittenBase, stable && writer.BytesRead - readBase < 2048,
                physicalBytes == writer.SavedBytes && physicalBytes <= PrtgSnapshotJournal.MaxBytes);
        }

        var one = Run(Path.Combine(_root, "one"), 1);
        var ten = Run(Path.Combine(_root, "ten"), 10);
        Assert.InRange(Math.Abs(one.Bytes - ten.Bytes), 0, 8); // manifest sequence文字位數差只增加固定幾個bytes。
        Assert.Equal(6, one.FilesWritten);
        Assert.Equal(6, ten.FilesWritten);
        Assert.Equal(3, one.FilesRead);
        Assert.Equal(3, ten.FilesRead);
        Assert.True(one.Stable);
        Assert.True(ten.Stable);
        Assert.True(one.QuotaAccurate);
        Assert.True(ten.QuotaAccurate);
    }

    [Fact]
    public void 接近64MiB時把舊generation與segment檔計入配額且超大segment讀取前拒絕()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Load("source", now);
        journal.EnableIncremental("source", now);
        journal.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
            [new(now, 1, 10, 1, 10, 10)], []), null, null, now);
        var originalManifest = File.ReadAllBytes(journal.ManifestFilePath);

        // 模擬 generation 切換中仍須保留的舊代檔案；此非 JSON segment 也必須納入真實目錄容量。
        var oldGeneration = Path.Combine(Path.GetDirectoryName(journal.FilePath)!, "checkpoint.g." + Guid.NewGuid().ToString("N"));
        var oldSegments = Path.Combine(oldGeneration, "segments");
        Directory.CreateDirectory(oldSegments);
        var retainedFile = Path.Combine(oldSegments, "retained-old-generation.tmp");
        var before = Directory.GetFiles(Path.GetDirectoryName(journal.FilePath)!, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        using (var stream = new FileStream(retainedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(PrtgSnapshotJournal.MaxBytes - before + 1);
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(journal.FilePath)!, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) > PrtgSnapshotJournal.MaxBytes);

        // 讓下一筆 delta 走 rotation/compaction，容量檢查需在切換 manifest 前拒絕。
        typeof(PrtgSnapshotJournal).GetField("_segmentCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(journal, 127);
        Assert.Throws<InvalidDataException>(() => journal.AppendDelta("source",
            new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 2, 20, 1, 20, 20)], []), null, null, now));
        Assert.Equal(originalManifest, File.ReadAllBytes(journal.ManifestFilePath));
        Assert.True(File.Exists(retainedFile));
        Assert.Equal(PrtgSnapshotJournal.MaxBytes - before + 1, new FileInfo(retainedFile).Length);

        // committed segment 自身超大時，ReadFile 應先檢查長度，不得先配置整個檔案。
        File.Delete(retainedFile);
        Directory.Delete(oldGeneration, recursive: true);
        // Generation 名稱來自 manifest；從其已驗證內容取得實際 segment 路徑。
        using var manifestDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(journal.ManifestFilePath));
        var generation = manifestDoc.RootElement.GetProperty("Generation").GetString()!;
        var segment = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(journal.FilePath)!, generation, "segments"), "*.json");
        var committedSegment = segment[0];
        using (var stream = new FileStream(committedSegment, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(PrtgSnapshotJournal.MaxBytes + 1);
        using var reader = new PrtgSnapshotJournal(_backend);
        Assert.Throws<InvalidDataException>(() => reader.Load("source", now));
        Assert.Equal(new FileInfo(journal.ManifestFilePath).Length, reader.BytesRead);
        Assert.True(File.Exists(committedSegment));
    }

    [Fact]
    public void V2遷移中斷_切換前保留V2切換後可恢復新generation()
    {
        var now = DateTime.Today;
        var row = new PrtgSnapshotAccumulator.CheckpointRow(now, 8, 40, 2, 10, 30, 50);
        using (var seed = new PrtgSnapshotJournal(_backend)) seed.Save("source", [row], [], now);
        var original = File.ReadAllBytes(new PrtgSnapshotJournal(_backend).FilePath);

        using (var beforeSwitch = new PrtgSnapshotJournal(_backend))
        {
            beforeSwitch.Load("source", now);
            beforeSwitch.FaultPoint = point => { if (point == "before-generation-switch") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => beforeSwitch.EnableIncremental("source", now));
        }
        Assert.Equal(original, File.ReadAllBytes(new PrtgSnapshotJournal(_backend).FilePath));
        Assert.Equal(40, Assert.Single(new PrtgSnapshotJournal(_backend).Load("source", now)!.Accumulator).Sum);

        using (var afterSwitch = new PrtgSnapshotJournal(_backend))
        {
            afterSwitch.Load("source", now);
            afterSwitch.FaultPoint = point => { if (point == "after-generation-switch") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => afterSwitch.EnableIncremental("source", now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(40, Assert.Single(recovered.Load("source", now)!.Accumulator).Sum);
        Assert.True(File.Exists(recovered.ManifestFilePath));
        Assert.False(File.Exists(recovered.FilePath));
    }

    [Fact]
    public void Segment發佈前後中斷_只重播manifest已提交的prefix()
    {
        var now = DateTime.Today;
        using (var setup = new PrtgSnapshotJournal(_backend))
        {
            setup.Load("source", now);
            setup.EnableIncremental("source", now);
        }
        using (var torn = new PrtgSnapshotJournal(_backend))
        {
            torn.Load("source", now);
            torn.FaultPoint = point => { if (point == "after-segment-flush") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => torn.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 1, 10, 1, 10, 10)], []), null, null, now));
        }
        using (var prefix = new PrtgSnapshotJournal(_backend))
            Assert.Empty(prefix.Load("source", now)!.Accumulator);

        using (var published = new PrtgSnapshotJournal(_backend))
        {
            published.Load("source", now);
            published.FaultPoint = point => { if (point == "after-append-manifest") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => published.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 2, 20, 1, 20, 20)], []), null, null, now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(20, Assert.Single(recovered.Load("source", now)!.Accumulator).Sum);
    }

    [Fact]
    public void V3已提交中段checksum毀損或sequence遺失_拒絕且保留其餘檔案()
    {
        var now = DateTime.Today;
        using (var writer = new PrtgSnapshotJournal(_backend))
        {
            writer.Load("source", now);
            writer.EnableIncremental("source", now);
            for (var i = 1; i <= 3; i++)
                writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                    [new(now, i, i, 1, i, i)], []), null, null, now);
        }
        var generation = Directory.GetDirectories(Path.GetDirectoryName(new PrtgSnapshotJournal(_backend).FilePath)!, "checkpoint.g.*").Single();
        var segmentDir = Path.Combine(generation, "segments");
        var middle = Path.Combine(segmentDir, "00000000000000000002.json");
        var remaining = File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json"));
        var damaged = File.ReadAllBytes(middle);
        damaged[damaged.Length / 2] ^= 1;
        File.WriteAllBytes(middle, damaged);
        using (var corrupt = new PrtgSnapshotJournal(_backend))
            Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => corrupt.Load("source", now)).Message);
        Assert.Equal(damaged, File.ReadAllBytes(middle));
        Assert.Equal(remaining, File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json")));

        File.Delete(middle);
        using var missing = new PrtgSnapshotJournal(_backend);
        Assert.Contains("遺失", Assert.Throws<InvalidDataException>(() => missing.Load("source", now)).Message);
        Assert.Equal(remaining, File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json")));
    }

    [Theory]
    [InlineData("before-generation-switch")]
    [InlineData("after-generation-switch")]
    public void Rotation切換中斷_完整恢復原generation或新generation(string interruptionPoint)
    {
        var now = DateTime.Today;
        using (var seed = new PrtgSnapshotJournal(_backend))
        {
            seed.Load("source", now);
            seed.EnableIncremental("source", now);
            for (var i = 1; i <= 126; i++)
                seed.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                    [new(now, i, i, 1, i, i)], []), null, null, now);
        }
        using (var writer = new PrtgSnapshotJournal(_backend))
        {
            writer.Load("source", now);
            writer.FaultPoint = point => { if (point == interruptionPoint) throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => writer.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 500, 500, 1, 500, 500)], []), null, null, now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(126, recovered.Load("source", now)!.Accumulator.Count);
    }

    [Fact]
    public void V2snapshot超過100000列_遷移拆成多個有界segments()
    {
        var now = DateTime.Today;
        var rows = Enumerable.Range(1, 100_001)
            .Select(id => new PrtgSnapshotAccumulator.CheckpointRow(now, id, id, 1, id, id, 100)).ToArray();
        using (var journal = new PrtgSnapshotJournal(_backend)) journal.Save("source", rows, [], now);
        using (var journal = new PrtgSnapshotJournal(_backend))
        {
            var state = journal.Load("source", now)!;
            journal.EnableIncremental("source", now);
            Assert.Equal(100_001, state.Accumulator.Count);
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(100_001, recovered.Load("source", now)!.Accumulator.Count);
        var generation = Directory.GetDirectories(Path.GetDirectoryName(recovered.FilePath)!, "checkpoint.g.*").Single();
        var segments = Directory.GetFiles(Path.Combine(generation, "segments"), "*.json");
        Assert.Equal(2, segments.Length);
        Assert.All(segments, path =>
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(json["Snapshot"]!["Accumulator"]!.AsArray().Count <= 100_000);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

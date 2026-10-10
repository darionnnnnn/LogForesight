using System.Text;
using System.Text.Json;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSensorBatchCapabilityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void Shared_runtime_snapshot_contract_accepts_bounded_batch_sizes(int count)
    {
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray();
        var rows = SnapshotRows(ids);
        var result = PrtgSnapshotResponseContract.Validate("{\"sensors\":[" + rows + "]}", ids);
        Assert.True(result.Compatible);
        Assert.True(result.MinimumFieldsObserved);
        Assert.Equal("runtime-snapshot-contract-compatible", result.Reason);
    }

    [Fact]
    public void Shared_runtime_snapshot_contract_enforces_byte_and_depth_bounds()
    {
        Assert.Equal("response-byte-limit", PrtgSnapshotResponseContract.Validate(new string(' ', 512 * 1024 + 1), [1]).Reason);
        var deeplyNested = "{\"sensors\":[{\"objid\":1,\"status\":\"Up\",\"lastcheck\":1,\"extra\":" +
            new string('[', 33) + "0" + new string(']', 33) + "}]}";
        Assert.Equal("malformed-json", PrtgSnapshotResponseContract.Validate(deeplyNested, [1]).Reason);
    }

    [Fact]
    public void Legacy_profile_identity_remains_two_to_five_id_shape_and_non_authoritative()
    {
        var result = PrtgSensorBatchCapability.Parse("{\"sensors\":[{\"objid\":982344},{\"objid\":871233}]}", [871233, 982344]);
        Assert.True(result.ExactRequestedSet);
        Assert.Equal(new[] { "b2", "b1" }, result.ReturnedAliases);
        Assert.False(result.AuthorizesFormalProfile);
        Assert.DoesNotContain("871233", JsonSerializer.Serialize(result));
        Assert.Throws<ArgumentException>(() => PrtgSensorBatchCapability.Parse("{}", Enumerable.Range(1, 6).Select(i => (long)i).ToArray()));
    }

    [Theory]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":11}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":33}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22},{\"objid\":33}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":11,\"objid\":22}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},null]}")]
    [InlineData("[]")]
    public void Legacy_profile_identity_requires_exact_unique_rows(string json)
    {
        Assert.False(PrtgSensorBatchCapability.Parse(json, [11, 22]).ExactRequestedSet);
    }

    [Fact]
    public void Snapshot100_requires_full_exact_set_and_bounds_metadata()
    {
        var ids = Enumerable.Range(1, 100).Select(i => 900000L + i).ToArray();
        var rows = SnapshotRows(ids.Reverse());
        var result = PrtgSnapshotBatch100Capability.Parse("{\"sensors\":[" + rows + "]}", ids, 4000);
        Assert.True(result.ExactRequestedSet);
        Assert.True(result.RuntimeResponseCompatible);
        Assert.True(result.MinimumFieldsObserved);
        Assert.True(result.FullBatch100Observed);
        Assert.False(result.ProfileAuthorized);
        Assert.False(result.CapacityAccepted);
        Assert.False(result.AuthorizesFormalProfile);
        Assert.Equal("snapshot-filter-batch100-v1", result.RequestShapeVersionValue);
        Assert.Equal(64, result.ShapeFingerprint.Length);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("900001", json);
        Assert.DoesNotContain("Up", json);
        Assert.DoesNotContain("2026-10-10", json);
        Assert.DoesNotContain("CUSTOMER_SECRET", json);
        Assert.Contains("maximum_response_bytes", json);
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, 64 * 1024);
    }

    [Fact]
    public void Sentinel_partial_or_legacy_only_evidence_never_claims_full100()
    {
        var ids = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var sentinelRows = string.Join(',', ids.Select(id => "{\"objid\":" + id + "}").Append("{\"objid\":1001}"));
        var sentinel = PrtgSnapshotBatch100Capability.Parse("{\"sensors\":[" + sentinelRows + "]}", ids, 4000);
        Assert.True(sentinel.Truncated);
        Assert.False(sentinel.ExactRequestedSet);
        Assert.False(sentinel.FullBatch100Observed);
        var shortIds = ids.Take(5).ToArray();
        var five = PrtgSnapshotBatch100Capability.Parse("{\"sensors\":[" + string.Join(',', shortIds.Select(id => "{\"objid\":" + id + "}")) + "]}", shortIds, 1000);
        Assert.True(five.ExactRequestedSet);
        Assert.False(five.FullBatch100Observed);
        var old = PrtgSensorBatchCapability.Parse("{\"sensors\":[{\"objid\":1},{\"objid\":2}]}", [1, 2]);
        Assert.True(old.ExactRequestedSet); // Still readable, but has no vNext snapshot observation.
        var oldEvidence = JsonSerializer.Serialize(new { sensor_batch_identity = old });
        Assert.DoesNotContain("snapshot_batch100_identity", oldEvidence);
    }

    [Fact]
    public async Task Full_probe_preserves_legacy_profile_and_adds_distinct_snapshot100_request()
    {
        var ids = Enumerable.Range(1, 100).Select(i => 880000L + i).ToArray();
        var samples = ids.Reverse().Concat(new[] { ids[0], ids[50], -19L, 0L })
            .Select(id => new PrtgProbeRunner.SensorTypeSample("fixture", null, null, id, "Up")).ToArray();
        string? legacyUrl = null;
        string? snapshotUrl = null;
        var legacyRows = string.Join(',', ids.Take(5).Select(id => "{\"objid\":" + id + "}"));
        var snapshotRows = SnapshotRows(ids.Reverse());
        var result = await PrtgCompatibilityProbe.ExecuteCoreAsync((_, _) => Task.FromResult("{}"), new BatchConsole(), samples, null,
            getSensorBatchJson: (url, _) => { legacyUrl = url; return Task.FromResult("{\"sensors\":[" + legacyRows + "]}"); },
            getSnapshotBatch100Json: (url, _) => { snapshotUrl = url; return Task.FromResult("{\"sensors\":[" + snapshotRows + "]}"); });

        Assert.Contains("columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince", legacyUrl);
        Assert.Contains("&count=6", legacyUrl);
        Assert.Contains("columns=objid,lastvalue,interval,lastcheck,status,primarychannel", snapshotUrl);
        Assert.Contains("&count=101", snapshotUrl);
        Assert.InRange(Encoding.UTF8.GetByteCount(snapshotUrl!), 1, PrtgSnapshotBatch100Capability.MaximumRelativeUrlBytes);
        var requestedIds = System.Text.RegularExpressions.Regex.Matches(snapshotUrl!, "&filter_objid=([0-9]+)")
            .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(ids.Order(), requestedIds);
        Assert.Equal(2, result.Summary.RequestsAttempted);
        Assert.True(result.SensorBatchIdentity!.ExactRequestedSet);
        Assert.True(result.SnapshotBatch100Identity!.ExactRequestedSet);
        Assert.True(result.SnapshotBatch100Identity.RuntimeResponseCompatible);
        Assert.True(result.SnapshotBatch100Identity.FullBatch100Observed);
        var serialized = PrtgCompatibilityProbe.SerializeEvidence(result);
        Assert.Contains("sensor_batch_identity", serialized);
        Assert.Contains("snapshot_batch100_identity", serialized);
        Assert.Contains("\"full_batch100_observed\":true", serialized);
        Assert.DoesNotContain("880001", serialized);
        Assert.DoesNotContain("/api/table.json?", serialized);
    }

    [Fact]
    public async Task Existing_bounded_sample_under_100_is_observed_as_partial_capability_not_full100()
    {
        var samples = Enumerable.Range(1, 99).Select(i => new PrtgProbeRunner.SensorTypeSample("fixture", null, null, 700000 + i, "Up")).ToArray();
        var returned = SnapshotRows(samples.Select(s => s.Objid!.Value));
        var result = await PrtgCompatibilityProbe.ExecuteCoreAsync((_, _) => Task.FromResult("{}"), new BatchConsole(), samples, null,
            getSnapshotBatch100Json: (url, _) =>
            {
                Assert.Contains("&count=100", url);
                Assert.DoesNotContain("700100", url);
                return Task.FromResult("{\"sensors\":[" + returned + "]}");
            });
        Assert.Equal(99, result.SnapshotBatch100Identity!.RequestedCount);
        Assert.True(result.SnapshotBatch100Identity.ExactRequestedSet);
        Assert.False(result.SnapshotBatch100Identity.FullBatch100Observed);
        Assert.False(result.SnapshotBatch100Identity.CapacityAccepted);
    }

    [Fact]
    public void Exact_id_set_without_status_or_lastcheck_is_not_runtime_compatible()
    {
        var ids = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var json = "{\"sensors\":[" + string.Join(',', ids.Select(id => "{\"objid\":" + id + "}")) + "]}";
        var result = PrtgSnapshotBatch100Capability.Parse(json, ids, 4000);
        Assert.True(result.ExactRequestedSet);
        Assert.False(result.RuntimeResponseCompatible);
        Assert.False(result.MinimumFieldsObserved);
        Assert.Equal("minimum-snapshot-fields-missing", result.RuntimeResponseReason);
        Assert.False(result.FullBatch100Observed);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("lastcheck")]
    public void Exact_id_set_with_either_minimum_field_missing_keeps_identity_only(string missingField)
    {
        var ids = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var rows = ids.Select(id => missingField switch
        {
            "status" => "{\"objid\":" + id + ",\"lastcheck\":1728547323}",
            _ => "{\"objid\":" + id + ",\"status\":\"Up\"}"
        });
        var result = PrtgSnapshotBatch100Capability.Parse("{\"sensors\":[" + string.Join(',', rows) + "]}", ids, 4000);
        Assert.True(result.ExactRequestedSet);
        Assert.False(result.RuntimeResponseCompatible);
        Assert.False(result.FullBatch100Observed);
        Assert.Equal("minimum-snapshot-fields-missing", result.RuntimeResponseReason);
    }

    [Fact]
    public void Case_ambiguous_nested_properties_keep_id_observation_but_fail_runtime_contract()
    {
        var ids = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var rows = string.Join(',', ids.Select(id => "{\"objid\":" + id + ",\"status\":\"Up\",\"lastcheck\":\"2026-10-10 00:00:00\",\"nested\":{\"x\":1,\"X\":2}}"));
        var result = PrtgSnapshotBatch100Capability.Parse("{\"sensors\":[" + rows + "]}", ids, 4000);
        Assert.True(result.ExactRequestedSet);
        Assert.False(result.RuntimeResponseCompatible);
        Assert.False(result.MinimumFieldsObserved);
        Assert.Equal("duplicate-or-case-ambiguous-properties", result.RuntimeResponseReason);
        Assert.False(result.FullBatch100Observed);
    }

    private static string SnapshotRows(IEnumerable<long> ids) => string.Join(',', ids.Select(id =>
        "{\"objid\":" + id + ",\"status\":\"Up\",\"lastcheck\":\"2026-10-10 00:00:00\"}"));

    private sealed class BatchConsole : IRunConsole { public void WriteLine(string message = "") { } }
}

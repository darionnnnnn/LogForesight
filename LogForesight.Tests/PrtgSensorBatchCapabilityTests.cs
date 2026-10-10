using System.Text.Json;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSensorBatchCapabilityTests
{
    [Fact]
    public void Permuted_numeric_and_string_ids_are_exact_but_do_not_authorize_profile()
    {
        var result = PrtgSensorBatchCapability.Parse("{\"sensors\":[{\"objid\":\"982344\",\"device\":\"private host\"},{\"objid\":871233}]}", [871233, 982344]);
        Assert.True(result.ExactRequestedSet);
        Assert.Equal(new[] { "b2", "b1" }, result.ReturnedAliases);
        Assert.False(result.AuthorizesFormalProfile);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("871233", json);
        Assert.DoesNotContain("982344", json);
        Assert.DoesNotContain("private host", json);
    }

    [Theory]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":11}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":33}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22},{\"objid\":33}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":11,\"objid\":22}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22,\"Objid\":33}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},null]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22.5}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":\"22e0\"}]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22}],\"sensors\":[]}")]
    [InlineData("{\"sensors\":[{\"objid\":11},{\"objid\":22}],\"Sensors\":[]}")]
    [InlineData("[]")]
    [InlineData("{")]
    public void Same_row_count_is_insufficient_for_exact_set(string json)
    {
        var result = PrtgSensorBatchCapability.Parse(json, [11, 22]);
        Assert.False(result.ExactRequestedSet);
        Assert.False(result.AuthorizesFormalProfile);
        Assert.NotEqual("ok", result.Status);
    }

    [Fact]
    public void Byte_row_and_depth_bounds_reject_without_partial_success()
    {
        Assert.False(PrtgSensorBatchCapability.Parse(new string(' ', 512 * 1024 + 1), [11, 22]).ExactRequestedSet);
        Assert.False(PrtgSensorBatchCapability.Parse("{\"sensors\":[{\"objid\":11},{\"objid\":22},{\"objid\":33},{\"objid\":44}]}", [11, 22]).ExactRequestedSet);
        Assert.False(PrtgSensorBatchCapability.Parse(new string('[', 33) + new string(']', 33), [11, 22]).ExactRequestedSet);
    }

    [Fact]
    public async Task Full_probe_adds_one_bounded_read_only_exact_set_observation()
    {
        string? batchUrl = null;
        var calls = 0;
        var result = await PrtgCompatibilityProbe.ExecuteCoreAsync((_, _) => Task.FromResult("{}"),
            new BatchConsole(), [new("SNMP CPU Load", null, null, 11, "Up"), new("SNMP Memory", null, null, 22, "Up")], null,
            getSensorBatchJson: (url, _) =>
            {
                calls++; batchUrl = url;
                return Task.FromResult("{\"sensors\":[{\"objid\":22},{\"objid\":11}]}");
            });
        Assert.Equal(1, calls);
        Assert.Contains("&filter_objid=11&filter_objid=22&count=3", batchUrl);
        Assert.True(result.SensorBatchIdentity!.ExactRequestedSet);
        Assert.False(result.EvidenceReady);
        var json = PrtgCompatibilityProbe.SerializeEvidence(result);
        Assert.Contains("sensor_batch_identity", json);
        Assert.DoesNotContain("filter_objid", json);
    }
    private sealed class BatchConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class NativePrimaryCapabilityRedTests
{
    [Fact]
    public async Task ExecuteAsync_serializesNativePrimaryCapabilityEvidenceFromReadOnlyClientPath()
    {
        var handler = new NativeProbeHandler();
        using var client = new PrtgClient("https://prtg.example.test", "test-token", 30, false,
            handler, PrtgAuthModes.Token, "", "", "");
        var sensor = new PrtgProbeRunner.SensorTypeSample("SNMP Disk Free", null, 100, 1003, "Up");

        var evidence = await PrtgCompatibilityProbe.ExecuteAsync(client, new TestConsole(), [sensor],
            new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" });
        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        using var document = JsonDocument.Parse(json);
        var target = document.RootElement.GetProperty("targets").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "disk");

        // Baseline-compiling RED assertion: before this feature, the serialized target has no key.
        var capability = target.GetProperty("native_primary_capability");
        Assert.Equal("s3", capability.GetProperty("requested_object_alias").GetString());
        Assert.Equal("31003", capability.GetProperty("primary_channel_id").GetString());
        Assert.Contains(handler.Requests, url => url.Contains(
            "/api/getobjectproperty.htm?id=1003&name=primarychannel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_requestsAndSerializesObservedSnapshotPrimaryChannelDiscovery()
    {
        var handler = new NativeProbeHandler();
        using var client = new PrtgClient("https://prtg.example.test", "test-token", 30, false,
            handler, PrtgAuthModes.Token, "", "", "");
        var sensor = new PrtgProbeRunner.SensorTypeSample("SNMP Disk Free", null, 100, 1003, "Up");

        var evidence = await PrtgCompatibilityProbe.ExecuteAsync(client, new TestConsole(), [sensor],
            new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" });
        using var document = JsonDocument.Parse(PrtgCompatibilityProbe.SerializeEvidence(evidence));
        var snapshot = document.RootElement.GetProperty("targets").EnumerateArray()
            .Single(item => item.GetProperty("category").GetString() == "disk")
            .GetProperty("snapshot");

        // This compiles against the baseline; it fails there because the response field is not requested or summarized.
        Assert.Equal("31003", snapshot.GetProperty("native_primary_channel_id").GetString());
        Assert.Equal("primarychannel", snapshot.GetProperty("primary_channel_field_name").GetString());
        Assert.False(snapshot.GetProperty("reported_at_sample_time").GetBoolean());
        Assert.Contains(handler.Requests, url => url.Contains(
            "columns=objid,type,status,lastvalue_raw,lastcheck,interval,primarychannel", StringComparison.Ordinal));
    }

    private sealed class TestConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private sealed class NativeProbeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Requests.Add(request.RequestUri.PathAndQuery);
            var body = path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? "<prtg><result>31003</result></prtg>"
                : path.EndsWith("historicdata.xml", StringComparison.Ordinal)
                    ? "<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Disk Free\" channelid=\"31003\">24 %</value><value_raw channel=\"Disk Free\" channelid=\"31003\">24</value_raw></item></histdata>"
                    : query.Contains("content=sensors", StringComparison.Ordinal)
                        ? "{\"sensors\":[{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"status_raw\":3,\"lastvalue_raw\":24,\"lastcheck_raw\":46288.5,\"interval\":60,\"primarychannel\":31003}]}"
                        : query.Contains("content=channels", StringComparison.Ordinal)
                            ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk Free\",\"unit\":\"percent\",\"scaling\":1,\"primary\":false}]}"
                            : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}";
            var contentType = body.StartsWith('<') ? "application/xml" : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}

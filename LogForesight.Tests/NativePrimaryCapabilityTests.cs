using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class NativePrimaryCapabilityTests
{
    private sealed class TestConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    [Fact]
    public async Task Snapshot_requestsPrimaryChannelDiscoveryAndRetainsOnlyObservedNonnegativeIntegerId()
    {
        var requests = new List<string>();
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) =>
        {
            requests.Add(url);
            return Task.FromResult(url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"lastvalue_raw\":24,\"lastcheck\":\"2026-10-08 00:00:00\",\"interval\":60,\"primarychannel\":0}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}");
        }, new TestConsole(), CreateSamples(), new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" },
            getHistoricXml: (_, _) => Task.FromResult(Response("<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Disk\" channelid=\"0\">24</value></item></histdata>")),
            getNativePrimaryXml: (_, _) => Task.FromResult(Response("<prtg><result>0</result></prtg>")));

        var target = evidence.Targets.Single(item => item.Category == "disk");
        Assert.Contains(requests, url => url.Contains("columns=objid,type,status,lastvalue_raw,lastcheck,interval,primarychannel", StringComparison.Ordinal));
        Assert.Equal("0", target.Snapshot!.NativePrimaryChannelId);
        Assert.Equal("primarychannel", target.Snapshot!.PrimaryChannelFieldName);
        Assert.DoesNotContain("primarychannel_raw", target.Snapshot.MissingFields);
        Assert.False(target.Snapshot.ReportedAtSampleTime);
        Assert.Equal((bool?)true, target.NativePrimaryCapability!.MatchesSnapshotPrimaryChannelId);
        Assert.False(target.NativePrimaryCapability.AuthorizesFormalProfile);
    }

    [Fact]
    public async Task SnapshotConflictingPrimaryAliasesStayUnknownAndDoNotClaimNativeMatch()
    {
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"lastvalue_raw\":24,\"primarychannel\":31003,\"primarychannel_raw\":31004}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}") ,
            new TestConsole(), CreateSamples(), new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" },
            getNativePrimaryXml: (_, _) => Task.FromResult(Response("<prtg><result>31003</result></prtg>")));

        var target = evidence.Targets.Single(item => item.Category == "disk");
        Assert.Null(target.Snapshot!.NativePrimaryChannelId);
        Assert.Null(target.Snapshot.PrimaryChannelFieldName);
        Assert.Contains("primarychannel", target.Snapshot.MissingRequestedFields);
        Assert.False(target.Snapshot.ReportedAtSampleTime);
        Assert.Null(target.NativePrimaryCapability!.MatchesSnapshotPrimaryChannelId);
        Assert.False(target.NativePrimaryCapability.AuthorizesFormalProfile);
    }

    [Fact]
    public async Task SnapshotEqualPrimaryAliasesRetainIdAndReportObservedRawAlias()
    {
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"lastvalue_raw\":24,\"primarychannel\":31003,\"primarychannel_raw\":31003}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}") ,
            new TestConsole(), CreateSamples(), null);

        var snapshot = evidence.Targets.Single(item => item.Category == "disk").Snapshot!;
        Assert.Equal("31003", snapshot.NativePrimaryChannelId);
        Assert.Equal("primarychannel_raw", snapshot.PrimaryChannelFieldName);
        Assert.DoesNotContain("primarychannel", snapshot.MissingRequestedFields);
        Assert.DoesNotContain("primarychannel_raw", snapshot.MissingFields);
        Assert.False(snapshot.ReportedAtSampleTime);
    }

    [Fact]
    public async Task NativePropertyDifferentFromSnapshotPrimaryIsDiagnosticOnly()
    {
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"lastvalue_raw\":24,\"primarychannel\":31004}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}") ,
            new TestConsole(), CreateSamples(), new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" },
            getNativePrimaryXml: (_, _) => Task.FromResult(Response("<prtg><result>31003</result></prtg>")));

        var target = evidence.Targets.Single(item => item.Category == "disk");
        Assert.Equal("31004", target.Snapshot!.NativePrimaryChannelId);
        Assert.Equal((bool?)false, target.NativePrimaryCapability!.MatchesSnapshotPrimaryChannelId);
        Assert.False(target.NativePrimaryCapability.AuthorizesFormalProfile);
    }

    [Theory]
    [InlineData("\"primarychannel\":\"31003\"")]
    [InlineData("\"primarychannel\":true")]
    public async Task SnapshotNonIntegerOrBooleanPrimaryValueDoesNotBecomeAnId(string primaryField)
    {
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) => Task.FromResult(
            url.Contains("content=sensors", StringComparison.Ordinal)
                ? $"{{\"sensors\":[{{\"objid\":1003,\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"lastvalue_raw\":24,{primaryField}}}]}}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}") ,
            new TestConsole(), CreateSamples(), null);

        var snapshot = evidence.Targets.Single(item => item.Category == "disk").Snapshot!;
        Assert.Null(snapshot.NativePrimaryChannelId);
        Assert.Equal("primarychannel", snapshot.PrimaryChannelFieldName);
        Assert.Contains("primarychannel", snapshot.MissingRequestedFields);
        Assert.False(snapshot.ReportedAtSampleTime);
    }

    [Fact]
    public async Task NativePropertyProbe_usesSelectedSensorAndMatchesReturnedIdAgainstObservedRawChannels()
    {
        var requests = new List<string>();
        var evidence = await RunAsync(
            nativeXml: (url, _) =>
            {
                requests.Add(url);
                Assert.Equal("/api/getobjectproperty.htm?id=1003&name=primarychannel", url);
                return Task.FromResult(Response("<prtg><result>31003</result></prtg>"));
            });

        Assert.Single(requests);
        Assert.Equal(5, evidence.Summary.RequestsAttempted);
        var target = evidence.Targets.Single(target => target.Category == "disk");
        var capability = target.NativePrimaryCapability!;
        Assert.Equal("getobjectproperty.htm", capability.Endpoint);
        Assert.Equal("primarychannel", capability.RequestedPropertyName);
        Assert.Equal("s3", capability.RequestedObjectAlias);
        Assert.Equal("ok", capability.Status);
        Assert.Equal("success", capability.ResponseStatus);
        Assert.Equal("2xx", capability.HttpStatus);
        Assert.Equal("xml", capability.ResponseFormat);
        Assert.Equal("31003", capability.PropertyValue);
        Assert.Equal("31003", capability.PrimaryChannelId);
        Assert.True(capability.MatchesRawObservedChannelIds);
        Assert.Equal("24.1.92.1554+", capability.SourceVersion);
        Assert.False(capability.AuthorizesFormalProfile);
        Assert.False(evidence.EvidenceReady);

        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);
        // The channel ID 31003 legitimately contains the sensor ID's digits. Check the
        // serialized object identity and request URL, rather than an unrelated substring.
        Assert.DoesNotContain("id=1003", json);
        using var document = JsonDocument.Parse(json);
        var serializedTarget = document.RootElement.GetProperty("targets").EnumerateArray()
            .Single(target => target.GetProperty("category").GetString() == "disk");
        var serializedObject = serializedTarget.GetProperty("snapshot").GetProperty("fields")
            .EnumerateArray().Single(field => field.GetProperty("field").GetString() == "objid");
        Assert.Equal("s3", serializedObject.GetProperty("value").GetString());
        var serialized = serializedTarget.GetProperty("native_primary_capability");
        Assert.Equal("s3", serialized.GetProperty("requested_object_alias").GetString());
        Assert.Equal("31003", serialized.GetProperty("primary_channel_id").GetString());
        Assert.Equal("success", serialized.GetProperty("response_status").GetString());
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, PrtgCompatibilityProbe.MaxJsonSizeBytes);
    }

    [Theory]
    [InlineData("<prtg><result>not-an-id</result></prtg>", "invalid_property_value")]
    [InlineData("<prtg><result>-2</result></prtg>", "invalid_property_value")]
    [InlineData("<prtg><result>1</result><result>2</result></prtg>", "ambiguous_property_value")]
    [InlineData("<prtg><result><nested>31003</nested></result></prtg>", "invalid_property_result_shape")]
    [InlineData("<prtg><result>31003</result><data><result>31003</result></data></prtg>", "ambiguous_property_value")]
    [InlineData("<prtg><result xmlns=\"urn:vendor\">31003</result></prtg>", "invalid_property_result_shape")]
    [InlineData("<prtg><result>31003</result><result xmlns=\"urn:vendor\">31003</result></prtg>", "ambiguous_property_value")]
    [InlineData("<prtg><error>private source response</error></prtg>", "property_result_missing")]
    [InlineData("<prtg><result>", "malformed_xml")]
    [InlineData("<html>redirect or error page</html>", "unexpected_xml_root")]
    [InlineData("<!DOCTYPE prtg [<!ENTITY x '1'>]><prtg><result>&x;</result></prtg>", "malformed_xml")]
    public async Task NativePropertyProbe_invalidOrAmbiguousResponseNeverAuthorizes(string xml, string expectedError)
    {
        var evidence = await RunAsync((_, _) => Task.FromResult(Response(xml)));
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("unknown", capability.Status);
        Assert.Equal(expectedError, capability.Error);
        Assert.Null(capability.PropertyValue);
        Assert.Null(capability.PrimaryChannelId);
        Assert.False(capability.AuthorizesFormalProfile);
        Assert.False(evidence.EvidenceReady);
    }

    [Fact]
    public async Task NativePropertyProbe_overLimitResponseIsUnknownWithoutRetainingBody()
    {
        var response = Response("<prtg><result>" + new string('8', 512 * 1024) + "</result></prtg>");
        var evidence = await RunAsync((_, _) => Task.FromResult(response));
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("unknown", capability.Status);
        Assert.Equal("response_over_limit", capability.Error);
        Assert.Null(capability.PropertyValue);
        Assert.False(capability.AuthorizesFormalProfile);
        Assert.DoesNotContain(new string('8', 64), PrtgCompatibilityProbe.SerializeEvidence(evidence));
    }

    [Fact]
    public async Task NativePropertyProbe_malformedSourceVersionRemainsUnknownAndDoesNotRetainPropertyValue()
    {
        var evidence = await RunAsync((_, _) => Task.FromResult(Response("<prtg><result>31003</result></prtg>")),
            rawHistory: "<histdata totalcount=\"1\"><prtg-version>vendor-secret</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Disk\" channelid=\"31003\">24</value><value_raw channel=\"Disk\" channelid=\"31003\">24</value_raw></item></histdata>");
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("unknown", capability.Status);
        Assert.Equal("unknown", capability.SourceVersion);
        Assert.Equal("source-version-malformed", capability.Error);
        Assert.Null(capability.PropertyValue);
        Assert.False(capability.AuthorizesFormalProfile);
        Assert.DoesNotContain("vendor-secret", PrtgCompatibilityProbe.SerializeEvidence(evidence));
    }

    [Fact]
    public async Task NativePropertyProbe_transportErrorReportsOnlySanitizedHttpClass()
    {
        var evidence = await RunAsync((_, _) => Task.FromException<PrtgSourceResponse>(
            new PrtgClientException("PRTG server HTTP 403 secret=source-sensitive")));
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("error", capability.Status);
        Assert.Equal("error", capability.ResponseStatus);
        Assert.Equal("403", capability.HttpStatus);
        Assert.Equal("HTTP 403", capability.Error);
        Assert.False(capability.AuthorizesFormalProfile);
        Assert.DoesNotContain("source-sensitive", PrtgCompatibilityProbe.SerializeEvidence(evidence));
    }

    [Fact]
    public async Task NativePropertyProbe_withoutValidRawChannelIdStillQueriesSelectedSensorAndKeepsMatchUnknown()
    {
        var calls = 0;
        var evidence = await RunAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("<prtg><result>31003</result></prtg>"));
        }, rawHistory: "<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Disk\" channelid=\"private-id\">24</value><value_raw channel=\"Disk\" channelid=\"private-id\">24</value_raw></item></histdata>");

        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;
        Assert.Equal(1, calls);
        Assert.Equal("ok", capability.Status);
        Assert.Equal("s3", capability.RequestedObjectAlias);
        Assert.Equal("31003", capability.PrimaryChannelId);
        Assert.Null(capability.MatchesRawObservedChannelIds);
        Assert.False(capability.AuthorizesFormalProfile);

        using var json = JsonDocument.Parse(PrtgCompatibilityProbe.SerializeEvidence(evidence));
        var serializedCapability = json.RootElement.GetProperty("targets").EnumerateArray()
            .Single(target => target.GetProperty("category").GetString() == "disk")
            .GetProperty("native_primary_capability");
        Assert.Equal(JsonValueKind.Null, serializedCapability.GetProperty("matches_raw_observed_channel_ids").ValueKind);
    }

    [Fact]
    public async Task NativePropertyProbe_reportsFalseWhenReturnedPrimaryIdDoesNotMatchRawHistoryIds()
    {
        var evidence = await RunAsync((_, _) => Task.FromResult(Response("<prtg><result>31004</result></prtg>")));
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("ok", capability.Status);
        Assert.Equal("31004", capability.PrimaryChannelId);
        Assert.False(capability.MatchesRawObservedChannelIds);
        Assert.False(capability.AuthorizesFormalProfile);
    }

    [Fact]
    public async Task NativePropertyProbe_runsWhenHistoricXmlDelegateIsUnavailable()
    {
        var requests = new List<string>();
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(
            (url, _) => Task.FromResult(url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"status\":\"Up\",\"lastvalue_raw\":24}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":31003,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}"),
            new TestConsole(), CreateSamples(), new PrtgProbeEvidenceContext(),
            getNativePrimaryXml: (url, _) =>
            {
                requests.Add(url);
                return Task.FromResult(Response("<prtg><result>31003</result></prtg>"));
            });
        var capability = evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability!;

        Assert.Equal("/api/getobjectproperty.htm?id=1003&name=primarychannel", Assert.Single(requests));
        Assert.Equal(4, evidence.Summary.RequestsAttempted);
        Assert.Equal("31003", capability.PrimaryChannelId);
        Assert.Null(capability.MatchesRawObservedChannelIds);
        Assert.False(capability.AuthorizesFormalProfile);
    }

    [Fact]
    public async Task LegacyExecuteCoreWithoutOptionalXmlDelegatesDoesNotAddRequestsOrCapabilityAuthority()
    {
        var calls = 0;
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync((url, _) =>
        {
            calls++;
            return Task.FromResult(url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"status\":\"Up\",\"lastvalue_raw\":24}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":3,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}" );
        }, new TestConsole(), CreateSamples(), null);

        Assert.Equal(3, calls);
        Assert.Equal(3, evidence.Summary.RequestsAttempted);
        Assert.Null(evidence.Targets.Single(target => target.Category == "disk").NativePrimaryCapability);
        Assert.False(evidence.EvidenceReady);
    }

    private static Task<PrtgCompatibilityProbeEvidence> RunAsync(
        Func<string, CancellationToken, Task<PrtgSourceResponse>> nativeXml,
        string? rawHistory = null)
    {
        var context = new PrtgProbeEvidenceContext { SourcePrtgVersion = "24.1.92.1554+" };
        var xml = rawHistory ?? "<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>46288.5</datetime_raw><value channel=\"Disk\" channelid=\"31003\">24 %</value><value_raw channel=\"Disk\" channelid=\"31003\">24</value_raw></item></histdata>";
        return PrtgCompatibilityProbe.ExecuteCoreAsync(
            (url, _) => Task.FromResult(url.Contains("content=sensors", StringComparison.Ordinal)
                ? "{\"sensors\":[{\"objid\":1003,\"status\":\"Up\",\"lastvalue_raw\":24}]}"
                : url.Contains("content=channels", StringComparison.Ordinal)
                    ? "{\"channels\":[{\"objid\":444,\"name\":\"Disk\"}]}"
                    : "{\"histdata\":[{\"datetime_raw\":46288.5,\"value_raw\":24}]}"),
            new TestConsole(), CreateSamples(), context,
            getHistoricXml: (_, _) => Task.FromResult(Response(xml)),
            getNativePrimaryXml: nativeXml);
    }

    private static PrtgSourceResponse Response(string body) =>
        new(body, DateTimeOffset.Parse("2026-10-09T00:00:00Z"), DateTimeOffset.Parse("2026-10-09T00:00:01Z"));

    private static List<PrtgProbeRunner.SensorTypeSample> CreateSamples() =>
    [new("SNMP Disk Free", null, 100, 1003, "Up")];
}

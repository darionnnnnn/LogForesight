using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotPrimaryChannelFallbackTests
{
    [Fact]
    public void CandidateWithoutProfileUsesCapturedBindingButExistingProfileMustAgree()
    {
        var binding = PrtgTrustedSamplingBinding.Create(77, "3", "CPU",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds", "UTC", "UTC",
            "time-basis", "settings-v1", "policy-v1", new string('A', 64), "source-v1",
            "resource-v1", 1, "channel-v1", 1);
        var now = DateTime.UtcNow;
        var bracket = new PrtgNativePrimaryChannelBracket(77, "3", "3", now, now, now);

        // Candidate upper bounds may contain a valid binding that has not been qualified yet.
        Assert.True(PrtgSnapshotPrimaryChannelFallback.MatchesCapturedBinding(bracket, binding, null));

        var profile = new PrtgTrustedSamplingProfile(77, "source-v1", "resource-v1", 1, "channel-v1",
            "cpu", "4", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "v1",
            "strategy", 15, DateTime.UtcNow, TimeSpan.FromMinutes(15), "minutes", "UTC", "UTC", "UTC",
            DateTimeOffset.UtcNow, "metadata-ref", "sample-ref", true, 1, 1,
            DateTime.UtcNow.ToOADate(), DateTime.UtcNow.ToOADate(), "digest", BindingRevision: 1,
            QualificationProofReference: "");
        Assert.False(PrtgSnapshotPrimaryChannelFallback.MatchesCapturedBinding(bracket, binding, profile));
        Assert.False(PrtgSnapshotPrimaryChannelFallback.MatchesCapturedBinding(
            bracket with { BeforeId = "4", AfterId = "4" }, binding, null));
    }

    [Fact]
    public async Task FallbackReadsStablePropertyBeforeAndAfterExactBoundedTableRefetch()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? "<prtg><result>3</result></prtg>"
                : "{\"sensors\":[{\"objid\":77,\"status\":\"Up\",\"lastcheck\":\"46000\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, path.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? "application/xml" : "application/json") });
        });
        using var client = NewClient(handler);

        var result = await PrtgSnapshotPrimaryChannelFallback.ReadGroupAsync(client, [77], CancellationToken.None);

        Assert.Equal(3, result.RequestsSent);
        Assert.Equal(1, result.Rows.Count);
        Assert.Equal(1, result.Brackets.Count);
        var bracket = result.Brackets[77];
        Assert.Equal("3", bracket.BeforeId);
        Assert.Equal("3", bracket.AfterId);
        Assert.True(bracket.BeforeObservedAtUtc <= bracket.SnapshotObservedAtUtc);
        Assert.True(bracket.SnapshotObservedAtUtc <= bracket.AfterObservedAtUtc);
        Assert.InRange(result.ElapsedMilliseconds, 0, 10_000);
        Assert.EndsWith("getobjectproperty.htm", handler.Paths[0]);
        Assert.EndsWith("table.json", handler.Paths[1]);
        Assert.EndsWith("getobjectproperty.htm", handler.Paths[2]);
        Assert.Contains("&count=2", handler.Queries[1]);
    }

    [Fact]
    public async Task FallbackRejectsUnexpectedNativeFieldOrOutOfBoundGroup()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal)
                ? "<prtg><result>3</result></prtg>"
                : "{\"sensors\":[{\"objid\":77,\"status\":\"Up\",\"lastcheck\":\"46000\",\"primarychannel\":3}]}",
                Encoding.UTF8, "application/json")
        }));
        using var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrtgSnapshotPrimaryChannelFallback.ReadGroupAsync(client, [77], CancellationToken.None));
        Assert.Equal(2, handler.Paths.Count);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PrtgSnapshotPrimaryChannelFallback.ReadGroupAsync(client, [1, 2, 3, 4, 5, 6], CancellationToken.None));
    }

    private static PrtgClient NewClient(HttpMessageHandler handler)
    {
        var client = new PrtgClient("https://prtg.example.test", "token", 10, true, handler,
            PrtgAuthModes.Token, "", "", "", new PrtgRequestBudget());
        client.RequestPurpose = PrtgRequestPurpose.CapacityPilot;
        return client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Queries.Add(request.RequestUri.Query);
            return respond(request, cancellationToken);
        }
    }
}

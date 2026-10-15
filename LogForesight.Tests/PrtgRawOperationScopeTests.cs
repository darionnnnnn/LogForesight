using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgRawOperationScopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-raw-operation-scope-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly PrtgMonitoringPolicyStore _policy;

    public PrtgRawOperationScopeTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "scope.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        _policy.Update(policy =>
        {
            policy.CoreSystemId = "core-a";
            policy.SourceGeneration = Guid.NewGuid().ToString("N");
            policy.EndpointHint = "endpoint-a";
            policy.ValidFrom = DateTimeOffset.UtcNow;
            policy.HostIds = [11];
            policy.SensorIds = [21];
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "fixture";
        });
    }

    [Fact]
    public void RawOperationFenceIgnoresMessageFormatButRetainsLegacyFullScopeFence()
    {
        var reader = new PrtgScopeRevisionReader(_backend, _hosts);
        var rawBefore = reader.ReadRawSemantic();
        var fullBefore = reader.Read();

        _policy.Update(policy =>
        {
            policy.MessageDateFormat = PrtgMessageDateTimeParser.Ymd24;
            policy.MessageTimeAuthorityRevision++;
        });

        Assert.Equal(rawBefore, reader.ReadRawSemantic());
        Assert.NotEqual(fullBefore, reader.Read());
    }

    [Fact]
    public void RawOperationFenceRejectsRawAuthorityAtoBtoAAndScopeMembershipChange()
    {
        var reader = new PrtgScopeRevisionReader(_backend, _hosts);
        var initial = reader.ReadRawSemantic();
        var policyBefore = _policy.Get();

        _policy.Update(policy => policy.RawTimestampTimeZoneId = "Pacific Standard Time");
        _policy.Update(policy => policy.RawTimestampTimeZoneId = policyBefore.RawTimestampTimeZoneId);

        Assert.Equal(policyBefore.RawTimestampTimeZoneId, _policy.Get().RawTimestampTimeZoneId);
        Assert.True(_policy.Get().RawAuthorityRevision >= policyBefore.RawAuthorityRevision + 2);
        Assert.NotEqual(initial, reader.ReadRawSemantic());

        var beforeMembershipChange = reader.ReadRawSemantic();
        _policy.Update(policy => policy.SensorIds = [21, 22]);
        Assert.NotEqual(beforeMembershipChange, reader.ReadRawSemantic());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}

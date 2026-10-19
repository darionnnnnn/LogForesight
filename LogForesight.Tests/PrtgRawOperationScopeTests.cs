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
            policy.HostIds = [11];
            policy.SensorIds = [21];
        });
    }

    [Fact]
    public void RawOperationFenceRejectsScopeMembershipChange()
    {
        var reader = new PrtgScopeRevisionReader(_backend, _hosts);
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

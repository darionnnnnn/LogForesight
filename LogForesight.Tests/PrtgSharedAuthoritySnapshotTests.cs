using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSharedAuthoritySnapshotTests
{
    [Fact]
    public void ReturnsBothPersistedDocumentsAndExactVersionsWithoutWriting()
    {
        var backend = NewBackend();
        var settings = new SystemSettings { PrtgUrl = "https://authority.example.invalid", Revision = "saved-revision" };
        var policy = new PrtgMonitoringPolicy { SourceGeneration = Guid.NewGuid().ToString("N") };
        backend.Blob("system_settings").Mutate(_ => (JsonSerializer.Serialize(settings),true));
        backend.Blob(PrtgMonitoringPolicyStore.BlobKey).Mutate(_ => (JsonSerializer.Serialize(policy),true));
        var snapshot = PrtgSharedAuthoritySnapshot.Read(backend);
        Assert.Equal(settings.PrtgUrl,snapshot.Settings.PrtgUrl);
        Assert.Equal(settings.Revision,snapshot.Settings.Revision);
        Assert.Equal(policy.SourceGeneration,snapshot.Policy.SourceGeneration);
        Assert.Equal(1,snapshot.SettingsBlobVersion);
        Assert.Equal(1,snapshot.PolicyBlobVersion);
        Assert.Equal(1,backend.Blob("system_settings").ReadVersion());
    }

    [Theory]
    [InlineData("system_settings",false)]
    [InlineData("prtg_monitoring_policy",false)]
    [InlineData("system_settings",true)]
    [InlineData("prtg_monitoring_policy",true)]
    public void RejectsMalformedOrOversizedSharedDocumentWithReasonAndNoMutation(string key,bool oversized)
    {
        var backend = NewBackend();
        var raw = oversized ? "{\"Future\":\"" + new string('x',PrtgSharedAuthoritySnapshot.MaximumDocumentCharacters) + "\"}" : "{";
        backend.Blob(key).Mutate(_ => (raw,true));
        var before = backend.Blob(key).ReadWithVersion();
        Assert.False(PrtgSharedAuthoritySnapshot.TryRead(backend,out var snapshot,out var reason));
        Assert.Null(snapshot);
        Assert.Equal(PrtgSharedAuthoritySnapshot.UnavailableReason,reason);
        Assert.Equal(before,backend.Blob(key).ReadWithVersion());
    }

    private static StorageBackend NewBackend()
    {
        var root = Path.Combine(Path.GetTempPath(),"lf-r54-shared-authority-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new(new StorageSettings { Type = "Sqlite", ConnectionString = "Data Source="+Path.Combine(root,"owned-test.db")+";Pooling=False" },root);
    }
}

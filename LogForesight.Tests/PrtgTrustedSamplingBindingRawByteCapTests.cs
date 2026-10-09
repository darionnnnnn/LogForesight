using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingBindingRawByteCapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-binding-byte-cap-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;

    public PrtgTrustedSamplingBindingRawByteCapTests() =>
        backend = new(new StorageSettings { Type = "Sqlite" }, root);

    [Fact]
    public void GetAndGetManyBothRejectUtf8OversizeUnknownPaddingWithinCharacterLimit()
    {
        const long sensorId = 11;
        var binding = PrtgTrustedSamplingBinding.Create(sensorId, "7", "CPU load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis", "settings-r1", "policy-r1",
            new string('A', 64), "source-r1", "resource-r1", 1, "channel-r1", 1);
        var json = JsonSerializer.Serialize(binding, LfJsonOptions.Pretty);
        var raw = json[..^1] + ",\"unknownPadding\":\"" + new string('界', 3000) + "\"}";
        Assert.True(raw.Length < PrtgTrustedSamplingBinding.MaximumSerializedBytes);
        Assert.True(Encoding.UTF8.GetByteCount(raw) > PrtgTrustedSamplingBinding.MaximumSerializedBytes);
        backend.Blob(PrtgTrustedSamplingBinding.StorePrefix + sensorId)
            .Mutate(_ => (raw, true));

        var store = new PrtgTrustedSamplingBindingStore(backend);

        Assert.Throws<InvalidDataException>(() => store.Get(sensorId));
        Assert.Throws<InvalidDataException>(() => store.GetMany([sensorId]));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

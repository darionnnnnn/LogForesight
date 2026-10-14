using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskBoundedConsumerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-disk-bounded-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;

    public PrtgDiskBoundedConsumerTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        foreach (var id in new long[] { 11, 12 })
        {
            var evidence = new PrtgDiskSemanticEvidence(id, id + 100, id, "disk", "free", "Free", "%", 1,
                "descending-danger", PrtgDiskSemanticEvidenceSource.Manual, 1, "typed evidence", DateTime.UtcNow,
                PrtgDiskAssessmentService.ParserSemanticVersion, "source", "resource", "channel", 1);
            var result = new PrtgDiskVerificationResult(id, id + 100, id, "disk", "Verified", "typed result",
                "free", "Free", "%", 1, "descending-danger", 1, true, DateTime.UtcNow, DateTime.Today,
                PrtgDiskAssessmentService.ParserSemanticVersion, false, "source", "resource", "channel", 1);
            new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey))
                .Update(all => all[id] = evidence);
            new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(result);
        }
    }

    private static PrtgResourceIdentity Identity(long id) => new()
    {
        SensorId = id, DeviceId = id + 100, HostId = id, Epoch = 1, Active = true,
        SourceGeneration = "source", Generation = "resource", ChannelGeneration = "channel"
    };

    [Theory]
    [InlineData(PrtgDiskSemanticEvidenceStore.BlobKey, false)]
    [InlineData(PrtgDiskVerificationResultStore.BlobKey, false)]
    [InlineData(PrtgDiskSemanticEvidenceStore.BlobKey, true)]
    [InlineData(PrtgDiskVerificationResultStore.BlobKey, true)]
    public void CorruptSharedDocumentRejectsBothDependentHostsAndExport(string key, bool malformed)
    {
        Assert.True(PrtgResourceQualification.IsCurrentDiskChannel(backend, 11, Identity(11)));
        Assert.True(PrtgResourceQualification.IsCurrentDiskChannel(backend, 12, Identity(12)));
        backend.Blob(key).Mutate(raw =>
        {
            using var doc = JsonDocument.Parse(raw!);
            return (malformed ? "not-json" : "{\"11\":" + doc.RootElement.GetProperty("11").GetRawText() + ",\"12\":null}", true);
        });
        var version = backend.Blob(key).ReadVersion();

        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(backend, 11, Identity(11)));
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(backend, 12, Identity(12)));
        var semantic = new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var results = new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey));
        if (malformed)
        {
            Assert.Throws<JsonException>(() => PrtgDataTransfer.Export(backend, DateTime.Today, DateTime.Today));
            if (key == PrtgDiskSemanticEvidenceStore.BlobKey)
            {
                Assert.Throws<JsonException>(() => semantic.Get(11));
                Assert.Throws<JsonException>(() => semantic.GetAll());
            }
            else
            {
                Assert.Throws<JsonException>(() => results.Get(11));
                Assert.Throws<JsonException>(() => results.GetRecent());
            }
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => PrtgDataTransfer.Export(backend, DateTime.Today, DateTime.Today));
            if (key == PrtgDiskSemanticEvidenceStore.BlobKey)
            {
                Assert.Throws<InvalidDataException>(() => semantic.Get(11));
                Assert.Throws<InvalidDataException>(() => semantic.GetAll());
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => results.Get(11));
                Assert.Throws<InvalidDataException>(() => results.GetRecent());
            }
        }
        Assert.Equal(version, backend.Blob(key).ReadVersion());
    }

    [Fact]
    public void ValidExportAndDirectReadRetainCompleteTypedCollections()
    {
        var semantic = new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var results = new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey));
        Assert.Equal(2, semantic.GetAll().Count);
        Assert.Equal(2, semantic.Get().Count);
        Assert.Equal(2, results.GetRecent().Count);
        Assert.NotNull(semantic.Get(11));
        Assert.NotNull(results.Get(12));
        var package = PrtgDataTransfer.Export(backend, DateTime.Today, DateTime.Today);
        Assert.Equal(new long[] { 11, 12 }, package.SemanticEvidence.Select(item => item.SensorObjid).Order());
        Assert.Equal(new long[] { 11, 12 }, package.SemanticResults.Select(item => item.SensorObjid).Order());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgAuthorityStrictJsonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-authority-json-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgAuthorityStrictJsonTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _root);
    }

    [Fact]
    public void DiskSemanticReadAndUpdateRejectDuplicateAliasesAndNonCanonicalKeysWithoutWriting()
    {
        SeedDiskRows();
        var key = PrtgDiskSemanticEvidenceStore.BlobKey;
        var blob = _backend.Blob(key);
        var original = blob.Read()!;
        var originalVersion = blob.ReadVersion();
        var root = JsonDocument.Parse(original).RootElement;
        var first = root.GetProperty("11").GetRawText();
        var second = root.GetProperty("12").GetRawText();

        var duplicateExact = ReplaceEntry(second, "\"SensorObjid\": 12");
        AssertRejectedWithoutWrite(blob, $"{{\"11\":{first},\"12\":{duplicateExact}}}", originalVersion);

        var alias = ReplaceEntry(second, "\"sensorobjid\": 999");
        AssertRejectedWithoutWrite(blob, $"{{\"11\":{first},\"12\":{alias}}}", originalVersion);

        var nonCanonical = $"{{\"11\":{first},\"011\":{first}}}";
        AssertRejectedWithoutWrite(blob, nonCanonical, originalVersion);

        var validSingleAlias = second.Replace("\"SensorObjid\"", "\"sensorobjid\"", StringComparison.Ordinal);
        var validUnknownExtensions = ReplaceEntry(validSingleAlias,
            "\"futureField\": 1, \"FutureField\": 2");
        blob.Mutate(_ => ($"{{\"11\":{first},\"12\":{validUnknownExtensions}}}", true));
        var store = new PrtgDiskSemanticEvidenceStore(blob);
        Assert.Equal(12, store.Get(12)!.SensorObjid);
        store.Update(all => all[12] = Evidence(12));
        using var preserved = JsonDocument.Parse(blob.Read()!);
        Assert.Equal(1, preserved.RootElement.GetProperty("12").GetProperty("futureField").GetInt32());
        Assert.Equal(2, preserved.RootElement.GetProperty("12").GetProperty("FutureField").GetInt32());
    }

    [Fact]
    public void VerificationConsumerRejectsAValidFirstAndAliasedLaterEntryAndKeepsStoredVersion()
    {
        SeedDiskRows();
        var key = PrtgDiskVerificationResultStore.BlobKey;
        var blob = _backend.Blob(key);
        var original = blob.Read()!;
        var version = blob.ReadVersion();
        using var doc = JsonDocument.Parse(original);
        var first = doc.RootElement.GetProperty("11").GetRawText();
        var later = doc.RootElement.GetProperty("12").GetRawText();
        var badLater = ReplaceEntry(later, "\"status\": \"Mismatch\"");
        var bad = $"{{\"11\":{first},\"12\":{badLater}}}";
        blob.Mutate(_ => (bad, true));
        var badVersion = blob.ReadVersion();

        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        Assert.Throws<JsonException>(() => new PrtgDiskVerificationResultStore(blob).Get(11));
        Assert.Equal(badVersion, blob.ReadVersion());
        Assert.Equal(bad, blob.Read());
        Assert.True(badVersion > version);
    }

    [Fact]
    public void DiskMutationCannotOverwriteOrRemoveAWellFormedButInvalidLaterEntry()
    {
        SeedDiskRows();
        foreach (var key in new[] { PrtgDiskSemanticEvidenceStore.BlobKey, PrtgDiskVerificationResultStore.BlobKey })
        {
            var blob = _backend.Blob(key);
            using var source = JsonDocument.Parse(blob.Read()!);
            var first = source.RootElement.GetProperty("11").GetRawText();
            var malformedLater = "null";
            var bad = $"{{\"11\":{first},\"12\":{malformedLater}}}";
            blob.Mutate(_ => (bad, true));
            var version = blob.ReadVersion();

            if (key == PrtgDiskSemanticEvidenceStore.BlobKey)
            {
                var store = new PrtgDiskSemanticEvidenceStore(blob);
                Assert.Throws<InvalidDataException>(() => store.Update(all => all[12] = Evidence(12)));
                Assert.Throws<InvalidDataException>(() => store.Update(all => all.Remove(12)));
            }
            else
            {
                var store = new PrtgDiskVerificationResultStore(blob);
                Assert.Throws<InvalidDataException>(() => store.Update(all => all[12] = Result(12)));
                Assert.Throws<InvalidDataException>(() => store.Update(all => all.Remove(12)));
            }

            Assert.Equal(version, blob.ReadVersion());
            Assert.Equal(bad, blob.Read());
        }
    }

    [Fact]
    public void VerificationUpdatePreservesUnknownFieldsOnUntouchedAndReplacedEntries()
    {
        SeedDiskRows();
        var blob = _backend.Blob(PrtgDiskVerificationResultStore.BlobKey);
        using var source = JsonDocument.Parse(blob.Read()!);
        var first = source.RootElement.GetProperty("11").GetRawText();
        var later = source.RootElement.GetProperty("12").GetRawText();
        var firstWithExtension = ReplaceEntry(first, "\"futureResultField\": {\"Version\":1}");
        var laterWithExtension = ReplaceEntry(later, "\"FutureResultField\": {\"Version\":2}");
        blob.Mutate(_ => ($"{{\"11\":{firstWithExtension},\"12\":{laterWithExtension}}}", true));

        new PrtgDiskVerificationResultStore(blob).Update(all => all[11] = Result(11));

        using var persisted = JsonDocument.Parse(blob.Read()!);
        Assert.Equal(1, persisted.RootElement.GetProperty("11").GetProperty("futureResultField")
            .GetProperty("Version").GetInt32());
        Assert.Equal(2, persisted.RootElement.GetProperty("12").GetProperty("FutureResultField")
            .GetProperty("Version").GetInt32());
    }

    [Fact]
    public void ModeConsumerRejectsDuplicateTypedAuthorityButAcceptsSingleCaseVariantAndDistinctExtensions()
    {
        const long hostId = 7;
        var grant = Grant(hostId, 71);
        var valid = JsonSerializer.Serialize(new PrtgResourcePressureModeDocument { Grants = [grant] });
        var key = PrtgResourcePressureModeStore.BlobKey(hostId);
        var blob = _backend.Blob(key);
        var root = (JsonObject)JsonNode.Parse(valid)!;
        var grants = root["Grants"]!.DeepClone();
        root.Remove("Grants");
        root["grants"] = grants;
        root["grants"]![0]!["futureGrant"] = JsonNode.Parse("{\"Kept\":3}");
        root["grants"]![0]!["FutureGrant"] = JsonNode.Parse("{\"Kept\":4}");
        root["futureField"] = JsonNode.Parse("{\"Kept\":1}");
        root["FutureField"] = JsonNode.Parse("{\"Kept\":2}");
        var lowerCase = root.ToJsonString();
        blob.Mutate(_ => (lowerCase, true));
        var read = new PrtgResourcePressureModeStore(blob).ReadHostSnapshot(hostId);
        Assert.Single(read.Grants);
        Assert.Contains("futureGrant", read.Grants[0].AdditionalProperties!.Keys);
        Assert.Contains("FutureGrant", read.Grants[0].AdditionalProperties!.Keys);
        PrtgResourcePressureModeStore.ValidateRepairDocument(lowerCase, hostId);

        var duplicate = lowerCase.Replace("\"grants\":", "\"Grants\":[],\"grants\":", StringComparison.Ordinal);
        blob.Mutate(_ => (duplicate, true));
        var duplicateVersion = blob.ReadVersion();
        Assert.Throws<JsonException>(() => new PrtgResourcePressureModeStore(blob).ReadHostSnapshot(hostId));
        Assert.Throws<JsonException>(() => new PrtgResourcePressureModeStore(blob).RemoveTrial(hostId, 71,
            PrtgResourceFamily.Cpu, new string('c', 64)));
        Assert.Equal(duplicateVersion, blob.ReadVersion());
        Assert.Equal(duplicate, blob.Read());
    }

    [Fact]
    public void SyntaxRepairStoreSeamMarksRestoredEvidenceInvalidatedAndUnqualified()
    {
        SeedDiskRows();
        var semanticKey = PrtgDiskSemanticEvidenceStore.BlobKey;
        Assert.True(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        var raw = _backend.Blob(semanticKey).Read()!;
        var entry = raw.IndexOf("\"12\"", StringComparison.Ordinal);
        var comma = raw.LastIndexOf(',', entry);
        Assert.True(comma > 0);
        var malformed = raw.Remove(comma, 1);
        _backend.Blob(semanticKey).Mutate(_ => (malformed, true));
        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var snapshot = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, snapshot.State);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, snapshot.Version,
            [new(comma, 0, ",", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_backend.Blob(semanticKey));
        Assert.Equal("repair-invalidated", evidenceStore.Get(11)!.ParserSemanticVersion);
    }

    [Fact]
    public void TextPatchCanRepairStrictAmbiguityButCannotRewriteAValidDocument()
    {
        SeedDiskRows();
        var key = PrtgDiskSemanticEvidenceStore.BlobKey;
        var blob = _backend.Blob(key);
        using var source = JsonDocument.Parse(blob.Read()!);
        var first = source.RootElement.GetProperty("11").GetRawText();
        var later = source.RootElement.GetProperty("12").GetRawText();
        var duplicateField = ",\"sensorobjid\":999";
        var badLater = ReplaceEntry(later, "\"sensorobjid\":999");
        var bad = $"{{\"11\":{first},\"12\":{badLater}}}";
        blob.Mutate(_ => (bad, true));

        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, before.State);
        var deleteAt = bad.IndexOf(duplicateField, StringComparison.Ordinal);
        Assert.True(deleteAt > 0);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, before.Version,
            [new(deleteAt, duplicateField.Length, string.Empty,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(duplicateField))))]);

        var restored = blob.Read()!;
        Assert.Equal("repair-invalidated", new PrtgDiskSemanticEvidenceStore(blob).Get(11)!.ParserSemanticVersion);
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        var restoredVersion = blob.ReadVersion();
        Assert.Throws<InvalidOperationException>(() => repair.ApplyTextPatches(
            PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, restoredVersion,
            [new(0, 0, restored[..1], Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]));
        Assert.Equal(restoredVersion, blob.ReadVersion());
        Assert.Equal(restored, blob.Read());
    }

    [Fact]
    public void TextPatchCanRemoveAnExactDuplicateJsonPropertyAndInvalidatesEveryRestoredEntry()
    {
        SeedDiskRows();
        var blob = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey);
        using var source = JsonDocument.Parse(blob.Read()!);
        var first = source.RootElement.GetProperty("11").GetRawText();
        var later = source.RootElement.GetProperty("12").GetRawText();
        const string duplicate = ",\"SensorObjid\": 12";
        var badLater = ReplaceEntry(later, "\"SensorObjid\": 12");
        var bad = $"{{\"11\":{first},\"12\":{badLater}}}";
        blob.Mutate(_ => (bad, true));

        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, before.State);
        var deleteAt = bad.LastIndexOf(duplicate, StringComparison.Ordinal);
        Assert.True(deleteAt > 0);
        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, before.Version,
            [new(deleteAt, duplicate.Length, string.Empty,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(duplicate))))]);

        var restored = new PrtgDiskSemanticEvidenceStore(blob).Get();
        Assert.Equal("repair-invalidated", restored[11].ParserSemanticVersion);
        Assert.Equal("repair-invalidated", restored[12].ParserSemanticVersion);
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 12, Identity(12)));
    }

    [Fact]
    public void TypedKnownFieldCanUseItsExistingSingleCaseInsensitiveSpelling()
    {
        var value = JsonSerializer.Serialize(new Dictionary<long, PrtgDiskSemanticEvidence>
        {
            [11] = Evidence(11)
        });
        using var document = JsonDocument.Parse(value);
        var entry = document.RootElement.GetProperty("11").GetRawText()
            .Replace("\"SensorObjid\"", "\"sensorobjid\"", StringComparison.Ordinal);
        var raw = $"{{\"11\":{entry}}}";
        PrtgDiskSemanticEvidenceStore.ValidateRepairDocument(raw);
    }

    private void SeedDiskRows()
    {
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var resultStore = new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey));
        foreach (var id in new long[] { 11, 12 })
        {
            evidenceStore.ConfirmManually(Context(id), 42, "seed proof", DateTime.UtcNow,
                PrtgDiskAssessmentService.ParserSemanticVersion, "source", "resource", "channel", 1);
            resultStore.Save(Result(id));
        }
    }

    private static PrtgDiskSemanticContext Context(long id) =>
        new(id, id + 100, id, "disk", "free", "Free", "%", 1, "descending-danger");

    private static PrtgDiskSemanticEvidence Evidence(long id) => new(id, id + 100, id, "disk", "free", "Free", "%", 1,
        "descending-danger", PrtgDiskSemanticEvidenceSource.Manual, 42, "seed proof", DateTime.UtcNow,
        PrtgDiskAssessmentService.ParserSemanticVersion, "source", "resource", "channel", 1);

    private static PrtgDiskVerificationResult Result(long id, string? parserVersion = null) => new(id, id + 100, id,
        "disk", "Verified", "seed probe", "free", "Free", "%", 1, "descending-danger", 1, true,
        DateTime.UtcNow, DateTime.Today, parserVersion ?? PrtgDiskAssessmentService.ParserSemanticVersion,
        false, "source", "resource", "channel", 1);

    private static PrtgResourceIdentity Identity(long id) => new()
    {
        SensorId = id, DeviceId = id + 100, HostId = id, Epoch = 1, Active = true,
        SourceGeneration = "source", Generation = "resource", ChannelGeneration = "channel"
    };

    private static PrtgResourcePressureModeGrant Grant(long hostId, long sensorId)
    {
        var now = DateTime.UtcNow;
        return new(hostId, sensorId, PrtgResourceFamily.Cpu, new string('a', 64),
            PrtgResourcePressureEvaluator.RulesVersion, "source", "resource", "channel", "epoch", "semantic",
            "strategy", "rule", new string('b', 64), new string('c', 64), true, true, now, now.AddMinutes(5),
            ModeTransitionId: Guid.NewGuid().ToString("N"));
    }

    private static string ReplaceEntry(string entry, string replacementProperty)
    {
        var close = entry.LastIndexOf('}');
        Assert.True(close > 0);
        return entry.Insert(close, "," + replacementProperty);
    }

    private static void AssertRejectedWithoutWrite(EfJsonBlobStore blob, string candidate, long version)
    {
        blob.Mutate(_ => (candidate, true));
        var badVersion = blob.ReadVersion();
        Assert.Throws<JsonException>(() => new PrtgDiskSemanticEvidenceStore(blob).Update(all => all.Remove(12)));
        Assert.Equal(badVersion, blob.ReadVersion());
        Assert.Equal(candidate, blob.Read());
        Assert.True(badVersion > version);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

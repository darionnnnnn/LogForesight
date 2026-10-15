using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgAuthorityRepairStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-authority-repair-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgAuthorityRepairStoreTests()
    {
        Directory.CreateDirectory(_root);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "repair.db")}"
        }, _root);
    }

    [Fact]
    public void OverFormalLimitDocumentCanBeCompactedWithoutLosingUnknownOrUnselectedFields()
    {
        var good = Evidence(1);
        var raw = JsonSerializer.Serialize(new Dictionary<long, PrtgDiskSemanticEvidence> { [1] = good });
        var root = (JsonObject)JsonNode.Parse(raw)!;
        root["1"]!["futureField"] = JsonNode.Parse("{\"kept\":true}");
        root["2"] = JsonNode.Parse("{\"SensorObjid\":2,\"oversizeNoise\":\"" + new string('x', 700) + "\"}");
        raw = root.ToJsonString();
        Assert.InRange(raw.Length, 901, 1800);
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (raw, true));

        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 900, testRepairBudget: 1800);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.OverFormalLimit, before.State);
        Assert.Equal(raw, before.Content);

        var result = repair.RemoveJsonPaths(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence,
            before.Version, [new("/2")]);

        Assert.Equal(before.Version + 1, result.NewVersion);
        Assert.True(result.NewCharacters <= before.FormalLimitCharacters);
        var afterRaw = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Read()!;
        var afterNode = JsonNode.Parse(afterRaw)!.AsObject();
        Assert.Null(afterNode["2"]);
        Assert.Equal("true", afterNode["1"]!["futureField"]!["kept"]!.ToJsonString());
        Assert.Equal(good.MainChannelIdentifier, new PrtgDiskSemanticEvidenceStore(
            _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Get(1)!.MainChannelIdentifier);
    }

    [Fact]
    public void SyntaxRepairUsesDeletedTextHashAndInvalidatesTheWholeDiskAuthoritySnapshot()
    {
        SeedDiskAuthority();
        Assert.True(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        var key = PrtgDiskSemanticEvidenceStore.BlobKey;
        var valid = _backend.Blob(key).Read()!;
        var secondEntry = valid.IndexOf("\"12\"", StringComparison.Ordinal);
        Assert.True(secondEntry > 0);
        var comma = valid.LastIndexOf(',', secondEntry);
        Assert.True(comma > 0);
        var malformed = valid.Remove(comma, 1);
        _backend.Blob(key).Mutate(_ => (malformed, true));
        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, before.State);

        var result = repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence,
            before.Version,
            [new(comma, 0, ",", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        Assert.Equal(before.Version + 1, result.NewVersion);
        var evidence = new PrtgDiskSemanticEvidenceStore(_backend.Blob(key)).Get(11)!;
        Assert.Equal("repair-invalidated", evidence.ParserSemanticVersion);
        Assert.Equal("source", evidence.SourceGeneration);
        Assert.Equal(1, evidence.IdentityEpoch);
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
        var validity = new PrtgDiskSemanticEvidenceStore(_backend.Blob(key)).CheckValidity(11,
            new(11, 111, 11, "disk", "free", "Free", "%", 1, "descending-danger"),
            PrtgDiskAssessmentService.ParserSemanticVersion);
        Assert.False(validity.IsValid);
    }

    [Fact]
    public void VerificationSyntaxRepairMarksResultsStaleAndOldProbeCannotBeUsed()
    {
        SeedDiskAuthority();
        var key = PrtgDiskVerificationResultStore.BlobKey;
        var raw = _backend.Blob(key).Read()!;
        var secondEntry = raw.IndexOf("\"12\"", StringComparison.Ordinal);
        var comma = raw.LastIndexOf(',', secondEntry);
        Assert.True(comma > 0);
        var malformed = raw.Remove(comma, 1);
        _backend.Blob(key).Mutate(_ => (malformed, true));
        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskVerificationResults);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, before.State);

        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.DiskVerificationResults,
            before.Version,
            [new(comma, 0, ",", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))]);

        var rows = new PrtgDiskVerificationResultStore(_backend.Blob(key)).Get();
        Assert.Equal("Stale", rows[11].Status);
        Assert.Equal("repair-invalidated", rows[11].ParserSemanticVersion);
        Assert.Null(rows[11].ValuesMatch);
        Assert.Equal(0, rows[11].ComparedPointCount);
        Assert.False(PrtgResourceQualification.IsCurrentDiskChannel(_backend, 11, Identity(11)));
    }

    [Fact]
    public void ModeSyntaxRepairDisablesEveryFormalGrantAndPreservesUnknownFields()
    {
        const long hostId = 7;
        var now = DateTime.UtcNow;
        var grants = new[]
        {
            new PrtgResourcePressureModeGrant(hostId, 71, PrtgResourceFamily.Cpu,
                new string('a', 64), PrtgResourcePressureEvaluator.RulesVersion, "source-a", "resource-a", "channel-a", "epoch-a", "semantic", "strategy",
                "rule-a", new string('b', 64), new string('c', 64), true, true, now, now.AddDays(2),
                ModeTransitionId: "transition-a") { AdditionalProperties = new() { ["futureGrant"] = JsonDocument.Parse("{\"kept\":1}").RootElement.Clone() } },
            new PrtgResourcePressureModeGrant(hostId, 72, PrtgResourceFamily.Memory,
                new string('d', 64), PrtgResourcePressureEvaluator.RulesVersion, "source-b", "resource-b", "channel-b", "epoch-b", "semantic", "strategy",
                "rule-b", new string('e', 64), new string('f', 64), true, true, now, now.AddDays(2),
                ModeTransitionId: "transition-b")
        };
        var raw = JsonSerializer.Serialize(new PrtgResourcePressureModeDocument { Grants = grants.ToList() });
        var root = (JsonObject)JsonNode.Parse(raw)!;
        root["futureTopLevel"] = JsonNode.Parse("{\"kept\":true}");
        raw = root.ToJsonString();
        var second = raw.IndexOf("\"SensorObjid\":72", StringComparison.Ordinal);
        Assert.True(second > 0);
        var comma = raw.LastIndexOf(',', second);
        Assert.True(comma > 0);
        var malformed = raw.Remove(comma, 1);
        var key = PrtgResourcePressureModeStore.BlobKey(hostId);
        _backend.Blob(key).Mutate(_ => (malformed, true));
        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 4096, testRepairBudget: 8192);
        var before = repair.Inspect(PrtgAuthorityRepairDocumentKind.ResourcePressureMode, hostId);
        Assert.Equal(PrtgAuthorityRepairState.InvalidJson, before.State);

        repair.ApplyTextPatches(PrtgAuthorityRepairDocumentKind.ResourcePressureMode, before.Version,
            [new(comma, 0, ",", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))))], hostId);

        var after = new PrtgResourcePressureModeStore(_backend.Blob(key)).ReadHostSnapshot(hostId);
        Assert.Equal(2, after.Grants.Count);
        Assert.All(after.Grants, grant => Assert.False(grant.FormalEnabled));
        Assert.All(after.Grants, grant => Assert.NotEqual("transition-a", grant.ModeTransitionId));
        var restored = JsonNode.Parse(_backend.Blob(key).Read()!)!.AsObject();
        Assert.Equal("true", restored["futureTopLevel"]!["kept"]!.ToJsonString());
        Assert.Equal("1", restored["Grants"]![0]!["futureGrant"]!["kept"]!.ToJsonString());
    }

    [Fact]
    public void StaleVersionAndBeyondRepairBudgetNeverWrite()
    {
        var raw = JsonSerializer.Serialize(new Dictionary<long, PrtgDiskSemanticEvidence> { [1] = Evidence(1) });
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (raw, true));
        var repair = new PrtgAuthorityRepairStore(_backend, testFormalLimit: 1000, testRepairBudget: 1200);
        var snapshot = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        var current = JsonNode.Parse(raw)!.AsObject();
        current["1"]!["unselectedUnknown"] = JsonNode.Parse("{\"future\":7}");
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (current.ToJsonString(), true));
        var changed = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).ReadWithVersion();

        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>(() => repair.RemoveJsonPaths(
            PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, snapshot.Version, [new("/1")]));
        Assert.Equal(changed, _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).ReadWithVersion());

        var overBudget = current.ToJsonString() + new string(' ', 1300);
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (overBudget, true));
        var bounded = repair.Inspect(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence);
        Assert.Equal(PrtgAuthorityRepairState.RepairBudgetExceeded, bounded.State);
        var version = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).ReadVersion();
        Assert.Throws<InvalidOperationException>(() => repair.RemoveJsonPaths(
            PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence, version, [new("/1")]));
        Assert.Equal(version, _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).ReadVersion());
    }

    [Fact]
    public void ServerAdminGetsOnlyRepairMetadataAndCannotReadRawBusinessDocument()
    {
        var raw = JsonSerializer.Serialize(new Dictionary<long, PrtgDiskSemanticEvidence> { [1] = Evidence(1) });
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (raw, true));
        var visibility = DispatchProxy.Create<IVisibilityService, TestVisibilityProxy>();
        ((TestVisibilityProxy)(object)visibility).VisibleHosts = new HashSet<long> { 1 };
        var current = new TestCurrentUser(Capability.Maintain);
        var audit = new TestAuditService();
        var controller = new PrtgAuthorityRepairController(_backend, current, visibility, audit);

        var status = controller.GetStatus().Data!;
        Assert.False(status.CanReadDetails);
        Assert.True(status.HasDiskSemanticDocument);
        Assert.IsType<ForbidResult>(controller.ReadDocument(nameof(PrtgAuthorityRepairDocumentKind.DiskSemanticEvidence)));
        Assert.DoesNotContain("SensorObjid", JsonSerializer.Serialize(status), StringComparison.Ordinal);
        Assert.DoesNotContain("verified manually", JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    private void SeedDiskAuthority()
    {
        var evidence = new Dictionary<long, PrtgDiskSemanticEvidence>
        {
            [11] = Evidence(11), [12] = Evidence(12)
        };
        var results = new Dictionary<long, PrtgDiskVerificationResult>
        {
            [11] = Result(11), [12] = Result(12)
        };
        _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Mutate(_ => (JsonSerializer.Serialize(evidence), true));
        _backend.Blob(PrtgDiskVerificationResultStore.BlobKey).Mutate(_ => (JsonSerializer.Serialize(results), true));
    }

    private static PrtgDiskSemanticEvidence Evidence(long id) => new(id, id + 100, id,
        "disk", "free", "Free", "%", 1, "descending-danger", PrtgDiskSemanticEvidenceSource.Manual,
        7, "verified manually", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion,
        "source", "resource", "channel", 1);

    private static PrtgDiskVerificationResult Result(long id) => new(id, id + 100, id, "disk", "Verified",
        "verified probe", "free", "Free", "%", 1, "descending-danger", 3, true, DateTime.UtcNow,
        DateTime.Today, PrtgDiskAssessmentService.ParserSemanticVersion, false, "source", "resource", "channel", 1);

    private static PrtgResourceIdentity Identity(long id) => new()
    {
        SensorId = id, DeviceId = id + 100, HostId = id, Epoch = 1, Active = true,
        SourceGeneration = "source", Generation = "resource", ChannelGeneration = "channel"
    };

    public class TestVisibilityProxy : DispatchProxy
    {
        public IReadOnlySet<long> VisibleHosts { get; set; } = new HashSet<long>();
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name.StartsWith("GetVisibleHostIds", StringComparison.Ordinal) == true)
                return VisibleHosts;
            var returnType = targetMethod?.ReturnType;
            return returnType is { IsValueType: true } ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class TestCurrentUser(params Capability[] capabilities) : ICurrentUser
    {
        private readonly HashSet<Capability> _capabilities = capabilities.ToHashSet();
        public bool IsAuthenticated => true;
        public long UserId => 0;
        public string Account => "serverAdmin";
        public string DisplayName => "serverAdmin";
        public IReadOnlySet<Capability> Capabilities => _capabilities;
        public bool IsServerAdmin => true;
        public bool Has(Capability capability) => _capabilities.Contains(capability);
    }

    private sealed class TestAuditService : IAuditService
    {
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) { }
        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

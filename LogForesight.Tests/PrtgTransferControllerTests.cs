using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using LogForesight.Web.Middleware;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTransferControllerTests
{
    [Fact]
    public void TransferApiUsesMaintainAndBoundedRawChunkRoute()
    {
        var controller = typeof(PrtgTransferController);
        Assert.Equal("api/admin/settings/prtg-import-transfers",
            controller.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>().Single().Template);
        var permission = controller.GetCustomAttributes(typeof(PermissionAttribute), inherit: true)
            .Cast<PermissionAttribute>().Single();
        Assert.Equal(Capability.Maintain,
            permission.Arguments!.Cast<Capability[]>().Single().Single());

        var put = controller.GetMethod(nameof(PrtgTransferController.PutChunk))!;
        Assert.Equal("application/octet-stream",
            put.GetCustomAttributes(typeof(ConsumesAttribute), inherit: true).Cast<ConsumesAttribute>().Single().ContentTypes.Single());
        var requestSizeLimit = put.GetCustomAttributesData()
            .Single(attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(4L * 1024 * 1024,
            Convert.ToInt64(requestSizeLimit.ConstructorArguments.Single().Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(controller.GetMethod(nameof(PrtgTransferController.Abandon)));
    }

    [Fact]
    public async Task LegacyJsonParserHandlesChunkAndUtf8BoundariesWithoutTrustingRows()
    {
        var json = """{"FormatVersion":1,"ExportedAt":"2026-10-05T00:00:00Z","FromDate":"2026-10-01T00:00:00","ToDate":"2026-10-05T00:00:00","Devices":[{"Objid":17,"Name":"診斷主機"}],"Sensors":[],"StateChanges":[],"Values":[],"HostMaps":[],"ManualMaps":[]}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var split = Encoding.UTF8.GetByteCount("""{"FormatVersion":1,"ExportedAt":"2026-10-05T00:00:00Z","FromDate":"2026-10-01T00:00:00","ToDate":"2026-10-05T00:00:00","Devices":[{"Objid":17,"Name":"診""" ) - 1;
        var chunks = new[] { bytes[..split], bytes[split..(split + 1)], bytes[(split + 1)..] };
        var validator = new PrtgLegacyJsonTransferValidator();

        var result = await validator.ValidateAsync(chunks.Length,
            (ordinal, _) => Task.FromResult(chunks[ordinal]), CancellationToken.None);

        Assert.Equal(1, result.FormatVersion);
        Assert.Equal(bytes.LongLength, result.DeclaredBytes);
        Assert.Equal(1, result.Counts["Devices"]);
        Assert.Equal(0, result.Counts["Sensors"]);
        Assert.Contains("untrusted-diagnostic-payload", result.ResultManifestJson, StringComparison.Ordinal);
        Assert.Contains("legacy-v1-unmanifested", result.ResultManifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("診斷主機", result.ResultManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyJsonParserAcceptsFullUnmanifestedV2PackageAsUntrustedLegacy()
    {
        var validator = new PrtgLegacyJsonTransferValidator();
        var json = JsonSerializer.Serialize(LegacyPackage(2));
        var roundTrip = JsonSerializer.Deserialize<PrtgDataPackage>(json)!;
        Assert.All(new[] { roundTrip.Devices.Count, roundTrip.Sensors.Count, roundTrip.StateChanges.Count,
            roundTrip.Values.Count, roundTrip.HostMaps.Count, roundTrip.ManualMaps.Count,
            roundTrip.Observations.Count, roundTrip.Timelines.Count, roundTrip.SemanticEvidence.Count,
            roundTrip.SemanticResults.Count }, count => Assert.Equal(1, count));

        var bytes = Encoding.UTF8.GetBytes(json);
        var split = bytes.Length / 2;
        var chunks = new[] { bytes[..split], bytes[split..] };
        var result = await validator.ValidateAsync(chunks.Length,
            (ordinal, _) => Task.FromResult(chunks[ordinal]), CancellationToken.None);

        Assert.Equal(2, result.FormatVersion);
        Assert.Equal(bytes.LongLength, result.DeclaredBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.PackageSha256);
        Assert.Contains("legacy-v2-unmanifested", result.ResultManifestJson, StringComparison.Ordinal);
        Assert.All(new[] { "Devices", "Sensors", "StateChanges", "Values", "HostMaps", "ManualMaps",
            "Observations", "Timelines", "SemanticEvidence", "SemanticResults", "TimelineCoverageEntries", "TimelineStateEntries" },
            category => Assert.Equal(1, result.Counts[category]));

        var nullablePolicy = Encoding.UTF8.GetBytes("""{"FormatVersion":2,"Purpose":"diagnostic-only","Devices":[],"Sensors":[],"StateChanges":[],"Values":[],"HostMaps":[],"ManualMaps":[],"SourcePolicy":null,"Observations":[],"Timelines":[],"SemanticEvidence":[],"SemanticResults":[]}""");
        var nullPolicyResult = await validator.ValidateAsync(1, (_, _) => Task.FromResult(nullablePolicy), CancellationToken.None);
        Assert.Contains("legacy-v2-unmanifested", nullPolicyResult.ResultManifestJson, StringComparison.Ordinal);
    }

    private static PrtgDataPackage LegacyPackage(int formatVersion) => new()
    {
        FormatVersion = formatVersion,
        ExportedAt = new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc),
        FromDate = new DateTime(2026, 10, 1),
        ToDate = new DateTime(2026, 10, 5),
        Devices = [new() { Objid = 17, Name = "device", GroupPath = "g", SyncedAt = new DateTime(2026, 10, 5), CreatedAt = new DateTime(2026, 10, 1) }],
        Sensors = [new() { Objid = 18, DeviceObjid = 17, Name = "sensor", SensorType = "cpu", SyncedAt = new DateTime(2026, 10, 5), CreatedAt = new DateTime(2026, 10, 1) }],
        StateChanges = [new() { Id = 19, SensorObjid = 18, ChangedAt = new DateTime(2026, 10, 2), Status = "Up", Quality = "ok", CreatedAt = new DateTime(2026, 10, 2) }],
        Values = [new() { Id = 20, SensorObjid = 18, PeriodStart = new DateTime(2026, 10, 2), AvgValue = 4.2, Quality = "ok", CreatedAt = new DateTime(2026, 10, 2) }],
        HostMaps = [new() { MapDate = new DateTime(2026, 10, 2), DeviceObjid = 17, HostId = 23, MapStatus = "ok", CreatedAt = new DateTime(2026, 10, 2) }],
        ManualMaps = [new() { DeviceObjid = 17, HostId = 23, CreatedAt = new DateTime(2026, 10, 2) }],
        Observations = [new() { SnapshotId = "snapshot", DecisionKey = "decision", HostId = 23, RecordDate = new DateTime(2026, 10, 2), DeviceObjid = 17, RuleCode = "rule", EventKey = "event", SourceName = "source", Category = "category", SeverityRank = 1, ElevatesDayRisk = false, Suppressed = false, SourceHint = "hint", QualityReason = "quality", FormatVersion = 1, ContentJson = "{}", RecordedAtUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) }],
        Timelines = [new() { SensorId = 18, HostId = 23, SourceGeneration = "source-generation", ResourceGeneration = "resource-generation", IdentityFingerprint = "identity", ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Coverage = [new(18, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-02T00:00:00Z"), "source-generation", "resource-generation")], States = [new(18, DateTimeOffset.Parse("2026-10-01T12:00:00Z"), "Up", "source-generation", "resource-generation")] }],
        SemanticEvidence = [new(18, 17, 23, "disk", "used", "Used", "%", 1, "up", PrtgDiskSemanticEvidenceSource.Manual, 7, "confirmed", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), "v1")],
        SemanticResults = [new(18, 17, 23, "disk", "ok", "matched", "used", "Used", "%", 1, "up", 1, true, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2), "v1")],
        SourcePolicy = new PrtgMonitoringPolicy { Revision = "r1", CoreSystemId = "core", SourceGeneration = "source-generation", EndpointHint = "hint", ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), HostIds = [23], SensorIds = [18], ConfirmedBy = "admin", SourceTimeZoneId = "UTC", SourceCultureName = "en-US" }
    };

    [Fact]
    public async Task LegacyJsonParserAllowsRealV1WithoutPurposeAndRejectsMinimalV2AndDuplicateKeys()
    {
        var json = """{"FormatVersion":1,"Devices":[],"Sensors":[],"StateChanges":[],"Values":[],"HostMaps":[],"ManualMaps":[]}""";
        var validator = new PrtgLegacyJsonTransferValidator();
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = await validator.ValidateAsync(1, (_, _) => Task.FromResult(bytes), CancellationToken.None);
        Assert.Equal(1, result.FormatVersion);
        Assert.Contains("legacy-v1-unmanifested", result.ResultManifestJson, StringComparison.Ordinal);

        var malformedV2 = Encoding.UTF8.GetBytes("""{"FormatVersion":2,"Devices":[],"Sensors":[],"StateChanges":[],"Values":[],"HostMaps":[],"ManualMaps":[]}""");
        var malformed = await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(1,
            (_, _) => Task.FromResult(malformedV2), CancellationToken.None));
        Assert.StartsWith("legacy_v2_shape_invalid:", malformed.Message, StringComparison.Ordinal);

        var missingVersionV2 = Encoding.UTF8.GetBytes("""{"Purpose":"diagnostic-only","Devices":[],"Sensors":[],"StateChanges":[],"Values":[],"HostMaps":[],"ManualMaps":[],"SourcePolicy":null,"Observations":[],"Timelines":[],"SemanticEvidence":[],"SemanticResults":[]}""");
        var missingVersion = await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(1,
            (_, _) => Task.FromResult(missingVersionV2), CancellationToken.None));
        Assert.StartsWith("format_version_required:", missingVersion.Message, StringComparison.Ordinal);

        var duplicate = Encoding.UTF8.GetBytes("""{"FormatVersion":1,"formatversion":2,"Devices":[]}""");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(1,
            (_, _) => Task.FromResult(duplicate), CancellationToken.None));
        Assert.StartsWith("duplicate_root_property:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2ManifestIsCheckedForUtf8PrefixHashByteOffsetAndEveryArrayCount()
    {
        var valid = BuildManifestV2();
        var split = valid.Length - 19;
        var chunks = new[] { valid[..split], valid[split..] };
        var result = await new PrtgLegacyJsonTransferValidator().ValidateAsync(chunks.Length,
            (ordinal, _) => Task.FromResult(chunks[ordinal]), CancellationToken.None);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal(0, result.Counts["Devices"]);
        Assert.Contains("manifest-verified", result.ResultManifestJson, StringComparison.Ordinal);

        var json = Encoding.UTF8.GetString(valid);
        var rootPrefix = Encoding.UTF8.GetString(BuildManifestPrefix());
        var actualHash = Convert.ToHexString(SHA256.HashData(BuildManifestPrefix()));
        var wrongHash = json.Replace(actualHash, new string('0', 64), StringComparison.Ordinal);
        await AssertManifestRejected(Encoding.UTF8.GetBytes(wrongHash), "manifest_hash_mismatch");

        var wrongCount = json.Replace("\"Devices\":0", "\"Devices\":1", StringComparison.Ordinal);
        await AssertManifestRejected(Encoding.UTF8.GetBytes(wrongCount), "manifest_count_mismatch");

        var wrongOffset = json.Replace($"\"bytesBeforeManifest\":{Encoding.UTF8.GetByteCount(rootPrefix)}",
            $"\"bytesBeforeManifest\":{Encoding.UTF8.GetByteCount(rootPrefix) + 1}", StringComparison.Ordinal);
        await AssertManifestRejected(Encoding.UTF8.GetBytes(wrongOffset), "manifest_byte_count_mismatch");

        var unmarkedNative = BuildUnmarkedManifestV2();
        var unmarkedResult = await new PrtgLegacyJsonTransferValidator().ValidateAsync(1,
            (_, _) => Task.FromResult(unmarkedNative), CancellationToken.None);
        Assert.Contains("manifest-verified", unmarkedResult.ResultManifestJson, StringComparison.Ordinal);

        var unsupported = Encoding.UTF8.GetString(valid).Replace(
            "manifest-sha256-v1", "manifest-sha256-v2", StringComparison.Ordinal);
        await AssertManifestRejected(Encoding.UTF8.GetBytes(unsupported), "integrity_contract_unsupported");

        var missingManifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BuildManifestPrefix()) + "}");
        await AssertManifestRejected(missingManifest, "manifest_required");
    }

    [Fact]
    public async Task NativeWriterOutputCompletesThroughHttpChunkConsumerAsManifestVerifiedAndUntrusted()
    {
        using var fixture = new TransferFixture();
        var policyBefore = fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        var id = await CompleteTransfer(fixture, await WriteNativePackageAsync());
        var row = fixture.TransferRow(id);
        Assert.Equal(PrtgTransferStates.Complete, row.State);
        Assert.Contains("manifest-verified", row.ResultManifestJson, StringComparison.Ordinal);
        Assert.Contains("untrusted-diagnostic-payload", row.ResultManifestJson, StringComparison.Ordinal);
        Assert.Equal(policyBefore, fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion());
        using var db = fixture.Backend.CreateContext();
        Assert.Empty(db.PrtgDevices);
        Assert.Empty(db.PrtgSensors);
        Assert.Empty(db.PrtgValues);
        Assert.Empty(db.PrtgStateChanges);
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> NativeRows()
    {
        await Task.Yield();
        yield return new PrtgDiagnosticExportItem("SourcePolicy", "{}"u8.ToArray());
    }

    private static async Task<byte[]> WriteNativePackageAsync()
    {
        using var output = new MemoryStream();
        var today = DateTime.UtcNow.Date;
        await new PrtgDiagnosticExportWriter().WriteAsync(NativeRows(), output,
            new PrtgDiagnosticExportHeader(today, today.AddDays(-1), today));
        return output.ToArray();
    }

    private static byte[] BuildManifestPrefix() => Encoding.UTF8.GetBytes(
        "{\"FormatVersion\":2,\"Purpose\":\"diagnostic-only\",\"IntegrityContract\":\"manifest-sha256-v1\",\"Devices\":[],\"Sensors\":[],\"StateChanges\":[],\"Values\":[],\"HostMaps\":[],\"ManualMaps\":[],\"SourcePolicy\":{},\"Observations\":[],\"Timelines\":[],\"SemanticEvidence\":[],\"SemanticResults\":[]");

    private static byte[] BuildManifestV2()
    {
        var prefix = BuildManifestPrefix();
        var counts = PrtgDiagnosticExportSource.V2ArrayProperties.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
        var manifest = JsonSerializer.Serialize(new
        {
            format = "legacy-json",
            formatVersion = 2,
            purpose = "diagnostic-only",
            bytesBeforeManifest = prefix.LongLength,
            sha256BeforeManifest = Convert.ToHexString(SHA256.HashData(prefix)),
            rowCounts = counts
        });
        return prefix.Concat(Encoding.UTF8.GetBytes(",\"Manifest\":" + manifest + "}")).ToArray();
    }

    private static byte[] BuildUnmarkedManifestV2()
    {
        var markedPrefix = Encoding.UTF8.GetString(BuildManifestPrefix());
        var prefix = Encoding.UTF8.GetBytes(markedPrefix.Replace(
            ",\"IntegrityContract\":\"manifest-sha256-v1\"", "", StringComparison.Ordinal));
        var counts = PrtgDiagnosticExportSource.V2ArrayProperties.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
        var manifest = JsonSerializer.Serialize(new
        {
            format = "legacy-json",
            formatVersion = 2,
            purpose = "diagnostic-only",
            bytesBeforeManifest = prefix.LongLength,
            sha256BeforeManifest = Convert.ToHexString(SHA256.HashData(prefix)),
            rowCounts = counts
        });
        return prefix.Concat(Encoding.UTF8.GetBytes(",\"Manifest\":" + manifest + "}")).ToArray();
    }

    private static async Task AssertManifestRejected(byte[] bytes, string code)
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PrtgLegacyJsonTransferValidator().ValidateAsync(1,
                (_, _) => Task.FromResult(bytes), CancellationToken.None));
        Assert.StartsWith(code + ":", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyJsonParserRejectsFormalPurposeAndMalformedRows()
    {
        var validator = new PrtgLegacyJsonTransferValidator();
        var formal = Encoding.UTF8.GetBytes("""{"FormatVersion":2,"Purpose":"formal","Devices":[]}""");
        var wrongRow = Encoding.UTF8.GetBytes("""{"FormatVersion":2,"Devices":[{"Objid":"not-an-id","Name":"host"}]}""");

        var purposeError = await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(1,
            (_, _) => Task.FromResult(formal), CancellationToken.None));
        Assert.StartsWith("purpose_rejected:", purposeError.Message, StringComparison.Ordinal);

        var shapeError = await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(1,
            (_, _) => Task.FromResult(wrongRow), CancellationToken.None));
        Assert.StartsWith("row_shape_invalid:", shapeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadDoesNotReadBodyBeforeDurableAdmissionAndBindingChangesRejectOldTransfer()
    {
        using var fixture = new TransferFixture();
        var transferId = fixture.CreateTransfer();
        var row = fixture.TransferRow(transferId);
        var binding = new PrtgTransferBinding(row.OwnerId, row.ScopeHash, row.SourceIdentityHash);
        var store = new EfPrtgTransferStore(fixture.Backend, fixture.Capacity);
        var expectedBytes = checked((int)row.DeclaredBytes);
        var held = store.BeginChunkWrite(transferId, 0, expectedBytes, binding, DateTimeOffset.UtcNow);

        var body = new CountingReadStream();
        var http = new DefaultHttpContext();
        http.Request.Body = body;
        http.Request.ContentLength = expectedBytes;
        http.Request.ContentType = "application/octet-stream";
        var controller = fixture.Controller;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        var denied = Assert.IsType<ObjectResult>(controller.PutChunk(transferId, 0, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(409, denied.StatusCode);
        Assert.Equal(0, body.ReadCount);
        store.ReleaseChunkWrite(held, binding, DateTimeOffset.UtcNow);
        Assert.Equal(0, store.GetStatus(transferId, binding).ReceivedBytes);

        var truncatedBody = new CountingReadStream();
        var truncatedHttp = new DefaultHttpContext();
        truncatedHttp.Request.Body = truncatedBody;
        truncatedHttp.Request.ContentLength = expectedBytes;
        truncatedHttp.Request.ContentType = "application/octet-stream";
        controller.ControllerContext = new ControllerContext { HttpContext = truncatedHttp };
        var truncated = Assert.IsType<BadRequestObjectResult>(
            controller.PutChunk(transferId, 0, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(400, truncated.StatusCode);
        Assert.True(truncatedBody.ReadCount > 0);
        Assert.Equal(0, store.GetStatus(transferId, binding).ReceivedBytes);

        var cancelledBody = new CountingReadStream();
        var cancelledHttp = new DefaultHttpContext();
        cancelledHttp.Request.Body = cancelledBody;
        cancelledHttp.Request.ContentLength = expectedBytes;
        cancelledHttp.Request.ContentType = "application/octet-stream";
        controller.ControllerContext = new ControllerContext { HttpContext = cancelledHttp };
        var cancelled = Assert.IsType<ObjectResult>(controller.PutChunk(transferId, 0,
            new CancellationToken(canceled: true)).GetAwaiter().GetResult());
        Assert.Equal(499, cancelled.StatusCode);
        Assert.Equal(0, store.GetStatus(transferId, binding).ReceivedBytes);

        fixture.User.Account = "renamed-account";
        Assert.Equal(403, Assert.IsType<ObjectResult>(fixture.Controller.GetStatus(transferId)).StatusCode);
        fixture.User.Account = "maintainer";
        fixture.Settings.Update(settings => settings.PrtgUrl = "https://changed.example/prtg");
        Assert.Equal(403, Assert.IsType<ObjectResult>(fixture.Controller.GetStatus(transferId)).StatusCode);

        var secondId = fixture.CreateTransfer();
        fixture.Hosts.Upsert(new() { HostName = "third", Source = "netiq", Active = true });
        Assert.Equal(403, Assert.IsType<ObjectResult>(fixture.Controller.GetStatus(secondId)).StatusCode);

        var policyStore = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy => policy.SourceGeneration = "new-local-generation");
        Assert.Equal(403, Assert.IsType<ObjectResult>(fixture.Controller.GetStatus(secondId)).StatusCode);
    }

    [Fact]
    public async Task CancellationAfterBodyReadCannotCommitChunkOrCompletePackage()
    {
        using var fixture = new TransferFixture();
        var bytes = Encoding.UTF8.GetBytes("{\"FormatVersion\":2,\"Devices\":[]}");
        var id = fixture.CreateTransfer(bytes);
        using var cancel = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/octet-stream";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new CancelAfterReadStream(bytes, cancel);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Assert.Equal(499, Assert.IsAssignableFrom<ObjectResult>(await fixture.Controller.PutChunk(id, 0, cancel.Token)).StatusCode);
        var row = fixture.TransferRow(id);
        Assert.Equal(0, row.ReceivedBytes);
        Assert.Null(row.ActiveWriteId);
        Assert.Equal(499, Assert.IsAssignableFrom<ObjectResult>(await fixture.Controller.Complete(id, cancel.Token)).StatusCode);
        Assert.Equal(PrtgTransferStates.Receiving, fixture.TransferRow(id).State);
        Assert.Null(fixture.TransferRow(id).LeaseOwner);
    }

    [Fact]
    public async Task RealPackageConsumerCompletesIsolatedPayloadAndFreshOwnerCanAbandonOldContext()
    {
        using var fixture = new TransferFixture();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(LegacyPackage(2));
        var policyBefore = fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        var id = fixture.CreateTransfer(bytes);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/octet-stream";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Assert.IsType<OkObjectResult>(await fixture.Controller.PutChunk(id, 0, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await fixture.Controller.Complete(id, CancellationToken.None));
        Assert.Equal(PrtgTransferStates.Complete, fixture.TransferRow(id).State);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), fixture.TransferRow(id).PackageSha256);
        Assert.Contains("untrusted-diagnostic-payload", fixture.TransferRow(id).ResultManifestJson);
        Assert.Equal(policyBefore, fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion());
        using (var db = fixture.Backend.CreateContext())
        {
            Assert.Empty(db.PrtgDevices);
            Assert.Empty(db.PrtgSensors);
            Assert.Empty(db.PrtgValues);
            Assert.Empty(db.PrtgStateChanges);
        }
        var other = fixture.CreateTransfer();
        fixture.Settings.Update(settings => settings.PrtgUrl = "https://changed.example/prtg");
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(fixture.Controller.GetStatus(other)).StatusCode);
        Assert.IsType<OkObjectResult>(fixture.Controller.Abandon(other));
        Assert.Equal(PrtgTransferStates.Abandoned, fixture.TransferRow(other).State);
    }

    [Fact]
    public async Task TransferConsumerAcceptsValidNativeManifestAndQuarantinesTamperedManifest()
    {
        using var fixture = new TransferFixture();
        var policyBefore = fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion();
        var validBytes = await WriteNativePackageAsync();
        var validId = await CompleteTransfer(fixture, validBytes);
        Assert.Equal(PrtgTransferStates.Complete, fixture.TransferRow(validId).State);

        var priorNativeId = await CompleteTransfer(fixture, BuildUnmarkedManifestV2());
        Assert.Equal(PrtgTransferStates.Complete, fixture.TransferRow(priorNativeId).State);
        Assert.Contains("manifest-verified", fixture.TransferRow(priorNativeId).ResultManifestJson, StringComparison.Ordinal);

        using var manifestDocument = JsonDocument.Parse(validBytes);
        var actualHash = manifestDocument.RootElement.GetProperty("Manifest")
            .GetProperty("sha256BeforeManifest").GetString()!;
        var altered = Encoding.UTF8.GetString(validBytes);
        altered = altered.Replace(actualHash, new string('0', 64), StringComparison.Ordinal);
        var invalidId = await CompleteTransfer(fixture, Encoding.UTF8.GetBytes(altered));
        Assert.Equal(PrtgTransferStates.ValidationFailed, fixture.TransferRow(invalidId).State);
        Assert.Equal("manifest_hash_mismatch", fixture.TransferRow(invalidId).FailureCode);

        Assert.Equal(policyBefore, fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadWithVersion());
        using var db = fixture.Backend.CreateContext();
        Assert.Empty(db.PrtgDevices);
        Assert.Empty(db.PrtgSensors);
        Assert.Empty(db.PrtgValues);
        Assert.Empty(db.PrtgStateChanges);
    }

    private static async Task<Guid> CompleteTransfer(TransferFixture fixture, byte[] bytes)
    {
        var id = fixture.CreateTransfer(bytes);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/octet-stream";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        fixture.Controller.ControllerContext = new ControllerContext { HttpContext = context };
        Assert.IsType<OkObjectResult>(await fixture.Controller.PutChunk(id, 0, CancellationToken.None));
        var result = await fixture.Controller.Complete(id, CancellationToken.None);
        Assert.IsAssignableFrom<ObjectResult>(result);
        return id;
    }

    [Fact]
    public async Task KestrelMvcReceivesRawChunksUnderPathBaseWithCsrfAndRejectsOversizeOrDeniedWrites()
    {
        using var fixture = new TransferFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.Backend);
        builder.Services.AddSingleton<IPrtgTransferCapacityProvider>(fixture.Capacity);
        builder.Services.AddSingleton<IHostStore>(fixture.Hosts);
        builder.Services.AddSingleton<IVisibilityService>(new FixtureVisibility(fixture.Hosts));
        builder.Services.AddSingleton<ICurrentUser>(fixture.User);
        builder.Services.AddSingleton<ISystemSettingsStore>(fixture.Settings);
        builder.Services.AddSingleton<IAuditService, NoopAudit>();
        builder.Services.AddControllers().AddApplicationPart(typeof(PrtgTransferController).Assembly);
        await using var app = builder.Build();
        app.UsePathBase("/lf-r11");
        app.UseMiddleware<CsrfHeaderMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address + "/lf-r11/"), Timeout = TimeSpan.FromSeconds(30) };
            var bytes = LargeLegacyV1Payload();
            var id = Guid.NewGuid();
            const string route = "api/admin/settings/prtg-import-transfers";
            var request = new PrtgTransferCreateRequestDto(id, bytes.Length, 2, Convert.ToHexString(SHA256.HashData(bytes)));
            using (var noCsrf = await client.PostAsJsonAsync(route, request)) Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            using (var verify = fixture.Backend.CreateContext()) Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == id));
            client.DefaultRequestHeaders.Add(CsrfHeaderMiddleware.HeaderName, CsrfHeaderMiddleware.HeaderValue);
            using (var created = await client.PostAsJsonAsync(route, request)) Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            using (var tooLarge = new ByteArrayContent(bytes))
            {
                tooLarge.Headers.ContentType = new("application/octet-stream");
                // This rejection is decided from headers. Wait for that decision before sending
                // an oversized body, which Kestrel may otherwise abort while draining it.
                using var oversizedRequest = new HttpRequestMessage(HttpMethod.Put, $"{route}/{id}/chunks/0") { Content = tooLarge };
                oversizedRequest.Headers.ExpectContinue = true;
                using var response = await client.SendAsync(oversizedRequest);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(0, fixture.TransferRow(id).ReceivedBytes);
            }
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                using var content = new ByteArrayContent(ordinal == 0 ? bytes[..EfPrtgTransferStore.MaxChunkBytes] : bytes[EfPrtgTransferStore.MaxChunkBytes..]);
                content.Headers.ContentType = new("application/octet-stream");
                using var accepted = await client.PutAsync($"{route}/{id}/chunks/{ordinal}", content);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }
            using (var completed = await client.PostAsJsonAsync($"{route}/{id}/complete", new { }))
                Assert.True(completed.StatusCode == HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
            Assert.Equal(PrtgTransferStates.Complete, fixture.TransferRow(id).State);
            Assert.Equal(bytes.Length, fixture.TransferRow(id).ReceivedBytes);
            fixture.User.AllowMaintain = false;
            using (var forbidden = await client.GetAsync($"{route}/{id}")) Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private static byte[] LargeLegacyV1Payload()
    {
        var name = new string('a', 2_100_000);
        var json = "{\"FormatVersion\":1,\"Devices\":[{\"Objid\":1,\"Name\":\"" + name +
                   "\"},{\"Objid\":2,\"Name\":\"" + name + "\"}],\"Sensors\":[],\"StateChanges\":[]," +
                   "\"Values\":[],\"HostMaps\":[],\"ManualMaps\":[]}";
        return Encoding.UTF8.GetBytes(json);
    }

    private sealed class NoopAudit : IAuditService
    {
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) { }
        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    private sealed class TransferFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-prtg-transfer-http-" + Guid.NewGuid().ToString("N"));
        public StorageBackend Backend { get; }
        public HostStore Hosts { get; }
        public SystemSettingsStore Settings { get; }
        public FixtureUser User { get; } = new();
        public PrtgTransferCapacityProbe Capacity { get; }
        public PrtgTransferController Controller { get; }

        public TransferFixture()
        {
            Backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);
            Hosts = new HostStore(Backend.Blob("hosts"));
            Hosts.Upsert(new() { HostName = "one", Source = "netiq", Active = true });
            Hosts.Upsert(new() { HostName = "two", Source = "netiq", Active = true });
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Settings.Update(settings => { settings.PrtgUrl = "https://initial.example/prtg"; settings.PrtgEnabled = true; });
            var policy = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            policy.Update(value =>
            {
                value.Revision = "policy-r1";
                value.CoreSystemId = "local-core";
                value.SourceGeneration = "local-generation";
                value.EndpointHint = "local-hint";
                value.ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
                value.SourceTimeZoneId = "UTC";
                value.SourceCultureName = "en-US";
                value.HostIds = [1, 2];
                value.SensorIds = [11];
            });
            Capacity = new PrtgTransferCapacityProbe(Backend);
            Controller = new PrtgTransferController(Backend, Capacity, Hosts, new FixtureVisibility(Hosts), User, Settings);
        }

        public Guid CreateTransfer(byte[]? payload = null)
        {
            var bytes = payload ?? Encoding.UTF8.GetBytes("{\"FormatVersion\":1,\"Devices\":[],\"Sensors\":[],\"StateChanges\":[],\"Values\":[],\"HostMaps\":[],\"ManualMaps\":[]}");
            var id = Guid.NewGuid();
            var result = Assert.IsType<OkObjectResult>(Controller.Create(new PrtgTransferCreateRequestDto(id,
                bytes.Length, 1, Convert.ToHexString(SHA256.HashData(bytes)))));
            Assert.NotNull(result.Value);
            return id;
        }

        public PrtgTransferSessionRow TransferRow(Guid id)
        {
            using var db = Backend.CreateContext();
            return db.PrtgTransferSessions.Single(x => x.TransferId == id);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixtureUser : ICurrentUser
    {
        public bool AllowMaintain { get; set; } = true;
        public bool IsAuthenticated => true;
        public long UserId => 73;
        public string Account { get; set; } = "maintainer";
        public string DisplayName => Account;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Maintain };
        public bool IsServerAdmin => false;
        public bool Has(Capability capability) => capability == Capability.Maintain && AllowMaintain;
    }

    private sealed class FixtureVisibility(IHostStore hosts) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => hosts.GetAll().Select(x => x.HostId).ToHashSet();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public List<WebHost> GetVisibleHosts() => hosts.GetAll();
        public void EnsureVisible(long hostId) { if (!GetVisibleHostIds().Contains(hostId)) throw new InvalidOperationException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
    }

    private sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            source.Cancel();
            return read;
        }
    }

    private sealed class CountingReadStream : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { ReadCount++; return 0; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCount++; return ValueTask.FromResult(0); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { ReadCount++; return Task.FromResult(0); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

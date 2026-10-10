using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

// Regression checks for bounded incident evidence and immutable invalid host documents.
public sealed class PrtgAcceptanceBoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-acceptance-bounds-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly RecordingAuditService _audit = new();
    private sealed class Visible(params long[] ids) : IVisibilityService
    {
        public bool CaseOnly { get; set; }
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot) => GetVisibleHostIds();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => CaseOnly;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }
    public PrtgAcceptanceBoundsTests() => _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
    private PrtgAcceptanceController Api(Visible? visible = null) => new(_backend, visible ?? new Visible(1, 2),
        FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit);
    private static JsonElement Data(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return doc.RootElement.GetProperty("Data").Clone();
    }
    private void PutRows(long hostId, IEnumerable<PrtgAcceptanceIncident> rows) =>
        _backend.Blob("prtg_acceptance_labels_" + hostId).Mutate(_ => (JsonSerializer.Serialize(rows), true));

    [Fact]
    public void GlobalLabelCapFailsClosedAndDoesNotComparePartialLabels()
    {
        PutRows(1, Enumerable.Range(0, 600).Select(i => new PrtgAcceptanceIncident { HostId = 1, IncidentId = "a" + i, Outcome = "unknown" }));
        PutRows(2, Enumerable.Range(0, 600).Select(i => new PrtgAcceptanceIncident { HostId = 2, IncidentId = "b" + i, Outcome = "unknown" }));
        var data = Data(Api(new Visible(1, 2)).Incidents());
        Assert.Equal(1000, data.GetProperty("Items").GetArrayLength());
        Assert.False(data.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("Comparison").ValueKind);
    }

    [Fact]
    public void HostFilterRejectsForgedEmbeddedHostAndCaseOnlyGrant()
    {
        PutRows(1, [new PrtgAcceptanceIncident { HostId = 2, IncidentId = "forged", Outcome = "unknown" }]);
        var data = Data(Api(new Visible(1, 2)).Incidents(1));
        Assert.Equal(0, data.GetProperty("Items").GetArrayLength());
        Assert.False(data.GetProperty("ScopeComplete").GetBoolean());
        var caseOnly = new Visible(1, 2) { CaseOnly = true };
        Assert.IsType<ForbidResult>(Api(caseOnly).Incidents(1));
        Assert.IsType<ForbidResult>(Api(caseOnly).Save(new PrtgAcceptanceIncident { HostId = 1, IncidentId = "case-only", Outcome = "unknown" }));
    }

    [Fact]
    public void ExportPeriodFilterRunsBeforeGlobalLabelCap()
    {
        var oldDate = DateTimeOffset.UtcNow.AddYears(-2);
        var within = DateTimeOffset.UtcNow.AddDays(-1);
        PutRows(1, Enumerable.Range(0, 1000).Select(i => new PrtgAcceptanceIncident { HostId = 1, IncidentId = "old" + i,
            Outcome = "occurred", OccurredAt = oldDate }));
        PutRows(2, [new PrtgAcceptanceIncident { HostId = 2, IncidentId = "within", Outcome = "occurred", OccurredAt = within }]);
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => { p.HostIds = [1, 2]; p.SensorIds = []; });
        var from = within.Date.AddDays(-1); var through = within.Date.AddDays(1);
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1, 2)).Export(from, through));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.True(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal("within", doc.RootElement.GetProperty("Labels")[0].GetProperty("IncidentId").GetString());
    }

    [Fact]
    public void MaximumLegalHostDocumentFitsBoundedRead()
    {
        var escaped = new string('\u0001', 1000);
        PutRows(1, Enumerable.Range(0, 1000).Select(i => new PrtgAcceptanceIncident
        {
            HostId = 1, IncidentId = i.ToString().PadLeft(128, 'x'), Outcome = "unknown",
            ActionDetails = escaped, BeforeMeasurement = escaped, AfterMeasurement = escaped,
            EvidenceReference = escaped, Reason = escaped, Segment = escaped[..512]
        }));
        var data = Data(Api(new Visible(1)).Incidents(1));
        Assert.True(data.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(1000, data.GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public void TimelineExportPreservesSelectedDenominatorAndHidesForgedHost()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => { p.HostIds = [1]; p.SensorIds = [100, 101]; });
        var forged = new PrtgSensorTimelineEvidence { SensorId = 100, HostId = 2, SourceGeneration = "private" };
        _backend.Blob(PrtgSensorTimelineStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(forged), true));
        var from = DateTime.UtcNow.Date.AddDays(-1); var through = DateTime.UtcNow.Date;
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(from, through));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.False(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Comparison").ValueKind);
        Assert.Equal(2, doc.RootElement.GetProperty("TimelineSelectedTotal").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("TimelineUnknownTotal").GetInt32());
        Assert.Equal(100L, doc.RootElement.GetProperty("Timelines")[0].GetProperty("SensorId").GetInt64());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Timelines")[0].GetProperty("HostId").ValueKind);
    }

    [Fact]
    public void PartialVisibilityDoesNotEnumerateOrCountPrivateAndUnattributedSensors()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => { p.HostIds = [1, 2]; p.SensorIds = [100, 101]; });
        var privateIdentity = new PrtgResourceIdentity { SensorId = 100, HostId = 2, Generation = "private" };
        _backend.Blob(PrtgResourceIdentityStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(privateIdentity), true));
        var privateTimeline = new PrtgSensorTimelineEvidence { SensorId = 100, HostId = 2, SourceGeneration = "private-secret" };
        _backend.Blob(PrtgSensorTimelineStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(privateTimeline), true));
        var from = DateTime.UtcNow.Date.AddDays(-1); var through = DateTime.UtcNow.Date;
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(from, through));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.False(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("TimelineScopeUnknown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("TimelineSelectedTotal").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("TimelineUnknownTotal").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("Timelines").EnumerateArray());
        Assert.DoesNotContain("private-secret", System.Text.Encoding.UTF8.GetString(file.FileContents));
        Assert.DoesNotContain("\"SensorId\": 100", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("null-row")]
    [InlineData("oversized")]
    public void MissingMalformedOrOversizedHostDocumentFailsClosed(string kind)
    {
        if (kind != "missing")
        {
            var raw = kind switch { "malformed" => "not-json", "null-row" => "[null]", _ => "[" + new string('x', 40 * 1024 * 1024) + "]" };
            _backend.Blob("prtg_acceptance_labels_1").Mutate(_ => (raw, true));
        }
        var data = Data(Api(new Visible(1)).Incidents(1));
        Assert.False(data.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("Comparison").ValueKind);
    }


    [Theory]
    [InlineData("forged-row")]
    [InlineData("malformed")]
    [InlineData("null-document")]
    [InlineData("null-row")]
    [InlineData("too-many-update-existing")]
    public void SaveRejectsInvalidExistingHostDocumentWithoutOverwriting(string kind)
    {
        string raw;
        string incidentId;
        if (kind == "forged-row")
        {
            raw = JsonSerializer.Serialize(new[] { new PrtgAcceptanceIncident { HostId = 2, IncidentId = "target", Outcome = "unknown" } });
            incidentId = "target";
        }
        else if (kind == "null-row") { raw = "[null]"; incidentId = "new"; }
        else if (kind == "malformed") { raw = "{broken"; incidentId = "new"; }
        else if (kind == "null-document") { raw = "null"; incidentId = "new"; }
        else
        {
            raw = JsonSerializer.Serialize(Enumerable.Range(0, 1001).Select(i => new PrtgAcceptanceIncident
                { HostId = 1, IncidentId = i == 0 ? "target" : "old" + i, Outcome = "unknown" }));
            incidentId = "target";
        }
        _backend.Blob("prtg_acceptance_labels_1").Mutate(_ => (raw, true));
        Assert.Throws<DomainException>(() => Api(new Visible(1)).Save(new PrtgAcceptanceIncident
            { HostId = 1, IncidentId = incidentId, Outcome = "unknown", Segment = "candidate" }));
        Assert.Equal(raw, _backend.Blob("prtg_acceptance_labels_1").Read());
    }

    [Fact]
    public void SaveRejectsOversizedExistingHostDocumentWithoutOverwriting()
    {
        var raw = "[\"" + new string('x', 40 * 1024 * 1024) + "\"]";
        _backend.Blob("prtg_acceptance_labels_1").Mutate(_ => (raw, true));
        var result = Api(new Visible(1)).Save(new PrtgAcceptanceIncident
            { HostId = 1, IncidentId = "new", Outcome = "unknown", Segment = "candidate" });
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(raw, _backend.Blob("prtg_acceptance_labels_1").Read());
    }

    [Fact]
    public void SmallHostReadAndSaveReturnsFullHostComparison()
    {
        var api = Api(new Visible(1));
        var saved = api.Save(new PrtgAcceptanceIncident { HostId = 1, IncidentId = "small", Outcome = "unknown", Segment = "candidate" });
        var data = Data(saved);
        Assert.True(data.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(1, data.GetProperty("Items").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, data.GetProperty("Comparison").ValueKind);
    }

    [Fact]
    public void CompleteOwnedTimelineExportsSummaryWithoutPrivateLeaseOrHistoricalArrays()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => { p.HostIds = [1]; p.SensorIds = [100]; });
        PutRows(1, []);
        var identity = new PrtgResourceIdentity { SensorId = 100, HostId = 1, Epoch = 1, Active = true, Generation = "resource" };
        _backend.Blob(PrtgResourceIdentityStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(identity), true));
        var timeline = new PrtgSensorTimelineEvidence { SensorId = 100, HostId = 1, SourceGeneration = "source", ResourceGeneration = "resource", LeaseOwner = "private-lease-owner" };
        _backend.Blob(PrtgSensorTimelineStore.Prefix + 100).Mutate(_ => (JsonSerializer.Serialize(timeline), true));
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow.Date));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.True(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal("bounded-summary-v1", doc.RootElement.GetProperty("TimelineRepresentation").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("TimelineSelectedTotal").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("TimelineUnknownTotal").GetInt32());
        var row = Assert.Single(doc.RootElement.GetProperty("Timelines").EnumerateArray());
        Assert.Equal("known", row.GetProperty("Status").GetString());
        Assert.Equal(100, row.GetProperty("SensorId").GetInt64());
        Assert.False(row.TryGetProperty("States", out _));
        Assert.False(row.TryGetProperty("Coverage", out _));
        Assert.DoesNotContain("private-lease-owner", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public void EmptySelectedPolicyIsNotACompleteAcceptanceScope()
    {
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow.Date));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.False(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Comparison").ValueKind);
        Assert.Contains("selected-host-scope-empty", doc.RootElement.GetProperty("ScopeStatus").EnumerateArray().Select(row => row.GetString()));
    }

    [Theory]
    [InlineData("occurred", 0.5)]
    [InlineData("occurred", 23.5)]
    [InlineData("prevented", 0.5)]
    [InlineData("prevented", 23.5)]
    [InlineData("unknown", 0.5)]
    [InlineData("unknown", 23.5)]
    public void UtcIncidentTimesBelongToNetiqLocalHostDay(string outcome, double hour)
    {
        var day = new DateTime(2026, 10, 10);
        var wall = day.AddHours(hour);
        var utc = new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)).ToUniversalTime();
        var label = new PrtgAcceptanceIncident { HostId = 1, IncidentId = "local-day", Outcome = outcome, ReviewedAt = utc };
        if (outcome == "occurred") label.OccurredAt = utc;
        if (outcome == "prevented") label.DispositionAt = utc;
        PutRows(1, [label]);
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        { p.HostIds = [1]; p.SensorIds = []; p.AnalysisTimeZoneId = TimeZoneInfo.Local.Id == "UTC" ? "Asia/Taipei" : "UTC"; });
        var file = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(day, day));
        using var doc = JsonDocument.Parse(file.FileContents);
        Assert.True(doc.RootElement.GetProperty("ScopeComplete").GetBoolean());
        Assert.Equal("local-day", Assert.Single(doc.RootElement.GetProperty("Labels").EnumerateArray()).GetProperty("IncidentId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("Comparison").GetProperty("Incidents").GetInt32());
        foreach (var adjacent in new[] { day.AddDays(-1), day.AddDays(1) })
        {
            var adjacentFile = Assert.IsType<FileContentResult>(Api(new Visible(1)).Export(adjacent, adjacent));
            using var adjacentDoc = JsonDocument.Parse(adjacentFile.FileContents);
            Assert.True(adjacentDoc.RootElement.GetProperty("ScopeComplete").GetBoolean());
            Assert.Empty(adjacentDoc.RootElement.GetProperty("Labels").EnumerateArray());
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}




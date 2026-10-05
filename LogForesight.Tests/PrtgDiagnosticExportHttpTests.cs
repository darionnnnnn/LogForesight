using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogForesight.Tests;

/// <summary>只供主代理複製採用的真 Kestrel/MVC 串流匯出驗收 fixture。</summary>
public sealed class PrtgDiagnosticExportHttpTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime Through = new(2026, 10, 3);
    private const string PathBase = "/lf-r11-export";
    private const string ExportPath = "/api/admin/settings/prtg-export";
    private const string LegacyImportPath = "/api/admin/settings/prtg-import";

    [Fact]
    public async Task KestrelSettingsRouteStreamsNativeV2AndLeavesFormalRowsUnchanged()
    {
        using var fixture = new HttpExportFixture(sqliteWal: true);
        await fixture.SeedExportRowsAsync();
        var formalBefore = fixture.FormalSnapshot();
        await using var app = fixture.BuildApp();
        await app.StartAsync();
        try
        {
            using var client = fixture.CreateClient(app);
            using var response = await client.GetAsync(ExportUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var disposition = Assert.IsType<ContentDispositionHeaderValue>(response.Content.Headers.ContentDisposition);
            Assert.Equal("attachment", disposition.DispositionType);
            Assert.Contains("prtg-export-20261001-20261003.json", disposition.FileNameStar ?? disposition.FileName,
                StringComparison.Ordinal);

            var package = await response.Content.ReadAsByteArrayAsync();
            Assert.True(package.Length > EfPrtgTransferStore.MaxChunkBytes);
            using var document = JsonDocument.Parse(package);
            var root = document.RootElement;
            foreach (var property in PrtgDiagnosticExportSource.V2ArrayProperties)
                Assert.Equal(JsonValueKind.Array, root.GetProperty(property).ValueKind);
            Assert.Equal(JsonValueKind.Object, root.GetProperty("SourcePolicy").ValueKind);
            var firstDevice = root.GetProperty("Devices").EnumerateArray().First();
            Assert.Equal("診斷🧪", firstDevice.GetProperty("Name").GetString());
            Assert.Equal(777, root.GetProperty("Timelines").EnumerateArray().Single().GetProperty("SensorId").GetInt64());

            var manifest = root.GetProperty("Manifest");
            var marker = Encoding.UTF8.GetBytes(",\"Manifest\":");
            var manifestOffset = package.AsSpan().IndexOf(marker);
            Assert.True(manifestOffset > 0);
            Assert.Equal(manifestOffset, manifest.GetProperty("bytesBeforeManifest").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(package.AsSpan(0, manifestOffset))),
                manifest.GetProperty("sha256BeforeManifest").GetString());
            Assert.InRange(Encoding.UTF8.GetByteCount(manifest.GetRawText()), 1,
                PrtgDiagnosticExportWriter.MaximumManifestUtf8Bytes);

            var chunks = Split(package, EfPrtgTransferStore.MaxChunkBytes);
            var validation = await new PrtgLegacyJsonTransferValidator().ValidateAsync(chunks.Count,
                (ordinal, _) => Task.FromResult(chunks[ordinal]), CancellationToken.None);
            Assert.Equal(2, validation.FormatVersion);
            Assert.Equal(package.LongLength, validation.DeclaredBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(package)), validation.PackageSha256);
            Assert.Equal(205, validation.Counts["Devices"]);
            Assert.Equal(1, validation.Counts["Sensors"]);
            Assert.Equal(1, validation.Counts["StateChanges"]);
            Assert.Equal(1, validation.Counts["Values"]);
            Assert.Equal(1, validation.Counts["HostMaps"]);
            Assert.Equal(1, validation.Counts["ManualMaps"]);
            Assert.Equal(1, validation.Counts["Observations"]);
            Assert.Equal(1, validation.Counts["Timelines"]);
            Assert.Equal(1, validation.Counts["SemanticEvidence"]);
            Assert.Equal(1, validation.Counts["SemanticResults"]);
            foreach (var (name, count) in validation.Counts.Where(pair => PrtgDiagnosticExportSource.V2ArrayProperties.Contains(pair.Key)))
                Assert.Equal(count, manifest.GetProperty("rowCounts").GetProperty(name).GetInt64());

            Assert.Equal(formalBefore, fixture.FormalSnapshot());
            fixture.AssertOnlyReleasedExportLeases(expectedCount: 1);
            Assert.Contains(fixture.Audit.Entries, entry => entry.Action == AuditActions.PrtgDataExport);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task KestrelCancellationAfterBodyStartsDoesNotReturnSuccessAndReleasesReaderLease()
    {
        using var fixture = new HttpExportFixture(sqliteWal: true);
        await fixture.SeedExportRowsAsync();
        fixture.AbortExportAfterBytes = 1024 * 1024;
        await using var app = fixture.BuildApp();
        await app.StartAsync();
        try
        {
            using var client = fixture.CreateClient(app);
            using var received = new MemoryStream();
            try
            {
                using var response = await client.GetAsync(ExportUrl(), HttpCompletionOption.ResponseHeadersRead);
                using var body = await response.Content.ReadAsStreamAsync();
                var scratch = new byte[64 * 1024];
                int count;
                while ((count = await body.ReadAsync(scratch.AsMemory())) > 0)
                    await received.WriteAsync(scratch.AsMemory(0, count));
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { }
            var partial = received.ToArray();
            Assert.True(fixture.ExportAbortTriggered);
            // Connection abort may discard buffered bytes; every received prefix must remain incomplete.
            Assert.InRange(partial.Length, 0, EfPrtgTransferStore.MaxChunkBytes + 64 * 1024);
            var partialChunks = Split(partial, EfPrtgTransferStore.MaxChunkBytes);
            var invalid = await Record.ExceptionAsync(() => new PrtgLegacyJsonTransferValidator().ValidateAsync(
                partialChunks.Count, (ordinal, _) => Task.FromResult(partialChunks[ordinal]), CancellationToken.None));
            Assert.True(invalid is InvalidDataException or JsonException, "A truncated export must be rejected as malformed or incomplete JSON.");
            await fixture.WaitForReleasedExportLeasesAsync(expectedCount: 1);
            fixture.AssertOnlyReleasedExportLeases(expectedCount: 1);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task KestrelMaintainRevocationAndRestrictedVisibilityReturn403AndLegacyPostReadsNoBody()
    {
        using var fixture = new HttpExportFixture(sqliteWal: true);
        await fixture.SeedExportRowsAsync();
        await using var app = fixture.BuildApp();
        await app.StartAsync();
        try
        {
            using var client = fixture.CreateClient(app);
            fixture.User.SetCapabilities(Capability.ViewAll);
            using (var denied = await client.GetAsync(ExportUrl()))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            fixture.User.SetCapabilities(Capability.Maintain);
            fixture.Visibility.SetVisibleHostIds([fixture.HostIds[0]]);
            using (var restricted = await client.GetAsync(ExportUrl()))
                Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);

            fixture.Visibility.SetVisibleHostIds(fixture.HostIds);
            client.DefaultRequestHeaders.Add(CsrfHeaderMiddleware.HeaderName, CsrfHeaderMiddleware.HeaderValue);
            using var oldImport = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"Devices\":[]}"));
            oldImport.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var retired = await client.PostAsync(PathBase + LegacyImportPath, oldImport);
            Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);
            Assert.Equal(0, fixture.LegacyImportBodyReads);
            fixture.AssertOnlyReleasedExportLeases(expectedCount: 0);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task KestrelRejectsSqliteWithoutWalBeforeNativeDownloadStarts()
    {
        using var fixture = new HttpExportFixture(sqliteWal: false);
        await fixture.SeedExportRowsAsync();
        await using var app = fixture.BuildApp();
        await app.StartAsync();
        try
        {
            using var client = fixture.CreateClient(app);
            using var response = await client.GetAsync(ExportUrl());
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentDisposition);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("export_snapshot_unavailable", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            fixture.AssertOnlyReleasedExportLeases(expectedCount: 1);
        }
        finally { await app.StopAsync(); }
    }

    private static string ExportUrl() => PathBase + ExportPath + "?from=2026-10-01&to=2026-10-03";

    private static List<byte[]> Split(byte[] bytes, int maximumBytes)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += maximumBytes)
            chunks.Add(bytes.AsSpan(offset, Math.Min(maximumBytes, bytes.Length - offset)).ToArray());
        return chunks;
    }

    private sealed class HttpExportFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-r11-export-http-" + Guid.NewGuid().ToString("N"));
        private readonly string _sourceUrl = "https://fixture.invalid/prtg";
        public StorageBackend Backend { get; }
        public HostStore Hosts { get; }
        public SystemSettingsStore Settings { get; }
        public PrtgTransferCapacityProbe Capacity { get; }
        public RecordingAuditService Audit { get; } = new();
        public ExportUser User { get; } = new();
        public ExportVisibility Visibility { get; }
        public long[] HostIds { get; }
        public int AbortExportAfterBytes { get; set; }
        public bool ExportAbortTriggered { get; private set; }
        public int LegacyImportBodyReads { get; private set; }

        public HttpExportFixture(bool sqliteWal)
        {
            Directory.CreateDirectory(_root);
            Backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", SqliteWal = sqliteWal,
                ConnectionString = $"Data Source={Path.Combine(_root, "r11-export.db")};Pooling=False"
            }, _root);
            Hosts = new HostStore(Backend.Blob("hosts"));
            HostIds =
            [
                Hosts.Upsert(new WebHost { HostName = "export-one", Source = "netiq", Active = true }).HostId,
                Hosts.Upsert(new WebHost { HostName = "export-two", Source = "netiq", Active = true }).HostId
            ];
            Visibility = new ExportVisibility(HostIds);
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Settings.Update(settings => { settings.PrtgUrl = _sourceUrl; settings.PrtgEnabled = true; });
            new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
            {
                policy.Revision = "http-fixture-r1";
                policy.CoreSystemId = "fixture-core";
                policy.SourceGeneration = "fixture-source-generation";
                policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(_sourceUrl);
                policy.ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
                policy.SourceTimeZoneId = "UTC";
                policy.SourceCultureName = "en-US";
                policy.HostIds = HostIds.ToList();
                policy.SensorIds = [777];
            });
            Capacity = new PrtgTransferCapacityProbe(Backend);
        }

        public WebApplication BuildApp()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = _root });
            builder.Logging.ClearProviders().AddConsole();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.Configure<KestrelServerOptions>(options => options.AllowSynchronousIO = false);
            builder.Services.AddSingleton(Backend);
            builder.Services.AddSingleton<ISystemSettingsService>(new FakeSystemSettingsService());
            builder.Services.AddSingleton(new AiUsageStore(Backend.Blob("ai_usage")));
            builder.Services.AddSingleton<IAuditService>(Audit);
            builder.Services.AddSingleton<IHostStore>(Hosts);
            builder.Services.AddSingleton<IVisibilityService>(Visibility);
            builder.Services.AddSingleton<ICurrentUser>(User);
            builder.Services.AddSingleton<IPrtgTransferCapacityProvider>(Capacity);
            builder.Services.AddSingleton<ISystemSettingsStore>(Settings);
            builder.Services.AddControllers(options => options.Filters.Add<ApiExceptionFilter>())
                .AddApplicationPart(typeof(SettingsController).Assembly);

            var app = builder.Build();
            app.UsePathBase(PathBase);
            app.UseMiddleware<CsrfHeaderMiddleware>();
            app.Use(async (context, next) =>
            {
                var bodyControl = context.Features.Get<IHttpBodyControlFeature>();
                if (bodyControl != null) bodyControl.AllowSynchronousIO = false;
                if (context.Request.Method == HttpMethods.Get && context.Request.Path == ExportPath && AbortExportAfterBytes > 0)
                {
                    var originalBody = context.Response.Body;
                    var abortingBody = new AbortAfterWriteStream(originalBody, AbortExportAfterBytes, () =>
                    {
                        ExportAbortTriggered = true;
                        context.Abort();
                    });
                    context.Response.Body = abortingBody;
                    try { await next(); }
                    finally { context.Response.Body = originalBody; }
                    return;
                }
                if (context.Request.Method == HttpMethods.Post && context.Request.Path == LegacyImportPath)
                {
                    var originalBody = context.Request.Body;
                    var counter = new CountingReadStream(originalBody);
                    context.Request.Body = counter;
                    try { await next(); }
                    finally
                    {
                        LegacyImportBodyReads = counter.ReadCount;
                        context.Request.Body = originalBody;
                    }
                    return;
                }
                await next();
            });
            app.MapControllers();
            return app;
        }

        public HttpClient CreateClient(WebApplication app)
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new HttpClient
            {
                BaseAddress = new Uri(address.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromMinutes(2)
            };
        }

        public async Task SeedExportRowsAsync()
        {
            using var db = Backend.CreateContext();
            for (var id = 1; id <= 205; id++)
                db.PrtgDevices.Add(new PrtgDeviceRow
                {
                    Objid = id,
                    Name = id == 1 ? "診斷🧪" : new string('d', 30_000),
                    GroupPath = "root", CreatedAt = From, SyncedAt = From
                });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 500, DeviceObjid = 1, Name = "fixture-sensor", SensorType = "disk",
                CreatedAt = From, SyncedAt = From
            });
            db.PrtgStateChanges.Add(new PrtgStateChangeRow
            {
                SensorObjid = 500, ChangedAt = From.AddHours(2), Status = "Up", Quality = "ok", CreatedAt = From
            });
            db.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 500, PeriodStart = From.AddHours(1), AvgValue = 7, Quality = "ok", CreatedAt = From
            });
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = From, DeviceObjid = 1, HostId = HostIds[0], HostName = "export-one",
                MapStatus = "ok", CreatedAt = From
            });
            db.PrtgManualMaps.Add(new PrtgManualMapRow
            {
                DeviceObjid = 1, HostId = HostIds[0], CreatedBy = "fixture", CreatedAt = From
            });
            db.PrtgObservations.Add(new PrtgObservationRow
            {
                SnapshotId = "fixture-snapshot", DecisionKey = "fixture-decision", HostId = HostIds[0],
                RecordDate = From, DeviceObjid = 1, RuleCode = "disk", EventKey = "fixture-event",
                SourceName = "fixture", Category = "disk", SourceGeneration = "fixture-source-generation",
                SourceHint = "fixture", QualityReason = "diagnostic", FormatVersion = 2, ContentJson = "{}",
                RecordedAtUtc = DateTime.SpecifyKind(From, DateTimeKind.Utc), SupplementStatus = "shadow"
            });
            await db.SaveChangesAsync();

            new PrtgDiskSemanticEvidenceStore(Backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Update(rows =>
                rows[500] = new PrtgDiskSemanticEvidence(500, 1, HostIds[0], "disk", "used", "Used", "%", 1,
                    "up", PrtgDiskSemanticEvidenceSource.Manual, 73, "fixture evidence", DateTime.UtcNow, "v1"));
            new PrtgDiskVerificationResultStore(Backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Update(rows =>
                rows[500] = new PrtgDiskVerificationResult(500, 1, HostIds[0], "disk", "matched", "fixture result",
                    "used", "Used", "%", 1, "up", 1, true, DateTime.UtcNow, From, "v1"));
        }

        public string FormalSnapshot()
        {
            using var db = Backend.CreateContext();
            return JsonSerializer.Serialize(new
            {
                Devices = db.PrtgDevices.AsNoTracking().OrderBy(row => row.Objid).ToList(),
                Sensors = db.PrtgSensors.AsNoTracking().OrderBy(row => row.Objid).ToList(),
                StateChanges = db.PrtgStateChanges.AsNoTracking().OrderBy(row => row.Id).ToList(),
                Values = db.PrtgValues.AsNoTracking().OrderBy(row => row.Id).ToList(),
                Batches = db.PrtgSampledBatches.AsNoTracking().OrderBy(row => row.BatchId).ToList(),
                HostMaps = db.PrtgHostMaps.AsNoTracking().OrderBy(row => row.MapDate).ThenBy(row => row.DeviceObjid).ToList(),
                ManualMaps = db.PrtgManualMaps.AsNoTracking().OrderBy(row => row.DeviceObjid).ToList(),
                Observations = db.PrtgObservations.AsNoTracking().OrderBy(row => row.SnapshotId).ToList(),
                IpExcludes = db.PrtgIpExcludes.AsNoTracking().OrderBy(row => row.Ip).ToList(),
                Policy = Backend.Blob(PrtgMonitoringPolicyStore.BlobKey).Read(),
                Settings = Backend.Blob(SystemSettingsStoreBlobKey).Read(),
                Hosts = Backend.Blob("hosts").Read(),
                Evidence = Backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey).Read(),
                Results = Backend.Blob(PrtgDiskVerificationResultStore.BlobKey).Read()
            });
        }

        public void AssertOnlyReleasedExportLeases(int expectedCount)
        {
            using var db = Backend.CreateContext();
            var sessions = db.PrtgTransferSessions.AsNoTracking().ToList();
            Assert.Equal(expectedCount, sessions.Count);
            Assert.All(sessions, row =>
            {
                Assert.Equal(PrtgTransferStates.Abandoned, row.State);
                Assert.Null(row.LeaseOwner);
                Assert.Null(row.LeaseUntilUtc);
            });
            Assert.Empty(db.PrtgTransferChunks);
        }

        public async Task WaitForReleasedExportLeasesAsync(int expectedCount)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var db = Backend.CreateContext();
                if (db.PrtgTransferSessions.Count() == expectedCount &&
                    db.PrtgTransferSessions.All(row => row.State == PrtgTransferStates.Abandoned && row.LeaseOwner == null))
                    return;
                await Task.Delay(50);
            }
            AssertOnlyReleasedExportLeases(expectedCount);
        }

        private const string SystemSettingsStoreBlobKey = "system_settings";

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ExportUser : ICurrentUser
    {
        private HashSet<Capability> _capabilities = [Capability.Maintain];
        public bool IsAuthenticated => true;
        public long UserId => 73;
        public string Account => "export-http-maintainer";
        public string DisplayName => Account;
        public bool IsServerAdmin => false;
        public IReadOnlySet<Capability> Capabilities => _capabilities;
        public bool Has(Capability capability) => _capabilities.Contains(capability);
        public void SetCapabilities(params Capability[] capabilities) => _capabilities = capabilities.ToHashSet();
    }

    private sealed class ExportVisibility(long[] allIds) : IVisibilityService
    {
        private HashSet<long> _visibleIds = allIds.ToHashSet();
        public void SetVisibleHostIds(IEnumerable<long> ids) => _visibleIds = ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds() => _visibleIds;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visibleIds;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visibleIds;
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long hostId) { if (!_visibleIds.Contains(hostId)) throw new InvalidOperationException(); }
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    }

    private sealed class AbortAfterWriteStream(Stream inner, int thresholdBytes, Action abort) : Stream
    {
        private long _written;
        private bool _aborted;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new InvalidOperationException("Response body 不允許同步 flush。");
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Response body 不允許同步寫入。");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Response body 不允許同步寫入。");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
            if (!_aborted && _written >= thresholdBytes)
            {
                _aborted = true;
                abort();
            }
        }
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) { ReadCount++; return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCount++; return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { ReadCount++; return await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}

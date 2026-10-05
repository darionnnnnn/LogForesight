using System.Text;
using System.Text.Json;
using System.Data.Common;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiagnosticExportSourceTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime Through = new(2026, 10, 3);

    [Fact]
    public async Task FileSqliteSnapshotOutputsAllV2RowsWithStableNamesAndDateBounds()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 0);
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var items = new List<PrtgDiagnosticExportItem>();
        await foreach (var item in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, Policy(), 7, CancellationToken.None))
            items.Add(item);

        Assert.Equal(205, items.Count(item => item.PropertyName == "Devices"));
        Assert.Single(items.Where(item => item.PropertyName == "Sensors"));
        Assert.Single(items.Where(item => item.PropertyName == "StateChanges"));
        Assert.Single(items.Where(item => item.PropertyName == "Values"));
        Assert.Single(items.Where(item => item.PropertyName == "HostMaps"));
        Assert.Single(items.Where(item => item.PropertyName == "ManualMaps"));
        Assert.Single(items.Where(item => item.PropertyName == "Observations"));
        Assert.Equal(2, items.Count(item => item.PropertyName == "Timelines"));
        Assert.Single(items.Where(item => item.PropertyName == "SemanticEvidence"));
        Assert.Single(items.Where(item => item.PropertyName == "SemanticResults"));
        Assert.Equal("ManualMaps", items[items.FindIndex(item => item.PropertyName == "SourcePolicy") - 1].PropertyName);
        Assert.Equal(new[] { "Devices", "Sensors", "StateChanges", "Values", "HostMaps", "ManualMaps", "Observations", "Timelines", "SemanticEvidence", "SemanticResults" },
            PrtgDiagnosticExportSource.V2ArrayProperties);
        Assert.Equal(2 * (int)Math.Ceiling(205d / PrtgDiagnosticExportSource.PageSize) + 1,
            fixture.Counter.DevicePageCommands);

        foreach (var item in items)
        {
            switch (item.PropertyName)
            {
                case "SourcePolicy": Assert.NotNull(JsonSerializer.Deserialize<PrtgMonitoringPolicy>(item.JsonUtf8)); break;
                case "Devices": Assert.NotNull(JsonSerializer.Deserialize<PrtgDeviceRow>(item.JsonUtf8)); break;
                case "Sensors": Assert.NotNull(JsonSerializer.Deserialize<PrtgSensorRow>(item.JsonUtf8)); break;
                case "StateChanges": Assert.NotNull(JsonSerializer.Deserialize<PrtgStateChangeRow>(item.JsonUtf8)); break;
                case "Values": Assert.NotNull(JsonSerializer.Deserialize<PrtgValueRow>(item.JsonUtf8)); break;
                case "HostMaps": Assert.NotNull(JsonSerializer.Deserialize<PrtgHostMapRow>(item.JsonUtf8)); break;
                case "ManualMaps": Assert.NotNull(JsonSerializer.Deserialize<PrtgManualMapRow>(item.JsonUtf8)); break;
                case "Observations": Assert.NotNull(JsonSerializer.Deserialize<PrtgObservationRow>(item.JsonUtf8)); break;
                case "Timelines": Assert.NotNull(JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(item.JsonUtf8)); break;
                case "SemanticEvidence": Assert.NotNull(JsonSerializer.Deserialize<PrtgDiskSemanticEvidence>(item.JsonUtf8)); break;
                case "SemanticResults": Assert.NotNull(JsonSerializer.Deserialize<PrtgDiskVerificationResult>(item.JsonUtf8)); break;
                default: throw new InvalidDataException($"未預期的 V2 匯出欄位 {item.PropertyName}。");
            }
        }

        var device = JsonSerializer.Deserialize<PrtgDeviceRow>(items.Single(item => item.PropertyName == "Devices" &&
            JsonDocument.Parse(item.JsonUtf8).RootElement.GetProperty("Objid").GetInt64() == 1).JsonUtf8)!;
        Assert.Equal("診斷🧪", device!.Name);
        var missingTimeline = JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(items.Single(item =>
            item.PropertyName == "Timelines" && JsonDocument.Parse(item.JsonUtf8).RootElement.GetProperty("SensorId").GetInt64() == 777).JsonUtf8)!;
        Assert.Equal(777, missingTimeline!.SensorId);
        Assert.Empty(missingTimeline.Coverage);
        Assert.Empty(missingTimeline.States);
        Assert.DoesNotContain(items, item => item.PropertyName == "StateChanges" &&
            JsonDocument.Parse(item.JsonUtf8).RootElement.GetProperty("Message").GetString() == "outside-date-range");
        Assert.All(items.Where(item => item.PropertyName != "SourcePolicy"), item =>
        {
            Assert.InRange(item.JsonUtf8.Length, 2, PrtgDiagnosticExportSource.MaximumRowBytes);
            using var document = JsonDocument.Parse(item.JsonUtf8);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        });

        var package = new PrtgDataPackage
        {
            FormatVersion = PrtgDataTransfer.CurrentFormatVersion,
            ExportedAt = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            FromDate = From,
            ToDate = Through,
            SourcePolicy = JsonSerializer.Deserialize<PrtgMonitoringPolicy>(items.Single(item => item.PropertyName == "SourcePolicy").JsonUtf8),
            Devices = items.Where(item => item.PropertyName == "Devices").Select(item => JsonSerializer.Deserialize<PrtgDeviceRow>(item.JsonUtf8)!).ToList(),
            Sensors = items.Where(item => item.PropertyName == "Sensors").Select(item => JsonSerializer.Deserialize<PrtgSensorRow>(item.JsonUtf8)!).ToList(),
            StateChanges = items.Where(item => item.PropertyName == "StateChanges").Select(item => JsonSerializer.Deserialize<PrtgStateChangeRow>(item.JsonUtf8)!).ToList(),
            Values = items.Where(item => item.PropertyName == "Values").Select(item => JsonSerializer.Deserialize<PrtgValueRow>(item.JsonUtf8)!).ToList(),
            HostMaps = items.Where(item => item.PropertyName == "HostMaps").Select(item => JsonSerializer.Deserialize<PrtgHostMapRow>(item.JsonUtf8)!).ToList(),
            ManualMaps = items.Where(item => item.PropertyName == "ManualMaps").Select(item => JsonSerializer.Deserialize<PrtgManualMapRow>(item.JsonUtf8)!).ToList(),
            Observations = items.Where(item => item.PropertyName == "Observations").Select(item => JsonSerializer.Deserialize<PrtgObservationRow>(item.JsonUtf8)!).ToList(),
            Timelines = items.Where(item => item.PropertyName == "Timelines").Select(item => JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(item.JsonUtf8)!).ToList(),
            SemanticEvidence = items.Where(item => item.PropertyName == "SemanticEvidence").Select(item => JsonSerializer.Deserialize<PrtgDiskSemanticEvidence>(item.JsonUtf8)!).ToList(),
            SemanticResults = items.Where(item => item.PropertyName == "SemanticResults").Select(item => JsonSerializer.Deserialize<PrtgDiskVerificationResult>(item.JsonUtf8)!).ToList()
        };
        var packageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(package));
        var validator = new PrtgLegacyJsonTransferValidator();
        var validation = await validator.ValidateAsync(1, (_, _) => Task.FromResult(packageBytes), CancellationToken.None);
        Assert.Equal(2, validation.FormatVersion);
        Assert.Equal(2, validation.Counts["Timelines"]);
    }

    [Fact]
    public async Task TwoMiBAsciiAndSmallCjkRowFitsActualJsonLimit()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 0);
        context.PrtgObservations.Add(new PrtgObservationRow
        {
            SnapshotId = "large-ascii", RecordDate = From, ContentJson = new string('a', 2 * 1024 * 1024) + "診斷🧪",
            DecisionKey = "large", HostId = 1, DeviceObjid = 1, RuleCode = "r", EventKey = "e",
            SourceName = "source", Category = "disk", SourceHint = "hint", QualityReason = "ok",
            FormatVersion = 2, RecordedAtUtc = DateTime.SpecifyKind(From, DateTimeKind.Utc), SupplementStatus = "shadow"
        });
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var row = new List<byte[]>();
        await foreach (var item in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, Policy(), 7, CancellationToken.None))
            if (item.PropertyName == "Observations") row.Add(item.JsonUtf8);
        using var large = row.Select(bytes => JsonDocument.Parse(bytes)).Single(document =>
            document.RootElement.GetProperty("SnapshotId").GetString() == "large-ascii");
        Assert.True(large.RootElement.GetRawText().Length > 2 * 1024 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(large.RootElement.GetRawText()) < PrtgDiagnosticExportSource.MaximumRowBytes);
        Assert.EndsWith("診斷🧪", large.RootElement.GetProperty("ContentJson").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticDictionaryLargerThanFourMiBStreamsInBoundedPages()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 5200);
        await context.SaveChangesAsync();
        Assert.True(Encoding.UTF8.GetByteCount(context.Blobs.Single(blob =>
            blob.BlobKey == PrtgDiskSemanticEvidenceStore.BlobKey).Content) > PrtgDiagnosticExportSource.MaximumRowBytes);
        await using var transaction = await context.Database.BeginTransactionAsync();
        fixture.Counter.Reset();

        var count = 0;
        long previous = 0;
        await foreach (var item in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, Policy(), 7, CancellationToken.None))
        {
            if (item.PropertyName != "SemanticEvidence") continue;
            using var document = JsonDocument.Parse(item.JsonUtf8);
            var id = document.RootElement.GetProperty("SensorObjid").GetInt64();
            Assert.True(id > previous);
            previous = id;
            Assert.True(item.JsonUtf8.Length <= PrtgDiagnosticExportSource.MaximumRowBytes);
            count++;
        }
        Assert.Equal(5200, count);
    }

    [Fact]
    public async Task RejectsSingleOversizedRowAndDuplicateDictionaryKeys()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 0);
        context.PrtgObservations.Add(new PrtgObservationRow
        {
            SnapshotId = "oversized-row", RecordDate = From, ContentJson = new string('漢', 800_000),
            DecisionKey = "d", ActiveKey = null, HostId = 1, DeviceObjid = 1,
            RuleCode = "r", EventKey = "e", SourceName = "source", Category = "disk",
            SourceHint = "hint", QualityReason = "ok", FormatVersion = 2, RecordedAtUtc = From,
            SupplementStatus = "shadow"
        });
        context.Blobs.Local.Single(blob => blob.BlobKey == PrtgDiskSemanticEvidenceStore.BlobKey).Content = "{\"1\":{},\"1\":{}}";
        await context.SaveChangesAsync();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
                    context, From, Through, Policy(), 7, CancellationToken.None)) { }
            });
            Assert.Contains("4 MiB", error.Message, StringComparison.Ordinal);
        }

        context.ChangeTracker.Clear();
        var oversized = await context.PrtgObservations.SingleAsync(row => row.SnapshotId == "oversized-row");
        context.PrtgObservations.Remove(oversized);
        await context.SaveChangesAsync();
        await using var duplicateTransaction = await context.Database.BeginTransactionAsync();
        var duplicateError = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
                context, From, Through, Policy(), 7, CancellationToken.None)) { }
        });
        Assert.Contains("重複 key", duplicateError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawOversizedStringIsRejectedByPageLengthProjectionBeforeEntityRead()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 0);
        context.PrtgObservations.Add(new PrtgObservationRow
        {
            SnapshotId = "raw-oversized", RecordDate = From, ContentJson = new string(' ', 10 * 1024 * 1024),
            DecisionKey = "large", HostId = 1, DeviceObjid = 1, RuleCode = "r", EventKey = "e",
            SourceName = "source", Category = "disk", SourceHint = "hint", QualityReason = "ok",
            FormatVersion = 2, RecordedAtUtc = DateTime.SpecifyKind(From, DateTimeKind.Utc), SupplementStatus = "shadow"
        });
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        fixture.Counter.Reset();

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
                context, From, Through, Policy(), 7, CancellationToken.None)) { }
        });
        Assert.Contains("9 MiB", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Counter.ObservationLengthPages);
        Assert.Equal(0, fixture.Counter.ObservationEntityPages);
    }

    [Fact]
    public async Task StalePolicyVersionAndPreCancelledEnumerationIssueNoDataQueries()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context, largeDictionaryEntries: 0);
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var source = new PrtgDiagnosticExportSource();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in source.EnumerateRowsAsync(context, From, Through, Policy(), 99, CancellationToken.None)) { }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            fixture.Counter.Reset();
            await foreach (var _ in source.EnumerateRowsAsync(context, From, Through, Policy(), 7,
                new CancellationToken(canceled: true))) { }
        });
        Assert.Equal(0, fixture.Counter.ReaderCommands);

        foreach (var invalidSensors in new[] { new List<long> { 501, 501 }, new List<long> { 0 },
                     Enumerable.Range(1, 15001).Select(value => (long)value).ToList() })
        {
            fixture.Counter.Reset();
            var invalidPolicy = Policy();
            invalidPolicy.SensorIds = invalidSensors;
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (var _ in source.EnumerateRowsAsync(context, From, Through, invalidPolicy, 7,
                    CancellationToken.None)) { }
            });
            Assert.Equal(0, fixture.Counter.ReaderCommands);
        }
    }

    private static void SeedV2(LfDbContext context, int largeDictionaryEntries)
    {
        for (var id = 1; id <= 205; id++)
            context.PrtgDevices.Add(new PrtgDeviceRow
            {
                Objid = id, Name = id == 1 ? "診斷🧪" : $"device-{id}", GroupPath = "root",
                CreatedAt = From, SyncedAt = From
            });
        context.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 501, DeviceObjid = 1, Name = "sensor", SensorType = "cpu", CreatedAt = From, SyncedAt = From
        });
        context.PrtgStateChanges.AddRange(
            new PrtgStateChangeRow { SensorObjid = 501, ChangedAt = From.AddHours(2), Status = "Up", Message = "inside", Quality = "ok", CreatedAt = From },
            new PrtgStateChangeRow { SensorObjid = 501, ChangedAt = From.AddDays(-1), Status = "Down", Message = "outside-date-range", Quality = "ok", CreatedAt = From });
        context.PrtgValues.AddRange(
            new PrtgValueRow { SensorObjid = 501, PeriodStart = From.AddHours(1), AvgValue = 3, Quality = "ok", CreatedAt = From },
            new PrtgValueRow { SensorObjid = 501, PeriodStart = From.AddDays(5), AvgValue = 4, Quality = "ok", CreatedAt = From });
        context.PrtgHostMaps.AddRange(
            new PrtgHostMapRow { MapDate = From, DeviceObjid = 1, HostId = 11, HostName = "host", MapStatus = "ok", CreatedAt = From },
            new PrtgHostMapRow { MapDate = From.AddDays(5), DeviceObjid = 1, HostId = 11, HostName = "future", MapStatus = "ok", CreatedAt = From });
        context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = 1, HostId = 11, CreatedBy = "admin", CreatedAt = From });
        context.PrtgObservations.Add(new PrtgObservationRow
        {
            SnapshotId = "snapshot-1", DecisionKey = "decision", HostId = 11, RecordDate = From,
            DeviceObjid = 1, RuleCode = "rule", EventKey = "event", SourceName = "source", Category = "disk",
            SourceHint = "hint", QualityReason = "ok", FormatVersion = 2, ContentJson = "{}",
            RecordedAtUtc = DateTime.SpecifyKind(From, DateTimeKind.Utc), SupplementStatus = "shadow"
        });
        context.PrtgObservations.Add(new PrtgObservationRow
        {
            SnapshotId = "snapshot-outside", DecisionKey = "decision-outside", HostId = 11,
            RecordDate = From.AddDays(5), DeviceObjid = 1, RuleCode = "rule", EventKey = "event",
            SourceName = "source", Category = "disk", SourceHint = "hint", QualityReason = "ok",
            FormatVersion = 2, ContentJson = "{}", RecordedAtUtc = DateTime.SpecifyKind(From, DateTimeKind.Utc),
            SupplementStatus = "shadow"
        });

        context.Blobs.Add(new BlobRow { BlobKey = PrtgMonitoringPolicyStore.BlobKey,
            Content = JsonSerializer.Serialize(Policy()), Version = 7, UpdatedAt = From });
        context.Blobs.Add(new BlobRow { BlobKey = PrtgSensorTimelineStore.Prefix + "501",
            Content = JsonSerializer.Serialize(new PrtgSensorTimelineEvidence
            {
                SensorId = 501, HostId = 11, SourceGeneration = "source", ResourceGeneration = "resource",
                ValidFrom = DateTimeOffset.UtcNow, LastAttemptAt = DateTimeOffset.UtcNow,
                Coverage = [new(501, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-02T00:00:00Z"), "source", "resource")],
                States = [new(501, DateTimeOffset.Parse("2026-10-01T12:00:00Z"), "Up", "source", "resource")]
            }), Version = 1, UpdatedAt = From });

        var semanticCount = Math.Max(1, largeDictionaryEntries);
        var evidence = new Dictionary<long, PrtgDiskSemanticEvidence>(semanticCount);
        for (var index = 1; index <= semanticCount; index++)
        {
            var id = (long)index;
            evidence[id] = new PrtgDiskSemanticEvidence(id, id, 11, "disk", "used", "Used", "%", 1,
                "up", PrtgDiskSemanticEvidenceSource.Manual, 1, new string('e', 700),
                DateTime.SpecifyKind(From, DateTimeKind.Utc), "v1");
        }
        context.Blobs.Add(new BlobRow { BlobKey = PrtgDiskSemanticEvidenceStore.BlobKey,
            Content = JsonSerializer.Serialize(evidence), Version = 1, UpdatedAt = From });
        context.Blobs.Add(new BlobRow { BlobKey = PrtgDiskVerificationResultStore.BlobKey,
            Content = JsonSerializer.Serialize(new Dictionary<long, PrtgDiskVerificationResult>
            {
                [501] = new(501, 1, 11, "disk", "matched", "summary", "used", "Used", "%", 1,
                    "up", 1, true, DateTime.SpecifyKind(From, DateTimeKind.Utc), From, "v1")
            }), Version = 1, UpdatedAt = From });
    }

    private static PrtgMonitoringPolicy Policy() => new()
    {
        Revision = "r1", CoreSystemId = "core", SourceGeneration = "source", EndpointHint = "endpoint",
        ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), HostIds = [11], SensorIds = [501, 777],
        ConfirmedBy = "admin", SourceTimeZoneId = "UTC", SourceCultureName = "en-US"
    };

    private sealed class ExportFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-export-source-" + Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(_directory, "export.db");
        public ExportCommandCounter Counter { get; } = new();

        public ExportFixture() => Directory.CreateDirectory(_directory);

        public LfDbContext OpenContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .AddInterceptors(Counter)
            .Options);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ExportCommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }
        public int DevicePageCommands { get; private set; }
        public int ObservationLengthPages { get; private set; }
        public int ObservationEntityPages { get; private set; }

        public void Reset() { ReaderCommands = 0; DevicePageCommands = 0; ObservationLengthPages = 0; ObservationEntityPages = 0; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            if (command.CommandText.Contains("lf_prtg_devices", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)) DevicePageCommands++;
            if (command.CommandText.Contains("lf_prtg_observations", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                if (command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) ObservationLengthPages++;
                else ObservationEntityPages++;
            }
            return ValueTask.FromResult(result);
        }
    }
}

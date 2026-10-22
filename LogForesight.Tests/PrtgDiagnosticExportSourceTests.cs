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
        SeedV2(context);
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var items = new List<PrtgDiagnosticExportItem>();
        await foreach (var item in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, Policy(), 7, CancellationToken.None))
            items.Add(item);

        Assert.Equal(205, items.Count(item => item.PropertyName == "Devices"));
        Assert.Single(items, item => item.PropertyName == "Sensors");
        Assert.Single(items, item => item.PropertyName == "StateChanges");
        Assert.Single(items, item => item.PropertyName == "Values");
        Assert.Single(items, item => item.PropertyName == "HostMaps");
        Assert.Single(items, item => item.PropertyName == "ManualMaps");
        // 第 57 輪起不再輸出 Observations 區段。
        Assert.DoesNotContain(items, item => item.PropertyName == "Observations");
        // 三組舊陣列名稱保留給舊匯入檔相容，現行匯出不輸出任何列。
        Assert.DoesNotContain(items, item => item.PropertyName is "Timelines" or "SemanticEvidence" or "SemanticResults");
        Assert.Equal("ManualMaps", items[items.FindIndex(item => item.PropertyName == "SourcePolicy") - 1].PropertyName);
        Assert.Equal(new[] { "Devices", "Sensors", "StateChanges", "Values", "HostMaps", "ManualMaps", "Timelines", "SemanticEvidence", "SemanticResults" },
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
                default: throw new InvalidDataException($"未預期的 V2 匯出欄位 {item.PropertyName}。");
            }
        }

        var device = JsonSerializer.Deserialize<PrtgDeviceRow>(items.Single(item => item.PropertyName == "Devices" &&
            JsonDocument.Parse(item.JsonUtf8).RootElement.GetProperty("Objid").GetInt64() == 1).JsonUtf8)!;
        Assert.Equal("診斷🧪", device!.Name);
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
            ManualMaps = items.Where(item => item.PropertyName == "ManualMaps").Select(item => JsonSerializer.Deserialize<PrtgManualMapRow>(item.JsonUtf8)!).ToList()
        };
        // 三組舊陣列名稱只存在於封套（現行套件型別已不含），序列化後以空陣列補上。
        var packageNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(package))!.AsObject();
        foreach (var name in new[] { "Timelines", "SemanticEvidence", "SemanticResults" })
            packageNode[name] = new System.Text.Json.Nodes.JsonArray();
        var packageBytes = Encoding.UTF8.GetBytes(packageNode.ToJsonString());
        var validator = new PrtgLegacyJsonTransferValidator();
        var validation = await validator.ValidateAsync(1, (_, _) => Task.FromResult(packageBytes), CancellationToken.None);
        Assert.Equal(2, validation.FormatVersion);
        Assert.Equal(0, validation.Counts["Timelines"]);
    }

    [Fact]
    public async Task TwoMiBAsciiAndSmallCjkRowFitsActualJsonLimit()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context);
        context.PrtgStateChanges.Add(new PrtgStateChangeRow
        {
            SensorObjid = 501, ChangedAt = From.AddHours(3), Status = "Down", Quality = "ok", CreatedAt = From,
            Message = new string('a', 2 * 1024 * 1024) + "診斷🧪"
        });
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var row = new List<byte[]>();
        await foreach (var item in new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, Policy(), 7, CancellationToken.None))
            if (item.PropertyName == "StateChanges") row.Add(item.JsonUtf8);
        using var large = row.Select(bytes => JsonDocument.Parse(bytes)).Single(document =>
            document.RootElement.GetProperty("Message").GetString()!.StartsWith("aaaa", StringComparison.Ordinal));
        Assert.True(large.RootElement.GetRawText().Length > 2 * 1024 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(large.RootElement.GetRawText()) < PrtgDiagnosticExportSource.MaximumRowBytes);
        Assert.EndsWith("診斷🧪", large.RootElement.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawOversizedStringIsRejectedByPageLengthProjectionBeforeEntityRead()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context);
        context.PrtgStateChanges.Add(new PrtgStateChangeRow
        {
            SensorObjid = 501, ChangedAt = From.AddHours(3), Status = "Down", Quality = "ok", CreatedAt = From,
            Message = new string(' ', 10 * 1024 * 1024)
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
        Assert.Equal(1, fixture.Counter.StateChangeLengthPages);
        Assert.Equal(0, fixture.Counter.StateChangeEntityPages);
    }


    [Fact]
    public async Task StalePolicyVersionAndPreCancelledEnumerationIssueNoDataQueries()
    {
        using var fixture = new ExportFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        SeedV2(context);
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

    private static void SeedV2(LfDbContext context)
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
        context.Blobs.Add(new BlobRow { BlobKey = PrtgMonitoringPolicyStore.BlobKey,
            Content = JsonSerializer.Serialize(Policy()), Version = 7, UpdatedAt = From });
    }

    private static PrtgMonitoringPolicy Policy() => new()
    {
        Revision = "r1", HostIds = [11], SensorIds = [501, 777]
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
        public int StateChangeLengthPages { get; private set; }
        public int StateChangeEntityPages { get; private set; }
        public int ValueLengthPages { get; private set; }
        public int ValueEntityPages { get; private set; }

        public void Reset() { ReaderCommands = 0; DevicePageCommands = 0; StateChangeLengthPages = 0; StateChangeEntityPages = 0; ValueLengthPages = 0; ValueEntityPages = 0; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            if (command.CommandText.Contains("lf_prtg_values", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                if (command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) ValueLengthPages++;
                else ValueEntityPages++;
            }
            if (command.CommandText.Contains("lf_prtg_devices", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)) DevicePageCommands++;
            if (command.CommandText.Contains("lf_prtg_state_changes", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            {
                if (command.CommandText.Contains("length(", StringComparison.OrdinalIgnoreCase)) StateChangeLengthPages++;
                else StateChangeEntityPages++;
            }
            return ValueTask.FromResult(result);
        }
    }
}

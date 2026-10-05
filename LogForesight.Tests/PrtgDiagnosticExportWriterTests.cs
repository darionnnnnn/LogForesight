using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiagnosticExportWriterTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime Through = new(2026, 10, 3);

    [Fact]
    public async Task RealSourceStreamsLargeV2PackageWithEmptyArraysUtf8AndFallbackTimeline()
    {
        using var fixture = new WriterFixture();
        using var context = fixture.OpenContext();
        await context.Database.EnsureCreatedAsync();
        var policy = Policy();
        policy.SensorIds = [777];
        context.Blobs.Add(new BlobRow
        {
            BlobKey = PrtgMonitoringPolicyStore.BlobKey,
            Content = JsonSerializer.Serialize(policy), Version = 7, UpdatedAt = From
        });
        for (var id = 1; id <= 48; id++)
            context.PrtgDevices.Add(new PrtgDeviceRow
            {
                Objid = id,
                Name = id == 1 ? "診斷🧪" : new string('a', 100_000),
                GroupPath = "root", CreatedAt = From, SyncedAt = From
            });
        await context.SaveChangesAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var source = new PrtgDiagnosticExportSource().EnumerateRowsAsync(
            context, From, Through, policy, 7, CancellationToken.None);
        using var output = new AsyncOnlyWriteStream();
        var result = await new PrtgDiagnosticExportWriter().WriteAsync(source, output,
            new PrtgDiagnosticExportHeader(DateTime.SpecifyKind(new DateTime(2026, 10, 5), DateTimeKind.Utc), From, Through));

        var package = output.ToArray();
        Assert.True(package.Length > EfPrtgTransferStore.MaxChunkBytes);
        Assert.InRange(output.MaxSingleWrite, 1, EfPrtgTransferStore.MaxChunkBytes);
        Assert.Equal(48, result.Counts["Devices"]);
        Assert.Equal(1, result.Counts["Timelines"]);
        Assert.All(PrtgDiagnosticExportSource.V2ArrayProperties.Where(name => name != "Devices" && name != "Timelines"),
            name => Assert.Equal(0, result.Counts[name]));
        Assert.InRange(result.ManifestUtf8Bytes, 1, PrtgDiagnosticExportWriter.MaximumManifestUtf8Bytes);

        var manifestMarker = Encoding.UTF8.GetBytes(",\"Manifest\":");
        var manifestOffset = package.AsSpan().IndexOf(manifestMarker);
        Assert.True(manifestOffset > 0);
        var expectedHash = Convert.ToHexString(SHA256.HashData(package.AsSpan(0, manifestOffset)));
        Assert.Equal(expectedHash, result.Sha256BeforeManifest);
        Assert.Equal(manifestOffset, result.BytesBeforeManifest);
        Assert.Equal(package.LongLength, result.TotalBytes);

        var chunks = Split(package, EfPrtgTransferStore.MaxChunkBytes);
        var validation = await new PrtgLegacyJsonTransferValidator().ValidateAsync(
            chunks.Count, (ordinal, _) => Task.FromResult(chunks[ordinal]), CancellationToken.None);
        Assert.Equal(2, validation.FormatVersion);
        Assert.Equal(48, validation.Counts["Devices"]);
        Assert.Equal(1, validation.Counts["Timelines"]);
        Assert.Equal(0, validation.Counts["Sensors"]);
        Assert.Equal(0, validation.Counts["SemanticEvidence"]);
        using var document = JsonDocument.Parse(package);
        Assert.Equal("診斷🧪", document.RootElement.GetProperty("Devices")[0].GetProperty("Name").GetString());
        Assert.Equal("diagnostic-only", document.RootElement.GetProperty("Purpose").GetString());
        var timeline = document.RootElement.GetProperty("Timelines").EnumerateArray().Single();
        Assert.Equal(777, timeline.GetProperty("SensorId").GetInt64());
        Assert.Equal(0, timeline.GetProperty("Coverage").GetArrayLength());
        Assert.Equal(0, timeline.GetProperty("States").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("Sensors").GetArrayLength());
        Assert.Equal(PrtgDiagnosticExportSource.V2ArrayProperties.Count,
            PrtgDiagnosticExportSource.V2ArrayProperties.Count(name => document.RootElement.TryGetProperty(name, out _)));
    }

    [Fact]
    public async Task CancellationDoesNotEnumerateFurtherOrReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var enumerationCount = 0;

        async IAsyncEnumerable<PrtgDiagnosticExportItem> Rows()
        {
            enumerationCount++;
            yield return new PrtgDiagnosticExportItem("Devices", JsonSerializer.SerializeToUtf8Bytes(
                new PrtgDeviceRow { Objid = 1, Name = "one" }));
            enumerationCount++;
            cancellation.Cancel();
            await Task.Yield();
            yield return new PrtgDiagnosticExportItem("Devices", JsonSerializer.SerializeToUtf8Bytes(
                new PrtgDeviceRow { Objid = 2, Name = "two" }));
            enumerationCount++;
        }

        using var output = new AsyncOnlyWriteStream(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PrtgDiagnosticExportWriter().WriteAsync(
            Rows(), output, Header(), cancellation.Token));
        Assert.Equal(2, enumerationCount);
        Assert.Equal(0, output.WritesAfterCancellation);
        Assert.DoesNotContain("Manifest", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidSequences))]
    public async Task RejectsUnknownRepeatedAndOutOfOrderProperties(PrtgDiagnosticExportItem[] rows)
    {
        using var output = new AsyncOnlyWriteStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => new PrtgDiagnosticExportWriter().WriteAsync(
            FromRows(rows), output, Header()));
        Assert.DoesNotContain("Manifest", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InvalidSequences()
    {
        var device = Row("Devices", new PrtgDeviceRow { Objid = 1, Name = "one" });
        var sensor = Row("Sensors", new PrtgSensorRow { Objid = 2, DeviceObjid = 1, Name = "two", SensorType = "x" });
        yield return [new[] { Row("Unrecognized", new { Value = 1 }) }];
        yield return [new[] { sensor, device }];
        yield return [new[] { device, device }];
    }

    private static PrtgDiagnosticExportItem Row<T>(string name, T value) =>
        new(name, JsonSerializer.SerializeToUtf8Bytes(value));

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> FromRows(IEnumerable<PrtgDiagnosticExportItem> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private static List<byte[]> Split(byte[] bytes, int max)
    {
        var result = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += max)
            result.Add(bytes.AsSpan(offset, Math.Min(max, bytes.Length - offset)).ToArray());
        return result;
    }

    private static PrtgDiagnosticExportHeader Header() => new(
        DateTime.SpecifyKind(new DateTime(2026, 10, 5), DateTimeKind.Utc), From, Through);

    private static PrtgMonitoringPolicy Policy() => new()
    {
        Revision = "r1", CoreSystemId = "core", SourceGeneration = "source", EndpointHint = "endpoint",
        ValidFrom = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), HostIds = [11], SensorIds = [],
        ConfirmedBy = "admin", SourceTimeZoneId = "UTC", SourceCultureName = "en-US"
    };

    private sealed class WriterFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-export-writer-" + Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(_directory, "writer.db");

        public WriterFixture() => Directory.CreateDirectory(_directory);

        public LfDbContext OpenContext() => new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={DatabasePath};Pooling=False")
            .Options);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class AsyncOnlyWriteStream(CancellationToken observedCancellation = default) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public int MaxSingleWrite { get; private set; }
        public int WritesAfterCancellation { get; private set; }
        public byte[] ToArray() => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

        public override void Flush() => throw new InvalidOperationException("測試目的 stream 禁止同步 flush。");
        public override Task FlushAsync(CancellationToken cancellationToken) => _buffer.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("測試目的 stream 禁止同步寫入。");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException("測試目的 stream 禁止同步寫入。");

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxSingleWrite = Math.Max(MaxSingleWrite, buffer.Length);
            if (observedCancellation.IsCancellationRequested) WritesAfterCancellation++;
            await _buffer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _buffer.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _buffer.DisposeAsync();
    }
}

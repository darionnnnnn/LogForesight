using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTimelineMetadataQueryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly QueryCommandInterceptor _commands = new();
    private readonly DbContextOptions<LfDbContext> _options;

    public PrtgTimelineMetadataQueryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection).AddInterceptors(_commands).Options;
        using var db = NewContext();
        db.Database.EnsureCreated();
        var nullCoverage = Evidence(46);
        nullCoverage.Coverage = null!;
        var scalarCoverageItem = JsonNode.Parse(JsonSerializer.Serialize(Evidence(47), LfJsonOptions.Pretty))!.AsObject();
        scalarCoverageItem["Coverage"] = JsonSerializer.SerializeToNode(new object[] { "not-an-object" });
        var hugeCoverageDate = JsonNode.Parse(JsonSerializer.Serialize(Evidence(48), LfJsonOptions.Pretty))!.AsObject();
        hugeCoverageDate["Coverage"]!.AsArray()[0]!["From"] = new string('x', 2 * 1024 * 1024);
        var objectCoverageDate = JsonNode.Parse(JsonSerializer.Serialize(Evidence(49), LfJsonOptions.Pretty))!.AsObject();
        objectCoverageDate["Coverage"]!.AsArray()[0]!["Through"] = JsonSerializer.SerializeToNode(new { nested = true });
        db.Blobs.AddRange(
            Blob(PrtgSensorTimelineStore.Prefix + "41", Evidence(41)),
            Blob(PrtgSensorTimelineStore.Prefix + "42", "{ malformed"),
            Blob(PrtgSensorTimelineStore.Prefix + "43", Evidence(43, qualityReason: new string('q', 193))),
            Blob(PrtgSensorTimelineStore.Prefix + "44", Evidence(999)),
            Blob(PrtgSensorTimelineStore.Prefix + "46", nullCoverage),
            Blob(PrtgSensorTimelineStore.Prefix + "47", scalarCoverageItem.ToJsonString()),
            Blob(PrtgSensorTimelineStore.Prefix + "48", hugeCoverageDate.ToJsonString()),
            Blob(PrtgSensorTimelineStore.Prefix + "49", objectCoverageDate.ToJsonString()));
        db.SaveChanges();
        _commands.Reset();
    }

    [Fact]
    public async Task SQLite只投影有界標量並將畸形超限及key不符標為unready()
    {
        var query = new PrtgTimelineMetadataQuery(NewContext);

        var result = await query.ReadAsync([41, 42, 43, 44, 45, 46], PrtgSensorTimelineStore.Prefix,
            CancellationToken.None);

        Assert.True(result.StableVersions);
        var row = result.Rows[41];
        Assert.False(row.Malformed);
        Assert.Equal(41, row.SensorId);
        Assert.Equal(8, row.BootstrapPagesRead);
        Assert.Equal("capacity-unverified", row.BootstrapStatus);
        Assert.Equal("identity-warmup", row.QualityReason);
        Assert.Equal(1, row.CoverageSpanCount);
        Assert.Equal(1, row.StateCount);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00+00:00"), row.CoveredFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:00:00+00:00"), row.CoveredThrough);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00+00:00"), row.DiskSemanticValidFrom);
        Assert.True(result.Rows[42].Malformed);
        Assert.Equal("metadata-malformed", result.Rows[42].QualityReason);
        Assert.True(result.Rows[43].Malformed);
        Assert.True(result.Rows[44].Malformed);
        Assert.True(result.Rows[46].Malformed);
        Assert.DoesNotContain(45, result.Rows.Keys);

        Assert.Equal(2, _commands.BlobReads.Count);
        Assert.Contains(_commands.BlobReads, sql => sql.Contains("json_extract", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_commands.BlobReads, sql => sql.Contains("AS [States]", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("AS [Coverage]", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("json_extract(content, '$.States')", StringComparison.OrdinalIgnoreCase) ||
            StartsWithFullContentProjection(sql));
    }

    [Fact]
    public async Task 過頁上限及重複或無效識別碼在查詢前拒絕()
    {
        var query = new PrtgTimelineMetadataQuery(NewContext);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.ReadAsync(
            Enumerable.Range(1, 101).Select(id => (long)id).ToArray(), PrtgSensorTimelineStore.Prefix,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.ReadAsync(
            [41, 41], PrtgSensorTimelineStore.Prefix, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.ReadAsync(
            [0], PrtgSensorTimelineStore.Prefix, CancellationToken.None));
        Assert.Empty(_commands.BlobReads);
    }

    [Fact]
    public async Task coverage日期scalar只回傳49字元前綴並以原長度及畸形array項目failclosed()
    {
        var result = await new PrtgTimelineMetadataQuery(NewContext).ReadAsync(
            [47, 48, 49], PrtgSensorTimelineStore.Prefix, CancellationToken.None);

        Assert.True(result.StableVersions);
        Assert.True(result.Rows[47].Malformed);
        Assert.True(result.Rows[48].Malformed);
        Assert.True(result.Rows[49].Malformed);
        var scalarProjection = Assert.Single(_commands.BlobReads.Where(sql =>
            sql.Contains("AS [CoveredFromText]", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("substr(CAST(json_extract(content, '$.Coverage[0].From') AS TEXT), 1, 49)", scalarProjection,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("length(CAST(json_extract(content, '$.Coverage[0].From') AS TEXT))", scalarProjection,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("length(CAST(substr", scalarProjection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 讀取metadata後資料庫版本改變時回報整頁不穩定()
    {
        var changed = false;
        _commands.BeforeVersionRead = () =>
        {
            if (changed) return;
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE lf_blobs SET version = version + 1 WHERE blob_key = $key";
            command.Parameters.AddWithValue("$key", PrtgSensorTimelineStore.Prefix + "41");
            command.ExecuteNonQuery();
            changed = true;
        };

        var result = await new PrtgTimelineMetadataQuery(NewContext).ReadAsync(
            [41], PrtgSensorTimelineStore.Prefix, CancellationToken.None);

        Assert.True(changed);
        Assert.False(result.StableVersions);
        _commands.BeforeVersionRead = null;
    }

    private LfDbContext NewContext() => new(_options);

    private static bool StartsWithFullContentProjection(string sql)
    {
        var normalized = sql.Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal).TrimStart();
        return normalized.StartsWith("SELECT content ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("SELECT content,", StringComparison.OrdinalIgnoreCase);
    }

    private static BlobRow Blob(string key, PrtgSensorTimelineEvidence evidence) => Blob(key,
        JsonSerializer.Serialize(evidence, LfJsonOptions.Pretty));

    private static BlobRow Blob(string key, string content) => new()
    {
        BlobKey = key, Content = content, Version = 1, UpdatedAt = DateTime.UtcNow
    };

    private static PrtgSensorTimelineEvidence Evidence(long sensorId, string qualityReason = "identity-warmup")
    {
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00+00:00");
        var through = DateTimeOffset.Parse("2026-10-02T00:00:00+00:00");
        return new PrtgSensorTimelineEvidence
        {
            SensorId = sensorId,
            HostId = 7,
            SourceGeneration = "source-generation-a",
            ResourceGeneration = "resource-generation-a",
            EffectiveScopeFingerprint = new string('a', 64),
            BootstrapStatus = "capacity-unverified",
            BootstrapStartedAt = from,
            BootstrapPagesRead = 8,
            PendingNextPage = 9,
            QualityReason = qualityReason,
            Coverage = [new PrtgSensorCoverage(sensorId, from, through, "source-generation-a", "resource-generation-a")],
            States = [new PrtgTimedState(sensorId, from, "Up", "source-generation-a", "resource-generation-a")],
            DiskSemanticValidFrom = from,
            DiskSemanticCheckedAt = through
        };
    }

    public void Dispose() => _connection.Dispose();

    private sealed class QueryCommandInterceptor : DbCommandInterceptor
    {
        public List<string> BlobReads { get; } = [];
        public Action? BeforeVersionRead { get; set; }

        public void Reset() => BlobReads.Clear();

        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase))
                BlobReads.Add(command.CommandText);
            var isVersionReread = command.CommandText.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("BlobKey", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("Version", StringComparison.OrdinalIgnoreCase) &&
                !command.CommandText.Contains("json_extract", StringComparison.OrdinalIgnoreCase) &&
                !command.CommandText.Contains("OPENJSON", StringComparison.OrdinalIgnoreCase);
            if (BeforeVersionRead != null && isVersionReread)
            {
                var callback = BeforeVersionRead;
                BeforeVersionRead = null;
                callback();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Record(command); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
    }
}

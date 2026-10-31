using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 57 輪移除已停寫的 <c>lf_prtg_observations</c>：舊 DB 的殘留表要被冪等刪除；
/// 新匯出不再含 Observations 區段；舊包（含 Observations）仍可匯入，該區段只驗證、不寫入。
/// </summary>
public sealed class LegacyObservationsRemovalTests : IDisposable
{
    private const string ObservationRowJson =
        """{"SnapshotId":"snapshot","DecisionKey":"decision","HostId":23,"RecordDate":"2026-10-02T00:00:00","DeviceObjid":17,"RuleCode":"rule","EventKey":"event","SourceName":"source","Category":"category","SeverityRank":1,"ElevatesDayRisk":false,"Suppressed":false,"SourceHint":"hint","QualityReason":"quality","FormatVersion":1,"ContentJson":"{}","RecordedAtUtc":"2026-10-02T00:00:00Z"}""";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SchemaUpgrader_既有DB含停寫的觀察表_升級後移除且重跑不拋例外()
    {
        using var fx = new EfSqliteFixture();
        using (var ctx = fx.NewContext())
        {
            // 還原成舊部署：表與索引仍在、且有殘留資料。
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE lf_prtg_observations (
                    snapshot_id TEXT NOT NULL PRIMARY KEY, active_key TEXT NULL, host_id INTEGER NOT NULL,
                    record_date TEXT NOT NULL, recorded_at_utc TEXT NOT NULL
                )
                """);
            ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_lf_prtg_obs_host_date ON lf_prtg_observations (host_id, record_date)");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO lf_prtg_observations (snapshot_id, host_id, record_date, recorded_at_utc) VALUES ('old', 1, '2026-10-01', '2026-10-01')");
            Assert.Equal(1, ctx.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'lf_prtg_observations'").Single());
        }

        using (var ctx = fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using (var ctx = fx.NewContext())
        {
            Assert.Equal(0, ctx.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'lf_prtg_observations' OR name LIKE 'IX\\_lf\\_prtg\\_obs\\_%' ESCAPE '\\'").Single());
        }

        // 第二次升級：表已不存在，不得拋例外。
        using (var ctx = fx.NewContext()) SchemaUpgrader.Upgrade(ctx);
        using (var ctx = fx.NewContext())
        {
            Assert.Equal(0, ctx.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'lf_prtg_observations'").Single());
        }
    }

    [Fact]
    public async Task 串流匯出器的輸出與manifest都不含Observations_且拒收來源硬塞的該區段()
    {
        var from = new DateTime(2026, 10, 1);
        using var output = new MemoryStream();
        await new PrtgDiagnosticExportWriter().WriteAsync(PolicyOnly(), output,
            new PrtgDiagnosticExportHeader(from, from, from));

        using var document = JsonDocument.Parse(output.ToArray());
        Assert.False(document.RootElement.TryGetProperty("Observations", out _));
        Assert.False(document.RootElement.GetProperty("Manifest").GetProperty("rowCounts").TryGetProperty("Observations", out _));
        Assert.DoesNotContain("Observations", PrtgDiagnosticExportSource.V2ArrayProperties);

        async IAsyncEnumerable<PrtgDiagnosticExportItem> WithObservation()
        {
            await Task.Yield();
            yield return new PrtgDiagnosticExportItem("SourcePolicy", "{}"u8.ToArray());
            yield return new PrtgDiagnosticExportItem("Observations", "{}"u8.ToArray());
        }
        using var rejected = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() => new PrtgDiagnosticExportWriter().WriteAsync(WithObservation(), rejected,
            new PrtgDiagnosticExportHeader(from, from, from)));
    }

    [Fact]
    public async Task 舊版串流診斷包含Observations與manifest_驗證通過且筆數核對仍有效()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"FormatVersion\":2,\"Purpose\":\"diagnostic-only\",\"IntegrityContract\":\"manifest-sha256-v1\"," +
            "\"Devices\":[],\"Sensors\":[],\"StateChanges\":[],\"Values\":[],\"HostMaps\":[],\"ManualMaps\":[]," +
            "\"SourcePolicy\":{},\"Observations\":[" + ObservationRowJson + "]," +
            "\"Timelines\":[],\"SemanticEvidence\":[],\"SemanticResults\":[]");

        byte[] Build(long observationCount)
        {
            var counts = PrtgDiagnosticExportSource.V2ArrayProperties.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
            counts["Observations"] = observationCount;
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

        var validator = new PrtgLegacyJsonTransferValidator();
        var good = Build(1);
        var result = await validator.ValidateAsync(1, (_, _) => Task.FromResult(good), CancellationToken.None);
        Assert.Contains("manifest-verified", result.ResultManifestJson, StringComparison.Ordinal);
        Assert.Equal(1, result.Counts["Observations"]);

        var wrong = Build(2);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            validator.ValidateAsync(1, (_, _) => Task.FromResult(wrong), CancellationToken.None));
        Assert.StartsWith("manifest_count_mismatch:", error.Message, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> PolicyOnly()
    {
        await Task.Yield();
        yield return new PrtgDiagnosticExportItem("SourcePolicy", "{}"u8.ToArray());
    }
}

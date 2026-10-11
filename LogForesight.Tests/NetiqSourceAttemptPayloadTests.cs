using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class NetiqSourceAttemptPayloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-netiq-payload-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureInterceptor _interceptor = new();
    private readonly StorageBackend _backend;
    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    public NetiqSourceAttemptPayloadTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "attempts.db")}"
        }, _directory, _interceptor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LargeParentIsPatchedInDatabaseAndKeepsUnknownJsonAndOriginalRisk()
    {
        const long hostId = 940_001;
        var record = new DailyAnalysisRecord
        {
            HostId = hostId, Host = "large-host", Date = Day, LogSource = AnalysisLogSource.Netiq,
            RiskLevel = "High", Headline = "preserve", Summary = "payload"
        };
        var node = JsonSerializer.SerializeToNode(record)!.AsObject();
        node["UnknownFutureField"] = new string('u', 1_100_000);
        var content = node.ToJsonString();
        var originalRisk = "{\"rawRisk\":\"" + new string('r', 1_100_000) + "\"}";
        Seed(hostId, content, originalRisk);
        _interceptor.Commands.Clear();
        _interceptor.ParentReaderColumns.Clear();

        var attemptAt = new DateTime(2026, 10, 11, 3, 4, 5, DateTimeKind.Utc).AddTicks(6_789);
        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 84_001, "running", attemptAt);
        var recordBatchCommands = _interceptor.Commands.ToArray();
        var parentColumns = _interceptor.ParentReaderColumns.ToArray();

        var updated = Read(hostId);
        Assert.True(content.Length > 1_048_576);
        Assert.True(originalRisk.Length > 1_048_576);
        var updatedNode = JsonNode.Parse(updated.ContentJson)!.AsObject();
        Assert.Equal(new string('u', 1_100_000), updatedNode["UnknownFutureField"]!.GetValue<string>());
        Assert.Equal("running", updatedNode[nameof(DailyAnalysisRecord.LatestNetiqAttemptStatus)]!.GetValue<string>());
        Assert.Equal(attemptAt, updatedNode[nameof(DailyAnalysisRecord.LatestNetiqAttemptAtUtc)]!.GetValue<DateTime>());
        Assert.Equal(originalRisk, updated.OriginalRiskContentJson);
        Assert.Equal(8, updated.WriteRevision);
        var parentSelects = recordBatchCommands.Where(command =>
            command.Text.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
            command.Text.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(parentSelects);
        Assert.NotEmpty(parentColumns);
        Assert.All(parentColumns, column => Assert.True(column.Type == typeof(int) || column.Type == typeof(long),
            $"Parent metadata column {column.Name} unexpectedly returns {column.Type}."));
        Assert.All(parentSelects, command =>
        {
            Assert.DoesNotContain("SELECT p.content_json", command.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("original_risk_content_json", command.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void LocalAndPrunedParentsKeepJsonAndRevision()
    {
        const long localId = 940_002;
        const long prunedId = 940_003;
        Seed(localId, MakeJson(localId, AnalysisLogSource.Local));
        Seed(prunedId, MakeJson(prunedId, AnalysisLogSource.Netiq), pruned: true);
        var localBefore = Read(localId);
        var prunedBefore = Read(prunedId);

        NetiqSourceAttempt.RecordBatch(_backend, [localId, prunedId], Day, 84_002, "failed");

        Assert.Equal(localBefore.ContentJson, Read(localId).ContentJson);
        Assert.Equal(localBefore.WriteRevision, Read(localId).WriteRevision);
        Assert.Equal(prunedBefore.ContentJson, Read(prunedId).ContentJson);
        Assert.Equal(prunedBefore.WriteRevision, Read(prunedId).WriteRevision);
    }

    [Fact]
    public void JsonObjectWithLegalLeadingWhitespaceCanBePatched()
    {
        const long hostId = 940_010;
        var content = " \t\r\n" + MakeJson(hostId, AnalysisLogSource.Netiq);
        Seed(hostId, content);

        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 84_010, "running");

        var updated = Read(hostId);
        Assert.Equal(8, updated.WriteRevision);
        Assert.Equal("running", JsonSerializer.Deserialize<DailyAnalysisRecord>(updated.ContentJson)!.LatestNetiqAttemptStatus);
    }

    [Fact]
    public void HugeInvalidSourceValuesAreRejectedWithoutReturningTheirPayload()
    {
        var largeValue = new string('x', 500_000);
        var invalidPayloads = new[]
        {
            "{\"LogSource\":\"" + largeValue + "\"}",
            "{\"LogSource\":{\"payload\":\"" + largeValue + "\"}}"
        };
        var hostIds = new long[] { 940_011, 940_012 };
        for (var index = 0; index < hostIds.Length; index++) Seed(hostIds[index], invalidPayloads[index]);

        for (var index = 0; index < hostIds.Length; index++)
        {
            _interceptor.Commands.Clear();
            Assert.Throws<JsonException>(() => NetiqSourceAttempt.RecordBatch(_backend, [hostIds[index]], Day,
                84_011 + index, "running"));
            var parentSelects = _interceptor.Commands.Where(command =>
                command.Text.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.Text.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.NotEmpty(parentSelects);
            Assert.All(parentSelects, command => Assert.DoesNotContain(largeValue,
                string.Join("|", command.Values.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)))));
            Assert.Equal(invalidPayloads[index], Read(hostIds[index]).ContentJson);
            Assert.Equal(7, Read(hostIds[index]).WriteRevision);
        }
        Assert.Empty(ReadReceipts());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"LogSource\":\"2\"}")]
    [InlineData("{\"LogSource\":7}")]
    [InlineData("{\"LogSource\":2,\"LogSource\":2}")]
    [InlineData("{\"LogSource\":2,\"LatestNetiqAttemptStatus\":\"failed\",\"LatestNetiqAttemptStatus\":\"success\"}")]
    [InlineData("{\"LogSource\":2,\"LatestNetiqAttemptAtUtc\":\"2026-10-10T00:00:00Z\",\"LatestNetiqAttemptAtUtc\":\"2026-10-10T00:00:00Z\"}")]
    [InlineData("[]")]
    public void MalformedOrAmbiguousParentRollsBackReceiptAndParent(string content)
    {
        const long hostId = 940_004;
        Seed(hostId, content);

        Assert.Throws<JsonException>(() => NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 84_003, "running"));

        Assert.Empty(ReadReceipts());
        Assert.Equal(content, Read(hostId).ContentJson);
        Assert.Equal(7, Read(hostId).WriteRevision);
    }

    private string MakeJson(long hostId, AnalysisLogSource source) => JsonSerializer.Serialize(new DailyAnalysisRecord
    {
        HostId = hostId, Host = $"host-{hostId}", Date = Day, LogSource = source,
        RiskLevel = "High", Headline = "parent", Summary = "summary"
    });

    private void Seed(long hostId, string content, string? originalRisk = null, bool pruned = false)
    {
        using var context = _backend.CreateContext();
        context.DailyRecords.Add(new DailyRecordRow
        {
            HostId = hostId, HostName = $"host-{hostId}", RecordDate = Day, RiskLevel = "High",
            ContentJson = content, OriginalRiskContentJson = originalRisk, WriteRevision = 7,
            CreatedAt = DateTime.UtcNow, DetailPruned = pruned
        });
        context.SaveChanges();
    }

    private DailyRecordRow Read(long hostId)
    {
        using var context = _backend.CreateContext();
        return context.DailyRecords.AsNoTracking().Single(row => row.HostId == hostId && row.RecordDate == Day);
    }

    private List<LogLineRow> ReadReceipts()
    {
        using var context = _backend.CreateContext();
        return context.LogLines.AsNoTracking().Where(row => row.LogKey.StartsWith("netiq_hostday_attempt_")).ToList();
    }

    private sealed class CaptureInterceptor : DbCommandInterceptor
    {
        public List<(string Text, int ParameterCount, object?[] Values)> Commands { get; } = [];
        public List<(string Name, Type Type)> ParentReaderColumns { get; } = [];

        private void Capture(DbCommand command) => Commands.Add((command.CommandText, command.Parameters.Count,
            command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result)
        {
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase))
                for (var index = 0; index < result.FieldCount; index++)
                    ParentReaderColumns.Add((result.GetName(index), result.GetFieldType(index)));
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            Capture(command);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            Capture(command);
            return result;
        }
    }
}

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class NetiqSourceAttemptAtomicTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-netiq-attempt-atomic-" + Guid.NewGuid().ToString("N"));
    private readonly FaultingCommandInterceptor _interceptor = new();
    private readonly StorageBackend _backend;

    public NetiqSourceAttemptAtomicTests()
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

    private static DailyAnalysisRecord Parent(long hostId, AnalysisLogSource source = AnalysisLogSource.Netiq,
        string? attemptStatus = null, DateTime? attemptAtUtc = null) => new()
    {
        HostId = hostId,
        Host = $"host-{hostId}",
        Date = Day,
        RiskLevel = "High",
        RiskBasis = "source evidence",
        Headline = "preserve this parent",
        Summary = "preserve this summary",
        LogSource = source,
        LatestNetiqAttemptStatus = attemptStatus,
        LatestNetiqAttemptAtUtc = attemptAtUtc,
        TopIssues = [new LogIssueSignature { LogName = "System", Source = "fixture", EventId = 42, EventKey = $"fixture:{hostId}" }]
    };

    private const long BaseHostId = 930_000;
    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    private DailyRecordRow Seed(long hostId, DailyAnalysisRecord? record = null, bool pruned = false)
    {
        using var context = _backend.CreateContext();
        var entity = new DailyRecordRow
        {
            HostId = hostId,
            HostName = $"host-{hostId}",
            RecordDate = Day,
            RiskLevel = "High",
            ContentJson = record is null ? "{not-json" : JsonSerializer.Serialize(record),
            WriteRevision = 7,
            CreatedAt = DateTime.UtcNow,
            DetailPruned = pruned
        };
        context.DailyRecords.Add(entity);
        context.SaveChanges();
        return entity;
    }

    private DailyRecordRow ReadParent(long hostId)
    {
        using var context = _backend.CreateContext();
        return context.DailyRecords.AsNoTracking().Single(row => row.HostId == hostId && row.RecordDate == Day);
    }

    private List<LogLineRow> ReadReceipts()
    {
        using var context = _backend.CreateContext();
        return context.LogLines.AsNoTracking().Where(row => row.LogKey.StartsWith("netiq_hostday_attempt_"))
            .OrderBy(row => row.LogKey).ToList();
    }

    [Theory]
    [InlineData("running", "running")]
    [InlineData("failed", "failed")]
    [InlineData("partial", "partial")]
    [InlineData("retained-incomplete", "retained-incomplete")]
    [InlineData("stopped", "failed")]
    public void NonSuccessReceiptAtomicallyWithdrawsOnlyExistingUnprunedNetiqParent(string status, string parentStatus)
    {
        var netiqId = BaseHostId + 1;
        var localId = BaseHostId + 2;
        var prunedId = BaseHostId + 3;
        var withoutParentId = BaseHostId + 4;
        var originalNetiq = Parent(netiqId);
        Seed(netiqId, originalNetiq);
        Seed(localId, Parent(localId, AnalysisLogSource.Local));
        Seed(prunedId, Parent(prunedId), pruned: true);
        var beforeNetiq = ReadParent(netiqId);
        var beforeLocal = ReadParent(localId);
        var beforePruned = ReadParent(prunedId);

        NetiqSourceAttempt.RecordBatch(_backend, [netiqId, localId, prunedId, withoutParentId], Day, 81_001, status);

        var afterNetiq = ReadParent(netiqId);
        Assert.Equal(beforeNetiq.WriteRevision + 1, afterNetiq.WriteRevision);
        AssertJsonEqualExceptAttemptFields(beforeNetiq.ContentJson, afterNetiq.ContentJson);
        var withdrawn = JsonSerializer.Deserialize<DailyAnalysisRecord>(afterNetiq.ContentJson)!;
        Assert.Equal(parentStatus, withdrawn.LatestNetiqAttemptStatus);
        Assert.Equal(NetiqSourceAttempt.ReadLatest(_backend, Day, Day)[(netiqId, Day)].AttemptAtUtc,
            withdrawn.LatestNetiqAttemptAtUtc);
        Assert.False(withdrawn.CanSupplementWithPrtg());
        var store = _backend.RecordStore(new HostKey { HostId = netiqId, HostName = $"host-{netiqId}" });
        Assert.Contains(Day, MissingDateFinder.FindWithExistingDates(store, lookbackDays: 1).MissingDates);
        Assert.Equal(beforeLocal.ContentJson, ReadParent(localId).ContentJson);
        Assert.Equal(beforeLocal.WriteRevision, ReadParent(localId).WriteRevision);
        Assert.Equal(beforePruned.ContentJson, ReadParent(prunedId).ContentJson);
        Assert.Equal(beforePruned.WriteRevision, ReadParent(prunedId).WriteRevision);
        Assert.Single(ReadReceipts(), row => row.LogKey == NetiqSourceAttempt.LogKey(Day, withoutParentId));
    }

    [Fact]
    public void ParentUpdateFaultRollsBackReceiptAndPreviouslySavedFiveHundredHostChunk()
    {
        var hostIds = Enumerable.Range(0, 501).Select(index => BaseHostId + 100 + index).ToArray();
        var parentBefore = Seed(hostIds[0], Parent(hostIds[0]));
        var finalParentBefore = Seed(hostIds[500], Parent(hostIds[500]));
        _interceptor.FailParentUpdate = true;
        _interceptor.FailAtParentUpdate = 2;

        Assert.ThrowsAny<Exception>(() => NetiqSourceAttempt.RecordBatch(_backend, hostIds, Day, 81_002, "running"));

        Assert.True(_interceptor.ParentUpdateFailureObserved);
        Assert.Equal(2, _interceptor.ParentUpdateCount);
        Assert.Empty(ReadReceipts());
        var parentAfter = ReadParent(hostIds[0]);
        Assert.Equal(parentBefore.ContentJson, parentAfter.ContentJson);
        Assert.Equal(parentBefore.WriteRevision, parentAfter.WriteRevision);
        Assert.Equal(finalParentBefore.ContentJson, ReadParent(hostIds[500]).ContentJson);
        Assert.Equal(finalParentBefore.WriteRevision, ReadParent(hostIds[500]).WriteRevision);
    }

    [Fact]
    public void DelayedOlderRunOrTimestampLeavesReceiptAndQualifiedParentUntouched()
    {
        var hostId = BaseHostId + 700;
        Seed(hostId, Parent(hostId));
        var latestAt = DateTime.UtcNow.AddMinutes(-1);
        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 82_002, "success", latestAt);
        var savedReceipt = Assert.Single(ReadReceipts()).Line;
        var savedParent = ReadParent(hostId);

        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 82_001, "running", latestAt.AddSeconds(1));
        Assert.Equal(savedReceipt, Assert.Single(ReadReceipts()).Line);
        Assert.Equal(savedParent.ContentJson, ReadParent(hostId).ContentJson);
        Assert.Equal(savedParent.WriteRevision, ReadParent(hostId).WriteRevision);

        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 82_002, "failed", latestAt.AddSeconds(-1));
        Assert.Equal(savedReceipt, Assert.Single(ReadReceipts()).Line);
        Assert.Equal(savedParent.ContentJson, ReadParent(hostId).ContentJson);
        Assert.Equal(savedParent.WriteRevision, ReadParent(hostId).WriteRevision);
        Assert.True(JsonSerializer.Deserialize<DailyAnalysisRecord>(ReadParent(hostId).ContentJson)!.CanSupplementWithPrtg());
    }

    [Fact]
    public void SuccessReceiptDoesNotRequalifyRetainedFailedParent()
    {
        var hostId = BaseHostId + 701;
        Seed(hostId, Parent(hostId, attemptStatus: "failed", attemptAtUtc: DateTime.UtcNow.AddHours(-1)));

        NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 82_003, "success");

        var receipt = JsonSerializer.Deserialize<NetiqSourceAttempt.Receipt>(Assert.Single(ReadReceipts()).Line)!;
        Assert.Equal("success", receipt.Status);
        var retained = JsonSerializer.Deserialize<DailyAnalysisRecord>(ReadParent(hostId).ContentJson)!;
        Assert.Equal("failed", retained.LatestNetiqAttemptStatus);
        Assert.False(retained.CanSupplementWithPrtg());
    }

    [Fact]
    public void MalformedExistingParentRollsBackRunningReceipt()
    {
        var hostId = BaseHostId + 702;
        Seed(hostId);

        Assert.ThrowsAny<Exception>(() => NetiqSourceAttempt.RecordBatch(_backend, [hostId], Day, 82_004, "running"));

        Assert.Empty(ReadReceipts());
        Assert.Equal("{not-json", ReadParent(hostId).ContentJson);
    }

    [Fact]
    public void ParentInvalidationPagesFullJsonAtSixteenRowsWithoutPerHostQueries()
    {
        var hostIds = Enumerable.Range(0, 1_001).Select(index => BaseHostId + 1_000 + index).ToArray();
        using (var context = _backend.CreateContext())
        {
            context.DailyRecords.AddRange(hostIds.Select(id => new DailyRecordRow
            {
                HostId = id, HostName = $"host-{id}", RecordDate = Day,
                RiskLevel = "High", ContentJson = JsonSerializer.Serialize(Parent(id)),
                WriteRevision = 3, CreatedAt = DateTime.UtcNow
            }));
            context.SaveChanges();
        }
        _interceptor.CapturedCommands.Clear();

        NetiqSourceAttempt.RecordBatch(_backend, hostIds, Day, 82_005, "running");

        var parentReads = _interceptor.CapturedCommands
            .Where(command => command.Text.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
                              command.Text.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(parentReads);
        Assert.All(parentReads, command =>
        {
            Assert.Contains("LIMIT", command.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(command.Text.Contains("LIMIT 16", StringComparison.OrdinalIgnoreCase) ||
                        command.Values.Any(value => Convert.ToString(value) == "16"),
                "Each parent query must cap the loaded page at 16 rows.");
            Assert.InRange(command.ParameterCount, 1, 20);
        });
        Assert.InRange(parentReads.Length, 63, 70);
        Assert.Equal(1_001, NetiqSourceAttempt.ReadLatest(_backend, Day, Day).Count);
        using var verification = _backend.CreateContext();
        Assert.All(verification.DailyRecords.AsNoTracking().Select(row => row.ContentJson).ToList(), json =>
            Assert.False(JsonSerializer.Deserialize<DailyAnalysisRecord>(json)!.CanSupplementWithPrtg()));
    }

    private static void AssertJsonEqualExceptAttemptFields(string before, string after)
    {
        var beforeNode = JsonNode.Parse(before)!;
        var afterNode = JsonNode.Parse(after)!;
        beforeNode.AsObject().Remove(nameof(DailyAnalysisRecord.LatestNetiqAttemptStatus));
        beforeNode.AsObject().Remove(nameof(DailyAnalysisRecord.LatestNetiqAttemptAtUtc));
        afterNode.AsObject().Remove(nameof(DailyAnalysisRecord.LatestNetiqAttemptStatus));
        afterNode.AsObject().Remove(nameof(DailyAnalysisRecord.LatestNetiqAttemptAtUtc));
        Assert.True(JsonNode.DeepEquals(beforeNode, afterNode), "Attempt invalidation changed unrelated parent content.");
    }

    private sealed class FaultingCommandInterceptor : DbCommandInterceptor
    {
        public bool FailParentUpdate { get; set; }
        public int FailAtParentUpdate { get; set; } = 1;
        public int ParentUpdateCount { get; private set; }
        public bool ParentUpdateFailureObserved { get; private set; }
        public List<(string Text, int ParameterCount, object?[] Values)> CapturedCommands { get; } = [];

        private void Observe(DbCommand command)
        {
            CapturedCommands.Add((command.CommandText, command.Parameters.Count,
                command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            if (FailParentUpdate && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase))
            {
                ParentUpdateCount++;
                if (ParentUpdateCount < FailAtParentUpdate) return;
                ParentUpdateFailureObserved = true;
                throw new InvalidOperationException("Injected failure during NetIQ parent invalidation.");
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Observe(command);
            return result;
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            Observe(command);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            Observe(command);
            return result;
        }
    }
}

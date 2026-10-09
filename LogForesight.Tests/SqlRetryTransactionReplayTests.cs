using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Exercises transactional report and retention writes under a simulated lost commit acknowledgement.</summary>
public sealed class SqlRetryTransactionReplayTests
{
    [Fact]
    public void RiskReportAttachAndLowRiskClearReplayAfterCommitAcknowledgementLoss()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var commitFault = new CommitAcknowledgementLossInterceptor();
        DbContextOptions<LfDbContext> Options() => new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, CommitAcknowledgementRetryStrategyFactory>()
            .AddInterceptors(commitFault)
            .Options;
        using (var initial = new LfDbContext(Options())) initial.Database.EnsureCreated();
        LfDbContext Context() => new(Options());
        var store = new EfAnalysisRecordStore(Context, "sqlite-retry-test");

        var reportDay = DateTime.Today.AddDays(-2);
        var reportParent = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "RETRY-REPORT-HOST",
            Date = reportDay,
            RiskLevel = RiskLevels.High,
            RiskReportPending = true,
            TopIssues = []
        };
        store.Append(reportParent);
        var savedParent = Assert.Single(store.ReadRecent(reportDay, 1));
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(savedParent);
        var draft = new PreparedRiskReport(reportDay,
            new HostKey { HostId = savedParent.HostId, HostName = savedParent.Host },
            $"{reportDay:yyyy-MM-dd}_高風險_服務.txt", "retry-safe report content",
            new ReportMeta(RiskLevels.High, "服務"), prtgFingerprint,
            HostDayWorkflowFingerprint.ForReportInput(savedParent));

        commitFault.RunAfterNextCommit(() =>
        {
            draft.Host.HostId = 999;
            draft.Host.HostName = "MUTATED-AFTER-COMMIT";
        });
        commitFault.FailAfterNextCommit();
        var reportRef = store.AttachDailyRiskReport(reportDay, draft, savedParent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(savedParent), prtgFingerprint);

        Assert.NotNull(reportRef);
        var attached = Assert.Single(store.ReadRecent(reportDay, 1));
        Assert.False(attached.RiskReportPending);
        Assert.Equal(reportRef, attached.ReportFile);
        using (var verify = Context())
        {
            var reportRow = Assert.Single(verify.Reports.Where(row =>
                row.ReportDate == reportDay && row.Kind == ReportKinds.DailyRisk));
            Assert.Equal(savedParent.HostId, reportRow.HostId);
            Assert.Equal(savedParent.Host, reportRow.HostName);
        }

        var lowDay = reportDay.AddDays(1);
        var lowParent = new DailyAnalysisRecord
        {
            HostId = 102,
            Host = "RETRY-LOW-HOST",
            Date = lowDay,
            RiskLevel = RiskLevels.Low,
            RiskReportPending = true,
            TopIssues = []
        };
        store.Append(lowParent);
        var savedLowParent = Assert.Single(store.ReadRecent(lowDay, 1));
        commitFault.FailAfterNextCommit();

        Assert.True(store.TryClearPendingDailyRiskReport(lowDay, savedLowParent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(savedLowParent),
            HostDayWorkflowFingerprint.PrtgInputFingerprint(savedLowParent)));

        Assert.False(Assert.Single(store.ReadRecent(lowDay, 1)).RiskReportPending);
    }

    [Fact]
    public void FormalMailPruneReplaysTheSamePageAfterCommitAcknowledgementLoss()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "lf-sql-retry-prune-" + Guid.NewGuid().ToString("N"));
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, dataRoot);
        try
        {
            var oldDay = DateTime.UtcNow.Date.AddDays(-120);
            var keys = new[]
            {
                PrtgFormalMailClaimStore.BlobKey(501, oldDay),
                PrtgFormalMailClaimStore.BlobKey(502, oldDay)
            };
            using (var seed = backend.CreateContext())
            {
                foreach (var key in keys)
                {
                    var hostId = long.Parse(key.Split('_')[5], System.Globalization.CultureInfo.InvariantCulture);
                    seed.Blobs.Add(new BlobRow
                    {
                        BlobKey = key,
                        Content = System.Text.Json.JsonSerializer.Serialize(
                            new PrtgFormalMailClaimShard { HostId = hostId }, LfJsonOptions.Pretty),
                        UpdatedAt = DateTime.UtcNow.AddDays(-120),
                        Version = 1
                    });
                }
                seed.SaveChanges();
            }

            var commitFault = new CommitAcknowledgementLossInterceptor();
            var databasePath = StorageBackend.DefaultSqlitePath(dataRoot);
            DbContextOptions<LfDbContext> Options() => new DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .ReplaceService<IExecutionStrategyFactory, CommitAcknowledgementRetryStrategyFactory>()
                .AddInterceptors(commitFault)
                .Options;
            LfDbContext Context() => new(Options());

            commitFault.FailAfterNextCommit();
            var result = PrtgFormalMailClaimStore.PruneExpiredShardsForTesting(backend, Context,
                recordRetentionDays: 30, cursor: null, nowUtc: DateTime.UtcNow, maximumKeys: 1);

            Assert.Equal(1, result.DeletedShards);
            Assert.True(result.HasMore);
            Assert.Equal(keys[0], result.Cursor);

            var resumed = PrtgFormalMailClaimStore.PruneExpiredShardsForTesting(backend, Context,
                recordRetentionDays: 30, cursor: result.Cursor, nowUtc: DateTime.UtcNow, maximumKeys: 1);
            Assert.Equal(1, resumed.DeletedShards);
            Assert.True(resumed.HasMore);
            Assert.Equal(keys[1], resumed.Cursor);

            var drained = PrtgFormalMailClaimStore.PruneExpiredShardsForTesting(backend, Context,
                recordRetentionDays: 30, cursor: resumed.Cursor, nowUtc: DateTime.UtcNow, maximumKeys: 1);
            Assert.Equal(0, drained.DeletedShards);
            Assert.False(drained.HasMore);
            Assert.Null(drained.Cursor);
            using var verify = backend.CreateContext();
            Assert.DoesNotContain(verify.Blobs, row => keys.Contains(row.BlobKey, StringComparer.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { }
        }
    }
}

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

}

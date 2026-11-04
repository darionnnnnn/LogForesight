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
    public void 降級寫回遇到提交確認遺失重放_次數只加一次()
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

        var day = DateTime.Today.AddDays(-2);
        store.Append(new DailyAnalysisRecord
        {
            HostId = 201, Host = "RETRY-DEGRADED-HOST", Date = day, RiskLevel = RiskLevels.Medium,
            AiPending = true, TopIssues = []
        });
        var parent = Assert.Single(store.ReadRecent(day, 1));
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        var degraded = new AiOutcome("規則層結論", "（AI 回覆經 1 次嘗試仍未通過 JSON 檢查，保留原文供參考）[]",
            string.Empty, string.Empty, RiskLevels.Medium, null, true, 0, new List<string>(), null,
            new List<CategoryDeepDive>(), InputPrtgFingerprint: prtgFingerprint, AiDegraded: true);

        // 第一次提交成功但確認遺失：策略整段重放，重放必須認得這是自己剛寫下的列，不能再 +1
        commitFault.FailAfterNextCommit();
        Assert.True(store.TryAttachAiResult(day, degraded, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtgFingerprint));

        using (var verify = Context())
        {
            var row = verify.DailyRecords.AsNoTracking().Single(r => r.HostId == 201);
            Assert.Equal(1, row.AiDegradedAttempts);
            Assert.True(row.AiPending);
            Assert.True(row.AiAnalyzed);
            Assert.Equal("規則層結論（AI 摘要待補，第 1 次）", row.Headline);
        }

        // 對照：沒有確認遺失時，第二次降級寫回才推進到 2
        var second = Assert.Single(store.ReadRecent(day, 1));
        var secondFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(second);
        Assert.True(store.TryAttachAiResult(day, degraded with { InputPrtgFingerprint = secondFingerprint }, second.RecordId,
            HostDayWorkflowFingerprint.ForRecord(second), secondFingerprint));
        using (var verify = Context())
        {
            var row = verify.DailyRecords.AsNoTracking().Single(r => r.HostId == 201);
            Assert.Equal(2, row.AiDegradedAttempts);
            Assert.Equal("規則層結論（AI 摘要待補，第 2 次）", row.Headline);
        }
    }

    [Fact]
    public void 降級寫回帶報告草稿_草稿以結論標題驗證指紋仍可寫入_且標題補後綴()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        DbContextOptions<LfDbContext> Options() => new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options;
        using (var initial = new LfDbContext(Options())) initial.Database.EnsureCreated();
        LfDbContext Context() => new(Options());
        var store = new EfAnalysisRecordStore(Context, "sqlite-degraded-report");

        var day = DateTime.Today.AddDays(-2);
        store.Append(new DailyAnalysisRecord
        {
            HostId = 301, Host = "DEGRADED-REPORT-HOST", Date = day, RiskLevel = RiskLevels.High,
            AiPending = true, RiskReportPending = true, TopIssues = []
        });
        var parent = Assert.Single(store.ReadRecent(day, 1));
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);

        // 報告草稿是以「結論標題、非待補、AiAnalyzed=true」的定案視角組出來的（RetryAiAsync 的 scratch）
        var scratch = new DailyAnalysisRecord
        {
            HostId = parent.HostId, Host = parent.Host, Date = parent.Date, LogSource = parent.LogSource,
            RiskLevel = parent.RiskLevel, RiskReportPending = parent.RiskReportPending, TopIssues = [],
            Headline = "規則層結論", Summary = "（AI 回覆經 1 次嘗試仍未通過 JSON 檢查，保留原文供參考）[]",
            AiAnalyzed = true, AiPending = false
        };
        var draft = new PreparedRiskReport(day, new HostKey { HostId = parent.HostId, HostName = parent.Host },
            $"{day:yyyy-MM-dd}_高風險_服務.txt", "degraded report content", new ReportMeta(RiskLevels.High, "服務"),
            prtgFingerprint, HostDayWorkflowFingerprint.ForReportInput(scratch));
        var degraded = new AiOutcome(scratch.Headline, scratch.Summary, string.Empty, string.Empty,
            RiskLevels.High, null, true, 0, new List<string>(), null, new List<CategoryDeepDive>(),
            InputPrtgFingerprint: prtgFingerprint, ReportDraft: draft, AiDegraded: true);

        Assert.True(store.TryAttachAiResult(day, degraded, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtgFingerprint));

        var saved = Assert.Single(store.ReadRecent(day, 1));
        Assert.Equal("規則層結論（AI 摘要待補，第 1 次）", saved.Headline);
        Assert.True(saved.AiPending);
        Assert.True(saved.AiAnalyzed);
        Assert.Equal(1, saved.AiDegradedAttempts);
        Assert.NotNull(saved.ReportFile);
        Assert.False(saved.RiskReportPending);
    }
}

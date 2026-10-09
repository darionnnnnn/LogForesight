using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Public-operation coverage for operation-local residual candidate reuse and refresh.</summary>
[Collection("CalibrationCacheState")]
public sealed class CalibrationResidualCacheTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();

    public CalibrationResidualCacheTests()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.RawEventRetentionDays = 120;
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationService CreateService()
    {
        CalibrationService.ClearAssessmentCache();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        return new CalibrationService(_fx.NewContext, prtgStore, issueQuery, _settingsStore, _ruleStore);
    }

    [Fact]
    public void 匯出單次操作共用殘差候選列且重跑與清快取後讀取最新來源()
    {
        var anchor = new DateTime(2026, 8, 31);
        DailyAnalysisRecord MakeRecord(int totalCount) => new()
        {
            HostId = 101,
            Host = "SEC-SRV-01",
            Date = anchor,
            RiskLevel = "高",
            TopIssues =
            [
                new LogIssueSignature
                {
                    LogName = "Security",
                    Source = "Microsoft-Windows-Security-Auditing",
                    EventId = 4625,
                    LoginFailureDetails = [new LoginFailureDetail
                    {
                        Account = "redacted", Source = "redacted", LogonType = 3, Count = totalCount
                    }],
                    LoginFailureTotalCount = totalCount
                }
            ]
        };

        using (var ctx = _fx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor,
                DetailPruned = false, ContentJson = JsonSerializer.Serialize(MakeRecord(1))
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId, HostId = 101, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var first = service.BuildExportPackage(anchor);
        Assert.Equal(first.ResidualCandidates.Count,
            Convert.ToInt32(first.Summary.ResidualCredentialThresholds.KeyMetrics["SampledCandidates"]));
        Assert.Equal(1, Assert.Single(first.ResidualCandidates).TotalDetailCount);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single();
            row.ContentJson = JsonSerializer.Serialize(MakeRecord(27));
            ctx.SaveChanges();
        }
        CalibrationService.ClearAssessmentCache();

        var second = service.BuildExportPackage(anchor);
        Assert.NotSame(first.ResidualCandidates, second.ResidualCandidates);
        Assert.Equal(second.ResidualCandidates.Count,
            Convert.ToInt32(second.Summary.ResidualCredentialThresholds.KeyMetrics["SampledCandidates"]));
        Assert.Equal(27, Assert.Single(second.ResidualCandidates).TotalDetailCount);
    }
}

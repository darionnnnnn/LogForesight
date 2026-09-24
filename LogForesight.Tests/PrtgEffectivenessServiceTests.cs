using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgEffectivenessServiceTests
{
    [Fact]
    public void CountsExistingDatedEvidenceWithSeparateDenominators()
    {
        using var fixture = new EfSqliteFixture();
        var day = new DateTime(2026, 9, 10);
        using (var db = fixture.NewContext())
        {
            var record = new DailyAnalysisRecord
            {
                Date = day,
                TopIssues = new List<LogIssueSignature>
                {
                    PrtgIssue("PRTG:down", "prtg:down:11", false),
                    PrtgIssue("PRTG:warning", "prtg:warning:12", true),
                    new() { LogName = "System", Source = "Kernel", EventId = 1 }
                },
                CorrelationAlertRefs = new List<CorrelationAlertRef> { new() { PatternId = "prtg-outage-corroborated" } }
            };
            var json = JsonSerializer.Serialize(record);
            db.DailyRecords.Add(new DailyRecordRow { RecordId = 1, HostId = 1, HostName = "host", RecordDate = day, ContentJson = json });
            db.TopIssues.AddRange(
                Finding(1, day, "PRTG", "PRTG:down", "prtg:down:11"),
                Finding(1, day, "PRTG", "PRTG:warning", "prtg:warning:12"),
                Finding(1, day, "System", "Kernel", ""));
            db.IssueCases.Add(new IssueCaseRow { CaseId = "c1", HostName = "host", HostNameKey = "HOST", IssueKey = "k", IssueLabel = "x", Status = "open", FirstLinkedDate = day, LastLinkedDate = day, CreatedAt = day.AddHours(1), CreatedByAccount = "a", UpdatedAt = day.AddHours(1), SourceKey = "PRTG:DOWN", SourceName = "PRTG:down", EventId = 0 });
            db.WorkOrders.AddRange(
                new WorkOrderRow { WorkOrderId = 1, SourceKey = "PRTG:DOWN", SourceName = "PRTG:down", EventId = 0, IssueLabel = "x", HandlerId = 1, Origin = "manual", ScopeKind = "Hosts", CreatedAt = day.AddHours(2), UpdatedAt = day.AddHours(2), LastReplyAt = day.AddHours(3) },
                new WorkOrderRow { WorkOrderId = 2, SourceKey = "PRTG:WARNING", SourceName = "PRTG:warning", EventId = 0, IssueLabel = "y", HandlerId = 1, Origin = "manual", ScopeKind = "Hosts", CreatedAt = day.AddHours(2), UpdatedAt = day.AddHours(2) });
            db.SaveChanges();
        }

        var result = new PrtgEffectivenessService(fixture.NewContext, new FakeIssueOwnerStore()).Get(day, day);

        Assert.Equal(2, result.PrtgFindings);
        Assert.Equal(0, result.LowCoverageSampledHours);
        Assert.Equal(1, result.CasesCreated);
        Assert.Equal(2, result.WorkOrdersCreated);
        Assert.Equal(1, result.WorkOrdersReplied);
        Assert.Equal(1, result.SuppressedFindings);
        Assert.Equal(0, result.MutedPrtgProfiles);
        Assert.Equal(1, result.CorroboratedHostDays);
        Assert.Contains("separate denominators", result.MetricSemantics);
    }

    [Fact]
    public void CountsOnlyObservedLowCoverageSampledHourlyRowsWithinSelectedPeriod()
    {
        using var fixture = new EfSqliteFixture();
        var from = new DateTime(2026, 9, 10);
        var through = new DateTime(2026, 9, 11);
        using (var db = fixture.NewContext())
        {
            db.PrtgValues.AddRange(
                Value(from.AddTicks(-1), PrtgDataQuality.Sampled, 10),
                Value(from, PrtgDataQuality.Ok, 10),
                Value(from.AddHours(1), PrtgDataQuality.Sampled, PrtgValueUsability.SampledMinCoverage),
                Value(from.AddHours(2), PrtgDataQuality.Sampled, PrtgValueUsability.SampledMinCoverage - 0.01),
                Value(through.AddHours(23), PrtgDataQuality.Sampled, 0),
                Value(through.AddDays(1), PrtgDataQuality.Sampled, 10));
            db.SaveChanges();
        }

        var result = new PrtgEffectivenessService(fixture.NewContext, new FakeIssueOwnerStore()).Get(from, through);

        Assert.Equal(2, result.LowCoverageSampledHours);
        Assert.Contains("observed lf_prtg_values hourly rows", result.MetricSemantics);
        Assert.Contains("missing hourly periods", result.MetricSemantics);
    }

    [Fact]
    public void RejectsReversedAndOverlongDateRanges()
    {
        using var fixture = new EfSqliteFixture();
        var service = new PrtgEffectivenessService(fixture.NewContext, new FakeIssueOwnerStore());
        Assert.Throws<ArgumentException>(() => service.Get(new DateTime(2026, 9, 2), new DateTime(2026, 9, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Get(new DateTime(2025, 1, 1), new DateTime(2026, 9, 1)));
    }

    [Fact]
    public void CountsDistinctPrtgProfilesWithOverlappingMuteIntervalsWithoutClaimingFindingCount()
    {
        using var fixture = new EfSqliteFixture();
        var profiles = new FakeIssueOwnerStore();
        profiles.Upsert(new IssueProfile { SourceName = "PRTG:warning", EventId = 0, Mutes = new()
        {
            new() { From = new DateTime(2026, 9, 1), To = new DateTime(2026, 9, 12) },
            new() { From = new DateTime(2026, 9, 10), To = new DateTime(2026, 9, 20) }
        } });
        profiles.Upsert(new IssueProfile { SourceName = "PRTG:down", EventId = 0, Mutes = new()
        {
            new() { From = new DateTime(2026, 8, 1), To = new DateTime(2026, 8, 31) }
        } });
        profiles.Upsert(new IssueProfile { SourceName = "System", EventId = 1, Mutes = new()
        {
            new() { From = new DateTime(2026, 9, 1), To = new DateTime(2026, 9, 30) }
        } });

        var result = new PrtgEffectivenessService(fixture.NewContext, profiles)
            .Get(new DateTime(2026, 9, 10), new DateTime(2026, 9, 11));

        Assert.Equal(1, result.MutedPrtgProfiles);
        Assert.Equal(0, result.PrtgFindings);
        Assert.Contains("does not imply any finding occurred", result.MetricSemantics);
    }

    [Fact]
    public void EndpointRequiresMaintainPermission()
    {
        var permission = Assert.Single(typeof(PrtgEffectivenessController)
            .GetCustomAttributes(typeof(PermissionAttribute), inherit: false).Cast<PermissionAttribute>());
        Assert.Equal(new[] { Capability.Maintain }, (Capability[])permission.Arguments![0]!);
    }

    [Fact]
    public void EndpointCanBeConstructedAndServesSummary()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "lf-prtg-effectiveness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite",
                ConnectionString = $"Data Source={Path.Combine(dataRoot, "test.db") }"
            }, dataRoot);
            using var provider = new ServiceCollection()
                .AddSingleton(backend)
                .AddSingleton<IIssueOwnerStore>(new FakeIssueOwnerStore())
                .AddTransient<PrtgEffectivenessController>()
                .BuildServiceProvider();
            var controller = provider.GetRequiredService<PrtgEffectivenessController>();

            var result = controller.Get(new DateTime(2026, 9, 10), new DateTime(2026, 9, 10));

            var response = Assert.IsType<ApiResponse<PrtgEffectivenessSummary>>(result.Value);
            Assert.True(response.Success);
            Assert.Equal(0, response.Data!.PrtgFindings);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static LogIssueSignature PrtgIssue(string source, string eventKey, bool suppressed) => new()
    {
        LogName = "PRTG", Source = source, EventId = 0, EventKey = eventKey,
        Suppressed = suppressed
    };

    private static TopIssueRow Finding(long recordId, DateTime date, string logName, string source, string eventKey) => new()
    {
        RecordId = recordId, HostId = 1, RecordDate = date, LogName = logName, SourceName = source,
        EventId = 0, EventKey = eventKey
    };

    private static PrtgValueRow Value(DateTime periodStart, string quality, double coverage) => new()
    {
        SensorObjid = 1, PeriodStart = periodStart, Quality = quality, Coverage = coverage
    };
}

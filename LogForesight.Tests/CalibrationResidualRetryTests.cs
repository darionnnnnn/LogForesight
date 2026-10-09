using System.Data.Common;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace LogForesight.Tests;

[Collection("CalibrationCacheState")]
public sealed class CalibrationResidualRetryTests
{
    [Fact]
    public void 殘留讀取重試使用新context且回滾失敗嘗試的budget()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var interceptor = new FailFirstDailyRecordReadInterceptor();
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .ReplaceService<IExecutionStrategyFactory, ResidualRetryExecutionStrategyFactory>()
            .Options;
        var createdContexts = 0;
        LfDbContext NewContext()
        {
            Interlocked.Increment(ref createdContexts);
            return new LfDbContext(options);
        }
        using (var context = NewContext()) context.Database.EnsureCreated();
        var settings = new FakeSystemSettingsStore();
        var rules = new FakeRuleStore { Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() } };
        var service = new CalibrationService(NewContext, new EfPrtgStore(NewContext),
            new EfIssueAggregateQuery(NewContext, new FakeHostStore()), settings, rules);
        using var budget = new PrtgCalibrationCaptureBudget(64);

        var rows = service.ExecuteResidualSnapshot(budget, CancellationToken.None, context =>
        {
            budget.Charge(64, "retry regression retained payload");
            return context.DailyRecords.AsNoTracking().Select(row => row.RecordId).ToList();
        });

        Assert.Empty(rows);
        Assert.True(interceptor.FailedFirstRead);
        Assert.True(createdContexts >= 4, "The retry must create a second attempt context in addition to the service and strategy contexts.");
        Assert.Equal(64, budget.ReservedBytes);
    }

    [Fact]
    public void 殘留摘要重試後只發布完整候選列()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var interceptor = new FailContentByIdsReadInterceptor();
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .ReplaceService<IExecutionStrategyFactory, ResidualRetryExecutionStrategyFactory>()
            .Options;
        var createdContexts = 0;
        LfDbContext NewContext()
        {
            Interlocked.Increment(ref createdContexts);
            return new LfDbContext(options);
        }
        interceptor.ObserveContexts(() => Volatile.Read(ref createdContexts));

        using (var context = NewContext()) context.Database.EnsureCreated();
        var anchor = new DateTime(2026, 8, 31);
        var record = new DailyAnalysisRecord
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
                        Account = "redacted", Source = "redacted", LogonType = 3, Count = 1
                    }],
                    LoginFailureTotalCount = 1
                }
            ]
        };
        using (var context = NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 101, HostName = "SEC-SRV-01", RecordDate = anchor,
                DetailPruned = false, ContentJson = JsonSerializer.Serialize(record)
            };
            context.DailyRecords.Add(row);
            context.SaveChanges();
            context.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId, HostId = 101, RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
            });
            context.SaveChanges();
        }

        var settings = new FakeSystemSettingsStore();
        settings.Update(value =>
        {
            value.PrtgEnabled = true;
            value.RawEventRetentionDays = 120;
        });
        var rules = new FakeRuleStore { Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() } };
        var service = new CalibrationService(NewContext, new EfPrtgStore(NewContext),
            new EfIssueAggregateQuery(NewContext, new FakeHostStore()), settings, rules);

        var package = service.BuildExportPackage(anchor);

        Assert.True(interceptor.SawCandidateMetadataPageBeforeFailure,
            "The candidate metadata page must be admitted before content is read by its record ids.");
        Assert.True(interceptor.FailedContentByIdsRead);
        Assert.True(createdContexts > interceptor.ContextCountAtFailure,
            "The retried residual snapshot must use a newly-created context.");
        Assert.Equal(1, Convert.ToInt32(package.Summary.ResidualCredentialThresholds.KeyMetrics["SampledCandidates"]));
        Assert.Equal(1, Assert.Single(package.ResidualCandidates).TotalDetailCount);
    }

    private sealed class FailFirstDailyRecordReadInterceptor : DbCommandInterceptor
    {
        public bool FailedFirstRead { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (!FailedFirstRead && command.CommandText.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase))
            {
                FailedFirstRead = true;
                throw new InjectedResidualTransientException();
            }
            return result;
        }
    }

    private sealed class FailContentByIdsReadInterceptor : DbCommandInterceptor
    {
        private int _failed;
        private Func<int>? _contextCount;

        public bool SawCandidateMetadataPage { get; private set; }
        public bool SawCandidateMetadataPageBeforeFailure { get; private set; }
        public bool FailedContentByIdsRead => Volatile.Read(ref _failed) == 1;
        public int ContextCountAtFailure { get; private set; }

        public void ObserveContexts(Func<int> contextCount) => _contextCount = contextCount;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            var sql = command.CommandText;
            if (!sql.Contains("lf_daily_records", StringComparison.OrdinalIgnoreCase)) return result;

            var contentByIdsRead = sql.Contains("content_json", StringComparison.OrdinalIgnoreCase) &&
                (sql.Contains("\"record_id\" IN (", StringComparison.OrdinalIgnoreCase) ||
                 sql.Contains("[record_id] IN (", StringComparison.OrdinalIgnoreCase) ||
                 sql.Contains("record_id IN (", StringComparison.OrdinalIgnoreCase));
            if (contentByIdsRead && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                SawCandidateMetadataPageBeforeFailure = SawCandidateMetadataPage;
                ContextCountAtFailure = _contextCount?.Invoke() ?? 0;
                throw new InjectedResidualTransientException();
            }

            if (!contentByIdsRead && sql.Contains("content_json", StringComparison.OrdinalIgnoreCase))
                SawCandidateMetadataPage = true;
            return result;
        }
    }

    private sealed class ResidualRetryExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
        : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new ResidualRetryExecutionStrategy(dependencies);
    }

    private sealed class ResidualRetryExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is InjectedResidualTransientException;
    }

    private sealed class InjectedResidualTransientException : Exception { }
}

using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>「處理中逾期」的今天取自注入的 TimeProvider：撥到預計完成日隔天算逾期，撥回當天不算（待辦與郵件兩條路徑口徑一致）。</summary>
public class IssueOverdueClockTests : IDisposable
{
    private static readonly DateTime Due = new(2026, 8, 8);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public IssueOverdueClockTests()
    {
        var records = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        records.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = "A", Date = Due, RiskLevel = RiskLevels.High,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Warning,
                        Category = IssueCategory.Storage, Severity = IssueSeverity.High, Count = 1 }
            }
        });
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = Due,
            IssueKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Warning),
            Status = IssueHandlingStatuses.InProgress, DueDate = Due
        });
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static FakeTimeProvider ClockAt(DateTime local)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var clock = new FakeTimeProvider(new DateTimeOffset(utc, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        return clock;
    }

    private IssueTodoQuery Todo(TimeProvider clock) => new(
        new EfIssueAggregateQuery(_fx.NewContext, _hosts),
        new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings),
        new FixedIssueExclusionSource(IssueExclusion.None), clock: clock);

    private MailIssueDigest Digest(TimeProvider clock) => new(
        new EfIssueAggregateQuery(_fx.NewContext, _hosts),
        new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings),
        _settings, new FixedIssueExclusionSource(IssueExclusion.None), clock);

    [Fact]
    public void 時鐘在預計完成日隔天_待辦與郵件都算逾期()
    {
        var clock = ClockAt(Due.AddDays(1).AddHours(9));
        Assert.Equal(1, Todo(clock).Build(Due, Due, null).OverdueIssueCount);
        Assert.Equal(MailIssueBucket.Overdue, Assert.Single(Digest(clock).Build(Due, Due, null)).Bucket);
    }

    [Fact]
    public void 時鐘在預計完成日當天_待辦與郵件都不算逾期()
    {
        var clock = ClockAt(Due.AddHours(23));
        Assert.Equal(0, Todo(clock).Build(Due, Due, null).OverdueIssueCount);
        Assert.NotEqual(MailIssueBucket.Overdue, Assert.Single(Digest(clock).Build(Due, Due, null)).Bucket);
    }
}

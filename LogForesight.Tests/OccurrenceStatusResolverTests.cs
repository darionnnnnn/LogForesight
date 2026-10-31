using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public class OccurrenceStatusResolverTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    private OccurrenceStatusResolver Resolver() =>
        new(_hosts, _issueHandlings, _cases, _settings);

    [Fact]
    public void PRTG問題第1天標誤報_第5天再出現無標記_已處理且InheritedFalsePositive()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "host1" });
        var issueKey = IssueSignatureKey.For(
            PrtgFindingMapper.PrtgLogName, "PRTG:disk_free_trend", 0, EventLogEntryType.Warning, "sensor-1");

        var day1 = new DateTime(2026, 8, 1);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = host.HostName,
            IssueKey = issueKey,
            Date = day1,
            Status = IssueHandlingStatuses.FalsePositive,
            UpdatedAt = day1
        });

        var day5 = new DateTime(2026, 8, 5);
        var occurrences = new[]
        {
            new HostIssueOccurrence
            {
                HostId = host.HostId,
                IssueKey = issueKey,
                LastSeen = day5,
                SeverityRank = (int)IssueSeverity.High
            }
        };

        var resolved = Resolver().Resolve(occurrences, day5, day5);
        var item = Assert.Single(resolved);

        Assert.Equal(HostIssueStatus.Resolved, item.Status);
        Assert.True(item.InheritedFalsePositive);
    }

    [Fact]
    public void PRTG問題第1天標誤報_第40天再出現_未處理且非InheritedFalsePositive()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "host1" });
        var issueKey = IssueSignatureKey.For(
            PrtgFindingMapper.PrtgLogName, "PRTG:disk_free_trend", 0, EventLogEntryType.Warning, "sensor-1");

        var day1 = new DateTime(2026, 8, 1);
        _issueHandlings.Save(new IssueHandling
        {
            HostName = host.HostName,
            IssueKey = issueKey,
            Date = day1,
            Status = IssueHandlingStatuses.FalsePositive,
            UpdatedAt = day1
        });

        var day40 = new DateTime(2026, 9, 10);
        var occurrences = new[]
        {
            new HostIssueOccurrence
            {
                HostId = host.HostId,
                IssueKey = issueKey,
                LastSeen = day40,
                SeverityRank = (int)IssueSeverity.High
            }
        };

        var resolved = Resolver().Resolve(occurrences, day40, day40);
        var item = Assert.Single(resolved);

        Assert.Equal(HostIssueStatus.Open, item.Status);
        Assert.False(item.InheritedFalsePositive);
    }

    [Fact]
    public void 舊資料SeverityRank3_CriticalRow_在未處理為High時計入未處理()
    {
        // 三級化前寫入的 Critical（rank 3）正規化為 High；未處理等級只含 High 時必須算作 Open。
        _settings.Update(s => s.UnhandledSeverities = ["High"]);
        var host = _hosts.Upsert(new WebHost { HostName = "host-legacy" });
        var issueKey = IssueSignatureKey.For("System", "Legacy", 1, EventLogEntryType.Error, "");
        var day = new DateTime(2026, 9, 10);
        var occurrences = new[]
        {
            new HostIssueOccurrence
            {
                HostId = host.HostId,
                IssueKey = issueKey,
                LastSeen = day,
                SeverityRank = 3
            }
        };

        var item = Assert.Single(Resolver().Resolve(occurrences, day, day));

        Assert.Equal(HostIssueStatus.Open, item.Status);
    }
}

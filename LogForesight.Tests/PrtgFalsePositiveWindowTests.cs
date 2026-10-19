using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgFalsePositiveWindowTests
{
    private static readonly string PrtgKey = IssueSignatureKey.For(
        PrtgFindingMapper.PrtgLogName, "PRTG:disk_free_trend", 0, EventLogEntryType.Warning, "sensor-1");

    private static readonly string NonPrtgKey = IssueSignatureKey.For(
        "System", "Disk", 153, EventLogEntryType.Error, "disk-error");

    [Fact]
    public void 標記日當天_涵蓋()
    {
        var markDate = new DateTime(2026, 8, 1);
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(PrtgKey, markDate, handlings);

        Assert.NotNull(result);
        Assert.Equal(IssueHandlingStatuses.FalsePositive, result.Status);
    }

    [Fact]
    public void 第30天_涵蓋()
    {
        var markDate = new DateTime(2026, 8, 1);
        var occDate = markDate.AddDays(30); // 2026-08-31
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(PrtgKey, occDate, handlings);

        Assert.NotNull(result);
        Assert.Equal(IssueHandlingStatuses.FalsePositive, result.Status);
    }

    [Fact]
    public void 第31天_不涵蓋()
    {
        var markDate = new DateTime(2026, 8, 1);
        var occDate = markDate.AddDays(31); // 2026-09-01
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(PrtgKey, occDate, handlings);

        Assert.Null(result);
    }

    [Fact]
    public void 標記之後某天改回open_不涵蓋()
    {
        var markDate = new DateTime(2026, 8, 1);
        var reopenDate = new DateTime(2026, 8, 10);
        var occDate = new DateTime(2026, 8, 15);
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            },
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = reopenDate,
                Status = IssueHandlingStatuses.Open,
                UpdatedAt = reopenDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(PrtgKey, occDate, handlings);

        Assert.Null(result);
    }

    [Fact]
    public void 非PRTG問題_一律不涵蓋()
    {
        var markDate = new DateTime(2026, 8, 1);
        var occDate = new DateTime(2026, 8, 5);
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = NonPrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(NonPrtgKey, occDate, handlings);

        Assert.Null(result);
    }

    [Fact]
    public void 早於標記日的出現_不涵蓋()
    {
        var markDate = new DateTime(2026, 8, 10);
        var occDate = new DateTime(2026, 8, 5);
        var handlings = new[]
        {
            new IssueHandling
            {
                HostName = "host1",
                IssueKey = PrtgKey,
                Date = markDate,
                Status = IssueHandlingStatuses.FalsePositive,
                UpdatedAt = markDate
            }
        };

        var result = PrtgFalsePositiveWindow.Covering(PrtgKey, occDate, handlings);

        Assert.Null(result);
    }
}

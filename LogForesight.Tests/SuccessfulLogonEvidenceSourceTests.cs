using System.Diagnostics;
using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

[Collection("KnownIssueCatalogState")]
public sealed class SuccessfulLogonEvidenceSourceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task Netiq不會使用本機4624補充但會接受來源提供的成功事件(
        bool includeRemote4624, bool includeRemoteRdp, bool expectRdpDescription)
    {
        var localScanCalls = 0;
        var day = new DateTime(2026, 10, 11);
        var logs = MakeBruteForceFailures(day);
        if (includeRemote4624)
        {
            logs.Add(Logon(day.AddHours(12), "Security", 4624,
                "Account Name: alice\nSource Network Address: 198.51.100.42"));
        }
        if (includeRemoteRdp)
        {
            logs.Add(Logon(day.AddHours(13), ChannelCatalog.RdpLsmChannel, 21,
                "User: alice\nClient Address: 198.51.100.42"));
        }

        var remote = NewService(AnalysisLogSource.Netiq, date =>
        {
            localScanCalls++;
            return new EventLogScanResult
            {
                Entries = [Logon(date.AddHours(14), "Security", 4624,
                    "Account Name: sentinel-local-only\nSource Network Address: 203.0.113.99")]
            };
        });

        var record = await remote.AnalyzeDayStatisticalAsync(day, logs, useAi: false, securityLogAvailable: true);

        Assert.Equal(0, localScanCalls);
        Assert.Contains(record.CorrelationAlertRefs, finding => finding.PatternId == CorrelationPatternIds.BruteSuccess);
        Assert.Equal(expectRdpDescription,
            record.CorrelationAlerts.Any(alert => alert.Contains("含 RDP 工作階段登入", StringComparison.Ordinal)));
        Assert.DoesNotContain(record.CorrelationAlerts, alert => alert.Contains("sentinel-local-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Netiq沒有提供成功事件時不會將本機成功事件當成遠端主機得手證據()
    {
        var localScanCalls = 0;
        var day = new DateTime(2026, 10, 10);
        var remote = NewService(AnalysisLogSource.Netiq, date =>
        {
            localScanCalls++;
            return new EventLogScanResult
            {
                Entries = [Logon(date.AddHours(14), "Security", 4624,
                    "Account Name: alice\nSource Network Address: 198.51.100.42")]
            };
        });

        var record = await remote.AnalyzeDayStatisticalAsync(day, MakeBruteForceFailures(day),
            useAi: false, securityLogAvailable: true);

        Assert.Equal(0, localScanCalls);
        Assert.DoesNotContain(record.CorrelationAlertRefs, finding => finding.PatternId == CorrelationPatternIds.BruteSuccess);
        Assert.DoesNotContain(record.CorrelationAlerts, alert => alert.Contains("破解得手", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local分析仍會以本機4624補充破解得手證據()
    {
        var localScanCalls = 0;
        var day = new DateTime(2026, 10, 9);
        var local = NewService(AnalysisLogSource.Local, date =>
        {
            localScanCalls++;
            return new EventLogScanResult
            {
                Entries = [Logon(date.AddHours(14), "Security", 4624,
                    "Account Name: alice\nSource Network Address: 198.51.100.42")]
            };
        });

        var record = await local.AnalyzeDayStatisticalAsync(day, MakeBruteForceFailures(day),
            useAi: false, securityLogAvailable: true);

        Assert.Equal(1, localScanCalls);
        Assert.Contains(record.CorrelationAlertRefs, finding => finding.PatternId == CorrelationPatternIds.BruteSuccess);
    }

    private LogAnalysisService NewService(AnalysisLogSource source, Func<DateTime, EventLogScanResult> localScanner) =>
        new(new EventLogService(), new FakeAiService(),
            new EfAnalysisRecordStore(_fixture.NewContext, $"source-boundary-{Guid.NewGuid():N}"),
            new FakeSuppressionStore(), host: "FIXTURE-REMOTE-HOST", hostId: 97531,
            logSource: source, localSecuritySupplementScanner: localScanner);

    private static List<EventLogEntryData> MakeBruteForceFailures(DateTime day) =>
        Enumerable.Range(0, 12).Select(index => Logon(day.AddHours(1).AddMinutes(index), "Security", 4625,
            "Account Name: alice\nSource Network Address: 198.51.100.42")).ToList();

    private static EventLogEntryData Logon(DateTime time, string logName, int eventId, string message) => new()
    {
        TimeGenerated = time,
        EntryType = eventId == 4625 ? EventLogEntryType.FailureAudit
            : eventId == 4624 ? EventLogEntryType.SuccessAudit : EventLogEntryType.Information,
        LogName = logName,
        Source = logName == "Security" ? "Microsoft-Windows-Security-Auditing" : logName,
        EventId = eventId,
        Message = message
    };
}

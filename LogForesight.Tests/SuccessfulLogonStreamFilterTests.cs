using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 暴力破解後成功登入的本機補掃串流過濾：補掃只保留「帳號或 IP 落在失敗集合內」的 4624，
/// 過濾前後判定結果必須相同，且傳給補掃提供者的 keep 判斷要正確。
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class SuccessfulLogonStreamFilterTests : IDisposable
{
    private const string VictimAccount = "victim";
    private const string LateAccount = "latecomer";
    private const string FailedIp = "10.0.0.9";
    private static readonly DateTime Day = new(2026, 10, 20);

    private readonly EfSqliteFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 補掃套用keep前後判定的帳號與IP相同()
    {
        var samples = MakeSuccessSamples();

        var unfiltered = await AnalyzeAsync((_, _) => new EventLogScanResult { Entries = samples.ToList() });
        var filteredScanCount = 0;
        var filtered = await AnalyzeAsync((_, keep) =>
        {
            var kept = samples.Where(keep).ToList();
            filteredScanCount = kept.Count;
            return new EventLogScanResult { Entries = kept };
        });

        var before = BruteSuccessDescriptions(unfiltered);
        var after = BruteSuccessDescriptions(filtered);
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
        Assert.Contains(before, d => d.Contains(VictimAccount, StringComparison.Ordinal));
        Assert.Contains(before, d => d.Contains(FailedIp, StringComparison.Ordinal));
        Assert.DoesNotContain(after, d => d.Contains("noise-", StringComparison.Ordinal));
        // 500 筆無關 4624 全被丟掉，只剩三筆有交集的成功事件
        Assert.Equal(3, filteredScanCount);
    }

    [Fact]
    public async Task 注入的scanner收到的keep只放行與失敗集合有交集的4624()
    {
        Func<EventLogEntryData, bool>? captured = null;
        await AnalyzeAsync((_, keep) =>
        {
            captured = keep;
            return new EventLogScanResult { Entries = new List<EventLogEntryData>() };
        });

        Assert.NotNull(captured);
        Assert.False(captured!(Logon(Day.AddHours(5), 4624, "Account Name: noise-1\nSource Network Address: 203.0.113.1")));
        Assert.True(captured(Logon(Day.AddHours(5), 4624, $"Account Name: {VictimAccount}\nSource Network Address: 203.0.113.1")));
        Assert.True(captured(Logon(Day.AddHours(5), 4624, $"Account Name: noise-2\nSource Network Address: {FailedIp}")));
        // 非 4624 即使帳號在失敗集合內也不放行
        Assert.False(captured(Logon(Day.AddHours(5), 4625, $"Account Name: {VictimAccount}\nSource Network Address: {FailedIp}")));
    }

    [Fact]
    public async Task 樣本最後一筆才成功的帳號仍被判定()
    {
        var record = await AnalyzeAsync((_, keep) =>
            new EventLogScanResult { Entries = MakeSuccessSamples().Where(keep).ToList() });

        Assert.Contains(BruteSuccessDescriptions(record), d => d.Contains(LateAccount, StringComparison.Ordinal));
    }

    [WindowsFact]
    public void ScanRange的keep全擋時結果為空且不擲例外()
    {
        var end = DateTime.Now;
        var result = new EventLogService().ScanRange(end.AddHours(-6), end, "System", keep: _ => false);

        Assert.Empty(result.Entries);
    }

    private async Task<DailyAnalysisRecord> AnalyzeAsync(Func<DateTime, Func<EventLogEntryData, bool>, EventLogScanResult> scanner)
    {
        var service = new LogAnalysisService(new EventLogService(), new FakeAiService(),
            new EfAnalysisRecordStore(_fixture.NewContext, $"stream-filter-{Guid.NewGuid():N}"),
            new FakeSuppressionStore(), host: "FIXTURE-STREAM-HOST", hostId: 86420,
            logSource: AnalysisLogSource.Local, localSecuritySupplementScanner: scanner);
        return await service.AnalyzeDayStatisticalAsync(Day, MakeFailures(), useAi: false, securityLogAvailable: true);
    }

    private static List<string> BruteSuccessDescriptions(DailyAnalysisRecord record) =>
        record.CorrelationAlerts.Where(a => a.Contains("破解得手", StringComparison.Ordinal)).ToList();

    private static List<EventLogEntryData> MakeFailures()
    {
        var list = Enumerable.Range(0, 12).Select(i => Logon(Day.AddHours(1).AddMinutes(i), 4625,
            $"Account Name: {VictimAccount}\nSource Network Address: {FailedIp}")).ToList();
        list.AddRange(Enumerable.Range(0, 3).Select(i => Logon(Day.AddHours(2).AddMinutes(i), 4625,
            $"Account Name: {LateAccount}\nSource Network Address: 192.0.2.77")));
        return list;
    }

    /// <summary>成功事件樣本：victim、10.0.0.9 各一筆在前，500 筆無關 4624 在中，latecomer 最後一筆才成功。</summary>
    private static List<EventLogEntryData> MakeSuccessSamples()
    {
        var list = new List<EventLogEntryData>
        {
            Logon(Day.AddHours(4), 4624, $"Account Name: {VictimAccount}\nSource Network Address: 203.0.113.250"),
            Logon(Day.AddHours(5), 4624, $"Account Name: other-user\nSource Network Address: {FailedIp}")
        };
        list.AddRange(Enumerable.Range(0, 500).Select(i => Logon(Day.AddHours(6).AddSeconds(i), 4624,
            $"Account Name: noise-{i}\nSource Network Address: 203.0.113.{i % 200 + 1}")));
        list.Add(Logon(Day.AddHours(23), 4624, $"Account Name: {LateAccount}\nSource Network Address: 203.0.113.251"));
        return list;
    }

    private static EventLogEntryData Logon(DateTime time, int eventId, string message) => new()
    {
        TimeGenerated = time,
        EntryType = eventId == 4625 ? LogEntryType.FailureAudit : LogEntryType.SuccessAudit,
        LogName = "Security",
        Source = "Microsoft-Windows-Security-Auditing",
        EventId = eventId,
        Message = message
    };
}

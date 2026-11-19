using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 夜間掛接（<see cref="IssueCaseCoordinator.AttachNewDay"/>）的處理歷程一次寫入（第 78 輪批次 2a，2-2）：
/// 歷程內容、相對順序與編號連續性不變，尾端探號讀取由逐筆一次降為整批一次。
/// </summary>
public sealed class AttachNewDayLogBatchTests : IDisposable
{
    private const string Host = "SRV-A";
    private static readonly DateTime Day = new(2026, 10, 10);
    private static readonly DateTime At = new(2026, 10, 11, 1, 0, 0);

    private readonly EfSqliteFixture _fx = new();
    private readonly SqlCommandRecorder _recorder = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _handlings = new();
    private readonly FakeIssueOwnerStore _profiles = new();
    private readonly ITestOutputHelper _output;

    public AttachNewDayLogBatchTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _fx.Dispose();

    private LfDbContext Ctx() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_fx.NewContext().Database.GetDbConnection()).AddInterceptors(_recorder).Options);

    private static LogIssueSignature Issue(string source, int eventId) =>
        new() { LogName = "System", Source = source, EventId = eventId, EntryType = LogEntryType.Error };

    private void OpenCase(string source, int eventId, string status, string note) =>
        _cases.Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("N"), HostName = Host,
            IssueKey = IssueSignatureKey.For(Issue(source, eventId)), IssueLabel = $"{source} {eventId}",
            Status = status, Note = note, FirstLinkedDate = Day.AddDays(-2), LastLinkedDate = Day.AddDays(-1),
            CreatedAt = Day.AddDays(-2), UpdatedAt = Day.AddDays(-1)
        });

    private (IssueCaseCoordinator Coordinator, EfRecordHandlingStore Log) Create()
    {
        var log = new EfRecordHandlingStore(Ctx, new EfJsonLogStore(Ctx, "handling_log"));
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = Host });
        return (new IssueCaseCoordinator(_cases, _handlings, log, new FakeAnalysisRecordQuery(), hosts, _profiles), log);
    }

    [Fact]
    public void A4_掛接三件既有案件加一件機房結論_歷程內容順序編號與逐筆寫入相同且探號只讀一次()
    {
        OpenCase("disk", 153, IssueHandlingStatuses.InProgress, "處理中甲");
        OpenCase("net", 4, IssueHandlingStatuses.Open, "待處理乙");
        OpenCase("cpu", 7, IssueHandlingStatuses.InProgress, "處理中丙");
        _profiles.Upsert(new IssueProfile
        {
            SourceName = "noise", EventId = 9,
            ConclusionStatus = IssueHandlingStatuses.KnownNoise, ConclusionNote = "已知雜訊", AutoApply = true
        });
        var (coordinator, log) = Create();

        var issues = new List<LogIssueSignature> { Issue("disk", 153), Issue("noise", 9), Issue("net", 4), Issue("cpu", 7) };
        _recorder.Clear();
        var result = coordinator.AttachNewDay(Host, Day, issues, At);
        var probeReads = _recorder.Selects().Count(c => c.Sql.Contains("lf_log_lines", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"尾端探號讀取（lf_log_lines SELECT）次數：{probeReads}");

        Assert.Equal(4, result.AttachedCount);
        var logs = log.GetLogs(Host, Day);
        Assert.Equal(4, logs.Count);
        // 內容與相對順序（依輸入問題順序）
        Assert.Equal(
            new[] { HandlingActions.CaseAttach, HandlingActions.FleetApply, HandlingActions.CaseAttach, HandlingActions.CaseAttach },
            logs.Select(l => l.Action).ToArray());
        Assert.Equal(
            issues.Select(IssueSignatureKey.For).ToArray(),
            logs.Select(l => l.IssueKey).ToArray());
        Assert.Equal(
            new[] { IssueHandlingStatuses.InProgress, IssueHandlingStatuses.KnownNoise, IssueHandlingStatuses.Open, IssueHandlingStatuses.InProgress },
            logs.Select(l => l.Status).ToArray());
        Assert.Equal("〔機房結論〕已知雜訊", logs[1].Note);
        Assert.Equal("disk 153", logs[0].IssueLabel);
        Assert.All(logs, l => { Assert.Equal(At, l.CreatedAt); Assert.Null(l.ActorId); Assert.Equal(string.Empty, l.ActorAccount); });
        // 編號連續遞增
        Assert.Equal(Enumerable.Range((int)logs[0].LogId, 4).Select(i => (long)i).ToArray(), logs.Select(l => l.LogId).ToArray());
        // 探號：整批一次
        Assert.Equal(1, probeReads);
    }

    [Fact]
    public void A4_沒有任何掛接時不寫歷程也不探號()
    {
        var (coordinator, log) = Create();

        _recorder.Clear();
        var result = coordinator.AttachNewDay(Host, Day, new[] { Issue("none", 1) }, At);

        Assert.Equal(0, result.AttachedCount);
        var probeReads = _recorder.Selects().Count(c => c.Sql.Contains("lf_log_lines", StringComparison.OrdinalIgnoreCase));
        _recorder.Clear();
        Assert.Empty(log.GetLogs(Host, Day));
        Assert.Equal(0, probeReads);
    }
}

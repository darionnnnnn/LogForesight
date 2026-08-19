using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題檔案負責人自動建立案件並指派測試（task-F2）。
/// 釘住第 4 段優先序行為：問題檔案有負責人時自動建立案件並指派給第一位負責人。
/// </summary>
public class IssueCaseCoordinatorAutoAssignTests
{
    private const string Host = "SRV-A";
    private const string IssueLabel = "disk 153";
    private static readonly string IssueKey = IssueSignatureKey.For("System", "disk", 153, System.Diagnostics.EventLogEntryType.Error);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _issueProfiles = new();

    public IssueCaseCoordinatorAutoAssignTests()
    {
        _hosts.Upsert(new WebHost { HostName = Host });
    }

    private IssueCaseCoordinator Create() => new(_cases, _issueHandlings, _handlingLog, _records, _hosts, _issueProfiles);

    private static List<LogIssueSignature> CreateDiskIssues() => new()
    {
        new() { LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error }
    };

    [Fact]
    public void 掛接_問題檔案有負責人且無進行中案件_自動建立案件並指派給第一位負責人()
    {
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            OwnerUserIds = new List<long> { 101, 102 }
        });

        var today = DateTime.Today;
        var coordinator = Create();
        var result = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);

        Assert.Equal(1, result.AttachedCount);

        // 驗證建立案件：HandlerId 為第一位負責人，狀態為 InProgress
        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(101, openCase.HandlerId);
        Assert.Equal(IssueHandlingStatuses.InProgress, openCase.Status);
        Assert.Equal(today.Date, openCase.FirstLinkedDate.Date);
        Assert.Equal(today.Date, openCase.LastLinkedDate.Date);

        // 驗證當日 IssueHandling 標記指向新案件
        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(IssueHandlingStatuses.InProgress, dayHandling.Status);
        Assert.Equal(openCase.CaseId, dayHandling.CaseId);
        Assert.Null(dayHandling.ActorId);

        // 驗證 RecordHandlingLog
        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.OwnerAutoAssign, log.Action);
        Assert.Equal("系統依問題檔案自動派送", log.Note);
        Assert.Null(log.ActorId);
    }

    [Fact]
    public void 掛接_已有進行中案件時不重複建案_走既有案件掛接()
    {
        // 先建立既有案件（HandlerId = 99）
        var yesterday = DateTime.Today.AddDays(-1);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, yesterday, handlerId: 99, note: "既有案件處理中",
            dueDate: null, actorId: 1, actorAccount: "admin", occurredAt: DateTime.Now);

        // 設定問題檔案負責人為 101
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            OwnerUserIds = new List<long> { 101 }
        });

        var today = DateTime.Today;
        var result = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);

        Assert.Equal(1, result.AttachedCount);

        // 案件依然是原案件（HandlerId 維持 99，未被 101 覆蓋）
        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(99, openCase.HandlerId);

        // 當日標記掛入既有案件
        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(openCase.CaseId, dayHandling.CaseId);

        // 歷程動作為 CaseAttach 而非 OwnerAutoAssign
        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.CaseAttach, log.Action);
    }

    [Fact]
    public void 掛接_同時有AutoApply機房結論與負責人時_機房結論優先不建案()
    {
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            ConclusionStatus = IssueHandlingStatuses.KnownNoise,
            ConclusionNote = "機房已知雜訊",
            AutoApply = true,
            OwnerUserIds = new List<long> { 101 }
        });

        var today = DateTime.Today;
        var coordinator = Create();
        var result = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);

        Assert.Equal(1, result.AttachedCount);

        // 不應建立案件
        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.Null(openCase);

        // 當日標記為機房結論
        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(IssueHandlingStatuses.KnownNoise, dayHandling.Status);
        Assert.Equal("〔機房結論〕機房已知雜訊", dayHandling.Note);
        Assert.Null(dayHandling.CaseId);

        // 歷程動作為 FleetApply
        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.FleetApply, log.Action);
    }

    [Fact]
    public void 掛接_同一天重跑AttachNewDay不產生第二筆案件或第二筆標記()
    {
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            OwnerUserIds = new List<long> { 101 }
        });

        var today = DateTime.Today;
        var coordinator = Create();

        var first = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);
        var second = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);

        Assert.Equal(1, first.AttachedCount);
        Assert.Equal(0, second.AttachedCount);

        // 標記與案件皆只有一筆
        var allCases = _cases.GetMany(new[] { Host });
        Assert.Single(allCases);
        Assert.Single(_issueHandlings.GetForDay(Host, today));
        Assert.Single(_handlingLog.GetLogs(Host, today));
    }

    [Fact]
    public void 接線測試_透過HostDayPostProcessorAttachCase可自動建立案件()
    {
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            OwnerUserIds = new List<long> { 101 }
        });

        var today = DateTime.Today;
        var coordinator = Create();

        // 透過批次實際呼叫的入口 HostDayPostProcessor.AttachCase
        HostDayPostProcessor.AttachCase(coordinator, Host, today, CreateDiskIssues());

        // 驗證確實建立案件與指派
        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(101, openCase.HandlerId);
        Assert.Equal(IssueHandlingStatuses.InProgress, openCase.Status);

        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(openCase.CaseId, dayHandling.CaseId);
    }
}

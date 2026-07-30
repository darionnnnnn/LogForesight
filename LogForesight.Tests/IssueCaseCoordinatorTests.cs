using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題案件的建案／同步／掛接規則（docs/FEEDBACK-4-PLAN.md §0.4）。
///
/// 這組測試釘住的核心語意：案件只是協調紀錄，逐日 IssueHandling 列才是唯一投影面；
/// 「合格日」規則（只排除已被使用者明確標成結案類的日子）在建案／同步兩處必須完全一致，
/// 否則會出現「案件說涵蓋 10 天、逐日列卻只有 6 天被同步」的漂移。
/// </summary>
public class IssueCaseCoordinatorTests
{
    private const string Host = "SRV-A";
    private const string IssueLabel = "disk 153";
    private static readonly string IssueKey = IssueSignatureKey.For("System", "disk", 153, System.Diagnostics.EventLogEntryType.Error);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();

    public IssueCaseCoordinatorTests()
    {
        _hosts.Upsert(new WebHost { HostName = Host });
    }

    private IssueCaseCoordinator Create() => new(_cases, _issueHandlings, _handlingLog, _records, _hosts);

    /// <summary>某天的分析紀錄，TopIssues 帶入固定的 disk 153 問題簽章</summary>
    private void AddRecord(DateTime date)
    {
        _records.Add(new DailyAnalysisRecord
        {
            Date = date,
            HostId = _hosts.FindByName(Host)!.HostId,
            Host = Host,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error }
            }
        });
    }

    // ── 建案（BuildCase）─────────────────────────────────────────────────────

    [Fact]
    public void 建案_回溯關聯歷史全部含此問題的風險日()
    {
        var d1 = DateTime.Today.AddDays(-5);
        var d2 = DateTime.Today.AddDays(-3);
        var trigger = DateTime.Today;
        AddRecord(d1);
        AddRecord(d2);
        AddRecord(trigger);

        var result = Create().BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1,
            note: "調查中", dueDate: null, actorId: 9, actorAccount: "acc", occurredAt: DateTime.Now);

        Assert.True(result.Created);
        Assert.Equal(3, result.LinkedDayCount);
        foreach (var date in new[] { d1, d2, trigger })
        {
            var row = _issueHandlings.GetForDay(Host, date).Single();
            Assert.Equal(IssueHandlingStatuses.InProgress, row.Status);
            Assert.Equal(result.CaseId, row.CaseId);
        }
    }

    [Fact]
    public void 建案_已有進行中案件時保留原處理人不搶走()
    {
        var trigger = DateTime.Today;
        AddRecord(trigger);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        // 另一次指派（例如改天再指派給別人）：不搶走，回報既有處理人
        var second = coordinator.BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 2, note: null, dueDate: null,
            actorId: 2, actorAccount: "b", occurredAt: DateTime.Now);

        Assert.False(second.Created);
        Assert.Equal(1, second.ExistingHandlerId);
        // 原案件的處理人沒被改掉
        Assert.Equal(1, _cases.GetOpen(Host, IssueKey)!.HandlerId);
    }

    [Fact]
    public void 建案_已被使用者明確標結案的日子不覆蓋()
    {
        var closedDay = DateTime.Today.AddDays(-2);
        var trigger = DateTime.Today;
        AddRecord(closedDay);
        AddRecord(trigger);

        // 使用者在 closedDay 手動標成誤報（結案類），無案件
        _issueHandlings.Save(new IssueHandling { HostName = Host, Date = closedDay, IssueKey = IssueKey, Status = IssueHandlingStatuses.FalsePositive, UpdatedAt = DateTime.Now });

        var result = Create().BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        // 建案只涵蓋 trigger 這天，closedDay 維持使用者原本標的誤報，不被吃進案件
        Assert.Equal(1, result.LinkedDayCount);
        var untouched = _issueHandlings.GetForDay(Host, closedDay).Single();
        Assert.Equal(IssueHandlingStatuses.FalsePositive, untouched.Status);
        Assert.Null(untouched.CaseId);
    }

    // ── 狀態同步（SyncStatus）───────────────────────────────────────────────

    [Fact]
    public void 同步_無進行中案件時Applied為false()
    {
        var result = Create().SyncStatus(Host, IssueKey, IssueLabel, DateTime.Today,
            status: IssueHandlingStatuses.Resolved, note: null, dueDate: null, clearing: false,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        Assert.False(result.Applied);
    }

    [Fact]
    public void 同步_標記結案時展開到案件涵蓋的其他日子並關閉案件()
    {
        var d1 = DateTime.Today.AddDays(-10);
        var d2 = DateTime.Today.AddDays(-5);
        var trigger = DateTime.Today;
        AddRecord(d1);
        AddRecord(d2);
        AddRecord(trigger);

        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, d1, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        // 模擬批次在 d2 掛接後才出現（案件建立時 d2 已存在於 records，實務上會直接被 BuildCase 涵蓋，
        // 這裡直接驗證 trigger 當天標結案時，d1/d2 全部同步）
        var sync = coordinator.SyncStatus(Host, IssueKey, IssueLabel, trigger,
            status: IssueHandlingStatuses.Resolved, note: "已換硬碟", dueDate: null, clearing: false,
            actorId: 2, actorAccount: "b", occurredAt: DateTime.Now);

        Assert.True(sync.Applied);
        Assert.True(sync.CaseClosed);
        foreach (var date in new[] { d1, d2, trigger })
        {
            var row = _issueHandlings.GetForDay(Host, date).Single();
            Assert.Equal(IssueHandlingStatuses.Resolved, row.Status);
        }
        Assert.NotNull(_cases.Get(_cases.GetMany(new[] { Host }).Single().CaseId)!.ClosedAt);
        Assert.Null(_cases.GetOpen(Host, IssueKey));
    }

    [Fact]
    public void 同步_清除標記時不缺列改落盤明確open()
    {
        var trigger = DateTime.Today;
        AddRecord(trigger);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        var sync = coordinator.SyncStatus(Host, IssueKey, IssueLabel, trigger,
            status: string.Empty, note: null, dueDate: null, clearing: true,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        Assert.True(sync.Applied);
        Assert.False(sync.CaseClosed);
        var row = _issueHandlings.GetForDay(Host, trigger).Single();
        Assert.Equal(IssueHandlingStatuses.Open, row.Status);   // 不是缺列，是明確 open
    }

    [Fact]
    public void 同步_重現的問題不受舊結案案件影響()
    {
        var oldDay = DateTime.Today.AddDays(-20);
        var newDay = DateTime.Today;
        AddRecord(oldDay);
        AddRecord(newDay);

        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, oldDay, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);
        coordinator.SyncStatus(Host, IssueKey, IssueLabel, oldDay,
            status: IssueHandlingStatuses.Resolved, note: null, dueDate: null, clearing: false,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        // 舊案件已結案；同問題在 newDay 重新出現後開新案件，不該把 oldDay 的結案紀錄復活
        var newCase = coordinator.BuildCase(Host, IssueKey, IssueLabel, newDay, handlerId: 2, note: null, dueDate: null,
            actorId: 2, actorAccount: "b", occurredAt: DateTime.Now);

        Assert.True(newCase.Created);
        Assert.Equal(1, newCase.LinkedDayCount);   // 只涵蓋 newDay，不含已結案的 oldDay
        var oldRow = _issueHandlings.GetForDay(Host, oldDay).Single();
        Assert.Equal(IssueHandlingStatuses.Resolved, oldRow.Status);   // 舊案件的結案結果原封不動
    }

    // ── 批次掛接（AttachNewDay）─────────────────────────────────────────────

    [Fact]
    public void 掛接_只掛進行中案件涵蓋的問題()
    {
        var trigger = DateTime.Today.AddDays(-1);
        var newDay = DateTime.Today;
        AddRecord(trigger);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1, note: "處理中", dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        var issues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error },
            new() { LogName = "System", Source = "other", EventId = 999, EntryType = System.Diagnostics.EventLogEntryType.Warning }
        };

        var result = coordinator.AttachNewDay(Host, newDay, issues, DateTime.Now);

        Assert.Equal(1, result.AttachedCount);   // 只有 disk 153 有進行中案件
        var row = _issueHandlings.GetForDay(Host, newDay).Single();
        Assert.Equal(IssueHandlingStatuses.InProgress, row.Status);
        Assert.Equal("處理中", row.Note);
    }

    [Fact]
    public void 掛接_冪等_已掛過的日子不重複寫()
    {
        var trigger = DateTime.Today.AddDays(-1);
        var newDay = DateTime.Today;
        AddRecord(trigger);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, trigger, handlerId: 1, note: null, dueDate: null,
            actorId: 1, actorAccount: "a", occurredAt: DateTime.Now);

        var issues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error }
        };

        var first = coordinator.AttachNewDay(Host, newDay, issues, DateTime.Now);
        var second = coordinator.AttachNewDay(Host, newDay, issues, DateTime.Now);

        Assert.Equal(1, first.AttachedCount);
        Assert.Equal(0, second.AttachedCount);   // 已有列 → 不覆蓋
        Assert.Single(_issueHandlings.GetForDay(Host, newDay));
    }

    [Fact]
    public void 掛接_沒有進行中案件時不動作()
    {
        var issues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error }
        };

        var result = Create().AttachNewDay(Host, DateTime.Today, issues, DateTime.Now);

        Assert.Equal(0, result.AttachedCount);
    }
}

using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 案件批次逐日寫入（<see cref="IssueCaseCoordinator.SubmitCaseDays"/>／<see cref="IssueCaseCoordinator.ApplyPendingCaseDays"/>）：
/// 三種模式的合格日與目標列、冪等、查詢次數不隨案件數線性增長、就地／背景分流結果一致、意圖競態與髒列。
/// 替身組裝方式同 <see cref="IssueCaseCoordinatorTests"/>。
/// </summary>
public class CaseDaySyncTests
{
    private const string IssueLabel = "disk 153";
    private static readonly string IssueKey = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Error);
    private static readonly DateTime D1 = DateTime.Today.AddDays(-3);
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);
    private static readonly DateTime D3 = DateTime.Today.AddDays(-1);
    private static readonly DateTime D4 = DateTime.Today;
    private static readonly DateTime T0 = new(2026, 9, 17, 10, 0, 0);

    /// <summary>一組獨立的替身（分流測試要兩組互不相干的資料比對）</summary>
    private sealed class World
    {
        public readonly FakeHostStore Hosts = new();
        public readonly CountingRecordQuery Records = new();
        public readonly FakeIssueCaseStore Cases = new();
        public readonly CountingIssueHandlingStore IssueHandlings = new();
        public readonly RecordingHandlingStore HandlingLog = new();
        public readonly FakeIssueOwnerStore IssueProfiles = new();

        public IssueCaseCoordinator Create() => new(Cases, IssueHandlings, HandlingLog, Records, Hosts, IssueProfiles);

        public void AddHostDays(string host, params DateTime[] dates)
        {
            var existing = Hosts.FindByName(host) ?? Hosts.Upsert(new WebHost { HostName = host });
            foreach (var date in dates)
            {
                Records.Add(new DailyAnalysisRecord
                {
                    Date = date, HostId = existing.HostId, Host = host,
                    TopIssues = new List<LogIssueSignature>
                    {
                        new() { LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error }
                    }
                });
            }
        }

        public IssueCase AddCase(string caseId, string host, DateTime first, DateTime last)
        {
            var c = new IssueCase
            {
                CaseId = caseId, HostName = host, IssueKey = IssueKey, IssueLabel = IssueLabel,
                Status = IssueHandlingStatuses.InProgress, HandlerId = 1,
                FirstLinkedDate = first, LastLinkedDate = last,
                CreatedAt = T0.AddDays(-1), CreatedByAccount = "admin", UpdatedAt = T0.AddDays(-1)
            };
            Cases.Save(c);
            return c;
        }

        public void Mark(string host, DateTime date, string status, string? caseId) =>
            IssueHandlings.Save(new IssueHandling
            {
                HostName = host, Date = date, IssueKey = IssueKey, Status = status, CaseId = caseId,
                ActorAccount = "user", UpdatedAt = T0.AddDays(-1)
            });

        public IssueHandling? Row(string host, DateTime date) =>
            IssueHandlings.GetForDay(host, date).SingleOrDefault(h => h.IssueKey == IssueKey);
    }

    internal sealed class CountingRecordQuery : FakeAnalysisRecordQuery, IAnalysisRecordQuery
    {
        public int IssueDaysCalls { get; private set; }

        List<IssueDayHit> IAnalysisRecordQuery.IssueDaysFor(IReadOnlyCollection<HostKey> hosts, IReadOnlyCollection<string> issueKeys)
        {
            IssueDaysCalls++;
            return base.IssueDaysFor(hosts, issueKeys);
        }
    }

    internal sealed class CountingIssueHandlingStore : FakeIssueHandlingStore, IIssueHandlingStore
    {
        public int GetManyCalls { get; private set; }
        public int GetByCasesCalls { get; private set; }

        List<IssueHandling> IIssueHandlingStore.GetByCases(IReadOnlyCollection<string> caseIds)
        {
            GetByCasesCalls++;
            return base.GetByCases(caseIds);
        }

        List<IssueHandling> IIssueHandlingStore.GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
        {
            GetManyCalls++;
            return base.GetMany(hostNames, from, to);
        }
    }

    internal sealed class RecordingHandlingStore : FakeHandlingStore, IRecordHandlingStore
    {
        public List<RecordHandlingLog> Logs { get; } = new();

        void IRecordHandlingStore.AppendLog(RecordHandlingLog log)
        {
            Logs.Add(log);
            base.AppendLog(log);
        }
    }

    private static CaseDayIntent Intent(string mode, string status = IssueHandlingStatuses.InProgress, string? note = "調查中",
        DateTime? trigger = null, bool clearing = false, DateTime? due = null, DateTime? occurredAt = null) => new()
    {
        Mode = mode, Status = status, Note = note, DueDate = due, Clearing = clearing,
        ActorId = 9, ActorAccount = "acc", OccurredAt = occurredAt ?? T0, TriggerDate = trigger
    };

    // ── assign ───────────────────────────────────────────────────────────────

    [Fact]
    public void 指派_三台各兩個候選日_已結案日不動_觸發日記CaseAssign()
    {
        var w = new World();
        var cases = new List<IssueCase>();
        foreach (var host in new[] { "H1", "H2", "H3" })
        {
            w.AddHostDays(host, D1, D2);
            cases.Add(w.AddCase("c-" + host, host, D2, D2));
        }
        w.Mark("H2", D1, IssueHandlingStatuses.Resolved, caseId: null);

        var result = w.Create().SubmitCaseDays(cases, Intent(CaseDayModes.Assign, trigger: D2), IssueCaseCoordinator.CaseDayInlineRowLimit);

        Assert.True(result.Inline);
        Assert.Equal(5, result.Rows);
        Assert.Equal(5, w.HandlingLog.Logs.Count);
        Assert.Equal(3, w.HandlingLog.Logs.Count(l => l.Action == HandlingActions.CaseAssign && l.Date == D2));
        Assert.Equal(2, w.HandlingLog.Logs.Count(l => l.Action == HandlingActions.CaseSync && l.Date == D1));
        Assert.All(w.HandlingLog.Logs, l => Assert.Equal("調查中", l.Note));

        var resolved = w.Row("H2", D1)!;
        Assert.Equal(IssueHandlingStatuses.Resolved, resolved.Status);
        Assert.Null(resolved.CaseId);

        var h1 = w.Row("H1", D1)!;
        Assert.Equal(IssueHandlingStatuses.InProgress, h1.Status);
        Assert.Equal("c-H1", h1.CaseId);
        Assert.Equal(T0, h1.UpdatedAt);

        Assert.Equal(D1, w.Cases.Get("c-H1")!.FirstLinkedDate);
        Assert.Equal(D2, w.Cases.Get("c-H1")!.LastLinkedDate);
        Assert.Equal(D2, w.Cases.Get("c-H2")!.FirstLinkedDate);
        Assert.Equal(D2, w.Cases.Get("c-H2")!.LastLinkedDate);
        Assert.All(cases, c => Assert.False(w.Cases.Get(c.CaseId)!.DaySyncPending));
    }

    // ── sync ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 同步_Clearing_目標open且備註清空_觸發日記清除標記()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var c = w.AddCase("c1", "H1", D1, D2);
        w.Mark("H1", D1, IssueHandlingStatuses.InProgress, "c1");
        w.Mark("H1", D2, IssueHandlingStatuses.InProgress, "c1");

        var result = w.Create().SubmitCaseDays(new[] { c },
            Intent(CaseDayModes.Sync, note: "不該留下", trigger: D2, clearing: true, due: D4), 100);

        Assert.Equal(2, result.Rows);
        foreach (var d in new[] { D1, D2 })
        {
            var row = w.Row("H1", d)!;
            Assert.Equal(IssueHandlingStatuses.Open, row.Status);
            Assert.Null(row.Note);
            Assert.Null(row.DueDate);
        }
        Assert.Equal(HandlingActions.IssueStatusCleared, w.HandlingLog.Logs.Single(l => l.Date == D2).Action);
        Assert.Equal(HandlingActions.CaseSync, w.HandlingLog.Logs.Single(l => l.Date == D1).Action);
        Assert.All(w.HandlingLog.Logs, l => Assert.Null(l.Note));
    }

    [Fact]
    public void 同步_觀察中帶期限_歷程備註含觀察至()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var c = w.AddCase("c1", "H1", D1, D2);

        w.Create().SubmitCaseDays(new[] { c },
            Intent(CaseDayModes.Sync, status: IssueHandlingStatuses.Observing, note: "先觀察", trigger: D2, due: D4), 100);

        Assert.Equal(D4, w.Row("H1", D1)!.DueDate);
        Assert.Equal("先觀察", w.Row("H1", D1)!.Note);
        var triggerLog = w.HandlingLog.Logs.Single(l => l.Date == D2);
        Assert.Equal(HandlingActions.IssueStatus, triggerLog.Action);
        Assert.Contains("觀察至", triggerLog.Note);
        Assert.All(w.HandlingLog.Logs, l => Assert.Contains("觀察至", l.Note));
    }

    // ── cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public void 取消_只調回本案件擁有且未結案的日子()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2, D3, D4);
        var c = w.AddCase("c1", "H1", D1, D4);
        w.Mark("H1", D1, IssueHandlingStatuses.InProgress, "c1");      // 本案件、進行中 → 調回 open
        w.Mark("H1", D2, IssueHandlingStatuses.InProgress, null);      // 使用者自標 → 不動
        w.Mark("H1", D3, IssueHandlingStatuses.InProgress, "other");   // 別的案件 → 不動
        w.Mark("H1", D4, IssueHandlingStatuses.Resolved, "c1");        // 本案件但已結案 → 不動

        var result = w.Create().SubmitCaseDays(new[] { c }, Intent(CaseDayModes.Cancel, note: "交辦取消"), 100);

        Assert.Equal(1, result.Rows);
        var d1 = w.Row("H1", D1)!;
        Assert.Equal(IssueHandlingStatuses.Open, d1.Status);
        Assert.Null(d1.Note);
        Assert.Null(d1.DueDate);
        Assert.Equal("c1", d1.CaseId);
        Assert.Equal(T0, d1.UpdatedAt);

        var userMarked = w.Row("H1", D2)!;
        Assert.Equal(IssueHandlingStatuses.InProgress, userMarked.Status);
        Assert.Null(userMarked.CaseId);
        Assert.Equal(IssueHandlingStatuses.InProgress, w.Row("H1", D3)!.Status);
        Assert.Equal("other", w.Row("H1", D3)!.CaseId);
        Assert.Equal(IssueHandlingStatuses.Resolved, w.Row("H1", D4)!.Status);

        var log = Assert.Single(w.HandlingLog.Logs);
        Assert.Equal(HandlingActions.IssueStatusCleared, log.Action);
        Assert.Equal("交辦取消", log.Note);
        Assert.Equal(D1, log.Date);
    }

    [Fact]
    public void 取消_案件擁有但日期在關聯範圍之前的列也調回open()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D3);
        var c = w.AddCase("c1", "H1", D3, D4);
        w.Mark("H1", D1, IssueHandlingStatuses.InProgress, "c1");   // 範圍外的觸發日
        w.Mark("H1", D3, IssueHandlingStatuses.InProgress, "c1");

        var result = w.Create().SubmitCaseDays(new[] { c }, Intent(CaseDayModes.Cancel, note: "取消"), 100);

        Assert.Equal(2, result.Rows);
        Assert.Equal(IssueHandlingStatuses.Open, w.Row("H1", D1)!.Status);
        Assert.Equal(HandlingActions.IssueStatusCleared, w.HandlingLog.Logs.Single(l => l.Date == D1).Action);
    }

    [Fact]
    public void 取消_300個案件_GetByCases呼叫次數等於批數()
    {
        var w = new World();
        var cases = new List<IssueCase>();
        for (var i = 0; i < 300; i++)
        {
            var host = $"H{i:D3}";
            w.AddHostDays(host, D1);
            cases.Add(w.AddCase("c" + i, host, D1, D1));
            w.Mark(host, D1, IssueHandlingStatuses.InProgress, "c" + i);
        }

        var result = w.Create().SubmitCaseDays(cases, Intent(CaseDayModes.Cancel, note: "取消"), IssueCaseCoordinator.CaseDayInlineRowLimit);

        Assert.Equal(300, result.Rows);
        Assert.Equal(1, w.IssueHandlings.GetByCasesCalls);   // 300 個案件 ≤ 每批 500 → 1 批
        Assert.Equal(0, w.Records.IssueDaysCalls);
    }

    // ── 冪等 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 冪等_同一意圖重送不重寫_換OccurredAt照常寫()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var c = w.AddCase("c1", "H1", D2, D2);
        var coordinator = w.Create();
        var intent = Intent(CaseDayModes.Assign, trigger: D2);

        var first = coordinator.SubmitCaseDays(new[] { c }, intent, 100);
        var second = coordinator.SubmitCaseDays(new[] { c }, intent, 100);

        Assert.Equal(2, first.Rows);
        Assert.Equal(0, second.Rows);
        Assert.Equal(2, w.HandlingLog.Logs.Count);

        var third = coordinator.SubmitCaseDays(new[] { c }, Intent(CaseDayModes.Assign, trigger: D2, occurredAt: T0.AddMinutes(1)), 100);
        Assert.Equal(2, third.Rows);
        Assert.Equal(4, w.HandlingLog.Logs.Count);
    }

    // ── 批次查詢次數 ─────────────────────────────────────────────────────────

    [Fact]
    public void 批次_300個案件_候選日查一次_逐日列依主機名批數查()
    {
        var w = new World();
        var cases = new List<IssueCase>();
        for (var i = 0; i < 300; i++)
        {
            var host = $"H{i:D3}";
            w.AddHostDays(host, D1);
            cases.Add(w.AddCase("c" + i, host, D1, D1));
        }

        var result = w.Create().SubmitCaseDays(cases, Intent(CaseDayModes.Assign, trigger: D1), IssueCaseCoordinator.CaseDayInlineRowLimit);

        Assert.Equal(300, result.Rows);
        Assert.Equal(1, w.Records.IssueDaysCalls);
        Assert.Equal(1, w.IssueHandlings.GetManyCalls);   // 300 個主機名 ≤ 每批 500 → 1 批
    }

    // ── 就地／背景分流 ───────────────────────────────────────────────────────

    private static World SplitWorld(out List<IssueCase> cases)
    {
        var w = new World();
        cases = new List<IssueCase>();
        foreach (var host in new[] { "H1", "H2", "H3" })
        {
            w.AddHostDays(host, D1, D2);
            cases.Add(w.AddCase("c-" + host, host, D2, D2));
        }
        w.Mark("H3", D1, IssueHandlingStatuses.InProgress, null);
        return w;
    }

    private static List<string> Snapshot(World w, IEnumerable<string> hosts)
    {
        var rows = hosts.SelectMany(h => w.IssueHandlings.GetMany(new[] { h }, DateTime.MinValue, DateTime.MaxValue))
            .Select(r => $"{r.HostName}|{r.Date:yyyy-MM-dd}|{r.IssueKey}|{r.Status}|{r.Note}|{r.DueDate:o}|{r.CaseId}|{r.ActorId}|{r.ActorAccount}|{r.UpdatedAt:o}")
            .OrderBy(s => s, StringComparer.Ordinal);
        var logs = w.HandlingLog.Logs
            .Select(l => $"LOG|{l.HostName}|{l.Date:yyyy-MM-dd}|{l.Status}|{l.IssueKey}|{l.IssueLabel}|{l.Note}|{l.ActorId}|{l.ActorAccount}|{l.Action}|{l.CreatedAt:o}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return rows.Concat(logs).ToList();
    }

    [Fact]
    public void 分流_超過門檻存意圖不寫_背景寫入結果與就地寫入逐筆相同()
    {
        var hosts = new[] { "H1", "H2", "H3" };
        var intent = Intent(CaseDayModes.Assign, note: "交辦", trigger: D2, due: D4);

        var inline = SplitWorld(out var inlineCases);
        var inlineResult = inline.Create().SubmitCaseDays(inlineCases, intent, IssueCaseCoordinator.CaseDayInlineRowLimit);
        Assert.True(inlineResult.Inline);
        Assert.Equal(6, inlineResult.Rows);

        var background = SplitWorld(out var bgCases);
        var before = Snapshot(background, hosts);
        var coordinator = background.Create();
        var deferred = coordinator.SubmitCaseDays(bgCases, intent, inlineResult.Rows - 1);

        Assert.False(deferred.Inline);
        Assert.Equal(6, deferred.Rows);
        Assert.Equal(3, deferred.PendingCases);
        Assert.Equal(before, Snapshot(background, hosts));   // 沒有任何逐日列與歷程寫入
        Assert.Empty(background.HandlingLog.Logs);
        Assert.Equal(3, background.Cases.CountDaySyncPending());
        Assert.All(bgCases, c =>
        {
            var stored = background.Cases.Get(c.CaseId)!;
            Assert.True(stored.DaySyncPending);
            Assert.NotNull(stored.DaySyncIntent);
        });

        Assert.Equal(3, coordinator.ApplyPendingCaseDays(200));

        Assert.Equal(Snapshot(inline, hosts), Snapshot(background, hosts));
        Assert.Equal(0, background.Cases.CountDaySyncPending());
        foreach (var c in bgCases)
        {
            var stored = background.Cases.Get(c.CaseId)!;
            Assert.Null(stored.DaySyncIntent);
            Assert.Equal(inline.Cases.Get(c.CaseId)!.FirstLinkedDate, stored.FirstLinkedDate);
            Assert.Equal(inline.Cases.Get(c.CaseId)!.LastLinkedDate, stored.LastLinkedDate);
        }
        Assert.Equal(0, coordinator.ApplyPendingCaseDays(200));
    }

    [Fact]
    public void 競態_待同步期間送新意圖_舊意圖清不掉_背景依新意圖寫()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var c = w.AddCase("c1", "H1", D2, D2);
        var intentA = Intent(CaseDayModes.Assign, note: "A", trigger: D2);
        var intentB = Intent(CaseDayModes.Assign, note: "B", trigger: D2, occurredAt: T0.AddMinutes(5));
        var coordinator = w.Create();

        Assert.False(coordinator.SubmitCaseDays(new[] { c }, intentA, 0).Inline);
        Assert.False(coordinator.SubmitCaseDays(new[] { c }, intentB, 0).Inline);

        Assert.False(w.Cases.ClearDaySyncPendingIfUnchanged("c1", intentA));
        Assert.True(w.Cases.Get("c1")!.DaySyncPending);

        Assert.Equal(1, coordinator.ApplyPendingCaseDays(200));

        Assert.Equal("B", w.Row("H1", D1)!.Note);
        Assert.Equal("B", w.Row("H1", D2)!.Note);
        Assert.All(w.HandlingLog.Logs, l => Assert.Equal("B", l.Note));
        Assert.False(w.Cases.Get("c1")!.DaySyncPending);
    }

    [Fact]
    public void 髒列_旗標為真但意圖為null_清旗標且零寫入()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var c = w.AddCase("c1", "H1", D2, D2);
        c.DaySyncPending = true;
        c.DaySyncIntent = null;
        w.Cases.Save(c);

        Assert.Equal(1, w.Create().ApplyPendingCaseDays(200));

        Assert.False(w.Cases.Get("c1")!.DaySyncPending);
        Assert.Null(w.Row("H1", D1));
        Assert.Null(w.Row("H1", D2));
        Assert.Empty(w.HandlingLog.Logs);
    }

    [Fact]
    public void 空案件清單_不做任何查詢()
    {
        var w = new World();
        var result = w.Create().SubmitCaseDays(Array.Empty<IssueCase>(), Intent(CaseDayModes.Assign), 100);

        Assert.Equal(new CaseDaySubmitResult(true, 0, 0), result);
        Assert.Equal(0, w.Records.IssueDaysCalls);
        Assert.Equal(0, w.IssueHandlings.GetManyCalls);
    }
}

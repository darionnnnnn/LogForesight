using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次 J-2：夜間派工途中單被結案／改派時的寫入判斷，以及 EF 與 SchemaUpgrader 索引命名收斂、
/// 既有資料庫重複索引的移除。
/// </summary>
public class DataConsistencyJ2Tests : IDisposable
{
    private const string Source = "Disk";
    private const int EventId = 153;

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly SystemSettings _settings = new();
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly FakeWorkOrderStore _orders;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly EfSqliteFixture _fx = new();

    public DataConsistencyJ2Tests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, _owners);
        _coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, _caseCoordinator, _handlingLog, _hosts);
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    // ── 夜間派工途中變動 ────────────────────────────────────────────────

    private static LogIssueSignature Issue() => new()
    {
        LogName = "System", Source = Source, EventId = EventId, EntryType = EventLogEntryType.Error,
        Severity = IssueSeverity.High
    };

    private static string IssueKey => IssueSignatureKey.For(Issue());

    private static WorkOrderActor Admin => new() { ActorId = 1, ActorAccount = "admin", OccurredAt = DateTime.Now };

    /// <summary>主機在群組 5、處理人 11 的群組續掛單；建好派工脈絡（脈絡快照看到這張單是進行中、處理人 11）</summary>
    private (NightlyDispatch Dispatch, long OrderId) ArrangeAutoAttachOrder()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 5 } });
        var orderId = _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = EventId, HandlerId = 11, Origin = WorkOrderOrigins.Manual,
            ScopeKind = WorkOrderScopes.Groups, ScopeGroupIds = new List<long> { 5 }, AutoAttach = true,
            CreatedAt = DateTime.Today.AddDays(-3)
        });
        var pool = new DispatchCandidatePool
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
        var ctx = DispatchContext.Build(pool, _owners, _orders, _cases, new FakeNoiseMarkStore(), _settings, DateTime.Now);
        return (new NightlyDispatch(_coordinator, ctx, _hosts), orderId);
    }

    [Fact]
    public void 夜間派工_建脈絡後單被取消_成員不寫入且計order_closed_midrun()
    {
        var (dispatch, orderId) = ArrangeAutoAttachOrder();

        _coordinator.Cancel(orderId, "管理者取消", Admin);
        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);

        Assert.Null(_cases.GetOpen("SRV-01", IssueKey));
        Assert.Empty(_cases.GetByWorkOrder(orderId, 0, 100));
        Assert.Empty(_issueHandlings.GetForDay("SRV-01", DateTime.Today));
        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(1, summary.SkipCounts[WorkOrderCoordinator.SkipOrderClosedMidrun]);
        Assert.Equal(0, summary.AttachedMembers);
        Assert.DoesNotContain(_orders.ListEvents(orderId), e => e.Action == WorkOrderEventActions.Appended);
        Assert.Contains("交辦單在派工途中已結案", NightlyDispatch.DescribeSummary(summary));
    }

    [Fact]
    public void 夜間派工_建脈絡後單被代為結案_成員不寫入且計order_closed_midrun()
    {
        var (dispatch, orderId) = ArrangeAutoAttachOrder();

        _coordinator.AdminClose(orderId, IssueHandlingStatuses.Resolved, "管理者結案", Admin);
        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);

        Assert.Null(_cases.GetOpen("SRV-01", IssueKey));
        Assert.Equal(1, dispatch.FlushRun(DateTime.Now).SkipCounts[WorkOrderCoordinator.SkipOrderClosedMidrun]);
    }

    [Fact]
    public void 夜間派工_建脈絡後單改派給B_成員寫入該單且處理人為B_計handler_changed_midrun()
    {
        var (dispatch, orderId) = ArrangeAutoAttachOrder();

        _coordinator.Reassign(orderId, 22, Admin);
        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);

        var openCase = _cases.GetOpen("SRV-01", IssueKey)!;
        Assert.Equal(orderId, openCase.WorkOrderId);
        Assert.Equal(22, openCase.HandlerId);
        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(1, summary.SkipCounts[WorkOrderCoordinator.HandlerChangedMidrun]);
        Assert.False(summary.SkipCounts.ContainsKey(WorkOrderCoordinator.SkipOrderClosedMidrun));
        Assert.Equal(1, summary.AttachedMembers);
        Assert.Equal(orderId, Assert.Single(summary.PerHandler[22]).WorkOrderId);
        Assert.False(summary.PerHandler.ContainsKey(11));
        Assert.Contains("交辦單在派工途中已改派，已依新處理人掛入", NightlyDispatch.DescribeSummary(summary));
    }

    [Fact]
    public void 夜間派工_單未變動_不計途中原因()
    {
        var (dispatch, orderId) = ArrangeAutoAttachOrder();

        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);

        Assert.Equal(11, _cases.GetOpen("SRV-01", IssueKey)!.HandlerId);
        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.False(summary.SkipCounts.ContainsKey(WorkOrderCoordinator.SkipOrderClosedMidrun));
        Assert.False(summary.SkipCounts.ContainsKey(WorkOrderCoordinator.HandlerChangedMidrun));
        Assert.Equal(orderId, Assert.Single(summary.PerHandler[11]).WorkOrderId);
    }

    // ── 索引 ─────────────────────────────────────────────────────────────

    private sealed record IndexShape(string Table, string Name, bool Unique, bool Partial, string Columns);

    /// <summary>每一張 lf_ 表的全部索引（排除 sqlite_autoindex_），欄位依 seqno 串接</summary>
    private static List<IndexShape> ReadIndexes(LfDbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        List<string> Strings(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0))!);
            return list;
        }

        var result = new List<IndexShape>();
        foreach (var table in Strings("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'lf\\_%' ESCAPE '\\'"))
        {
            foreach (var row in Strings("SELECT name || '|' || \"unique\" || '|' || partial FROM pragma_index_list('" + table + "')"))
            {
                var parts = row.Split('|');
                if (parts[0].StartsWith("sqlite_autoindex_", StringComparison.Ordinal)) continue;
                var columns = string.Join(",", Strings(
                    "SELECT ifnull(name, '#' || cid) FROM pragma_index_info('" + parts[0] + "') ORDER BY seqno"));
                result.Add(new IndexShape(table, parts[0], parts[1] == "1", parts[2] == "1", columns));
            }
        }
        return result;
    }

    private static List<string> DuplicateGroups(IEnumerable<IndexShape> indexes) =>
        indexes.Where(i => !i.Partial)
            .GroupBy(i => (i.Table, Columns: i.Columns.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Table + "(" + g.Key.Columns + "): " + string.Join(" / ", g.Select(i => i.Name)))
            .ToList();

    [Fact]
    public void 全新SQLite_EnsureCreated加升級器後_每張lf表沒有欄位組合相同的兩個索引()
    {
        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using var check = _fx.NewContext();
        var indexes = ReadIndexes(check);
        Assert.True(indexes.Count > 20, "應讀到全部 lf_ 表的索引，實際 " + indexes.Count);
        Assert.Contains(indexes, i => i.Partial);
        var duplicates = DuplicateGroups(indexes);
        Assert.True(duplicates.Count == 0, "重複索引：\n" + string.Join("\n", duplicates));
    }

    [Fact]
    public void 既有資料庫_EF預設名稱重複索引被移除保留升級器名稱_部分索引不動_重跑冪等()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_lf_top_issues_record_date_source_name_event_id ON lf_top_issues (record_date, source_name, event_id)");
            ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_lf_top_issues_partial_signature ON lf_top_issues (record_date, source_name, event_id) WHERE event_id > 0");
        }

        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);
        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using var check = _fx.NewContext();
        var sameColumns = ReadIndexes(check)
            .Where(i => i.Table == "lf_top_issues" && i.Columns == "record_date,source_name,event_id")
            .ToList();
        var full = Assert.Single(sameColumns, i => !i.Partial);
        Assert.Equal("IX_lf_top_issues_date_signature", full.Name);
        var partial = Assert.Single(sameColumns, i => i.Partial);
        Assert.Equal("IX_lf_top_issues_partial_signature", partial.Name);
    }

    [Fact]
    public void 既有資料庫_重複組內都不是升級器名稱_全部保留()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_custom_a ON lf_reports (kind, report_date)");
            ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_custom_b ON lf_reports (kind, report_date)");
        }

        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using var check = _fx.NewContext();
        var names = ReadIndexes(check).Where(i => i.Table == "lf_reports").Select(i => i.Name).ToList();
        Assert.Contains("IX_custom_a", names);
        Assert.Contains("IX_custom_b", names);
    }
}

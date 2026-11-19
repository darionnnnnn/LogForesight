using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 繞過 <c>SaveChanges</c> 的寫入（<c>ExecuteUpdate</c>、原生 INSERT）逐處套用與存檔相同的模型長度截斷（第 79 輪批次 D3）。
/// 這幾處不經 <c>LfDbContext.PrepareForSave</c>，上限一律取自模型（<see cref="ColumnClamp.ClampFor{TEntity}"/>），不寫字面值。
/// 每處邊界兩側各一例：剛好等於上限不動、多一個字元被截到上限。
/// 盤點後判斷不需要截斷的繞過寫入（只寫布林、數值、版本號、時間、固定常數或不設上限的 JSON／文字欄）列在 DB-SPEC。
/// </summary>
public sealed class BypassWriteTruncationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Date = new(2026, 10, 1);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 權限異動確認：ExecuteUpdate 寫確認人帳號 ──

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 權限異動確認_確認人帳號超過上限時截到上限_剛好等於上限不動(int over)
    {
        var max = ColumnClamp.For<PermissionChangeRow>(nameof(PermissionChangeRow.ConfirmedByAccount))!.Value;
        var store = new PermissionChangeStore(_fx.NewContext);
        var changeId = Guid.NewGuid().ToString("N");
        store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = changeId, HostName = "h1", DetectedAt = Date, Target = "t", ChangeType = "add", AlertText = "x" }
        });

        var account = new string('a', max + over);
        var updated = store.SaveConfirmations(new[] { changeId }, PermissionConfirmStatuses.Authorized, 7, account, Date, "備註");

        Assert.Equal(1, updated);
        using var ctx = _fx.NewContext();
        Assert.Equal(max, Assert.Single(ctx.PermissionChanges.AsNoTracking().ToList()).ConfirmedByAccount!.Length);
    }

    // ── 交辦單續掛停止原因：ExecuteUpdate ──

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 交辦單續掛停止原因_超過上限時截到上限_剛好等於上限不動(int over)
    {
        var max = ColumnClamp.For<WorkOrderRow>(nameof(WorkOrderRow.ContinuationStoppedReason))!.Value;
        var store = new EfWorkOrderStore(_fx.NewContext);
        var id = store.Insert(new WorkOrder
        {
            SourceName = "Disk", EventId = 153, IssueLabel = "Disk 153", HandlerId = 1,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.All, AutoAttach = true,
            CreatedByAccount = "a", CreatedAt = Date
        });

        store.SetContinuationStops(new[] { new ContinuationStopUpdate(id, new string('r', max + over), Date) });

        Assert.Equal(max, store.Get(id)!.ContinuationStoppedReason!.Length);
    }

    // ── 交辦單背景整併：解析 issue_key 後以 ExecuteUpdate 寫入來源名稱 ──

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void 背景整併_案件來源名稱超過上限時截到上限_剛好等於上限不動(int over)
    {
        var max = ColumnClamp.For<IssueCaseRow>(nameof(IssueCaseRow.SourceName))!.Value;
        var source = new string('s', max + over);
        var issueKey = IssueSignatureKey.For("System", source, 7, LogEntryType.Error);
        using (var ctx = _fx.NewContext())
        {
            ctx.IssueCases.Add(new IssueCaseRow
            {
                CaseId = "c1", HostName = "h1", HostNameKey = "H1", IssueKey = issueKey, IssueLabel = "l",
                Status = "open", FirstLinkedDate = Date, LastLinkedDate = Date, CreatedAt = Date, UpdatedAt = Date,
                CreatedByAccount = "a"
            });
            ctx.SaveChanges();
        }

        new WorkOrderBackfiller(new EfWorkOrderStore(_fx.NewContext)).Run(CancellationToken.None);

        using var read = _fx.NewContext();
        var row = Assert.Single(read.IssueCases.AsNoTracking().ToList());
        Assert.Equal(max, row.SourceName!.Length);
        Assert.Equal(7, row.EventId);
        Assert.True(row.SourceKey!.Length <= ColumnClamp.For<IssueCaseRow>(nameof(IssueCaseRow.SourceKey))!.Value);
    }

    // ── 首見日：批次新增走原生 INSERT ──

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 首見日批次新增_來源名稱超過上限時截到上限_剛好等於上限不動(int over)
    {
        var max = ColumnClamp.For<IssueFirstSeenRow>(nameof(IssueFirstSeenRow.SourceName))!.Value;
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");

        store.Append(new DailyAnalysisRecord
        {
            Date = Date, RiskLevel = "高", Headline = "h",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = new string('s', max + over), EventId = 1, Count = 1, Severity = IssueSeverity.Critical }
            }
        });

        using var ctx = _fx.NewContext();
        Assert.Equal(max, Assert.Single(ctx.IssueFirstSeen.AsNoTracking().ToList()).SourceName.Length);
    }
}

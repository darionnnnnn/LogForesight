using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IIssueCaseStore"/> 的真表實作（↔ lf_issue_cases，
/// docs/SCALE-ISSUE-FIRST-PLAN.md P3／根因 B）。
///
/// **為什麼案件也要一起搬**（它的列數比 issue_handling 少一個量級）：
///   1. <c>GetOpen</c> 是**每次標記都會走**的路徑（IssueCaseCoordinator.SyncStatus 開頭），
///      整份 blob 時每次標記都先反序列化全部案件。
///   2. 夜間掛接（AttachNewDay）過去在迴圈內逐案 <c>Save</c>，每次都是一次整份讀改寫
///      ——6000 台 × 平均 2 個命中案件＝每晚上萬次整份重寫（體檢 S4）。
///   3. 依問題視角逐群組呼叫 <c>GetMany</c>（規劃 N3），1000 種問題就是 1000 次整份讀。
///
/// 同一 (主機, 問題簽章) 同時至多一個進行中案件仍由呼叫端（IssueCaseCoordinator）保證，
/// 與原實作的職責分工一致——store 只管持久化。
/// </summary>
public sealed class EfIssueCaseStore : IIssueCaseStore
{
    private readonly Func<LfDbContext> _contextFactory;

    public EfIssueCaseStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IssueCase? GetOpen(string hostName, string issueKey)
    {
        var key = HostNameKey.Of(hostName);

        using var ctx = _contextFactory();
        var row = ctx.IssueCases.AsNoTracking()
            .FirstOrDefault(c => c.HostNameKey == key && c.IssueKey == issueKey && c.ClosedAt == null);
        return row == null ? null : ToModel(row);
    }

    public List<IssueCase> GetOpenForHost(string hostName)
    {
        var key = HostNameKey.Of(hostName);

        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HostNameKey == key && c.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetMany(IEnumerable<string> hostNames)
    {
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        if (keys.Count == 0) return new List<IssueCase>();

        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => keys.Contains(c.HostNameKey))
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetOpenByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == userId && c.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueCase> GetByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.HandlerId == userId)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public IssueCase? Get(string caseId)
    {
        using var ctx = _contextFactory();
        var row = ctx.IssueCases.AsNoTracking().FirstOrDefault(c => c.CaseId == caseId);
        return row == null ? null : ToModel(row);
    }

    public void Save(IssueCase issueCase)
    {
        using var ctx = _contextFactory();
        var row = ctx.IssueCases.FirstOrDefault(c => c.CaseId == issueCase.CaseId);

        if (row == null)
        {
            row = new IssueCaseRow
            {
                CaseId = issueCase.CaseId,
                HostName = issueCase.HostName,
                HostNameKey = HostNameKey.Of(issueCase.HostName),
                IssueKey = issueCase.IssueKey,
                IssueLabel = issueCase.IssueLabel,
                CreatedAt = issueCase.CreatedAt,
                CreatedByAccount = issueCase.CreatedByAccount
            };
            ctx.IssueCases.Add(row);
        }

        // 與原 blob 實作逐欄對齊：CaseId／HostName／IssueKey／IssueLabel／CreatedAt／
        // CreatedByAccount 是建案當下的事實，更新時不動
        row.Status = issueCase.Status;
        row.HandlerId = issueCase.HandlerId;
        row.Note = issueCase.Note;
        row.DueDate = issueCase.DueDate;
        row.FirstLinkedDate = issueCase.FirstLinkedDate;
        row.LastLinkedDate = issueCase.LastLinkedDate;
        row.ClosedAt = issueCase.ClosedAt;
        row.UpdatedAt = issueCase.UpdatedAt;

        ctx.SaveChanges();
    }

    /// <summary>
    /// 批次寫入（P3 新增，供 <c>IssueCaseCoordinator.AttachNewDay</c> 用）：體檢 S4 指出
    /// 夜間掛接在迴圈內逐案 Save，2000 台每晚約 4000 次整份 blob 讀改寫。改真表之後
    /// 逐案 Save 已經便宜很多，但每次仍是一趟 DB 往返——批次入口讓一晚的推進併成一次交易。
    /// </summary>
    public void SaveMany(IEnumerable<IssueCase> cases)
    {
        var list = cases.ToList();
        if (list.Count == 0) return;

        using var ctx = _contextFactory();
        var ids = list.Select(c => c.CaseId).ToList();
        var existing = ctx.IssueCases.Where(c => ids.Contains(c.CaseId)).ToDictionary(c => c.CaseId);

        foreach (var issueCase in list)
        {
            if (!existing.TryGetValue(issueCase.CaseId, out var row))
            {
                row = new IssueCaseRow
                {
                    CaseId = issueCase.CaseId,
                    HostName = issueCase.HostName,
                    HostNameKey = HostNameKey.Of(issueCase.HostName),
                    IssueKey = issueCase.IssueKey,
                    IssueLabel = issueCase.IssueLabel,
                    CreatedAt = issueCase.CreatedAt,
                    CreatedByAccount = issueCase.CreatedByAccount
                };
                ctx.IssueCases.Add(row);
                existing[issueCase.CaseId] = row;
            }

            row.Status = issueCase.Status;
            row.HandlerId = issueCase.HandlerId;
            row.Note = issueCase.Note;
            row.DueDate = issueCase.DueDate;
            row.FirstLinkedDate = issueCase.FirstLinkedDate;
            row.LastLinkedDate = issueCase.LastLinkedDate;
            row.ClosedAt = issueCase.ClosedAt;
            row.UpdatedAt = issueCase.UpdatedAt;
        }

        ctx.SaveChanges();
    }

    /// <summary>
    /// 清除超過保留天數的案件（docs/SCALE-FIX-PLAN-2026-08-06.md S-4）。
    ///
    /// **只刪已結案、且結案時間早於 cutoff 的**——這裡刻意不用 <c>LastLinkedDate</c>
    /// 之類的「事件日期」當條件：進行中案件不論多舊都要留著，因為它代表
    /// **還沒處理完**。一個掛了兩年沒人動的案件正是最該被看見的那種，
    /// 用日期把它清掉等於幫忙把爛帳藏起來。
    ///
    /// 結案時間才是案件自己的生命週期終點，也才是「可以忘記了」的正確判準。
    /// </summary>
    public int Prune(int retentionDays) => Prune(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);

        return BatchedPrune.Run<string>(_contextFactory,
            (ctx, take) => ctx.IssueCases
                .Where(c => c.ClosedAt != null && c.ClosedAt < cutoff)
                .OrderBy(c => c.ClosedAt)
                .Select(c => c.CaseId)
                .Take(take)
                .ToList(),
            (ctx, ids) => ctx.IssueCases.Where(c => ids.Contains(c.CaseId)).ExecuteDelete(),
            ctx => ctx.IssueCases.Count(c => c.ClosedAt != null && c.ClosedAt < cutoff),
            "已結案的問題案件", maxRows, batchSize);
    }

    private static IssueCase ToModel(IssueCaseRow row) => new()
    {
        CaseId = row.CaseId,
        HostName = row.HostName,
        IssueKey = row.IssueKey,
        IssueLabel = row.IssueLabel,
        Status = row.Status,
        HandlerId = row.HandlerId,
        Note = row.Note,
        DueDate = row.DueDate,
        FirstLinkedDate = row.FirstLinkedDate,
        LastLinkedDate = row.LastLinkedDate,
        ClosedAt = row.ClosedAt,
        CreatedAt = row.CreatedAt,
        CreatedByAccount = row.CreatedByAccount,
        UpdatedAt = row.UpdatedAt
    };
}

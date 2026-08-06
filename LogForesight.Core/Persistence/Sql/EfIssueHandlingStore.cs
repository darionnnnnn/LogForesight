using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IIssueHandlingStore"/> 的真表實作（↔ lf_issue_handling，
/// docs/SCALE-ISSUE-FIRST-PLAN.md P3／根因 B）。
///
/// **取代的是什麼**：原本整份存在 lf_blobs 的一列。實測（規劃 §8.5.1）在 100 萬列時，
/// 「標記一個問題」這個動作要 6.8 秒、配置 2.4 GB；6000 台 × 90 天推得的 324 萬列，
/// 序列化後約 10.5 億字元，正好貼上 C# string 的 2 GB 單一物件上限——那是
/// OutOfMemoryException，不是變慢。改成真表之後單筆標記是一列 UPDATE。
///
/// **介面完全不變**，呼叫端（IssueCaseCoordinator／各 Service）零修改。
///
/// 三個必須維持的既有語意：
///   1. 「缺列即未處理」——空狀態＝刪除該列，不留狀態為空的殭屍列。
///   2. 同一 (主機, 日期, 問題簽章) 至多一列——原本靠 SaveMany 的合併迴圈保證，
///      現在由資料庫的唯一索引保證（更強：繞過 store 也破不了）。
///   3. 主機名比對不分大小寫——以 host_name_key 正規化欄位達成，見 <see cref="HostNameKey"/>。
/// </summary>
public sealed class EfIssueHandlingStore : IIssueHandlingStore
{
    private readonly Func<LfDbContext> _contextFactory;
    private readonly SqlPerformanceMonitor? _performance;

    public EfIssueHandlingStore(Func<LfDbContext> contextFactory, SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _performance = performance;
    }

    public List<IssueHandling> GetForDay(string hostName, DateTime date)
    {
        var key = HostNameKey.Of(hostName);
        var day = date.Date;

        using var ctx = _contextFactory();
        return ctx.IssueHandlings.AsNoTracking()
            .Where(h => h.HostNameKey == key && h.RecordDate == day)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<IssueHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        if (keys.Count == 0) return new List<IssueHandling>();

        var f = from.Date;
        var t = to.Date;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctx = _contextFactory();
        // keys 是 EF 8 的 primitive collection：翻成單一 JSON 參數（OPENJSON／json_each），
        // 不是 N 個參數，主機數再多也不會撞參數上限
        var rows = ctx.IssueHandlings.AsNoTracking()
            .Where(h => keys.Contains(h.HostNameKey) && h.RecordDate >= f && h.RecordDate <= t)
            .ToList();
        _performance?.Record("issue_handling:GetMany", sw.ElapsedMilliseconds);

        return rows.Select(ToModel).ToList();
    }

    public List<IssueHandling> GetByCase(string caseId)
    {
        using var ctx = _contextFactory();
        return ctx.IssueHandlings.AsNoTracking()
            .Where(h => h.CaseId == caseId)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public void Save(IssueHandling handling) => SaveMany(new[] { handling });

    /// <summary>
    /// 批次寫入／更新。原實作是「整份讀→在記憶體逐筆 FirstOrDefault 合併→整份寫回」，
    /// 合併本身是 O(既有列 × 本次列)（規劃 N2，實測 90 列寫進 100 萬列的 blob 要 11.3 秒）。
    /// 這裡先以一次查詢把**本批次涉及的既有列**撈出來建索引，之後每筆合併都是 O(1)。
    /// </summary>
    public void SaveMany(IEnumerable<IssueHandling> handlings)
    {
        var list = handlings.ToList();
        if (list.Count == 0) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctx = _contextFactory();

        // 只撈本批次會碰到的列：以 (主機, 日期範圍) 粗篩後在記憶體精確比對，
        // 比逐筆各查一次少 N 個往返，也不會像舊實作那樣把整份資料讀進來
        var keys = list.Select(h => HostNameKey.Of(h.HostName)).Distinct().ToList();
        var minDate = list.Min(h => h.Date.Date);
        var maxDate = list.Max(h => h.Date.Date);

        var existing = ctx.IssueHandlings
            .Where(h => keys.Contains(h.HostNameKey) && h.RecordDate >= minDate && h.RecordDate <= maxDate)
            .ToList()
            .ToDictionary(h => (h.HostNameKey, h.RecordDate, h.IssueKey));

        foreach (var handling in list)
        {
            var rowKey = (HostNameKey.Of(handling.HostName), handling.Date.Date, handling.IssueKey);
            existing.TryGetValue(rowKey, out var row);

            // 空狀態＝清除標記：不留一列「狀態為空」的殭屍資料，直接回到未處理
            if (string.IsNullOrWhiteSpace(handling.Status))
            {
                if (row != null)
                {
                    ctx.IssueHandlings.Remove(row);
                    existing.Remove(rowKey);
                }
                continue;
            }

            if (row == null)
            {
                row = new IssueHandlingRow
                {
                    HostName = handling.HostName,
                    HostNameKey = rowKey.Item1,
                    RecordDate = handling.Date.Date,
                    IssueKey = handling.IssueKey
                };
                ctx.IssueHandlings.Add(row);
                existing[rowKey] = row;
            }

            Apply(row, handling);
        }

        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 樂觀鎖衝突（D3）：訊息要講出「哪一筆」——批次寫入時逐筆列出沒有意義，
            // 取第一筆衝突的列讓使用者知道從哪裡看起
            throw new ConcurrentUpdateException(DescribeConflict(ex), ex);
        }
        _performance?.Record($"issue_handling:SaveMany({list.Count})", sw.ElapsedMilliseconds);
    }

    /// <summary>衝突訊息的「哪一筆」：主機＋日期。拿不到（理論上不會）就退回泛稱。</summary>
    private static string DescribeConflict(DbUpdateConcurrencyException ex) =>
        ex.Entries.FirstOrDefault()?.Entity is IssueHandlingRow row
            ? $"{row.HostName} {row.RecordDate:yyyy-MM-dd} 的問題處理狀態"
            : "這筆問題處理狀態";

    public void Clear(string hostName, DateTime date, string issueKey)
    {
        var key = HostNameKey.Of(hostName);
        var day = date.Date;

        using var ctx = _contextFactory();
        ctx.IssueHandlings
            .Where(h => h.HostNameKey == key && h.RecordDate == day && h.IssueKey == issueKey)
            .ExecuteDelete();
    }

    private static void Apply(IssueHandlingRow row, IssueHandling handling)
    {
        row.HostName = handling.HostName;   // 顯示用快照跟著更新（改名後以最新一次寫入為準）
        row.Status = handling.Status;
        row.ActorId = handling.ActorId;
        row.ActorAccount = handling.ActorAccount;
        row.Note = handling.Note;
        row.DueDate = handling.DueDate;
        row.CaseId = handling.CaseId;
        row.UpdatedAt = handling.UpdatedAt;
    }

    /// <summary>
    /// 清除超過保留天數的問題處理狀態（docs/SCALE-FIX-PLAN-2026-08-06.md S-4）。
    ///
    /// **與分析紀錄同一個 <c>RetentionDays</c>**：這些列的意義是「這台主機那一天的那個問題
    /// 被標成什麼」，對應的 <c>lf_daily_records</c> 一被清掉，它們就再也沒有任何畫面讀得到——
    /// 是純粹的孤兒，卻繼續佔空間並拖慢 <see cref="GetMany"/> 的範圍查詢。
    ///
    /// **進行中案件的舊日列也一併刪**：看似會弄壞案件，實際不會——案件自己的狀態、處理人、
    /// 說明都在 lf_issue_cases，逐日列只是「那天的那個問題結了沒」，而那天的分析紀錄
    /// 已經不在了。案件本身仍會留著（<c>EfIssueCaseStore.Prune</c> 只刪已結案），
    /// 「還沒處理完」這件事不會因為清理而消失。
    /// </summary>
    public int Prune(int retentionDays) => Prune(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);

        return BatchedPrune.Run<long>(_contextFactory,
            (ctx, take) => ctx.IssueHandlings
                .Where(h => h.RecordDate < cutoff)
                .OrderBy(h => h.RecordDate)
                .Select(h => h.Id)
                .Take(take)
                .ToList(),
            (ctx, ids) => ctx.IssueHandlings.Where(h => ids.Contains(h.Id)).ExecuteDelete(),
            ctx => ctx.IssueHandlings.Count(h => h.RecordDate < cutoff),
            "問題處理狀態", maxRows, batchSize);
    }

    private static IssueHandling ToModel(IssueHandlingRow row) => new()
    {
        HostName = row.HostName,
        Date = row.RecordDate,
        IssueKey = row.IssueKey,
        Status = row.Status,
        ActorId = row.ActorId,
        ActorAccount = row.ActorAccount,
        Note = row.Note,
        DueDate = row.DueDate,
        CaseId = row.CaseId,
        UpdatedAt = row.UpdatedAt
    };
}

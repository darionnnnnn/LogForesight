using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把三份處理狀態自整份 JSON blob 搬進真表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3、
/// 修復規劃 docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三）。
///
/// **這是全案唯一會動到既有資料的一步**，五條約束：
///
/// 1. **中斷後可安全重來**：每一份的「讀 blob → 寫表 → 記錄完成」包在**單一交易**內。
///    被強制中止（服務啟動逾時被 SCM 砍掉）就整份回滾、表仍為空，下次啟動重搬。
///    **刻意不分批 commit**——blob 是無序的整份 JSON，沒有穩定的續跑游標，
///    分批就會留下「搬到一半、但不知道搬到哪」的狀態，那正是原本要修掉的問題。
/// 2. **完成與否是被寫下的事實**（<see cref="HandlingMigrationState"/>），
///    不是從「目標表空不空」反推——後者在中斷後會誤判成「已搬完」而靜默丟資料。
/// 3. **不刪舊 blob**：搬完保留當備份，由遷移狀態擔任它的失效標記。
/// 4. **不靜默丟資料**：解析失敗直接拋，讓遷移標記為失敗並在畫面上看得見，
///    而不是安靜地少了一半處理狀態——後者要好幾天後才會有人發現。
/// 5. **重入保護逐筆比對自然鍵，不是「表裡有資料就整批跳過」**。
///    遷移閘門（<c>MigrationGateMiddleware</c>）只擋得住 HTTP 寫入，而這三張表
///    還有另一個寫入端：<c>AnalysisOrchestrator</c> 把三個真表 store 交給
///    <c>IssueCaseCoordinator</c>，夜間分析每個主機日都會呼叫 <c>AttachNewDay</c> 寫入。
///    只要排程搶在遷移完成前寫進一列，「整批跳過」就會讓整份舊處理狀態永遠不被搬，
///    而且回報一個看起來正常的列數、狀態標成完成——沒有任何錯誤訊息。
///
/// **不在啟動路徑上執行**：由背景服務驅動（見 Web 端 HandlingMigrationHostedService）。
/// 2000 台約 108 萬列／350 MB，同步搬會直接撞上 Windows 服務 30 秒的啟動逾時。
/// </summary>
public sealed class HandlingBlobMigrator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly Func<string, EfJsonBlobStore> _blobFactory;
    private readonly HandlingMigrationStateStore _stateStore;

    public HandlingBlobMigrator(
        Func<LfDbContext> contextFactory,
        Func<string, EfJsonBlobStore> blobFactory,
        HandlingMigrationStateStore stateStore)
    {
        _contextFactory = contextFactory;
        _blobFactory = blobFactory;
        _stateStore = stateStore;
    }

    public HandlingMigrationState State => _stateStore.Get();

    /// <summary>
    /// 啟動時的**輕量**判定（毫秒級，可留在啟動路徑）：決定這個資料庫需不需要遷移。
    ///
    /// 判準刻意用「blob 有內容」而不是「表是空的」——表可能因為上次中斷而有部分資料，
    /// 那正是需要**繼續**搬的情況。實際搬移交給 <see cref="Run"/>。
    /// </summary>
    public void Evaluate()
    {
        var state = _stateStore.Get();
        if (state.State == HandlingMigrationState.Completed) return;

        var needed = HasBlobContent("issue_handling") || HasBlobContent("issue_cases") || HasBlobContent("record_handling");

        _stateStore.Update(s =>
        {
            if (!needed)
            {
                // 全新安裝（或本來就沒有舊資料）：直接標完成，寫入不必被擋
                s.State = HandlingMigrationState.Completed;
                s.IssueHandlingDone = s.IssueCasesDone = s.RecordHandlingDone = true;
                s.CompletedAt = DateTime.Now;
                return;
            }

            if (s.State == HandlingMigrationState.Unknown) s.State = HandlingMigrationState.Pending;
            // 上次卡在 running（行程被砍）→ 退回 pending 重來；未完成的那一份表已被交易回滾成空
            if (s.State == HandlingMigrationState.Running) s.State = HandlingMigrationState.Pending;
        });

        if (needed) Log.Info("[SQL] 偵測到舊格式的處理狀態 blob，將於背景搬移至資料表");
    }

    /// <summary>
    /// 實際搬移（背景執行）。逐份進行、逐份記錄完成；已完成的份不重做。
    /// 失敗時把訊息寫進遷移狀態並重新拋出——呼叫端負責記 log，畫面則從狀態讀得到。
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        var state = _stateStore.Get();
        if (state.State == HandlingMigrationState.Completed) return;

        _stateStore.Update(s =>
        {
            s.State = HandlingMigrationState.Running;
            s.StartedAt ??= DateTime.Now;
            s.LastError = null;
        });

        try
        {
            if (!state.IssueHandlingDone && !cancellationToken.IsCancellationRequested)
            {
                var rows = MigrateOne("issue_handling", MigrateIssueHandlings);
                _stateStore.Update(s => { s.IssueHandlingDone = true; s.IssueHandlingRows = rows; });
            }

            if (!state.IssueCasesDone && !cancellationToken.IsCancellationRequested)
            {
                var rows = MigrateOne("issue_cases", MigrateIssueCases);
                _stateStore.Update(s => { s.IssueCasesDone = true; s.IssueCasesRows = rows; });
            }

            if (!state.RecordHandlingDone && !cancellationToken.IsCancellationRequested)
            {
                var rows = MigrateOne("record_handling", MigrateRecordHandlings);
                _stateStore.Update(s => { s.RecordHandlingDone = true; s.RecordHandlingRows = rows; });
            }
        }
        catch (Exception ex)
        {
            // 失敗要看得見：留在狀態裡，畫面與 /api/health/detail 都讀得到。
            // 狀態退回 pending（不是 completed）——寫入繼續被擋，避免落進半搬完的表
            _stateStore.Update(s =>
            {
                s.State = HandlingMigrationState.Pending;
                s.LastError = ex.Message;
            });
            throw;
        }

        var current = _stateStore.Get();
        if (!current.AllDone) return;   // 被取消（站台關閉）：維持 running/pending，下次啟動接續

        _stateStore.Update(s =>
        {
            s.State = HandlingMigrationState.Completed;
            s.CompletedAt = DateTime.Now;
            s.LastError = null;
        });

        Log.Info("[SQL] 處理狀態遷移完成：issue_handling {A} 列、issue_cases {B} 列、record_handling {C} 列" +
                 "（原 blob 全部保留未刪，僅作備份）",
            current.IssueHandlingRows, current.IssueCasesRows, current.RecordHandlingRows);
    }

    /// <summary>
    /// 單一份的搬移：**整份包在一個交易裡**。中途被砍＝整份回滾，表回到空的狀態，
    /// 下次啟動重搬——這是「中斷可安全重來」的實作核心。
    /// </summary>
    private int MigrateOne(string blobKey, Func<LfDbContext, string?, int> migrate)
    {
        using var ctx = _contextFactory();
        var strategy = ctx.Database.CreateExecutionStrategy();

        return strategy.Execute(() =>
        {
            using var inner = _contextFactory();
            using var tx = inner.Database.BeginTransaction();

            var count = migrate(inner, _blobFactory(blobKey).Read());
            inner.SaveChanges();
            tx.Commit();

            if (count > 0) Log.Info("[SQL] {Key} 已自 blob 遷入資料表：{Count} 列", blobKey, count);
            return count;
        });
    }

    private static int MigrateIssueHandlings(LfDbContext ctx, string? json)
    {
        var items = Deserialize<IssueHandling>(json, "issue_handling");
        if (items.Count == 0) return 0;

        var deduped = items
            .GroupBy(h => (HostNameKey.Of(h.HostName), h.Date.Date, h.IssueKey))
            .Select(g => g.Last())
            .ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] issue_handling 遷移：{Dup} 筆重複鍵已保留最後一筆", items.Count - deduped.Count);

        // 逐筆比對自然鍵，只補表裡沒有的——不能用「表裡有資料就整批跳過」（見類別註解第 5 條）
        var existing = ctx.IssueHandlings.AsNoTracking()
            .Select(r => new { r.HostNameKey, r.RecordDate, r.IssueKey })
            .ToList()
            .Select(r => (r.HostNameKey, r.RecordDate, r.IssueKey))
            .ToHashSet();
        deduped = deduped
            .Where(h => !existing.Contains((HostNameKey.Of(h.HostName), h.Date.Date, h.IssueKey)))
            .ToList();
        if (deduped.Count == 0) return 0;

        ctx.IssueHandlings.AddRange(deduped.Select(h => new IssueHandlingRow
        {
            HostName = h.HostName,
            HostNameKey = HostNameKey.Of(h.HostName),
            RecordDate = h.Date.Date,
            IssueKey = h.IssueKey,
            Status = h.Status,
            ActorId = h.ActorId,
            ActorAccount = h.ActorAccount,
            Note = h.Note,
            DueDate = h.DueDate,
            CaseId = h.CaseId,
            UpdatedAt = h.UpdatedAt
        }));

        return deduped.Count;
    }

    private static int MigrateIssueCases(LfDbContext ctx, string? json)
    {
        var items = Deserialize<IssueCase>(json, "issue_cases");
        if (items.Count == 0) return 0;

        var deduped = items.GroupBy(c => c.CaseId).Select(g => g.Last()).ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] issue_cases 遷移：{Dup} 筆重複 case_id 已保留最後一筆", items.Count - deduped.Count);

        var existing = ctx.IssueCases.AsNoTracking().Select(r => r.CaseId).ToHashSet(StringComparer.Ordinal);
        deduped = deduped.Where(c => !existing.Contains(c.CaseId)).ToList();
        if (deduped.Count == 0) return 0;

        ctx.IssueCases.AddRange(deduped.Select(c => new IssueCaseRow
        {
            CaseId = c.CaseId,
            HostName = c.HostName,
            HostNameKey = HostNameKey.Of(c.HostName),
            IssueKey = c.IssueKey,
            IssueLabel = c.IssueLabel,
            Status = c.Status,
            HandlerId = c.HandlerId,
            Note = c.Note,
            DueDate = c.DueDate,
            FirstLinkedDate = c.FirstLinkedDate,
            LastLinkedDate = c.LastLinkedDate,
            ClosedAt = c.ClosedAt,
            CreatedAt = c.CreatedAt,
            CreatedByAccount = c.CreatedByAccount,
            UpdatedAt = c.UpdatedAt
        }));

        return deduped.Count;
    }

    private static int MigrateRecordHandlings(LfDbContext ctx, string? json)
    {
        var items = Deserialize<RecordHandling>(json, "record_handling");
        if (items.Count == 0) return 0;

        var deduped = items
            .GroupBy(h => (HostNameKey.Of(h.HostName), h.Date.Date))
            .Select(g => g.Last())
            .ToList();
        if (deduped.Count != items.Count)
            Log.Warn("[SQL] record_handling 遷移：{Dup} 筆重複鍵已保留最後一筆", items.Count - deduped.Count);

        var existing = ctx.RecordHandlings.AsNoTracking()
            .Select(r => new { r.HostNameKey, r.RecordDate })
            .ToList()
            .Select(r => (r.HostNameKey, r.RecordDate))
            .ToHashSet();
        deduped = deduped
            .Where(h => !existing.Contains((HostNameKey.Of(h.HostName), h.Date.Date)))
            .ToList();
        if (deduped.Count == 0) return 0;

        ctx.RecordHandlings.AddRange(deduped.Select(h => new RecordHandlingRow
        {
            HostName = h.HostName,
            HostNameKey = HostNameKey.Of(h.HostName),
            RecordDate = h.Date.Date,
            Status = h.Status,
            HandlerId = h.HandlerId,
            DueDate = h.DueDate,
            Note = h.Note,
            UpdatedAt = h.UpdatedAt
        }));

        return deduped.Count;
    }

    private bool HasBlobContent(string key) => !string.IsNullOrWhiteSpace(_blobFactory(key).Read());

    /// <summary>
    /// 解析失敗**不吞**：處理狀態靜默當空會讓整站看起來「所有問題都沒人處理過」，
    /// 比顯性失敗難查得多——與 <see cref="JsonBlobCollection{T}"/> 的既有取捨一致。
    /// </summary>
    private static List<T> Deserialize<T>(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, LfJsonOptions.Pretty) ?? new List<T>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"處理狀態遷移失敗：blob「{key}」無法解析（{ex.Message}）。" +
                "資料未被修改，請確認 lf_blobs 的內容後重新啟動。", ex);
        }
    }
}

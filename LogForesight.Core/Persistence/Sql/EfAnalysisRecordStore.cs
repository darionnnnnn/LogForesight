using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IAnalysisRecordStore"/> ＋ <see cref="IAnalysisRecordQuery"/> 的 SQL 後端實作。
/// 語意由合約測試（SQLite 上跑）強制。
///
/// **關鍵共用規則**（不重寫，直接呼叫 Core 的單點函數，避免規則各處各寫一份）：
///   - 寫入精簡：<see cref="RecordStorageShaper.ForStorage"/>（低風險日砍範例訊息/KeyDetails）
///   - 查詢過濾：<see cref="RecordFilterMatcher.Matches"/>（除 Hosts 外的欄位）
///   - 主機比對：<see cref="HostMatcher"/>（PK 優先、空集合＝零結果的授權語意）
///
/// 每個進出 DB 的操作都記 log（含條件、筆數、耗時），方便在真實環境用 log 診斷。
/// </summary>
public class EfAnalysisRecordStore : IAnalysisRecordStore, IAnalysisRecordQuery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly string _location;

    /// <summary>
    /// 批次寫入端的「本機」識別。非 null 時，批次面讀取
    /// （ReadRecent/HasRecord/HasAnyRecord/LastWeeklyCheckupDate/Prune）只看這台主機自己的紀錄，
    /// 缺日判定與趨勢基準才不會被別台主機汙染。null＝不分主機（Web 查詢面走 Query/GetOne，不經這些方法）。
    /// </summary>
    private readonly HostKey? _ownerHost;

    /// <summary>慢操作統計（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）；測試直接建構時可不傳</summary>
    private readonly SqlPerformanceMonitor? _performance;

    public EfAnalysisRecordStore(
        Func<LfDbContext> contextFactory, string location, HostKey? ownerHost = null,
        SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _location = location;
        _ownerHost = ownerHost;
        _performance = performance;
    }

    public string Location => _location;

    /// <summary>批次面讀取限縮到 owner 的紀錄（owner 為 null＝不分主機）：
    /// id 命中或名稱命中任一即算自己的，名稱比對不分大小寫。
    /// SQL 端 `=` 的大小寫語意依 provider collation 而異（SQLite 預設 BINARY 區分大小寫、
    /// SqlServer 常見 CI 不分），兩邊都以 UPPER() 正規化才逐位一致；主機名為 ASCII，
    /// UPPER 等價 OrdinalIgnoreCase。</summary>
    private IQueryable<DailyRecordRow> OwnedRows(LfDbContext ctx)
    {
        var q = ctx.DailyRecords.AsQueryable();
        if (_ownerHost == null) return q;
        var id = _ownerHost.HostId;
        var name = _ownerHost.HostName.ToUpperInvariant();
        return q.Where(r => (id != 0 && r.HostId == id) || r.HostName.ToUpper() == name);
    }

    // ── 寫入 ─────────────────────────────────────────────────────────────────

    public void Append(DailyAnalysisRecord record)
    {
        var sw = Stopwatch.StartNew();
        var shaped = RecordStorageShaper.ForStorage(record);

        // 主列與問題子列必須同進退——分兩次 SaveChanges 但不包交易時，子列寫入失敗會留下
        // 一筆「沒有問題列」的紀錄，SQL 聚合（IIssueAggregateQuery）會靜默漏算這一天。
        // execution strategy／BeginTransaction 的用法同 EfJsonBlobStore.Mutate。
        using var probe = _contextFactory();
        var strategy = probe.Database.CreateExecutionStrategy();

        strategy.Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();

            var row = new DailyRecordRow
            {
                HostId = shaped.HostId,
                HostName = shaped.Host,
                RecordDate = shaped.Date.Date,
                RiskLevel = shaped.RiskLevel,
                HasCorrelation = shaped.CorrelationAlerts.Count > 0,
                WeeklyCheckupDate = shaped.WeeklyCheckup?.CheckupDate.Date,
                ContentJson = JsonSerializer.Serialize(shaped),
                CreatedAt = DateTime.Now,
                // 讀取面 SQL 化的抽出欄（回饋十九輪批次B）：寫入時一併填好，語意同 lf_top_issues
                // 既有聚合維度的分工。ExtractVersion=1 標記「這是本輪寫入的新列」，
                // 舊列（回填前）維持 0，DailyRecordBackfiller 依此判定候選。
                Headline = shaped.Headline,
                DataIncomplete = shaped.DataIncomplete,
                SecurityLogAvailable = shaped.SecurityLogAvailable,
                ErrorCount = shaped.ErrorCount,
                WarningCount = shaped.WarningCount,
                AiAnalyzed = shaped.AiAnalyzed,
                AiPending = shaped.AiPending,
                ExtractVersion = 1
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();   // 先存主列拿到 RecordId

            foreach (var issue in shaped.TopIssues)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = row.RecordId,
                    SourceName = issue.Source,
                    EventId = issue.EventId,
                    Category = issue.Category.ToString(),
                    SeverityRank = (int)issue.Severity,
                    // 聚合維度（P4）：寫入時一併填好，查詢端直接 GROUP BY，不必查詢期重算——
                    // 同 lf_record_categories「寫入時算好」的既有分工（WEB-SPEC §10.3），
                    // 分析層看不到這張表，批次的分析邏輯零修改
                    HostId = shaped.HostId,
                    RecordDate = shaped.Date.Date,
                    EventCount = issue.Count,
                    ElevatesDayRisk = issue.ElevatesDayRisk,
                    LogName = issue.LogName,
                    EntryType = (int)issue.EntryType,
                    KnownIssue = issue.KnownIssue,
                    EventKey = issue.EventKey
                });
            }
            ctx.SaveChanges();
            tx.Commit();
        });

        // 機房首見日（回饋十九輪批次B）：獨立於主交易之外——這是輔助的呈現用資料，
        // 不該因為它偶發的並發競態（見 UpsertFirstSeen）而讓當天的分析結果整筆遺失。
        UpsertFirstSeen(shaped);

        Log.Info("[SQL] Append 主機 {Host}（id={HostId}）{Date:yyyy-MM-dd} 風險 {Risk}，問題 {Issues} 項，{Ms}ms",
            shaped.Host, shaped.HostId, shaped.Date, shaped.RiskLevel, shaped.TopIssues.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// 機房首見日 insert-if-absent，取較早的日期為準（回饋十九輪批次B）。
    ///
    /// 平行處理多台主機（NetIQ MaxParallelServers）時，兩台主機可能同時第一次寫入同一個
    /// 新問題——insert 可能撞唯一鍵。這裡當一般情況處理（catch 後改走「取較早日期」的
    /// 更新），不是異常：先查是否已存在，不存在才嘗試新增；新增撞鍵或已存在時，
    /// 改成「較早的日期才更新」的條件式 UPDATE，不會被較晚出現的紀錄覆蓋掉更早的首見日。
    /// </summary>
    private void UpsertFirstSeen(DailyAnalysisRecord shaped)
    {
        var date = shaped.Date.Date;
        foreach (var (source, eventId) in shaped.TopIssues.Select(i => (i.Source, i.EventId)).Distinct())
        {
            var sourceKey = source.ToUpperInvariant();
            try
            {
                using var ctx = _contextFactory();
                if (ctx.IssueFirstSeen.Any(f => f.SourceKey == sourceKey && f.EventId == eventId))
                {
                    // 已存在：只有新日期更早才更新，一句 SQL 完成、不必先讀出來比較
                    ctx.Database.ExecuteSqlInterpolated($"""
                        UPDATE lf_issue_first_seen SET first_seen = {date}
                        WHERE source_key = {sourceKey} AND event_id = {eventId} AND first_seen > {date}
                        """);
                    continue;
                }

                ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                    { SourceKey = sourceKey, EventId = eventId, SourceName = source, FirstSeen = date });
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                // 唯一鍵競態：另一台主機的平行寫入搶先建了這一列，補一次「較早才更新」即可，
                // 不是需要中止分析的錯誤
                Log.Debug("[SQL] IssueFirstSeen 新增撞鍵（{Source}/{EventId}），改走較早日期更新：{Msg}",
                    source, eventId, ex.Message);
                try
                {
                    using var ctx = _contextFactory();
                    ctx.Database.ExecuteSqlInterpolated($"""
                        UPDATE lf_issue_first_seen SET first_seen = {date}
                        WHERE source_key = {sourceKey} AND event_id = {eventId} AND first_seen > {date}
                        """);
                }
                catch (Exception retryEx)
                {
                    Log.Warn(retryEx, "[SQL] IssueFirstSeen 補更新仍失敗，這個問題的首見日這次先不更新：{Msg}", retryEx.Message);
                }
            }
        }
    }

    public void AttachWeeklyCheckup(DateTime date, WeeklyCheckupResult checkup)
    {
        using var ctx = _contextFactory();
        var row = OwnedRows(ctx).FirstOrDefault(r => r.RecordDate == date.Date);
        if (row == null)
        {
            // 契約：找不到對應日期安靜略過（呼叫端在分析寫入之後才附掛，理論上必找得到）
            Log.Warn("[SQL] AttachWeeklyCheckup：找不到 {Date:yyyy-MM-dd} 的紀錄，略過", date);
            return;
        }

        var record = Deserialize(row);
        record.WeeklyCheckup = checkup;
        row.ContentJson = JsonSerializer.Serialize(record);
        row.WeeklyCheckupDate = checkup.CheckupDate.Date;
        ctx.SaveChanges();

        Log.Info("[SQL] AttachWeeklyCheckup {Date:yyyy-MM-dd}（結論長度 {Len}）", date, checkup.Conclusion.Length);
    }

    public void AttachAiResult(DateTime date, AiOutcome outcome)
    {
        using var ctx = _contextFactory();
        var row = OwnedRows(ctx).FirstOrDefault(r => r.RecordDate == date.Date);
        if (row == null)
        {
            // 契約同 AttachWeeklyCheckup：找不到對應日期安靜略過（呼叫端在統計段 Append 之後
            // 才附掛，理論上必找得到）
            Log.Warn("[SQL] AttachAiResult：找不到 {Date:yyyy-MM-dd} 的紀錄，略過", date);
            return;
        }

        var record = Deserialize(row);
        record.Headline = outcome.Headline;
        record.Summary = outcome.Summary;
        record.TrendAssessment = outcome.TrendAssessment;
        record.Action = outcome.Action;
        record.RiskLevel = outcome.RiskLevel;
        record.RiskBasis = outcome.RiskBasis;
        record.AiAnalyzed = outcome.AiAnalyzed;
        record.AiPending = false;
        record.ScreenedTailCount = outcome.ScreenedTailCount;
        record.ScreeningNotes = outcome.ScreeningNotes;
        record.ReportFile = outcome.ReportFile;
        record.DeepDives = outcome.DeepDives;
        // 追加而非覆寫（回饋十三輪 C）：統計段寫入的 UncoveredChecks 已經定案，
        // AI 段（含孤兒補跑）只在這裡把自己才知道的新缺口併進去
        if (outcome.UncoveredChecksAddendum is { Count: > 0 })
        {
            record.UncoveredChecks.AddRange(outcome.UncoveredChecksAddendum);
        }
        row.ContentJson = JsonSerializer.Serialize(record);

        // 抽出欄同步（docs/archive/FEEDBACK-12-PLAN.md §3.5）：AI 把風險往上拉（ai_raise）時，
        // 清單／排行／儀表板查詢讀的是這個抽出欄，只改 JSON 內容不改這裡就是欄位漂移
        row.RiskLevel = outcome.RiskLevel;
        ctx.SaveChanges();

        Log.Info("[SQL] AttachAiResult {Date:yyyy-MM-dd}（風險={Risk}, aiAnalyzed={AiAnalyzed}）",
            date, outcome.RiskLevel, outcome.AiAnalyzed);
    }

    /// <summary>
    /// 單次清理的列數上限（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E2）。
    ///
    /// 為什麼要有上限：正常每晚只有一天份過期（2000~6000 列），但**只要停機幾天沒跑、
    /// 或管理者把保留天數調短**，一次要刪的就是數十萬列。無上限時那會變成一筆超長交易——
    /// 交易日誌暴增、鎖住整張表、夜間批次卡在清理階段出不來。
    /// 超過的部分留給下一次執行（清理天生冪等，分幾晚刪完不影響正確性）。
    /// </summary>
    private const int MaxPruneRowsPerRun = 50_000;

    /// <summary>一批刪除的列數：夠大到不會來回太多次，夠小到單筆交易不會過長</summary>
    private const int PruneBatchSize = 2_000;

    public int Prune(int retentionDays) => Prune(retentionDays, MaxPruneRowsPerRun, PruneBatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為（不必真的造五萬列）</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var sw = Stopwatch.StartNew();
        using var ctx = _contextFactory();

        var total = 0;
        while (total < maxRows)
        {
            // **只撈主鍵，不撈實體**：舊寫法是 `.ToList()` 整批載入（含完整 ContentJson，
            // 每列數 KB）再 RemoveRange——刪 50 萬列等於先把數 GB 的 JSON 讀進記憶體。
            // 這裡只取 record_id（8 bytes/列），實際刪除交給 ExecuteDelete 在 DB 端完成。
            var batch = Math.Min(batchSize, maxRows - total);
            var recordIds = OwnedRows(ctx)
                .Where(r => r.RecordDate < cutoff)
                .OrderBy(r => r.RecordDate)
                .Select(r => r.RecordId)
                .Take(batch)
                .ToList();
            if (recordIds.Count == 0) break;

            // **先刪子表再刪主表，不依賴 FK cascade**：cascade 是否真的生效取決於 provider
            // 與連線設定（SQLite 需 PRAGMA foreign_keys=ON），兩個後端不保證一致。
            // 顯式刪除的語意在哪個後端都一樣，也不會留下孤兒的 top_issues 列。
            ctx.TopIssues.Where(t => recordIds.Contains(t.RecordId)).ExecuteDelete();
            total += ctx.DailyRecords.Where(r => recordIds.Contains(r.RecordId)).ExecuteDelete();
        }

        if (total == 0)
        {
            Log.Info("[SQL] Prune（保留 {Days} 天）：無可清除紀錄", retentionDays);
            return 0;
        }

        var remaining = OwnedRows(ctx).Count(r => r.RecordDate < cutoff);
        if (remaining > 0)
        {
            Log.Info("[SQL] Prune（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆、{Ms}ms，" +
                     "另有 {Remaining} 筆超過本次上限 {Max}，留待下次執行",
                retentionDays, cutoff, total, sw.ElapsedMilliseconds, remaining, maxRows);
        }
        else
        {
            Log.Info("[SQL] Prune（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆、{Ms}ms",
                retentionDays, cutoff, total, sw.ElapsedMilliseconds);
        }

        return total;
    }

    /// <summary>回報「若現在執行 Prune，會刪掉幾列」，不做任何異動。</summary>
    public int CountPrunableRecords(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        using var ctx = _contextFactory();
        return OwnedRows(ctx).Count(r => r.RecordDate < cutoff);
    }

    /// <summary>回報「若現在執行 PruneDetails，會清掉幾列詳情」，不做任何異動。
    /// 不受單次上限影響——它回答的是「總共還有多少」，不是「這次會清多少」。</summary>
    public int CountPrunableDetails(int detailRetentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-detailRetentionDays);
        using var ctx = _contextFactory();
        return OwnedRows(ctx).Count(r => r.RecordDate < cutoff && !r.DetailPruned);
    }

    /// <summary>
    /// 清除過期的詳情內容（<c>content_json</c>），**整列保留**——與 <see cref="Prune"/> 的
    /// 整列刪除是兩層不同的保留期：統計層（抽出欄與 lf_top_issues）要留到足以做年度同期比較，
    /// 而詳情只有風險日詳情頁在讀，是整張表的儲存量大宗。兩層分開才不必為了年度比較
    /// 把儲存量整個放大。
    ///
    /// <c>content_json</c> 與 <c>detail_pruned</c> **在同一次 ExecuteUpdate 設定**：
    /// 分兩趟的話中途失敗會留下「內容清了但標記沒設」的列，那列會被下次執行重複處理，
    /// 畫面也無從分辨詳情是被清掉還是本來就沒有。
    /// </summary>
    public int PruneDetails(int detailRetentionDays) => PruneDetails(detailRetentionDays, MaxPruneRowsPerRun, PruneBatchSize);

    internal int PruneDetails(int detailRetentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-detailRetentionDays);
        var sw = Stopwatch.StartNew();
        using var ctx = _contextFactory();

        var total = 0;
        while (total < maxRows)
        {
            var batch = Math.Min(batchSize, maxRows - total);
            var recordIds = OwnedRows(ctx)
                .Where(r => r.RecordDate < cutoff && !r.DetailPruned)
                .OrderBy(r => r.RecordDate)
                .Select(r => r.RecordId)
                .Take(batch)
                .ToList();
            if (recordIds.Count == 0) break;

            var updated = ctx.DailyRecords
                .Where(r => recordIds.Contains(r.RecordId))
                .ExecuteUpdate(s => s
                    .SetProperty(p => p.ContentJson, string.Empty)
                    .SetProperty(p => p.DetailPruned, true));
            total += updated;
        }

        if (total == 0)
        {
            Log.Info("[SQL] PruneDetails（保留 {Days} 天）：無可清除詳情", detailRetentionDays);
            return 0;
        }

        var remaining = OwnedRows(ctx).Count(r => r.RecordDate < cutoff && !r.DetailPruned);
        if (remaining > 0)
        {
            Log.Info("[SQL] PruneDetails（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆、{Ms}ms，" +
                     "另有 {Remaining} 筆超過本次上限 {Max}，留待下次執行",
                detailRetentionDays, cutoff, total, sw.ElapsedMilliseconds, remaining, maxRows);
        }
        else
        {
            Log.Info("[SQL] PruneDetails（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆、{Ms}ms",
                detailRetentionDays, cutoff, total, sw.ElapsedMilliseconds);
        }

        return total;
    }

    // ── 讀取（批次面）─────────────────────────────────────────────────────────

    public List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days)
    {
        // 窗口 [anchor-(days-1), anchor]，含兩端；錨定日之後一律不回傳（趨勢基準不得混入未來）
        var from = anchorDate.Date.AddDays(-(days - 1));
        var to = anchorDate.Date;

        using var ctx = _contextFactory();
        var rows = OwnedRows(ctx)
            .Where(r => r.RecordDate >= from && r.RecordDate <= to)
            .OrderBy(r => r.RecordDate)
            .ToList();

        Log.Debug("[SQL] ReadRecent 錨定 {Anchor:yyyy-MM-dd} 往回 {Days} 天：{Count} 筆", anchorDate, days, rows.Count);
        return rows.Select(Deserialize).ToList();
    }

    public bool HasAnyRecord()
    {
        using var ctx = _contextFactory();
        return OwnedRows(ctx).Any();
    }

    public bool HasRecord(DateTime date)
    {
        using var ctx = _contextFactory();
        return OwnedRows(ctx).Any(r => r.RecordDate == date.Date);
    }

    public DateTime? LastWeeklyCheckupDate()
    {
        using var ctx = _contextFactory();
        var dates = OwnedRows(ctx)
            .Where(r => r.WeeklyCheckupDate != null)
            .Select(r => r.WeeklyCheckupDate)
            .ToList();
        return dates.Count == 0 ? null : dates.Max();
    }

    // ── 查詢（Web 面）─────────────────────────────────────────────────────────

    /// <summary>
    /// 下推 <see cref="RecordQueryFilter"/> 除主機名稱 fallback 外的欄位到 SQL 端——
    /// <see cref="Query"/> 與 <see cref="QueryPage"/> 共用的單點，篩選語意不會因為兩處各自維護
    /// 一份而漂移（docs/archive/HISTORY.md P1-2）。
    ///
    /// Category／MinSeverity／EventId／Source 以 <c>lf_top_issues</c> 的 EXISTS 子查詢下推——
    /// 這張維度表當初就是為 filter-only 設計、索引已建（docs/DB-SPEC.md），此前一直沒有查詢端用到。
    /// </summary>
    private static IQueryable<DailyRecordRow> ApplyPushableFilters(
        LfDbContext ctx, IQueryable<DailyRecordRow> q, RecordQueryFilter filter)
    {
        // 日期／風險層級在 DB 端預篩（高選擇度、便宜）
        if (filter.From.HasValue) { var f = filter.From.Value.Date; q = q.Where(r => r.RecordDate >= f); }
        if (filter.To.HasValue) { var t = filter.To.Value.Date; q = q.Where(r => r.RecordDate <= t); }
        if (filter.RiskLevels is { Count: > 0 })
        {
            var risks = filter.RiskLevels.ToList();
            q = q.Where(r => risks.Contains(r.RiskLevel));
        }

        if (filter.Categories is { Count: > 0 })
        {
            var categoryNames = filter.Categories.Select(c => c.ToString()).ToList();
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && categoryNames.Contains(t.Category)));
        }
        if (filter.MinSeverity.HasValue)
        {
            var minRank = (int)filter.MinSeverity.Value;
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.SeverityRank >= minRank));
        }
        if (filter.EventId.HasValue)
        {
            var eventId = filter.EventId.Value;
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.EventId == eventId));
        }
        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            // 與 OwnedRows 同一個 UPPER() 正規化理由：provider collation 不保證一致，
            // 兩邊都正規化才與 RecordFilterMatcher 的 OrdinalIgnoreCase 逐位一致
            var source = filter.Source.ToUpperInvariant();
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.SourceName.ToUpper() == source));
        }

        // 主機以 id 在 DB 端粗篩（高選擇度、便宜）；host_id=0 的舊列一律先撈出，
        // 名稱比對留給呼叫端在記憶體用 HostMatcher 套用——**不**在 SQL 端比較字串。
        // SQL 端 `=` 的大小寫語意依 provider collation 而異（SQLite 預設 BINARY 區分大小寫、
        // SqlServer 常見 CI 不分），與 HostMatcher 的 OrdinalIgnoreCase 不保證一致；
        // 舊列是有限的存量資料，先撈回比對，才與既有語意逐位一致。
        if (filter.Hosts != null)
        {
            var ids = filter.Hosts.Select(k => k.HostId).Where(id => id != 0).ToList();
            q = q.Where(r => (r.HostId != 0 && ids.Contains(r.HostId)) || r.HostId == 0);
        }

        return q;
    }

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        var sw = Stopwatch.StartNew();
        using var ctx = _contextFactory();

        var q = ApplyPushableFilters(ctx, ctx.DailyRecords, filter);
        var rows = q.ToList();

        // 主機名稱 fallback（HostId=0 的舊列）以共用函數在記憶體套用——
        // 避免 LINQ→SQL 對字串大小寫的細微翻譯差異
        var records = rows.Select(Deserialize);
        if (filter.Hosts != null)
        {
            var matcher = new HostMatcher(filter.Hosts);
            records = records.Where(matcher.Matches);
        }

        var result = records
            // RecordFilterMatcher 在此仍會重新比對已下推的欄位——SQL 端已narrow，
            // 這裡是無成本的防禦性覆核，不是查詢正確性的唯一依據
            .Where(r => RecordFilterMatcher.Matches(r, filter))
            .OrderByDescending(r => r.Date)
            .ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Debug("[SQL] Query（from={From:yyyy-MM-dd} to={To:yyyy-MM-dd} hosts={Hosts} risk={Risk} cat={Cat} event={Event}）→ DB {Rows} 列、篩後 {Result} 筆、{Ms}ms",
            filter.From, filter.To, filter.Hosts?.Count, filter.RiskLevels?.Count, filter.Categories?.Count, filter.EventId, rows.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("records:Query", sw.ElapsedMilliseconds);
        return result;
    }

    public List<DailyAnalysisRecord> QueryLightweight(RecordQueryFilter filter)
    {
        var sw = Stopwatch.StartNew();
        using var ctx = _contextFactory();

        var q = ApplyPushableFilters(ctx, ctx.DailyRecords, filter);
        var rows = q
            .Select(r => new
            {
                r.RecordId, r.HostId, r.HostName, r.RecordDate, r.RiskLevel, r.HasCorrelation,
                r.Headline, r.ErrorCount, r.WarningCount, r.DataIncomplete, r.SecurityLogAvailable,
                r.AiAnalyzed, r.AiPending, r.DetailPruned
            })
            .ToList();

        // 只帶判定/分類需要的欄位（不含 SampleMessages／KeyDetails 等風險日詳情頁專用內容）
        var recordIds = rows.Select(r => r.RecordId).ToHashSet();
        var issuesByRecordId = recordIds.Count == 0
            ? new Dictionary<long, List<LogIssueSignature>>()
            : ctx.TopIssues.AsNoTracking()
                .Where(t => recordIds.Contains(t.RecordId))
                .Select(t => new
                {
                    t.RecordId, t.LogName, t.SourceName, t.EventId, t.Category, t.SeverityRank,
                    t.EntryType, t.EventCount, t.ElevatesDayRisk, t.KnownIssue, t.EventKey
                })
                .ToList()
                .GroupBy(t => t.RecordId)
                .ToDictionary(g => g.Key, g => g.Select(t => new LogIssueSignature
                {
                    LogName = t.LogName,
                    Source = t.SourceName,
                    EventId = t.EventId,
                    EntryType = (System.Diagnostics.EventLogEntryType)t.EntryType,
                    EventKey = t.EventKey,
                    Count = t.EventCount,
                    // 嚴重度刻意不在這裡正規化（Critical=3 原樣帶出）——與 Deserialize() 回傳的
                    // 完整紀錄行為一致，正規化是 RecordRepository.ApplySeverityVisibility 讀取時
                    // 的職責（單一咽喉），這裡另外做一次會漏掉 SiteHidden 那半套規則
                    Severity = (IssueSeverity)t.SeverityRank,
                    Category = Enum.TryParse<IssueCategory>(t.Category, out var cat) ? cat : IssueCategory.Other,
                    ElevatesDayRisk = t.ElevatesDayRisk,
                    KnownIssue = t.KnownIssue
                }).ToList());

        var records = rows.Select(r => new DailyAnalysisRecord
        {
            HostId = r.HostId,
            Host = r.HostName,
            Date = r.RecordDate,
            RiskLevel = r.RiskLevel,
            Headline = r.Headline,
            ErrorCount = r.ErrorCount,
            WarningCount = r.WarningCount,
            DataIncomplete = r.DataIncomplete,
            SecurityLogAvailable = r.SecurityLogAvailable,
            AiAnalyzed = r.AiAnalyzed,
            AiPending = r.AiPending,
            DetailPruned = r.DetailPruned,
            // 輕量列沒有整份關聯訊號內容（那要整份 blob）——呼叫端（RecordListQueryService.ToListItem）
            // 只檢查 Count > 0，用單一佔位元素表達「有」就夠，內容本身不會被讀取
            CorrelationAlerts = r.HasCorrelation ? new List<string> { "(lightweight)" } : new List<string>(),
            TopIssues = issuesByRecordId.TryGetValue(r.RecordId, out var issues) ? issues : new List<LogIssueSignature>()
        });

        // 主機名稱 fallback（HostId=0 的舊列）與 RecordFilterMatcher 防禦性覆核，與 Query() 同一套規則
        if (filter.Hosts != null)
        {
            var matcher = new HostMatcher(filter.Hosts);
            records = records.Where(matcher.Matches);
        }

        var result = records
            .Where(r => RecordFilterMatcher.Matches(r, filter))
            .OrderByDescending(r => r.Date)
            .ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Debug("[SQL] QueryLightweight（from={From:yyyy-MM-dd} to={To:yyyy-MM-dd} hosts={Hosts}）→ DB {Rows} 列、篩後 {Result} 筆、{Ms}ms",
            filter.From, filter.To, filter.Hosts?.Count, rows.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("records:QueryLightweight", sw.ElapsedMilliseconds);
        return result;
    }

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false)
    {
        var sw = Stopwatch.StartNew();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        using var ctx = _contextFactory();

        // HostId=0 的舊列需要在記憶體對名稱做精確比對（ApplyPushableFilters 的 collation 理由），
        // 這個殘餘條件存在時無法安全做 SQL 端真分頁——有這類舊列就整批撈回退回記憶體排序＋分頁，
        // 沒有就直接在 SQL 端 OFFSET/FETCH。這是本方法效能與正確性的分界點，
        // 契約測試（EfAnalysisRecordQueryTests）逐位比對兩條路徑與 Query() 結果一致。
        var hasLegacyHostRows = ctx.DailyRecords.Any(r => r.HostId == 0);
        var q = ApplyPushableFilters(ctx, ctx.DailyRecords, filter);

        if (!hasLegacyHostRows)
        {
            var total = q.Count();

            // 表頭排序（date/host/risk）與預設「風險→關聯→日期」緊急程度排序共用同一個下推點；
            // C# 巢狀三元運算式會被 EF Core 翻成 SQL CASE WHEN——無法呼叫 RiskLevels.Rank 本身
            // （表達式樹不支援任意方法呼叫），只能內嵌，故引用其 const 值而非重新硬寫字面值；
            // RiskRankConsistencyTests 斷言這裡的權重與 RiskLevels.Rank 一致
            IOrderedQueryable<DailyRecordRow> ordered = sortKey switch
            {
                "date" => ascending ? q.OrderBy(r => r.RecordDate) : q.OrderByDescending(r => r.RecordDate),
                "host" => ascending ? q.OrderBy(r => r.HostName) : q.OrderByDescending(r => r.HostName),
                "risk" => ascending
                    ? q.OrderBy(r => r.RiskLevel == RiskLevels.High ? 3 : r.RiskLevel == RiskLevels.Medium ? 2 : r.RiskLevel == RiskLevels.Low ? 1 : 0)
                    : q.OrderByDescending(r => r.RiskLevel == RiskLevels.High ? 3 : r.RiskLevel == RiskLevels.Medium ? 2 : r.RiskLevel == RiskLevels.Low ? 1 : 0),
                _ => q.OrderByDescending(r => r.RiskLevel == RiskLevels.High ? 3 : r.RiskLevel == RiskLevels.Medium ? 2 : r.RiskLevel == RiskLevels.Low ? 1 : 0)
                    .ThenByDescending(r => r.HasCorrelation)
                    .ThenByDescending(r => r.RecordDate)
            };

            var items = ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList()
                .Select(Deserialize)
                .ToList();

            Log.Debug("[SQL] QueryPage（全下推，SQL 端分頁，page={Page} size={Size} sort={Sort}）→ {Total} 筆符合、本頁 {Items} 筆、{Ms}ms",
                page, pageSize, sortKey ?? "(預設)", total, items.Count, sw.ElapsedMilliseconds);
            _performance?.Record("records:QueryPage", sw.ElapsedMilliseconds);

            return new PagedResult<DailyAnalysisRecord> { Items = items, Page = page, PageSize = pageSize, Total = total };
        }

        // 退回：SQL 已過濾掉大部分不相關的列（含 category/severity/eventId/source），
        // 但仍需在記憶體對 HostId=0 的舊列做精確名稱比對，因此無法在 SQL 端分頁；
        // 正確性優先，退化的只有效能（見類別註解）
        var rows = q.ToList();
        var records = rows.Select(Deserialize).AsEnumerable();
        if (filter.Hosts != null)
        {
            var matcher = new HostMatcher(filter.Hosts);
            records = records.Where(matcher.Matches);
        }

        var matched = records.Where(r => RecordFilterMatcher.Matches(r, filter));
        var ordered2 = sortKey switch
        {
            "date" => ascending ? matched.OrderBy(r => r.Date) : matched.OrderByDescending(r => r.Date),
            "host" => ascending
                ? matched.OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
                : matched.OrderByDescending(r => r.Host, StringComparer.OrdinalIgnoreCase),
            "risk" => ascending
                ? matched.OrderBy(r => RiskLevels.Rank(r.RiskLevel))
                : matched.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel)),
            _ => matched.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel))
                .ThenByDescending(r => r.CorrelationAlerts.Count > 0)
                .ThenByDescending(r => r.Date)
        };
        var finalOrdered = ordered2.ToList();

        Log.Debug("[SQL] QueryPage（含 HostId=0 舊列，退回全窗撈回 {Rows} 列後於記憶體排序＋分頁，sort={Sort}）→ {Total} 筆符合、{Ms}ms",
            rows.Count, sortKey ?? "(預設)", finalOrdered.Count, sw.ElapsedMilliseconds);
        // 退回路徑（N5）獨立命名：慢的時候要一眼看出是不是踩到了 HostId=0 舊列的全窗撈回
        _performance?.Record("records:QueryPage(legacy-fallback)", sw.ElapsedMilliseconds);

        return new PagedResult<DailyAnalysisRecord>
        {
            Items = finalOrdered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = finalOrdered.Count
        };
    }

    public HashSet<(long HostId, DateTime Date)> ListHostDates(DateTime from, DateTime to)
    {
        var f = from.Date;
        var t = to.Date;

        using var ctx = _contextFactory();
        return ctx.DailyRecords
            .Where(r => r.RecordDate >= f && r.RecordDate <= t && r.HostId != 0)
            .Select(r => new { r.HostId, r.RecordDate })
            .ToList()
            .Select(x => (x.HostId, x.RecordDate))
            .ToHashSet();
    }

    public DailyAnalysisRecord? GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date)
    {
        if (hosts.Count == 0) return null;

        using var ctx = _contextFactory();
        var sameDay = ctx.DailyRecords
            .Where(r => r.RecordDate == date.Date)
            .ToList()
            .Select(Deserialize)
            .ToList();
        if (sameDay.Count == 0) return null;

        // 依傳入順序擇一（存活主機排前面，見 HostIdentityResolver.Expand）
        foreach (var host in hosts)
        {
            var matcher = new HostMatcher(new[] { host });
            var match = sameDay.FirstOrDefault(matcher.Matches);
            if (match != null) return match;
        }
        return null;
    }

    /// <summary>
    /// 預設 JsonSerializer，round-trip 保真。
    /// 空字串是 PruneDetails 的產物，Deserialize 對它會丟 JsonException，
    /// 不是回 null，所以必須在呼叫前就攔下。
    /// </summary>
    private static DailyAnalysisRecord Deserialize(DailyRecordRow row)
    {
        if (row.DetailPruned || string.IsNullOrWhiteSpace(row.ContentJson))
        {
            return new DailyAnalysisRecord
            {
                HostId = row.HostId,
                Host = row.HostName,
                Date = row.RecordDate,
                RiskLevel = row.RiskLevel,
                DetailPruned = true
            };
        }

        var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson) ?? new DailyAnalysisRecord();
        record.DetailPruned = row.DetailPruned;
        return record;
    }
}

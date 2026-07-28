using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Sql;

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

    public EfAnalysisRecordStore(Func<LfDbContext> contextFactory, string location, HostKey? ownerHost = null)
    {
        _contextFactory = contextFactory;
        _location = location;
        _ownerHost = ownerHost;
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

        using var ctx = _contextFactory();
        var row = new DailyRecordRow
        {
            HostId = shaped.HostId,
            HostName = shaped.Host,
            RecordDate = shaped.Date.Date,
            RiskLevel = shaped.RiskLevel,
            HasCorrelation = shaped.CorrelationAlerts.Count > 0,
            WeeklyCheckupDate = shaped.WeeklyCheckup?.CheckupDate.Date,
            ContentJson = JsonSerializer.Serialize(shaped),
            CreatedAt = DateTime.Now
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
                SeverityRank = (int)issue.Severity
            });
        }
        ctx.SaveChanges();

        Log.Info("[SQL] Append 主機 {Host}（id={HostId}）{Date:yyyy-MM-dd} 風險 {Risk}，問題 {Issues} 項，{Ms}ms",
            shaped.Host, shaped.HostId, shaped.Date, shaped.RiskLevel, shaped.TopIssues.Count, sw.ElapsedMilliseconds);
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

    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        using var ctx = _contextFactory();
        var stale = OwnedRows(ctx).Where(r => r.RecordDate < cutoff).ToList();
        if (stale.Count == 0)
        {
            Log.Info("[SQL] Prune（保留 {Days} 天）：無可清除紀錄", retentionDays);
            return 0;
        }

        ctx.DailyRecords.RemoveRange(stale);   // top_issues 由 FK cascade 一併刪除
        ctx.SaveChanges();

        Log.Info("[SQL] Prune（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆", retentionDays, cutoff, stale.Count);
        return stale.Count;
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
    /// 一份而漂移（docs/OPS-HARDENING-PLAN.md P1-2）。
    ///
    /// Category／MinSeverity／EventId／Source 以 <c>lf_top_issues</c> 的 EXISTS 子查詢下推——
    /// 這張維度表當初就是為 filter-only 設計、索引已建（docs/DB-PLAN.md），此前一直沒有查詢端用到。
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
        return result;
    }

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize)
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

            var items = q
                // 與記憶體排序（RiskLevels.Rank／CorrelationAlerts.Count>0／Date）語意一致的 SQL 版本；
                // C# 巢狀三元運算式會被 EF Core 翻成 SQL CASE WHEN——無法呼叫 RiskLevels.Rank 本身
                // （表達式樹不支援任意方法呼叫），只能內嵌，故引用其 const 值而非重新硬寫字面值；
                // RiskRankConsistencyTests 斷言這裡的權重與 RiskLevels.Rank 一致
                .OrderByDescending(r => r.RiskLevel == RiskLevels.High ? 3 : r.RiskLevel == RiskLevels.Medium ? 2 : r.RiskLevel == RiskLevels.Low ? 1 : 0)
                .ThenByDescending(r => r.HasCorrelation)
                .ThenByDescending(r => r.RecordDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList()
                .Select(Deserialize)
                .ToList();

            Log.Debug("[SQL] QueryPage（全下推，SQL 端分頁，page={Page} size={Size}）→ {Total} 筆符合、本頁 {Items} 筆、{Ms}ms",
                page, pageSize, total, items.Count, sw.ElapsedMilliseconds);

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

        var ordered = records
            .Where(r => RecordFilterMatcher.Matches(r, filter))
            .OrderByDescending(r => RiskLevels.Rank(r.RiskLevel))
            .ThenByDescending(r => r.CorrelationAlerts.Count > 0)
            .ThenByDescending(r => r.Date)
            .ToList();

        Log.Debug("[SQL] QueryPage（含 HostId=0 舊列，退回全窗撈回 {Rows} 列後於記憶體排序＋分頁）→ {Total} 筆符合、{Ms}ms",
            rows.Count, ordered.Count, sw.ElapsedMilliseconds);

        return new PagedResult<DailyAnalysisRecord>
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = ordered.Count
        };
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

    /// <summary>預設 JsonSerializer，round-trip 保真</summary>
    private static DailyAnalysisRecord Deserialize(DailyRecordRow row) =>
        JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson) ?? new DailyAnalysisRecord();
}

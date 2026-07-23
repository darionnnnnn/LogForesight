using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Sql;

/// <summary>
/// <see cref="IAnalysisRecordStore"/> ＋ <see cref="IAnalysisRecordQuery"/> 的 SQL 後端實作。
/// 與 JSONL 的語意逐位一致由合約測試（SQLite 上跑同一組案例）強制。
///
/// **關鍵共用規則**（不重寫，直接呼叫 Core 的單點函數，保證兩後端一致）：
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
    /// 批次寫入端的「本機」識別（同 JsonlAnalysisRecordStore.ownerHost）。非 null 時，批次面讀取
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

    /// <summary>批次面讀取限縮到 owner 的紀錄（owner 為 null＝不分主機）。與
    /// JsonlAnalysisRecordStore.BelongsToOwner 同語意：id 命中或名稱命中任一即算自己的。</summary>
    private IQueryable<DailyRecordRow> OwnedRows(LfDbContext ctx)
    {
        var q = ctx.DailyRecords.AsQueryable();
        if (_ownerHost == null) return q;
        var id = _ownerHost.HostId;
        var name = _ownerHost.HostName;
        return q.Where(r => (id != 0 && r.HostId == id) || r.HostName == name);
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

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        var sw = Stopwatch.StartNew();
        using var ctx = _contextFactory();

        IQueryable<DailyRecordRow> q = ctx.DailyRecords;

        // 日期在 DB 端預篩（高選擇度、便宜）
        if (filter.From.HasValue) { var f = filter.From.Value.Date; q = q.Where(r => r.RecordDate >= f); }
        if (filter.To.HasValue) { var t = filter.To.Value.Date; q = q.Where(r => r.RecordDate <= t); }
        if (filter.RiskLevels is { Count: > 0 })
        {
            var risks = filter.RiskLevels.ToList();
            q = q.Where(r => risks.Contains(r.RiskLevel));
        }

        // 主機在 DB 端預篩，複製 HostMatcher 的 PK 優先語意：
        // 有 host_id 的列只認 id；host_id=0 的舊列才認名稱。**空集合＝零結果**（授權語意）。
        if (filter.Hosts != null)
        {
            var ids = filter.Hosts.Select(k => k.HostId).Where(id => id != 0).ToList();
            var names = filter.Hosts.Select(k => k.HostName).ToList();
            q = q.Where(r => (r.HostId != 0 && ids.Contains(r.HostId)) ||
                             (r.HostId == 0 && names.Contains(r.HostName)));
        }

        var rows = q.ToList();

        // 其餘欄位（category/eventId/source/severity）以共用函數在記憶體套用——
        // 與 JSONL 逐位一致，且避免 LINQ→SQL 對 JSON 內容的細微翻譯差異
        var result = rows
            .Select(Deserialize)
            .Where(r => RecordFilterMatcher.Matches(r, filter))
            .OrderByDescending(r => r.Date)
            .ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Debug("[SQL] Query（from={From:yyyy-MM-dd} to={To:yyyy-MM-dd} hosts={Hosts} risk={Risk} cat={Cat} event={Event}）→ DB {Rows} 列、篩後 {Result} 筆、{Ms}ms",
            filter.From, filter.To, filter.Hosts?.Count, filter.RiskLevels?.Count, filter.Categories?.Count, filter.EventId, rows.Count, result.Count, sw.ElapsedMilliseconds);
        return result;
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

    /// <summary>反序列化與 JSONL 用同一套（預設 JsonSerializer），round-trip 保真且逐位一致</summary>
    private static DailyAnalysisRecord Deserialize(DailyRecordRow row) =>
        JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson) ?? new DailyAnalysisRecord();
}

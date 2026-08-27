using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 報告全文的資料庫後端（`lf_reports`）：同時實作寫入端 <see cref="IReportSink"/> 與
/// 讀取端 <see cref="IReportReader"/>。
///
/// **為什麼一個類別實作兩個介面**：兩個介面存在的理由是「批次只寫、Web 只讀」的依賴切分
/// （ISP，見 <see cref="IReportReader"/>），那是**呼叫端**的切分；儲存實作這一側兩者是同一張表、
/// 同一組欄位對應，拆成兩個類別只會讓 upsert 鍵與讀取鍵各寫一次而有機會漂移。
///
/// **寫入回傳 report_id、讀取走自然鍵**：<c>DailyAnalysisRecord.ReportFile</c> 存的是
/// 寫入回傳的參照，但它的用途只剩「這天有沒有報告」的旗標——實際讀取一律以
/// 主機×日期×種類反查（見 <see cref="IReportReader"/>），三種報告同一條路徑，
/// 升級前留下的檔案路徑參照也因此不需要回頭改寫。
/// </summary>
public sealed class EfReportStore : IReportSink, IReportReader, IReportUsageQuery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;

    public EfReportStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public Task<string> WriteAsync(ReportKind kind, HostKey host, string fileName, string content, ReportMeta? meta = null)
        => Task.FromResult(Write(ReportKinds.Of(kind), host, fileName, content, meta, DateTime.Now));

    /// <summary>
    /// 同步寫入（遷移器與測試用；<see cref="WriteAsync"/> 是它的非同步門面）。
    /// </summary>
    /// <param name="createdAt">產生時間。遷移既有檔案時要能帶入原始時間，否則整批舊報告的
    /// 保留期會從遷移當天重新起算，等於把早該清掉的資料留成新的</param>
    /// <returns>report_id 的字串形式</returns>
    internal string Write(string kind, HostKey host, string fileName, string content, ReportMeta? meta, DateTime createdAt)
    {
        var reportDate = ParseReportDate(fileName) ?? createdAt.Date;
        using var ctx = _contextFactory();

        // upsert：同一主機同一天同一種報告只留一份。重新分析同一天要就地取代，
        // 不是留兩份讓使用者猜哪份是現行的。
        //
        // 查詢條件**必須與 Read 同一套 HostIdentity 語意**，不能只認四欄完全相等：
        // 主機在未登記時寫過報告（列上 host_id=0）、事後登記成功拿到 PK 再重跑同一天，
        // 完全相等的查詢會找不到 0 列而新增第二列——同一主機日兩列並存，Read 讀到哪列
        // 變成不確定。改為認領：命中 0 列時把 host_id/host_name 一併升級成現在的識別。
        var row = ctx.Reports.FirstOrDefault(r =>
            r.Kind == kind && r.ReportDate == reportDate &&
            ((host.HostId != 0 && r.HostId == host.HostId) || (r.HostId == 0 && r.HostName == host.HostName)));

        if (row == null)
        {
            row = new ReportRow { ReportDate = reportDate, Kind = kind };
            ctx.Reports.Add(row);
        }

        row.HostId = host.HostId;
        row.HostName = host.HostName;

        row.RiskLevel = meta?.RiskLevel;
        row.Categories = meta?.Categories;
        row.FileName = fileName;
        row.Content = content;
        row.CreatedAt = createdAt;

        ctx.SaveChanges();

        Log.Info("[SQL] 報告已寫入：{Kind} {Host} {Date:yyyy-MM-dd}（{Len} 字元，report_id={Id}）",
            kind, host.HostName, reportDate, content.Length, row.ReportId);
        return row.ReportId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 依主機×日期×種類讀取報告全文。比對規則與全站的 <c>HostIdentity</c> 一致：
    /// 先比 host_id，寫入當時主機還沒登記成功（列上是 0）的才退回名稱比對——
    /// 這樣主機事後登記成功也還讀得到當初以 0 寫下的報告。
    ///
    /// <c>host.HostId != 0</c> 這道前置條件不能省：查詢端自己也可能是未登記主機（id 為 0），
    /// 少了它就會變成「r.HostId == 0」而命中**所有**未登記主機的報告，讀到別台的內容。
    /// </summary>
    public ReportContent? Read(HostKey host, DateTime date, string kind)
    {
        using var ctx = _contextFactory();
        var day = date.Date;
        // OrderByDescending：命中集合只可能是「id 相符列」∪「0 列」，非 0 者（=id 相符）優先——
        // Write 的認領邏輯理論上不會留下兩列並存，這是對意外狀態的防禦，讓讀到哪列有確定答案
        var row = ctx.Reports.AsNoTracking()
            .Where(r => r.Kind == kind && r.ReportDate == day &&
                ((host.HostId != 0 && r.HostId == host.HostId) || (r.HostId == 0 && r.HostName == host.HostName)))
            .OrderByDescending(r => r.HostId)
            .FirstOrDefault();

        return row == null
            ? null
            : new ReportContent(row.ReportId, row.Kind, row.ReportDate, row.RiskLevel, row.Categories,
                row.FileName, row.Content);
    }

    public bool Exists(HostKey host, DateTime date, string kind)
    {
        using var ctx = _contextFactory();
        var day = date.Date;
        return ctx.Reports.AsNoTracking().Any(r =>
            r.Kind == kind && r.ReportDate == day &&
            ((host.HostId != 0 && r.HostId == host.HostId) || (r.HostId == 0 && r.HostName == host.HostName)));
    }

    /// <summary>目前存量：份數與內容總字元數（設定頁的空間告知用）</summary>
    public (int Count, long TotalChars) Usage()
    {
        using var ctx = _contextFactory();
        // 兩個聚合一次查完：分兩次查之間可能有夜間分析寫入，會湊出「份數與大小對不起來」的畫面。
        // Sum 在空表上回 null，投影成 long? 再取代預設值（SQLite/SqlServer 語意相同）。
        var stat = ctx.Reports.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), TotalChars = (long?)g.Sum(r => (long)r.Content.Length) })
            .FirstOrDefault();
        return stat == null ? (0, 0L) : (stat.Count, stat.TotalChars ?? 0L);
    }

    /// <summary>
    /// 保留期清理：依 <c>created_at</c>（不是 <c>report_date</c>）。
    /// 重跑 100 天前的主機日時，依 report_date 清理會讓剛補出來的報告立刻消失——
    /// 理由同 <c>lf_permission_changes</c>。
    /// </summary>
    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        return BatchedPrune.Run<long>(
            _contextFactory,
            (ctx, take) => ctx.Reports.Where(r => r.CreatedAt < cutoff)
                .OrderBy(r => r.ReportId).Select(r => r.ReportId).Take(take).ToList(),
            (ctx, keys) => ctx.Reports.Where(r => keys.Contains(r.ReportId)).ExecuteDelete(),
            ctx => ctx.Reports.Count(r => r.CreatedAt < cutoff),
            "報告全文");
    }

    /// <summary>
    /// 檔名開頭的 yyyy-MM-dd 即報告所屬日期（三種報告的檔名格式共同的前 10 個字元）。
    /// 解析不出來時由呼叫端退回產生日——那只會發生在檔名格式被改掉的情況，
    /// 寧可日期稍有偏差也不要讓報告寫不進去。
    /// </summary>
    internal static DateTime? ParseReportDate(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length < 10) return null;
        return DateTime.TryParseExact(fileName[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
    }
}

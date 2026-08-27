using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報告全文的資料庫後端（<c>lf_reports</c>）：upsert 語意、主機隔離、自然鍵讀取、保留期清理。
/// </summary>
public sealed class ReportStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfReportStore _store;

    private static readonly DateTime Day = new(2026, 8, 27);

    public ReportStoreTests() => _store = new EfReportStore(_fx.NewContext);

    public void Dispose() => _fx.Dispose();

    private static HostKey Host(long id, string name) => new() { HostId = id, HostName = name };

    private Task<string> WriteAsync(HostKey host, string fileName, string content,
        ReportKind kind = ReportKind.DailyRisk, ReportMeta? meta = null) =>
        _store.WriteAsync(kind, host, fileName, content, meta);

    [Fact]
    public async Task 寫入後以自然鍵讀回_內容與欄位一致()
    {
        var host = Host(7, "SRV01");
        await WriteAsync(host, "2026-08-27_高風險_儲存裝置+安全.txt", "報告內容",
            meta: new ReportMeta("高", "儲存裝置+安全"));

        var read = _store.Read(host, Day, ReportKinds.DailyRisk);

        Assert.NotNull(read);
        Assert.Equal("報告內容", read!.Content);
        Assert.Equal("高", read.RiskLevel);
        Assert.Equal("儲存裝置+安全", read.Categories);
        Assert.Equal("2026-08-27_高風險_儲存裝置+安全.txt", read.FileName);
        Assert.Equal(Day, read.ReportDate);
    }

    /// <summary>
    /// 報告所屬日期取自檔名前綴，不是寫入當下的日期——回補與重新分析都是「今天寫昨天的報告」。
    /// </summary>
    [Fact]
    public async Task 報告日期取自檔名前綴_不是寫入當日()
    {
        var host = Host(7, "SRV01");
        await WriteAsync(host, "2026-01-05_中風險_服務.txt", "內容");

        Assert.NotNull(_store.Read(host, new DateTime(2026, 1, 5), ReportKinds.DailyRisk));
        Assert.Null(_store.Read(host, DateTime.Today, ReportKinds.DailyRisk));
    }

    /// <summary>
    /// 重新分析同一天要就地取代，不是留兩份讓使用者猜哪份是現行的。
    /// 類別改變會讓檔名跟著變，所以連檔名一起換。
    /// </summary>
    [Fact]
    public async Task 同一主機日重寫_就地取代不新增列()
    {
        var host = Host(7, "SRV01");
        await WriteAsync(host, "2026-08-27_高風險_儲存裝置.txt", "舊內容", meta: new ReportMeta("高", "儲存裝置"));
        await WriteAsync(host, "2026-08-27_中風險_服務.txt", "新內容", meta: new ReportMeta("中", "服務"));

        using var ctx = _fx.NewContext();
        Assert.Equal(1, ctx.Reports.Count());

        var read = _store.Read(host, Day, ReportKinds.DailyRisk);
        Assert.Equal("新內容", read!.Content);
        Assert.Equal("中", read.RiskLevel);
        Assert.Equal("2026-08-27_中風險_服務.txt", read.FileName);
    }

    /// <summary>
    /// **本輪修掉的檔案時代 bug 的迴歸鎖**：升級前所有報告都寫在同一個 export 目錄、
    /// 檔名只由「日期＋風險等級＋類別」組成，兩台主機同日同風險同類別會產生完全相同的檔名
    /// 而互相覆蓋——A 主機的紀錄點開會看到 B 主機的報告。以主機日為鍵之後兩份並存。
    /// </summary>
    [Fact]
    public async Task 兩台主機同日同風險同類別_各自並存不互相覆蓋()
    {
        var a = Host(1, "SRV-A");
        var b = Host(2, "SRV-B");
        const string sameFileName = "2026-08-27_高風險_儲存裝置+安全.txt";

        await WriteAsync(a, sameFileName, "A 主機的報告", meta: new ReportMeta("高", "儲存裝置+安全"));
        await WriteAsync(b, sameFileName, "B 主機的報告", meta: new ReportMeta("高", "儲存裝置+安全"));

        Assert.Equal("A 主機的報告", _store.Read(a, Day, ReportKinds.DailyRisk)!.Content);
        Assert.Equal("B 主機的報告", _store.Read(b, Day, ReportKinds.DailyRisk)!.Content);
    }

    /// <summary>
    /// host_id = 0 是「主機尚未登記成功」的哨兵值而不是一台主機：兩台都還沒登記的主機
    /// 在同一天不得互相覆蓋（唯一鍵含 host_name 的理由）。
    /// </summary>
    [Fact]
    public async Task 兩台皆未登記的主機_以名稱區分不互相覆蓋()
    {
        var a = Host(0, "SRV-A");
        var b = Host(0, "SRV-B");

        await WriteAsync(a, "2026-08-27_高風險.txt", "A");
        await WriteAsync(b, "2026-08-27_高風險.txt", "B");

        Assert.Equal("A", _store.Read(a, Day, ReportKinds.DailyRisk)!.Content);
        Assert.Equal("B", _store.Read(b, Day, ReportKinds.DailyRisk)!.Content);
    }

    /// <summary>
    /// 寫入當時主機還沒登記成功（列上 host_id = 0），事後登記取得 PK——
    /// 名稱相符仍讀得回來，語意同全站的 HostIdentity fallback。
    /// </summary>
    [Fact]
    public async Task 寫入時未登記_事後取得主機PK仍讀得回來()
    {
        await WriteAsync(Host(0, "SRV01"), "2026-08-27_高風險.txt", "內容");

        Assert.Equal("內容", _store.Read(Host(42, "SRV01"), Day, ReportKinds.DailyRisk)!.Content);
    }

    /// <summary>
    /// 體檢輪抓到的 bug 的迴歸鎖：主機未登記時寫過報告（列上 host_id=0），事後登記成功
    /// 拿到 PK 再重跑同一天——Write 的 upsert 查詢若只認四欄完全相等，會找不到 0 列而
    /// 新增第二列，同一主機日兩列並存、Read 讀到哪列變成不確定。
    /// 正確行為是**認領**：命中 0 列時把主機識別一併升級，表中始終只有一列。
    /// </summary>
    [Fact]
    public async Task 未登記時寫過報告_登記後重跑同一天_認領原列不新增()
    {
        await WriteAsync(Host(0, "SRV01"), "2026-08-27_高風險.txt", "登記前的報告");
        await WriteAsync(Host(42, "SRV01"), "2026-08-27_中風險.txt", "登記後重跑的報告");

        using var ctx = _fx.NewContext();
        var row = Assert.Single(ctx.Reports);
        Assert.Equal(42, row.HostId);

        Assert.Equal("登記後重跑的報告", _store.Read(Host(42, "SRV01"), Day, ReportKinds.DailyRisk)!.Content);
    }

    [Fact]
    public async Task 三種報告種類同主機同日_彼此獨立()
    {
        var host = Host(7, "SRV01");
        await WriteAsync(host, "2026-08-27_高風險.txt", "風險", ReportKind.DailyRisk);
        await WriteAsync(host, "2026-08-27_週檢.txt", "體檢", ReportKind.WeeklyCheckup);
        await WriteAsync(host, "2026-08-27_權限異動.txt", "權限", ReportKind.Permission);

        Assert.Equal("風險", _store.Read(host, Day, ReportKinds.DailyRisk)!.Content);
        Assert.Equal("體檢", _store.Read(host, Day, ReportKinds.WeeklyCheckup)!.Content);
        Assert.Equal("權限", _store.Read(host, Day, ReportKinds.Permission)!.Content);
    }

    [Fact]
    public void 查無報告_Read回null且Exists為false()
    {
        Assert.Null(_store.Read(Host(7, "SRV01"), Day, ReportKinds.DailyRisk));
        Assert.False(_store.Exists(Host(7, "SRV01"), Day, ReportKinds.DailyRisk));
    }

    [Fact]
    public async Task Exists_只看有無不撈全文()
    {
        var host = Host(7, "SRV01");
        await WriteAsync(host, "2026-08-27_高風險.txt", new string('X', 50_000));

        Assert.True(_store.Exists(host, Day, ReportKinds.DailyRisk));
        Assert.False(_store.Exists(host, Day, ReportKinds.WeeklyCheckup));
    }

    [Fact]
    public async Task Usage_回報份數與內容總長度()
    {
        Assert.Equal((0, 0L), _store.Usage());

        await WriteAsync(Host(1, "A"), "2026-08-27_高風險.txt", new string('X', 100));
        await WriteAsync(Host(2, "B"), "2026-08-27_高風險.txt", new string('X', 250));

        Assert.Equal((2, 350L), _store.Usage());
    }

    // ── 保留期清理（依 created_at，不是 report_date）─────────────────────────

    private void SeedWithCreatedAt(HostKey host, DateTime reportDate, DateTime createdAt)
    {
        _store.Write(ReportKinds.DailyRisk, host, $"{reportDate:yyyy-MM-dd}_高風險.txt", "內容", null, createdAt);
    }

    [Fact]
    public void Prune_邊界日不刪_超過一天才刪()
    {
        var today = DateTime.Today;
        SeedWithCreatedAt(Host(1, "A"), today.AddDays(-90), today.AddDays(-90));   // 剛好等於保留天數
        SeedWithCreatedAt(Host(2, "B"), today.AddDays(-91), today.AddDays(-91));   // 超過一天

        Assert.Equal(1, _store.Prune(90));

        using var ctx = _fx.NewContext();
        Assert.Equal(1, ctx.Reports.Count());
        Assert.Equal("A", ctx.Reports.Single().HostName);
    }

    /// <summary>
    /// 清理依 created_at 而不是 report_date：重跑 100 天前的主機日時，
    /// 依 report_date 清理會讓剛補出來的報告立刻消失。
    /// </summary>
    [Fact]
    public void Prune_剛補寫的舊日期報告_不被清掉()
    {
        SeedWithCreatedAt(Host(1, "A"), DateTime.Today.AddDays(-300), DateTime.Now);

        Assert.Equal(0, _store.Prune(90));
        using var ctx = _fx.NewContext();
        Assert.Equal(1, ctx.Reports.Count());
    }

    [Fact]
    public void Prune_空表_回0不拋例外() => Assert.Equal(0, _store.Prune(90));
}

/// <summary>
/// <c>lf_reports</c> 的 schema 契約。<c>CreateTableIfMissing</c> 與 <c>EnsureCreated</c> 是兩條
/// 各自獨立的建表路徑，欄位漂移不會有任何測試自己失敗——把欄位清單寫成斷言，規格即測試。
/// </summary>
public sealed class ReportSchemaContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void lf_reports_欄位清單與規格一致()
    {
        using var ctx = _fx.NewContext();
        var columns = ReadColumns(ctx, "lf_reports");

        Assert.Equal(new[]
        {
            "categories", "content", "created_at", "file_name", "host_id", "host_name",
            "kind", "report_date", "report_id", "risk_level"
        }, columns.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 既有部署走的是 SchemaUpgrader 的冪等 DDL（EnsureCreated 對既有 DB 什麼都不做），
    /// 與新 DB 的 EnsureCreated 是兩條路徑——兩邊建出來的欄位必須一致，否則升級後的站台
    /// 會在讀寫時才炸，而且每個測試都是綠的。
    /// </summary>
    [Fact]
    public void 升級路徑建出的欄位_與新建DB一致且可重複執行()
    {
        using var ctx = _fx.NewContext();
        var fromEnsureCreated = ReadColumns(ctx, "lf_reports").OrderBy(c => c, StringComparer.Ordinal).ToList();

        ctx.Database.ExecuteSqlRaw("DROP TABLE lf_reports");
        SchemaUpgrader.Upgrade(ctx);
        SchemaUpgrader.Upgrade(ctx);   // 冪等：重跑不出錯

        var fromUpgrader = ReadColumns(ctx, "lf_reports").OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.Equal(fromEnsureCreated, fromUpgrader);
    }

    private static List<string> ReadColumns(LfDbContext ctx, string table)
    {
        var names = new List<string>();
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        ctx.Database.OpenConnection();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }
}

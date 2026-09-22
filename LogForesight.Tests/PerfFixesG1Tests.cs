using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋五十輪 G-1：清單分頁的舊列探測在篩選之後、使用者清單快取（最後登入時間搬出清單）、
/// 稽核／匯入 store 建構時不整表讀、稽核有篩選無起日時套用預設 90 天。
/// </summary>
public class PerfFixesG1Tests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    // ── 清單分頁：舊列探測 ─────────────────────────────────────────────────

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date, string risk = "低") => new()
    {
        HostId = hostId, Host = host, Date = date, RiskLevel = risk
    };

    private EfAnalysisRecordStore SeedLegacyAndNormal()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        store.Append(Rec(0, "SRV-A", DateTime.Today));                 // 舊列：未對應主機
        store.Append(Rec(2, "SRV-B", DateTime.Today, "高"));
        store.Append(Rec(2, "SRV-B", DateTime.Today.AddDays(-1)));
        return store;
    }

    private static List<string> Keys(IEnumerable<DailyAnalysisRecord> records) =>
        records.Select(r => $"{r.HostId}|{r.Host}|{r.Date:yyyy-MM-dd}").OrderBy(k => k, StringComparer.Ordinal).ToList();

    [Fact]
    public void QueryPage_表中有舊列但篩選不含它_走SQL分頁且與Query一致()
    {
        var store = SeedLegacyAndNormal();
        var filter = new RecordQueryFilter { Hosts = new[] { new HostKey { HostId = 2, HostName = "SRV-B" } } };

        var page = store.QueryPage(filter, 1, 50);

        Assert.True(store.LastQueryPageUsedSqlPaging);
        Assert.Equal(2, page.Total);
        Assert.Equal(Keys(store.Query(filter)), Keys(page.Items));
        Assert.DoesNotContain(page.Items, r => r.HostId == 0);
    }

    [Fact]
    public void QueryPage_篩選含舊列主機名_走記憶體路徑且包含舊列()
    {
        var store = SeedLegacyAndNormal();
        var filter = new RecordQueryFilter
        {
            // 名稱大小寫不同也要命中：與 HostMatcher 的 OrdinalIgnoreCase 一致
            Hosts = new[] { new HostKey { HostId = 2, HostName = "SRV-B" }, new HostKey { HostId = 0, HostName = "srv-a" } }
        };

        var page = store.QueryPage(filter, 1, 50);

        Assert.False(store.LastQueryPageUsedSqlPaging);
        Assert.Equal(3, page.Total);
        Assert.Contains(page.Items, r => r.HostId == 0 && r.Host == "SRV-A");
        Assert.Equal(Keys(store.Query(filter)), Keys(page.Items));
    }

    [Fact]
    public void QueryPage_舊列落在日期篩選之外_走SQL分頁()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        store.Append(Rec(0, "SRV-A", DateTime.Today.AddDays(-30)));
        store.Append(Rec(2, "SRV-B", DateTime.Today));
        var filter = new RecordQueryFilter { From = DateTime.Today.AddDays(-7), To = DateTime.Today };

        var page = store.QueryPage(filter, 1, 50);

        Assert.True(store.LastQueryPageUsedSqlPaging);
        Assert.Equal(Keys(store.Query(filter)), Keys(page.Items));
    }

    [Fact]
    public void QueryPage_無主機篩選且範圍內有舊列_走記憶體路徑()
    {
        var store = SeedLegacyAndNormal();
        var filter = new RecordQueryFilter();

        var page = store.QueryPage(filter, 1, 50);

        Assert.False(store.LastQueryPageUsedSqlPaging);
        Assert.Equal(3, page.Total);
    }

    // ── 使用者清單快取 ─────────────────────────────────────────────────────

    [Fact]
    public void TouchLogin_不改使用者清單版本_最後登入時間在另一份blob()
    {
        var users = new UserStore(_fx.Blob("users"), _fx.Blob("user_last_login"));
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "甲" });
        var versionBefore = _fx.Blob("users").ReadVersion();

        var at = new DateTime(2026, 9, 19, 8, 30, 0);
        users.TouchLogin(user.UserId, at);

        Assert.Equal(versionBefore, _fx.Blob("users").ReadVersion());
        Assert.Equal(at, users.GetLastLogins()[user.UserId]);
        Assert.Null(users.Get(user.UserId)!.LastLoginAt);   // 不再寫進清單
    }

    [Fact]
    public void Get_版本未變_命中快取回傳同一參考()
    {
        var users = new UserStore(_fx.Blob("users"), _fx.Blob("user_last_login"));
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\bob" });
        users.TouchLogin(user.UserId, DateTime.Now);

        var first = users.Get(user.UserId);
        users.TouchLogin(user.UserId, DateTime.Now.AddMinutes(1));   // 登入不讓快取失效
        var second = users.Get(user.UserId);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Get_清單被改寫後_快取更新()
    {
        var users = new UserStore(_fx.Blob("users"), _fx.Blob("user_last_login"));
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\carol", DisplayName = "舊" });
        var first = users.Get(user.UserId)!;

        users.Upsert(new WebUser { Account = "DOMAIN\\carol", DisplayName = "新" });
        var second = users.Get(user.UserId)!;

        Assert.NotSame(first, second);
        Assert.Equal("新", second.DisplayName);
        Assert.Equal("舊", first.DisplayName);
    }

    // ── 稽核 ───────────────────────────────────────────────────────────────

    private static AuditEntry Entry(DateTime occurredAt, string action = "login") => new()
    {
        Action = action, OccurredAt = occurredAt, Account = "tester"
    };

    [Fact]
    public void 稽核_有篩選無起日_套用近90天並標示()
    {
        var store = new AuditLogStore(_fx.LogStore("audit"));
        store.Append(Entry(DateTime.Today.AddDays(-100)));
        store.Append(Entry(DateTime.Today.AddDays(-1)));

        var result = store.Query(new AuditQuery { Actions = new List<string> { "login" } });

        Assert.True(result.DefaultRangeApplied);
        Assert.Single(result.Items);
        Assert.Equal(DateTime.Today.AddDays(-1), result.Items[0].OccurredAt);
    }

    [Fact]
    public void 稽核_有起日_不套用預設範圍()
    {
        var store = new AuditLogStore(_fx.LogStore("audit"));
        store.Append(Entry(DateTime.Today.AddDays(-100)));
        store.Append(Entry(DateTime.Today.AddDays(-1)));

        var result = store.Query(new AuditQuery
        {
            From = DateTime.Today.AddDays(-120),
            Actions = new List<string> { "login" }
        });

        Assert.False(result.DefaultRangeApplied);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void 稽核_服務層DTO帶出預設範圍旗標()
    {
        var store = new AuditLogStore(_fx.LogStore("audit"));
        store.Append(Entry(DateTime.Today.AddDays(-100)));
        var users = new UserStore(_fx.Blob("users"), _fx.Blob("user_last_login"));
        var service = new LogForesight.Web.Services.AuditQueryService(
            store, users, new LogForesight.Web.Services.UserDisplayNameService(new FakeSystemSettingsStore()));

        var dto = service.Query(new AuditQuery { Actions = new List<string> { "login" } });

        Assert.True(dto.DefaultRangeApplied);
        Assert.Equal(0, dto.Total);
    }

    [Fact]
    public void 稽核_重建store不整表讀_續號接在最大值之後()
    {
        var store = new AuditLogStore(_fx.LogStore("audit"));
        for (var i = 0; i < 1000; i++) store.Append(Entry(DateTime.Now));

        var monitor = new SqlPerformanceMonitor(thresholdMs: 0);   // 門檻 0：每一次操作都留名
        var rebuilt = new AuditLogStore(new EfJsonLogStore(_fx.NewContext, "audit", monitor));
        var next = Entry(DateTime.Now);
        rebuilt.Append(next);

        Assert.Equal(1001, next.AuditId);
        var ops = monitor.Snapshot().TopSlowOperations.Select(o => o.Operation).ToList();
        Assert.Contains("log:audit:ReadLastLines", ops);
        Assert.DoesNotContain("log:audit:ReadLines", ops);
    }

    // ── 共用續號探測 ───────────────────────────────────────────────────────

    [Fact]
    public void ProbeMaxId_取尾端可解析的最大值_不只看最後一行()
    {
        var log = _fx.LogStore("probe");
        log.AppendLine("{\"ImportId\":5}");
        log.AppendLine("{\"ImportId\":7}");
        log.AppendLine("{\"ImportId\":6}");
        log.AppendLine("{壞掉的一行");

        var max = log.ProbeMaxId<ImportLogEntry>(e => e.ImportId, 32, "測試", LfJsonOptions.Compact);

        Assert.Equal(7, max);
    }

    [Fact]
    public void ProbeMaxId_尾端全部無法解析_回0()
    {
        var log = _fx.LogStore("probe");
        log.AppendLine("{壞");
        log.AppendLine("也壞");

        Assert.Equal(0, log.ProbeMaxId<ImportLogEntry>(e => e.ImportId, 32, "測試", LfJsonOptions.Compact));
    }

    // ── 匯入紀錄 ───────────────────────────────────────────────────────────

    [Fact]
    public void 匯入紀錄_重建後續號接續_GetRecent取最新()
    {
        var store = new ImportLogStore(_fx.LogStore("import_logs"));
        for (var i = 0; i < 10; i++)
            store.Append(new ImportLogEntry { Kind = "Users", CreatedAt = DateTime.Today.AddMinutes(i) });

        var rebuilt = new ImportLogStore(_fx.LogStore("import_logs"));
        var entry = new ImportLogEntry { Kind = "Hosts", CreatedAt = DateTime.Today.AddHours(1) };
        rebuilt.Append(entry);

        Assert.Equal(11, entry.ImportId);
        var recent = rebuilt.GetRecent(3);
        Assert.Equal(new long[] { 11, 10, 9 }, recent.Select(e => e.ImportId).ToArray());
    }
}

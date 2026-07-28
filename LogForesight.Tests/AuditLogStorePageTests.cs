using LogForesight.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P1-2：<see cref="AuditLogStore.Query"/>／<see cref="AuditLogStore.Count"/> 的分頁下推。
/// 無篩選條件時走 SQL 端真分頁（<see cref="IJsonLogStore.ReadPage"/>）；有篩選條件時以日期範圍
/// 先在 SQL 端窄化候選集（<see cref="IJsonLogStore.ReadLines(DateTime?,DateTime?)"/>），
/// 其餘欄位在記憶體精確過濾——兩條路徑都必須與「全撈再過濾」的舊行為逐位一致。
/// </summary>
public class AuditLogStorePageTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AuditLogStore CreateStore() => new(_fixture.LogStore("audit"));

    private static AuditEntry Entry(string action, DateTime occurredAt, long? userId = null,
        string? targetKind = null, AuditResult result = AuditResult.Ok) => new()
    {
        Action = action,
        OccurredAt = occurredAt,
        UserId = userId,
        TargetKind = targetKind,
        Result = result,
        Account = "tester"
    };

    // ── 無篩選條件：SQL 端真分頁 ─────────────────────────────────────────────

    [Fact]
    public void Query_無篩選條件_分頁正確且新到舊()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
            store.Append(Entry("x", DateTime.Today.AddMinutes(i))); // AuditId 遞增＝附加順序＝時間遞增

        var page1 = store.Query(new AuditQuery { Page = 1, PageSize = 2 });
        var page2 = store.Query(new AuditQuery { Page = 2, PageSize = 2 });

        Assert.Equal(5, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        // 新到舊：最後附加的（AuditId 最大＝分鐘數最大）排最前面
        Assert.Equal(5, page1.Items[0].AuditId);
        Assert.Equal(4, page1.Items[1].AuditId);
        Assert.Equal(3, page2.Items[0].AuditId);
    }

    [Fact]
    public void Query_無篩選條件_無資料時Total為0()
    {
        var store = CreateStore();

        var result = store.Query(new AuditQuery());

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    // ── 有篩選條件：日期範圍窄化＋記憶體精確過濾 ────────────────────────────────

    [Fact]
    public void Query_指定日期範圍_只回範圍內()
    {
        var store = CreateStore();
        store.Append(Entry("x", DateTime.Today.AddDays(-10)));
        store.Append(Entry("x", DateTime.Today.AddDays(-5)));
        store.Append(Entry("x", DateTime.Today));
        // AppendLine 一律以「現在」戳記 CreatedAt——正式環境 OccurredAt 與 CreatedAt 是同一刻寫入的，
        // 兩者恆等；這裡人工回填歷史 OccurredAt，必須連底層 CreatedAt 也一併回填才符合這個不變量，
        // 否則測的是「不會真的發生」的情境（見 ReadLines(from,to) 以 CreatedAt 窄化的設計前提）
        BackdateCreatedAt(0, DateTime.Today.AddDays(-10));
        BackdateCreatedAt(1, DateTime.Today.AddDays(-5));
        BackdateCreatedAt(2, DateTime.Today);

        var result = store.Query(new AuditQuery
        {
            From = DateTime.Today.AddDays(-6),
            To = DateTime.Today.AddDays(-1)
        });

        Assert.Single(result.Items);
        Assert.Equal(DateTime.Today.AddDays(-5), result.Items[0].OccurredAt.Date);
    }

    /// <summary>依附加順序（0-based）把某一筆的 CreatedAt 回填成指定時間，模擬真實的歷史資料</summary>
    private void BackdateCreatedAt(int occurrence, DateTime createdAt)
    {
        using var ctx = _fixture.NewContext();
        var row = ctx.LogLines.Where(l => l.LogKey == "audit").OrderBy(l => l.Seq).Skip(occurrence).First();
        row.CreatedAt = createdAt;
        ctx.SaveChanges();
    }

    [Fact]
    public void Query_依UserId過濾()
    {
        var store = CreateStore();
        store.Append(Entry("x", DateTime.Today, userId: 1));
        store.Append(Entry("x", DateTime.Today, userId: 2));

        var result = store.Query(new AuditQuery { UserId = 1 });

        Assert.Single(result.Items);
        Assert.Equal(1, result.Items[0].UserId);
    }

    [Fact]
    public void Query_依Result過濾()
    {
        var store = CreateStore();
        store.Append(Entry("login", DateTime.Today, result: AuditResult.Ok));
        store.Append(Entry("login_failed", DateTime.Today, result: AuditResult.Failed));

        var result = store.Query(new AuditQuery { Result = AuditResult.Failed });

        Assert.Single(result.Items);
        Assert.Equal("login_failed", result.Items[0].Action);
    }

    [Fact]
    public void Query_依TargetKind過濾()
    {
        var store = CreateStore();
        store.Append(Entry("host_update", DateTime.Today, targetKind: "host"));
        store.Append(Entry("user_update", DateTime.Today, targetKind: "user"));

        var result = store.Query(new AuditQuery { TargetKind = "host" });

        Assert.Single(result.Items);
        Assert.Equal("host_update", result.Items[0].Action);
    }

    [Fact]
    public void Query_依Actions清單過濾()
    {
        var store = CreateStore();
        store.Append(Entry("login", DateTime.Today));
        store.Append(Entry("logout", DateTime.Today));
        store.Append(Entry("host_update", DateTime.Today));

        var result = store.Query(new AuditQuery { Actions = new List<string> { "login", "logout" } });

        Assert.Equal(2, result.Total);
    }

    /// <summary>沒有時間戳記的既存列（模擬 schema 升級前寫入）在有日期篩選時仍要能被記憶體端的精確 Matches 正確排除</summary>
    [Fact]
    public void Query_日期篩選下_無時間戳記的既存列仍依OccurredAt精確判斷()
    {
        var store = CreateStore();
        store.Append(Entry("x", DateTime.Today.AddDays(-100))); // 業務時間在範圍外
        ClearCreatedAt("audit");

        var result = store.Query(new AuditQuery { From = DateTime.Today.AddDays(-1), To = DateTime.Today });

        // CreatedAt 為 null 時 SQL 端會保守地把這行也撈進候選集，但記憶體端的 Matches
        // 用的是精確的 OccurredAt，仍必須把它排除——這是本方案「窄化不保證精確、精確判斷靠記憶體」的驗收點
        Assert.Empty(result.Items);
    }

    // ── Count ────────────────────────────────────────────────────────────────

    [Fact]
    public void Count_依期間與動作代碼()
    {
        var store = CreateStore();
        store.Append(Entry(AuditActions.LoginFailed, DateTime.Now.AddHours(-1)));
        store.Append(Entry(AuditActions.LoginFailed, DateTime.Now.AddHours(-30))); // 超過 24 小時
        store.Append(Entry(AuditActions.Login, DateTime.Now.AddHours(-1)));

        var count = store.Count(DateTime.Now.AddHours(-24), DateTime.Now, AuditActions.LoginFailed);

        Assert.Equal(1, count);
    }

    /// <summary>無時間戳記的既存列仍要能被 Count 的精確 OccurredAt 檢查正確排除在範圍外</summary>
    [Fact]
    public void Count_無時間戳記的既存列_仍依OccurredAt精確判斷()
    {
        var store = CreateStore();
        store.Append(Entry(AuditActions.LoginFailed, DateTime.Now.AddDays(-30))); // 業務時間在範圍外
        ClearCreatedAt("audit");

        var count = store.Count(DateTime.Now.AddHours(-24), DateTime.Now, AuditActions.LoginFailed);

        Assert.Equal(0, count);
    }

    /// <summary>模擬「schema 升級前寫入」：直接把底層列的 CreatedAt 改回 null</summary>
    private void ClearCreatedAt(string logKey)
    {
        using var ctx = _fixture.NewContext();
        foreach (var row in ctx.LogLines.Where(l => l.LogKey == logKey))
            row.CreatedAt = null;
        ctx.SaveChanges();
    }
}

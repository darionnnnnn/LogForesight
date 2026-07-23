using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IHostStore"/> 的合約測試基底（docs/WEB-SPEC.md §12）。
/// JSONL 與未來的 SQL 實作跑同一組案例。
/// </summary>
public abstract class HostStoreContractTests : IDisposable
{
    protected abstract IHostStore CreateStore();

    public virtual void Dispose() { }

    [Fact]
    public void Upsert_新主機_配發識別碼()
    {
        var store = CreateStore();

        var saved = store.Upsert(new WebHost { HostName = "SRV-01", RoleDesc = "網站主機" });

        Assert.True(saved.HostId > 0);
        Assert.Equal("網站主機", store.Get(saved.HostId)!.RoleDesc);
    }

    [Fact]
    public void FindByName_不分大小寫()
    {
        var store = CreateStore();
        store.Upsert(new WebHost { HostName = "SRV-01" });

        Assert.NotNull(store.FindByName("srv-01"));
    }

    [Fact]
    public void Touch_主機不存在_建立並記錄回報時間()
    {
        var store = CreateStore();
        var now = new DateTime(2026, 7, 21, 3, 0, 0);

        var host = store.Touch("SRV-NEW", now);

        Assert.True(host.HostId > 0);
        Assert.Equal(now, host.LastReportAt);
        Assert.Equal("local", host.Source);
    }

    /// <summary>
    /// **這是 Touch 與 Upsert 分開的全部理由**：批次每晚執行，它不知道也不該猜
    /// Web 維護的角色描述、群組與負責人。若批次走一般的 Upsert，就會用空值把
    /// 人工設定全部蓋掉——而且要等到有人發現「怎麼大家都看不到這台主機了」才會知道。
    /// </summary>
    [Fact]
    public void Touch_既有主機_只更新回報時間不動Web維護欄位()
    {
        var store = CreateStore();
        var original = store.Upsert(new WebHost
        {
            HostName = "SRV-01",
            RoleDesc = "OO部門資料庫",
            IpAddress = "10.1.2.12",
            NetiqServer = "SENTINEL-A",
            GroupIds = new List<long> { 3, 7 },
            OwnerUserIds = new List<long> { 11 }
        });

        var reportedAt = new DateTime(2026, 7, 21, 3, 0, 0);
        store.Touch("SRV-01", reportedAt);

        var after = store.Get(original.HostId)!;
        Assert.Equal(reportedAt, after.LastReportAt);
        Assert.Equal("OO部門資料庫", after.RoleDesc);
        Assert.Equal("10.1.2.12", after.IpAddress);
        Assert.Equal("SENTINEL-A", after.NetiqServer);
        Assert.Equal(new long[] { 3, 7 }, after.GroupIds);
        Assert.Equal(new long[] { 11 }, after.OwnerUserIds);
    }

    [Fact]
    public void Touch_不分大小寫_不會產生重複主機()
    {
        var store = CreateStore();
        store.Upsert(new WebHost { HostName = "SRV-01" });

        store.Touch("srv-01", DateTime.Now);

        Assert.Single(store.GetAll());
    }

    /// <summary>Upsert 是 Web 維護路徑，不該覆寫批次寫入的回報時間</summary>
    [Fact]
    public void Upsert_不覆寫回報時間()
    {
        var store = CreateStore();
        var reportedAt = new DateTime(2026, 7, 21, 3, 0, 0);
        var host = store.Touch("SRV-01", reportedAt);

        store.Upsert(new WebHost { HostName = "SRV-01", RoleDesc = "改過的描述" });

        var after = store.Get(host.HostId)!;
        Assert.Equal(reportedAt, after.LastReportAt);
        Assert.Equal("改過的描述", after.RoleDesc);
    }

    [Fact]
    public void SetGroups_與SetOwners_各自獨立不互相影響()
    {
        var store = CreateStore();
        var host = store.Upsert(new WebHost { HostName = "SRV-01" });

        store.SetGroups(host.HostId, new long[] { 1, 2 });
        store.SetOwners(host.HostId, new long[] { 5 });

        var after = store.Get(host.HostId)!;
        Assert.Equal(new long[] { 1, 2 }, after.GroupIds);
        Assert.Equal(new long[] { 5 }, after.OwnerUserIds);
    }

    /// <summary>綁定後舊主機留墓碑（不刪除），歷史才追溯得到、綁錯也能反向修復</summary>
    [Fact]
    public void Merge_來源主機保留為墓碑並停用()
    {
        var store = CreateStore();
        var oldHost = store.Upsert(new WebHost { HostName = "SRV-OLD" });
        var newHost = store.Upsert(new WebHost { HostName = "SRV-NEW" });

        store.Merge(oldHost.HostId, newHost.HostId);

        var after = store.Get(oldHost.HostId)!;
        Assert.Equal(newHost.HostId, after.MergedInto);
        Assert.False(after.Active);
        Assert.Equal("SRV-OLD", after.HostName);
    }

    // ── TouchNetiq（批次回填，NetIQ 來源）─────────────────────────────────────

    /// <summary>
    /// 與 Touch 同一條原則：批次只寫自己知道的欄位。這裡多一個 DisplayName，
    /// 但角色描述、群組、負責人一樣不准動。
    /// </summary>
    [Fact]
    public void TouchNetiq_只更新回報時間與顯示名()
    {
        var store = CreateStore();
        var host = store.Upsert(new WebHost
        {
            HostName = "10.1.2.12",
            Source = "netiq",
            RoleDesc = "OO部門資料庫",
            GroupIds = new List<long> { 3 },
            OwnerUserIds = new List<long> { 11 }
        });

        var reportedAt = new DateTime(2026, 7, 21, 3, 0, 0);
        store.TouchNetiq(host.HostId, "SRV-DB-01", reportedAt);

        var after = store.Get(host.HostId)!;
        Assert.Equal(reportedAt, after.LastReportAt);
        Assert.Equal("SRV-DB-01", after.DisplayName);
        Assert.Equal("OO部門資料庫", after.RoleDesc);
        Assert.Equal(new long[] { 3 }, after.GroupIds);
        Assert.Equal(new long[] { 11 }, after.OwnerUserIds);
    }

    /// <summary>Sentinel 沒回報名稱的那幾天，不該把既有的顯示名清空</summary>
    [Fact]
    public void TouchNetiq_顯示名為空_不覆寫既有值()
    {
        var store = CreateStore();
        var host = store.Upsert(new WebHost { HostName = "10.1.2.12", Source = "netiq" });
        store.TouchNetiq(host.HostId, "SRV-DB-01", DateTime.Now);

        store.TouchNetiq(host.HostId, null, DateTime.Now);

        Assert.Equal("SRV-DB-01", store.Get(host.HostId)!.DisplayName);
    }

    /// <summary>
    /// 主機不存在時回 null 而不是建立——NetIQ 主機一律由 Web 清單維護，
    /// 這裡若用名稱補建，admin 打錯字就會默默多出一台幽靈主機。
    /// </summary>
    [Fact]
    public void TouchNetiq_主機不存在_回null且不新增()
    {
        var store = CreateStore();

        Assert.Null(store.TouchNetiq(999, "SRV-X", DateTime.Now));
        Assert.Empty(store.GetAll());
    }

    // ── Merge 的描述性欄位搬移 ────────────────────────────────────────────────

    /// <summary>
    /// **這是搬移存在的全部理由**：把「CSV 預先登錄、已設好群組與負責人」的那列，
    /// 併入「NetIQ 剛回報、什麼都還沒設」的那列，若不帶過去，結果是群組全掉——
    /// 而且要等到有人問「怎麼大家都看不到這台主機了」才會發現。
    /// </summary>
    [Fact]
    public void Merge_目標空欄位_自來源帶入()
    {
        var store = CreateStore();
        var source = store.Upsert(new WebHost
        {
            HostName = "SRV-DB-01",
            RoleDesc = "OO部門資料庫",
            GroupIds = new List<long> { 3, 7 },
            OwnerUserIds = new List<long> { 11 }
        });
        var target = store.Upsert(new WebHost
        {
            HostName = "10.1.2.12",
            Source = "netiq",
            NetiqServer = "SENTINEL-A"
        });

        store.Merge(source.HostId, target.HostId);

        var after = store.Get(target.HostId)!;
        Assert.Equal("OO部門資料庫", after.RoleDesc);
        Assert.Equal(new long[] { 3, 7 }, after.GroupIds);
        Assert.Equal(new long[] { 11 }, after.OwnerUserIds);
        Assert.Equal("SENTINEL-A", after.NetiqServer);
        // 顯示名沒有就退而用來源的登錄名稱，清單上才不會只剩一串 IP
        Assert.Equal("SRV-DB-01", after.DisplayName);
    }

    /// <summary>合併不該悄悄改掉人已經設好的東西——目標有值就一律保留目標的</summary>
    [Fact]
    public void Merge_目標已有值_不被來源覆蓋()
    {
        var store = CreateStore();
        var source = store.Upsert(new WebHost
        {
            HostName = "SRV-OLD",
            RoleDesc = "舊的描述",
            GroupIds = new List<long> { 3 }
        });
        var target = store.Upsert(new WebHost
        {
            HostName = "SRV-NEW",
            RoleDesc = "現行描述",
            GroupIds = new List<long> { 9 }
        });

        store.Merge(source.HostId, target.HostId);

        var after = store.Get(target.HostId)!;
        Assert.Equal("現行描述", after.RoleDesc);
        Assert.Equal(new long[] { 9 }, after.GroupIds);
    }

    /// <summary>搬移是複製不是移動：來源保留自己的值，Unmerge 才還原得回來</summary>
    [Fact]
    public void Merge_來源欄位不被清空()
    {
        var store = CreateStore();
        var source = store.Upsert(new WebHost
        {
            HostName = "SRV-OLD",
            RoleDesc = "舊的描述",
            GroupIds = new List<long> { 3 }
        });
        var target = store.Upsert(new WebHost { HostName = "SRV-NEW" });

        store.Merge(source.HostId, target.HostId);

        var after = store.Get(source.HostId)!;
        Assert.Equal("舊的描述", after.RoleDesc);
        Assert.Equal(new long[] { 3 }, after.GroupIds);
    }

    // ── Unmerge ───────────────────────────────────────────────────────────────

    /// <summary>「留墓碑不刪除」要能真的救得回來，才不只是一句安慰</summary>
    [Fact]
    public void Unmerge_清除墓碑標記並恢復啟用()
    {
        var store = CreateStore();
        var source = store.Upsert(new WebHost { HostName = "SRV-OLD", RoleDesc = "舊的描述" });
        var target = store.Upsert(new WebHost { HostName = "SRV-NEW" });
        store.Merge(source.HostId, target.HostId);

        store.Unmerge(source.HostId);

        var after = store.Get(source.HostId)!;
        Assert.Null(after.MergedInto);
        Assert.True(after.Active);
        Assert.Equal("舊的描述", after.RoleDesc);
    }
}

public class JsonHostStoreContractTests : HostStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));

    protected override IHostStore CreateStore() => new JsonHostStore(Path.Combine(_dir, "hosts.json"));

    public override void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 同一組主機 store 合約，跑在 EF 的 JSON blob 後端（SQLite in-memory）——
/// 驗證 webdata store 透過 blob 抽象改走資料庫後，行為與檔案後端逐位一致
/// （store 邏輯完全沒改，只換了底層 IJsonBlobStore）。
/// </summary>
public class EfHostStoreContractTests : HostStoreContractTests
{
    private readonly EfSqliteFixture _fx = new();

    protected override IHostStore CreateStore() => new JsonHostStore(_fx.Blob("hosts"));

    public override void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}

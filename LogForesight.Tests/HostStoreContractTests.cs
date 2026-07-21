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

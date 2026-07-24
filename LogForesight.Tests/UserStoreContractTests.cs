using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IUserStore"/> 的**合約測試基底**（docs/WEB-SPEC.md §12、DB-PLAN 一致性機制 #3）。
/// 測試案例寫在這裡、與實作無關，見 <see cref="EfUserStoreContractTests"/>。
/// </summary>
public abstract class UserStoreContractTests : IDisposable
{
    protected abstract IUserStore CreateStore();

    public virtual void Dispose() { }

    [Fact]
    public void Upsert_新帳號_配發識別碼並可查回()
    {
        var store = CreateStore();

        var saved = store.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });

        Assert.True(saved.UserId > 0);
        var found = store.Get(saved.UserId);
        Assert.NotNull(found);
        Assert.Equal("王小明", found!.DisplayName);
    }

    [Fact]
    public void Upsert_相同帳號_更新而非新增()
    {
        var store = CreateStore();
        var first = store.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });

        var second = store.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王大明", Active = false });

        Assert.Equal(first.UserId, second.UserId);
        Assert.Single(store.GetAll());
        Assert.Equal("王大明", store.Get(first.UserId)!.DisplayName);
        Assert.False(store.Get(first.UserId)!.Active);
    }

    /// <summary>
    /// AD 帳號本來就不分大小寫：使用者輸入 domain\user 或 DOMAIN\USER 必須是同一個人。
    /// 分大小寫的話會產生兩筆使用者、各自帶不同的群組授權——授權出錯的典型來源。
    /// </summary>
    [Fact]
    public void FindByAccount_不分大小寫()
    {
        var store = CreateStore();
        store.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });

        Assert.NotNull(store.FindByAccount("domain\\WANG"));
        Assert.NotNull(store.FindByAccount("DOMAIN\\wang"));
    }

    [Fact]
    public void Upsert_大小寫不同的相同帳號_視為同一人()
    {
        var store = CreateStore();
        store.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });

        store.Upsert(new WebUser { Account = "domain\\WANG", DisplayName = "王小明（改）" });

        Assert.Single(store.GetAll());
    }

    [Fact]
    public void FindByAccount_查無帳號_回傳null()
    {
        Assert.Null(CreateStore().FindByAccount("DOMAIN\\nobody"));
    }

    [Fact]
    public void SetGroups_整組取代且去重()
    {
        var store = CreateStore();
        var user = store.Upsert(new WebUser { Account = "DOMAIN\\wang", GroupIds = new List<long> { 1, 2 } });

        store.SetGroups(user.UserId, new long[] { 3, 3, 4 });

        Assert.Equal(new long[] { 3, 4 }, store.Get(user.UserId)!.GroupIds);
    }

    [Fact]
    public void SetGroups_空清單_清除全部群組()
    {
        var store = CreateStore();
        var user = store.Upsert(new WebUser { Account = "DOMAIN\\wang", GroupIds = new List<long> { 1 } });

        store.SetGroups(user.UserId, Array.Empty<long>());

        Assert.Empty(store.Get(user.UserId)!.GroupIds);
    }

    [Fact]
    public void GetAll_初始狀態_回傳空清單而非例外()
    {
        Assert.Empty(CreateStore().GetAll());
    }
}

/// <summary>使用者 store 合約，跑在 EF 的 JSON blob 後端（SQLite in-memory）。</summary>
public class EfUserStoreContractTests : UserStoreContractTests
{
    private readonly EfSqliteFixture _fx = new();

    protected override IUserStore CreateStore() => new JsonUserStore(_fx.Blob("users"));

    public override void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }
}

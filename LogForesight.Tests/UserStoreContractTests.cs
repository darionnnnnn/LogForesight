using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IUserStore"/> 的**合約測試基底**（docs/WEB-SPEC.md §12、DB-PLAN 一致性機制 #3）。
///
/// 測試案例寫在這裡、與實作無關；JSONL 後端與未來的 SQL 後端各自繼承一個子類別，
/// 跑的是同一組案例。「兩個後端行為一致」因此由測試強制，不靠 code review 肉眼比對——
/// SQL 實作完成時，通過這組測試才算數。
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

/// <summary>JSONL 後端的合約測試（單機檔案相容模式）。</summary>
public class JsonUserStoreContractTests : UserStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));

    protected override IUserStore CreateStore() => new JsonUserStore(Path.Combine(_dir, "users.json"));

    public override void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 同一組使用者 store 合約，跑在 EF 的 JSON blob 後端（SQLite in-memory）——SQLite 現為
/// 主要測試方式，驗證 webdata store 透過 blob 抽象改走資料庫後，行為與檔案後端逐位一致。
/// </summary>
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

/// <summary>JSONL 後端的持久性驗證：跨實例讀得回來（不是只存在記憶體裡）</summary>
public class JsonUserStorePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "users.json");

    [Fact]
    public void 寫入後以新實例讀取_資料仍在()
    {
        new JsonUserStore(FilePath).Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王小明" });

        var reloaded = new JsonUserStore(FilePath).FindByAccount("DOMAIN\\wang");

        Assert.NotNull(reloaded);
        Assert.Equal("王小明", reloaded!.DisplayName);
    }

    [Fact]
    public void 寫入後不留下暫存檔_原子替換完成()
    {
        var store = new JsonUserStore(FilePath);
        store.Upsert(new WebUser { Account = "DOMAIN\\wang" });
        store.Upsert(new WebUser { Account = "DOMAIN\\lee" });

        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}

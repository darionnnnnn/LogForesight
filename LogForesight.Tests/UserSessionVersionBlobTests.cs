using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 工作階段版號的登出增量存獨立 blob：日常登出不得讓 users blob 版本前進
/// （PRTG 範圍清除作業以 users 版本當授權柵欄，登出就暫停清除是誤報）。
/// 停用仍在 users blob 內遞增（停用本身就是授權相關寫入）。
/// </summary>
public class UserSessionVersionBlobTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private UserStore NewStore() =>
        new(_fx.Blob("users"), _fx.Blob("user_last_login"), _fx.Blob("user_session_versions"));

    [Fact]
    public void 登出不改users_blob版本_有效版號遞增()
    {
        var store = NewStore();
        var user = store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });
        var usersVersionBefore = _fx.Blob("users").ReadVersion();

        Assert.Equal(1, store.BumpSessionVersion(user.UserId));
        Assert.Equal(2, store.BumpSessionVersion(user.UserId));

        Assert.Equal(usersVersionBefore, _fx.Blob("users").ReadVersion());
        Assert.Equal(2, store.GetSessionVersion(store.Get(user.UserId)!));
    }

    [Fact]
    public void 舊資料欄位值與增量合併_另一個行程的實例也讀得到()
    {
        var store = NewStore();
        var user = store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });
        // 既有部署：舊版已把版號寫在使用者資料裡（停用遞增或舊版登出）
        store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = false });
        store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });
        var legacy = store.Get(user.UserId)!.SessionVersion;
        Assert.Equal(1, legacy);

        Assert.Equal(legacy + 1, store.BumpSessionVersion(user.UserId));

        var other = NewStore();   // 新實例（模擬另一個行程）：版本探測後重讀
        Assert.Equal(legacy + 1, other.GetSessionVersion(other.Get(user.UserId)!));
        other.BumpSessionVersion(user.UserId);
        Assert.Equal(legacy + 2, store.GetSessionVersion(store.Get(user.UserId)!));   // 原實例的快取因版本前進而失效
    }

    [Fact]
    public void 停用仍讓舊版號失效_且重新啟用後不復活()
    {
        var store = NewStore();
        var user = store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });
        var issued = store.GetSessionVersion(store.Get(user.UserId)!);

        store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = false });
        store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });

        Assert.NotEqual(issued, store.GetSessionVersion(store.Get(user.UserId)!));
    }

    [Fact]
    public void 找不到使用者回0且不寫blob()
    {
        var store = NewStore();
        Assert.Equal(0, store.BumpSessionVersion(99999));
        Assert.Equal(0, _fx.Blob("user_session_versions").ReadVersion());
    }

    [Fact]
    public void 編輯使用者資料不會讓已登出的版號倒退()
    {
        var store = NewStore();
        var user = store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲", Active = true });
        store.BumpSessionVersion(user.UserId);
        var before = store.GetSessionVersion(store.Get(user.UserId)!);

        store.Upsert(new WebUser { Account = "D\\a", DisplayName = "甲改", Active = true });

        Assert.Equal(before, store.GetSessionVersion(store.Get(user.UserId)!));
    }
}

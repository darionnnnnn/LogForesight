using LogForesight.Core.Persistence;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// F1c-a：每位管理者的初始設定引導偏好（store 與 controller）
/// </summary>
public class UserPreferenceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Store_先設常用語再hidden為true_兩者都保留()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetNotePhrases(1, new List<string> { "常用語一", "常用語二" });
        store.SetSetupGuideHidden(1, true);

        var pref = store.Get(1);
        Assert.Equal(new[] { "常用語一", "常用語二" }, pref.NotePhrases);
        Assert.True(pref.SetupGuideHidden);
    }

    [Fact]
    public void Store_先設hidden為true再設常用語_兩者都保留()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetSetupGuideHidden(1, true);
        store.SetNotePhrases(1, new List<string> { "常用語一" });

        var pref = store.Get(1);
        Assert.True(pref.SetupGuideHidden);
        Assert.Equal(new[] { "常用語一" }, pref.NotePhrases);
    }

    [Fact]
    public void Store_hidden為true後清空常用語_hidden仍true且entry仍存在()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetNotePhrases(1, new List<string> { "常用語一" });
        store.SetSetupGuideHidden(1, true);

        store.SetNotePhrases(1, new List<string>());

        var pref = store.Get(1);
        Assert.True(pref.SetupGuideHidden);
        Assert.Empty(pref.NotePhrases);
        Assert.True(store.Get().ContainsKey(1));
    }

    [Fact]
    public void Store_hidden為false且無常用語後回預設偏好()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetSetupGuideHidden(1, true);
        Assert.True(store.Get().ContainsKey(1));

        store.SetSetupGuideHidden(1, false);

        Assert.False(store.Get().ContainsKey(1));
        var pref = store.Get(1);
        Assert.False(pref.SetupGuideHidden);
        Assert.Empty(pref.NotePhrases);
    }

    [Fact]
    public void Store_原本只有常用語_清空常用語後entry移除()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetNotePhrases(1, new List<string> { "常用語一" });
        Assert.True(store.Get().ContainsKey(1));

        store.SetNotePhrases(1, new List<string>());

        Assert.False(store.Get().ContainsKey(1));
        var pref = store.Get(1);
        Assert.False(pref.SetupGuideHidden);
        Assert.Empty(pref.NotePhrases);
    }

    [Fact]
    public void Store_不同使用者互不影響()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        store.SetNotePhrases(1, new List<string> { "User1片語" });
        store.SetSetupGuideHidden(1, true);

        store.SetNotePhrases(2, new List<string> { "User2片語" });
        store.SetSetupGuideHidden(2, false);

        // 修改 User 1 常用語
        store.SetNotePhrases(1, new List<string>());

        // User 1 狀態：常用語空、hidden=true、entry 仍在
        var u1 = store.Get(1);
        Assert.True(u1.SetupGuideHidden);
        Assert.Empty(u1.NotePhrases);
        Assert.True(store.Get().ContainsKey(1));

        // User 2 狀態：不受影響
        var u2 = store.Get(2);
        Assert.False(u2.SetupGuideHidden);
        Assert.Equal(new[] { "User2片語" }, u2.NotePhrases);
        Assert.True(store.Get().ContainsKey(2));
    }

    [Fact]
    public void Controller_GetAndPut_正常使用者讀寫成功()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        var controller = new MeController(store, _settings, FakeCurrentUser.ForUser(42));

        // 預設為 false
        var initial = controller.GetSetupGuide();
        Assert.False(initial.Data!.Hidden);

        // PUT 更新為 true
        var putRes = controller.SetSetupGuide(new SetSetupGuideRequest { Hidden = true });
        Assert.True(putRes.Data!.Hidden);

        // GET 讀回為 true
        var updated = controller.GetSetupGuide();
        Assert.True(updated.Data!.Hidden);
        Assert.True(store.Get(42).SetupGuideHidden);

        // PUT 更新回 false
        var putRes2 = controller.SetSetupGuide(new SetSetupGuideRequest { Hidden = false });
        Assert.False(putRes2.Data!.Hidden);
        Assert.False(controller.GetSetupGuide().Data!.Hidden);
        Assert.False(store.Get().ContainsKey(42));
    }

    [Fact]
    public void Controller_UserId為0_PUT失敗且store不變()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        var zeroController = new MeController(store, _settings, FakeCurrentUser.ForUser(0));

        // GET 讀取不拋例外，回 false
        var getRes = zeroController.GetSetupGuide();
        Assert.False(getRes.Data!.Hidden);

        // PUT 應拋出 DomainException.Validation("此帳號沒有個人偏好可儲存。")
        var ex = Assert.Throws<DomainException>(() => zeroController.SetSetupGuide(new SetSetupGuideRequest { Hidden = true }));
        Assert.Equal("此帳號沒有個人偏好可儲存。", ex.Message);
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);

        // store 必須維持不變（沒有任何 key 為 0 的 entry）
        Assert.False(store.Get().ContainsKey(0));
        Assert.Empty(store.Get());
    }

    [Fact]
    public void Controller_UserId小於0_PUT失敗且store不變()
    {
        var store = new UserPreferenceStore(_fx.Blob("user_prefs"));
        var negativeController = new MeController(store, _settings, FakeCurrentUser.ForUser(-1));

        var ex = Assert.Throws<DomainException>(() => negativeController.SetSetupGuide(new SetSetupGuideRequest { Hidden = true }));
        Assert.Equal("此帳號沒有個人偏好可儲存。", ex.Message);
        Assert.Empty(store.Get());
    }
}

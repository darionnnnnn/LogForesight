using LogForesight.Core.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="BrandProvider"/>：版面品牌三元素的回退規則。
/// 名稱空白回出廠名；副標與圖示空白是合法選擇，回 null 讓 View 略過節點；有值就原樣回傳（不 Trim）；
/// 設定讀取失敗退回出廠值，不讓裝飾性的品牌拖垮頁面；同一個實例只讀一次設定。
/// </summary>
public sealed class BrandProviderTests
{
    private static readonly SystemSettings Factory = new();

    private static BrandProvider Create(Action<SystemSettings>? configure = null, Exception? throwOnGet = null)
    {
        var store = new FakeSystemSettingsStore { ThrowOnGet = throwOnGet };
        if (configure != null) store.Update(configure);
        return new BrandProvider(store);
    }

    [Fact]
    public void 系統設定未填_名稱用出廠值_圖示為null()
    {
        var info = Create(s => { s.BrandName = ""; s.BrandSubtitle = ""; s.BrandIconDataUri = ""; }).Get();

        Assert.Equal(Factory.BrandName, info.Name);
        Assert.Null(info.Subtitle);
        Assert.Null(info.IconDataUri);
    }

    [Fact]
    public void 全新設定_名稱用出廠名_副標沿用設定預設值()
    {
        var info = Create().Get();

        Assert.Equal("LogForesight", info.Name);
        Assert.Equal(Factory.BrandSubtitle, info.Subtitle);
        Assert.Null(info.IconDataUri);
    }

    [Fact]
    public void 設定了名稱與副標_回傳設定值()
    {
        var info = Create(s => { s.BrandName = "夜巡"; s.BrandSubtitle = "主機預警"; }).Get();

        Assert.Equal("夜巡", info.Name);
        Assert.Equal("主機預警", info.Subtitle);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void 名稱只有空白_回退出廠名(string blank)
    {
        var info = Create(s => s.BrandName = blank).Get();

        Assert.Equal(Factory.BrandName, info.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 副標空白_回傳null(string blank)
    {
        var info = Create(s => s.BrandSubtitle = blank).Get();

        Assert.Null(info.Subtitle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void 圖示空白_IconDataUri為null(string blank)
    {
        var info = Create(s => s.BrandIconDataUri = blank).Get();

        Assert.Null(info.IconDataUri);
    }

    [Fact]
    public void 圖示有值_原樣回傳()
    {
        const string uri = "data:image/png;base64,iVBORw0KGgo=";

        var info = Create(s => s.BrandIconDataUri = uri).Get();

        Assert.Equal(uri, info.IconDataUri);
    }

    [Fact]
    public void 有值的名稱與副標不做Trim_原樣回傳()
    {
        var info = Create(s => { s.BrandName = " 夜巡 "; s.BrandSubtitle = " 副標 "; }).Get();

        Assert.Equal(" 夜巡 ", info.Name);
        Assert.Equal(" 副標 ", info.Subtitle);
    }

    [Fact]
    public void 設定讀取拋例外_退回出廠值而不往上拋()
    {
        var provider = Create(throwOnGet: new InvalidOperationException("blob 讀取失敗"));

        var info = provider.Get();

        Assert.Equal(Factory.BrandName, info.Name);
        Assert.Equal(Factory.BrandSubtitle, info.Subtitle);
        Assert.Null(info.IconDataUri);
    }

    [Fact]
    public void 同一實例重複呼叫回傳同一份快取()
    {
        var store = new FakeSystemSettingsStore();
        store.Update(s => s.BrandName = "第一次");
        var provider = new BrandProvider(store);

        var first = provider.Get();
        store.Update(s => s.BrandName = "第二次");
        var second = provider.Get();

        Assert.Same(first, second);
        Assert.Equal("第一次", second.Name);
    }
}

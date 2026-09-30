using LogForesight.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public class SettingsRevisionTests
{
    [Fact]
    public void 兩個Store實例_每次更新換版本且舊版本可在交易內拒絕()
    {
        using var fx = new EfSqliteFixture();
        var first = new SystemSettingsStore(fx.Blob("system_settings"));
        var second = new SystemSettingsStore(fx.Blob("system_settings"));
        var loaded = first.Get();
        var saved = second.Update(s => s.PrtgEnabled = true);
        Assert.NotEqual(loaded.Revision, saved.Revision);
        Assert.Throws<InvalidOperationException>(() => first.Update(s =>
        {
            if (s.Revision != loaded.Revision) throw new InvalidOperationException("stale");
            s.PrtgEnabled = false;
        }));
        Assert.True(second.Get().PrtgEnabled);
        Assert.Equal(saved.Revision, second.Get().Revision);
    }

    [Fact]
    public void 同時間戳的資料庫並行更新_仍由單調版本擋住()
    {
        using var fx = new EfSqliteFixture();
        fx.Blob("system_settings").Mutate(_ => ("{}", 0));
        using var first = fx.NewContext();
        using var second = fx.NewContext();
        var a = first.Blobs.Single();
        var b = second.Blobs.Single();
        a.Content = "first";
        a.Version++;
        first.SaveChanges();
        b.Content = "second";
        b.Version++;
        Assert.Throws<DbUpdateConcurrencyException>(() => second.SaveChanges());
        Assert.Equal("first", fx.Blob("system_settings").Read());
    }
}

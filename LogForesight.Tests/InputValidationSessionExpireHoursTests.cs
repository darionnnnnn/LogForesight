using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Extensions;
using LogForesight.Web.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Jwt:ExpireHours（登入閒置時限）的上限處理（第 80 輪批次 E3）：超過 24 小時絕對上限視為 24，只警告不擋啟動。</summary>
public class InputValidationSessionExpireHoursTests
{
    private static WebAppSettings Settings(int hours) => new()
    {
        Jwt = new SessionSettings { ExpireHours = hours },
        Auth = new AuthSettings
        {
            Provider = "Ldap",
            ServerAdmin = new ServerAdminSettings { Account = "svc-lfadmin", PasswordHash = PasswordHasher.Hash("Any-P@ssw0rd-1") }
        },
        Storage = new StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory }
    };

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(24, 24)]
    [InlineData(25, 24)]
    [InlineData(48, 24)]
    public void 有效閒置時限_最多24小時(int configured, int effectiveHours) =>
        Assert.Equal(TimeSpan.FromHours(effectiveHours), Settings(configured).Jwt.EffectiveIdleTimeout);

    [Fact]
    public void 剛好24_沒有警告_25_有一行中文警告且不擋啟動()
    {
        var at = Settings(24);
        Assert.Null(Record.Exception(() => at.Validate(strict: false, "Development")));
        Assert.Empty(at.StartupWarnings());

        var over = Settings(25);
        Assert.Null(Record.Exception(() => over.Validate(strict: false, "Development")));
        var warning = Assert.Single(over.StartupWarnings());
        Assert.Contains("24 小時", warning);
        Assert.Contains("Jwt:ExpireHours", warning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 零與負數_維持啟動錯誤(int hours)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Settings(hours).Validate(strict: false, "Development"));
        Assert.Contains("Jwt:ExpireHours", ex.Message);
    }

    [Theory]
    [InlineData(24 * 365, true)]
    [InlineData(24 * 365 + 1, false)]
    [InlineData(int.MaxValue, false)]
    public void 極端值_超過一年換算的小時數才是啟動錯誤_訊息中文(int hours, bool allowed)
    {
        var settings = Settings(hours);
        var ex = Record.Exception(() => settings.Validate(strict: false, "Development"));
        if (allowed) Assert.Null(ex);
        else
        {
            var message = Assert.IsType<InvalidOperationException>(ex).Message;
            Assert.Contains("Jwt:ExpireHours", message);
            Assert.Matches("[一-鿿]", message);
        }
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(24, 24)]
    [InlineData(25, 24)]
    [InlineData(24 * 365, 24)]
    public void cookie選項的閒置時限取有效值(int configured, int effectiveHours)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISystemSettingsStore>(new FakeSystemSettingsStore());
        services.AddLogForesightAuth(Settings(configured));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SessionCookie.Scheme);

        Assert.Equal(TimeSpan.FromHours(effectiveHours), options.ExpireTimeSpan);
    }
}

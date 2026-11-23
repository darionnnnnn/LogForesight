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

    /// <summary>
    /// 大值不擋啟動：換算一律先夾到 24 小時（EffectiveIdleTimeout），不會溢位；既有站台填了大值升級後照常啟動，只多一行警告。
    /// </summary>
    [Theory]
    [InlineData(24 * 365 + 1)]
    [InlineData(int.MaxValue)]
    public void 極大值_不擋啟動_以24小時計並警告(int hours)
    {
        var settings = Settings(hours);
        Assert.Null(Record.Exception(() => settings.Validate(strict: false, "Development")));
        Assert.Equal(TimeSpan.FromHours(24), settings.Jwt.EffectiveIdleTimeout);
        Assert.Single(settings.StartupWarnings());
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(24, 24)]
    [InlineData(25, 24)]
    [InlineData(int.MaxValue, 24)]
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

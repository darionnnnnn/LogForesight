using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 日層級指派的派工閘門（已知雜訊／不再打擾／靜音）相依在建構式是選用參數，null 時閘門靜默跳過——
/// 正式 DI 少註冊一個，閘門就無聲失效、服務層測試照樣全綠。這裡以正式註冊解析服務，守住三個相依都有注入。
/// </summary>
public class DayHandlingGateWiringTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Theory]
    [InlineData("_noiseMarks")]
    [InlineData("_cases")]
    [InlineData("_exclusions")]
    public void 正式DI解析的日層級指派服務_派工閘門相依皆已注入(string fieldName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddStorage(new WebAppSettings());
        services.AddLogForesightServices();
        services.AddScoped<LogForesight.Web.Auth.ICurrentUser>(_ => new FakeCurrentUser());
        services.AddHttpContextAccessor();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<DayHandlingCommandService>();
        var field = typeof(DayHandlingCommandService).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.NotNull(field!.GetValue(service));
    }
}

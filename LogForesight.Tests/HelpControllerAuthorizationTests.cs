using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public class HelpControllerAuthorizationTests
{
    private static HelpController Create(FakeCurrentUser user, bool aiAvailable)
    {
        var content = new HelpContentService();
        var qa = new HelpQaService(content, new FakeWebAi { Available = aiAvailable }, user);
        // AskAvailable 不碰 setupState，傳 null 即可
        return new HelpController(content, qa, null!, user);
    }

    [Fact]
    public void Ask需要Maintain能力()
    {
        var method = typeof(HelpController).GetMethod(nameof(HelpController.Ask))!;
        var attr = Assert.Single(method.GetCustomAttributes(typeof(PermissionAttribute), true).Cast<PermissionAttribute>());
        var caps = Assert.IsType<Capability[]>(Assert.Single(attr.Arguments!));
        Assert.Equal(new[] { Capability.Maintain }, caps);
    }

    [Fact]
    public void 無Maintain時ask_available回200且不可用_即使AI已設定()
    {
        var controller = Create(FakeCurrentUser.WithCapabilities(Capability.Handle), aiAvailable: true);
        var result = controller.AskAvailable();
        Assert.True(result.Success);
        Assert.False(result.Data!.Available);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 有Maintain時ask_available與AI可用性一致(bool aiAvailable)
    {
        var controller = Create(FakeCurrentUser.WithCapabilities(Capability.Maintain), aiAvailable);
        Assert.Equal(aiAvailable, controller.AskAvailable().Data!.Available);
    }
}

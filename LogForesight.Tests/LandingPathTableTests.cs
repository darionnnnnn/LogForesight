using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using Xunit;

namespace LogForesight.Tests;

/// <summary>登入落點表：各角色（以 <see cref="RoleCapabilityMap"/> 的能力組合）各一列；開發維運落到排程作業頁。</summary>
public class LandingPathTableTests
{
    public static TheoryData<string, UserRole[], bool, string> Rows() => new()
    {
        { "處理人（User）", new[] { UserRole.User }, false, "/handlers/7" },
        { "管理員（Admin）", new[] { UserRole.Admin }, false, "/" },
        { "主管（Manager）", new[] { UserRole.Manager }, false, "/" },
        { "開發維運（Dev）", new[] { UserRole.Dev }, false, "/runs" },
        { "處理人兼開發維運", new[] { UserRole.User, UserRole.Dev }, false, "/" },
        { "管理員兼開發維運", new[] { UserRole.Admin, UserRole.Dev }, false, "/" },
        { "救援帳號（沒有群組）", Array.Empty<UserRole>(), true, "/" },
        { "救援帳號即使帶 Dev 能力", new[] { UserRole.Dev }, true, "/" }
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void 落點表_各角色各一列(string label, UserRole[] roles, bool isServerAdmin, string expected)
    {
        var capabilities = RoleCapabilityMap.For(roles);

        Assert.True(expected == AuthController.LandingPathFor(7, isServerAdmin, capabilities), label);
    }

    [Fact]
    public void 落點表_主機負責人只有Handle_落到自己的工作頁()
    {
        // 主機負責人（沒有交辦單）：不靠群組角色，只被補上 Handle（見 UserCapabilityResolver 的負責人路徑）
        var capabilities = new HashSet<Capability> { Capability.Handle };

        Assert.Equal("/handlers/7", AuthController.LandingPathFor(7, false, capabilities));
    }

    [Theory]
    [InlineData(new[] { Capability.DevMonitor }, "/runs")]
    [InlineData(new[] { Capability.DevMonitor, Capability.ViewAll }, "/runs")]
    // 邊界：多了 Handle 或 Assign 任一個就不是「只看監控」（Handle 且沒有管理能力仍落到自己的工作頁）
    [InlineData(new[] { Capability.DevMonitor, Capability.Handle }, "/handlers/7")]
    [InlineData(new[] { Capability.DevMonitor, Capability.Handle, Capability.ViewAll }, "/")]
    [InlineData(new[] { Capability.DevMonitor, Capability.Assign }, "/")]
    public void 落點邊界_DevMonitor加上Handle或Assign就不落到排程頁(Capability[] capabilities, string expected)
    {
        Assert.Equal(expected, AuthController.LandingPathFor(7, false, capabilities.ToHashSet()));
    }
}

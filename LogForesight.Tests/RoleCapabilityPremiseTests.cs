using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 79 輪批次 G5：交辦的改派、拆單、取消、退回、管理結案在服務層不檢查可見範圍，
/// 完全依賴「持有 Assign 的角色一定也持有 ViewAll」。這個前提只靠對照表維持；
/// 日後把 Assign 開給沒有 ViewAll 的角色，那五個寫入動作會立刻變成越權入口，這裡先紅。
/// </summary>
public class RoleCapabilityPremiseTests
{
    [Fact]
    public void 持有交辦能力的角色都持有全站檢視()
    {
        foreach (var role in Enum.GetValues<UserRole>())
        {
            var caps = RoleCapabilityMap.For(role);
            if (caps.Contains(Capability.Assign))
                Assert.True(caps.Contains(Capability.ViewAll), $"角色 {role} 有 Assign 卻沒有 ViewAll：交辦寫入動作要先補可見範圍檢查");
        }
        var serverAdmin = RoleCapabilityMap.ForServerAdmin();
        Assert.True(!serverAdmin.Contains(Capability.Assign) || serverAdmin.Contains(Capability.ViewAll));
    }
}

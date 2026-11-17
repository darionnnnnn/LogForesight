using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 協調器的規則例外與使用者可見訊息的對應：交辦單服務層不再各自 try/catch（原本的兩份 Guard 已移除），
/// 由 <c>ApiExceptionFilter</c> 單點處理——對應測試在 <see cref="ApiExceptionFilterTests"/>。
/// 這裡只留「型別登錄」與「框架層例外原文不外洩」的規則。
/// </summary>
public class WorkOrderGuardLeakTests
{
    [Fact]
    public void WorkOrderRuleException已登錄為使用者可見且是InvalidOperationException子型別()
    {
        var ex = new WorkOrderRuleException("x");
        Assert.True(UserFacingMessage.IsUserFacing(ex));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void 框架層例外不是使用者可見型別_原文不外洩()
    {
        Assert.False(UserFacingMessage.IsUserFacing(new InvalidOperationException("Sequence contains no elements")));
        Assert.False(UserFacingMessage.IsUserFacing(new ArgumentException("Value cannot be null. (Parameter 'source')")));
        Assert.Equal("fallback", UserFacingMessage.For(new InvalidOperationException("Sequence contains no elements"), "fallback"));
    }
}

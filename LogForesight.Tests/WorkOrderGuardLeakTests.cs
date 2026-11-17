using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦單服務的 <c>Guard</c>：只把協調器自擲的 <see cref="WorkOrderRuleException"/> 轉成給使用者的驗證錯誤；
/// 框架層擲的 InvalidOperationException／ArgumentException（英文內部細節）不得外洩，要原樣往上交給 ApiExceptionFilter。
/// </summary>
public class WorkOrderGuardLeakTests
{
    public static IEnumerable<object[]> Guards() => new[]
    {
        new object[] { typeof(WorkOrderCommandService) },
        new object[] { typeof(WorkOrderReplyService) },
    };

    private static T Invoke<T>(Type owner, Func<T> action)
    {
        var guard = owner.GetMethod("Guard", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T));
        try { return (T)guard.Invoke(null, new object[] { action })!; }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    [Theory]
    [MemberData(nameof(Guards))]
    public void 規則例外轉成Validation並保留中文訊息(Type owner)
    {
        var ex = Assert.Throws<DomainException>(() =>
            Invoke<int>(owner, () => throw new WorkOrderRuleException("交辦單 1 已結案。")));
        Assert.Equal("交辦單 1 已結案。", ex.Message);
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Guards))]
    public void 框架層InvalidOperationException不轉成使用者訊息_原文不外洩(Type owner)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Invoke<int>(owner, () => throw new InvalidOperationException("Sequence contains no elements")));
        Assert.IsNotType<DomainException>(ex);
        Assert.False(UserFacingMessage.IsUserFacing(ex));
        Assert.Equal("fallback", UserFacingMessage.For(ex, "fallback"));
    }

    [Theory]
    [MemberData(nameof(Guards))]
    public void 框架層ArgumentException同樣不外洩(Type owner)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Invoke<int>(owner, () => throw new ArgumentException("Value cannot be null. (Parameter 'source')")));
        Assert.False(UserFacingMessage.IsUserFacing(ex));
    }

    [Fact]
    public void WorkOrderRuleException已登錄為使用者可見且是InvalidOperationException子型別()
    {
        var ex = new WorkOrderRuleException("x");
        Assert.True(UserFacingMessage.IsUserFacing(ex));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}

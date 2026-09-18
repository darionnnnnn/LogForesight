using System.Reflection;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦單事件動作代碼的中文對照守門：<see cref="WorkOrderTextHelpers.ActionText"/> 對未知代碼原樣回傳，
/// 新增 <see cref="WorkOrderEventActions"/> 常數卻忘了補對照時，時間軸會直接露出英文代碼。
/// 以反射列舉全部常數，逐一斷言有中文對照（比照 HandlingActionTextTests）。
/// </summary>
public class WorkOrderEventTextTests
{
    public static IEnumerable<object[]> AllActions() =>
        typeof(WorkOrderEventActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => new object[] { f.Name, (string)f.GetRawConstantValue()! });

    [Fact]
    public void 反射有列到常數_含回覆()
    {
        var codes = AllActions().Select(a => (string)a[1]).ToList();
        Assert.Contains(WorkOrderEventActions.Replied, codes);
        Assert.Equal(10, codes.Count);
    }

    [Fact]
    public void 未知代碼原樣回傳_守門斷言的前提()
    {
        Assert.Equal("no_such_action", WorkOrderTextHelpers.ActionText("no_such_action"));
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void 每個事件動作都有中文對照(string name, string code)
    {
        Assert.NotEqual(code, WorkOrderTextHelpers.ActionText(code));
        Assert.False(string.IsNullOrWhiteSpace(WorkOrderTextHelpers.ActionText(code)), name);
    }
}

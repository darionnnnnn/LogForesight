using System.Reflection;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理歷程動作代碼的中文對照守門：<see cref="HandlingTextHelpers.ActionText"/> 對未知代碼原樣回傳代碼本身，
/// 所以新增 <see cref="HandlingActions"/> 常數卻忘了補對照時，畫面會直接露出英文代碼。
/// 以反射列舉全部常數，逐一斷言回傳值不等於代碼本身。
/// </summary>
public class HandlingActionTextTests
{
    public static IEnumerable<object[]> AllActions() =>
        typeof(HandlingActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => new object[] { f.Name, (string)f.GetRawConstantValue()! });

    [Fact]
    public void 反射有列到常數_含本段新增兩項()
    {
        var codes = AllActions().Select(a => (string)a[1]).ToList();
        Assert.Contains(HandlingActions.AutoDispatch, codes);
        Assert.Contains(HandlingActions.WorkOrderAttach, codes);
        Assert.True(codes.Count >= 14);
    }

    [Fact]
    public void 未知代碼原樣回傳_守門斷言的前提()
    {
        Assert.Equal("no_such_action", HandlingTextHelpers.ActionText("no_such_action"));
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void 每個動作代碼都有中文對照(string name, string code)
    {
        Assert.NotEqual(code, HandlingTextHelpers.ActionText(code));
        Assert.False(string.IsNullOrWhiteSpace(HandlingTextHelpers.ActionText(code)), name);
    }
}

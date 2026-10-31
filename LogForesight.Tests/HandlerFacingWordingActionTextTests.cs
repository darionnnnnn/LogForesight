using System.Reflection;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪收尾：處理歷程的動作顯示字樣（HandlingTextHelpers.ActionText）會出現在處理人看得到的
/// 風險日詳情與處理歷程，不得含「案件」（CLAUDE.md 紅線：處理人側用「交辦」）。
/// 稽核頁的動作名（AuditQueryService.ActionNames）是管理者專屬，不在此守門範圍。
/// </summary>
public class HandlerFacingWordingActionTextTests
{
    [Fact]
    public void 處理歷程動作顯示字樣不得含案件()
    {
        var actions = typeof(HandlingActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.True(actions.Count >= 10, $"動作代碼數量異常：{actions.Count}");

        var violations = actions
            .Select(a => (Action: a, Text: HandlingTextHelpers.ActionText(a)))
            .Where(x => x.Text.Contains("案件"))
            .Select(x => $"{x.Action} → {x.Text}")
            .ToList();

        Assert.True(violations.Count == 0, "處理歷程動作字樣不得含「案件」：" + string.Join("；", violations));
    }
}

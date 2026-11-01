using System.Reflection;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 稽核動作常數與中文對照表的雙向守門：每個 <see cref="AuditActions"/> 動作常數都要有中文名，
/// 對照表的每個鍵也都要對得到一個動作常數（避免打錯字或留下已移除動作的死列）。
/// 沒有中文名時，稽核頁會直接顯示英文代碼。
/// </summary>
public sealed class AuditActionNamesTests
{
    /// <summary>
    /// 不是「動作」的常數，明確列出（不用模糊規則）：
    /// SystemAccount 是系統自動行為寫在帳號欄的值，不會出現在動作欄。
    /// </summary>
    private static readonly HashSet<string> NonActionConstants = new(StringComparer.Ordinal) { nameof(AuditActions.SystemAccount) };

    private static IReadOnlyList<(string Name, string Value)> ActionConstants() =>
        typeof(AuditActions).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Where(f => !NonActionConstants.Contains(f.Name))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    [Fact]
    public void 每個動作常數都有中文名()
    {
        var names = AuditQueryService.ActionNamesForTest;
        var missing = ActionConstants().Where(c => !names.ContainsKey(c.Value)).Select(c => $"{c.Name} = \"{c.Value}\"").ToList();
        Assert.True(missing.Count == 0, "下列 AuditActions 常數沒有在 AuditQueryService 對照表補中文名：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void 對照表每個鍵都對應一個動作常數()
    {
        var values = ActionConstants().Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var orphans = AuditQueryService.ActionNamesForTest.Keys.Where(k => !values.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(orphans.Count == 0, "下列對照表的鍵不是任何 AuditActions 動作常數的值：\n" + string.Join("\n", orphans));
    }

    [Fact]
    public void 動作常數的值不重複且中文名不空白()
    {
        var constants = ActionConstants();
        Assert.True(constants.Count > 50, "反射抓到的動作常數數量異常偏少，掃描條件可能壞了");
        var duplicates = constants.GroupBy(c => c.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
        Assert.All(AuditQueryService.ActionNamesForTest.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void 權限不足動作常數是access_denied()
    {
        Assert.Equal("access_denied", AuditActions.AccessDenied);
        Assert.Equal("權限不足被拒", AuditQueryService.ActionNamesForTest[AuditActions.AccessDenied]);
    }
}

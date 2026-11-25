using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 U：就緒檢查沒有自己的可見範圍或派工資格判斷（單一判定守門）。
/// 就緒檢查必須呼叫既有判定——候選人快照、<c>WorkOrderDispatcher.IneligibleReason</c>、<c>ScheduleCalculator.HasAnyWindow</c>、
/// <c>MailNotificationService.EligibleEmail</c>、<c>AdminMembersResolver</c>；不得出現可見範圍／能力規則本身。
/// 反例見 <see cref="反例_自己寫可見範圍或資格判斷_會被抓到"/>。
/// </summary>
public class DispatchReadinessGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    private static string ServiceSource() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Services", "DispatchReadinessService.cs"));

    /// <summary>去掉註解後的程式碼（註解可以提到被禁的名稱，程式碼不行）</summary>
    private static string Code(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    /// <summary>回傳違規描述；空＝通過</summary>
    internal static List<string> Violations(string source)
    {
        var code = Code(source);
        var violations = new List<string>();

        // 不得自己算可見範圍或處理能力
        foreach (var banned in new[] { "HostVisibilityResolver", "RoleCapabilityMap", "UserCapabilityResolver", "IVisibilityService",
                     "GetVisibleHostIds", "GroupVisibleHostIds", "VisibleHostIds", "Capability.", "IGroupAccessStore" })
            if (code.Contains(banned, StringComparison.Ordinal))
                violations.Add($"就緒檢查不得自己判斷可見範圍或處理能力：出現 {banned}");

        // 不得自己寫資格條件（暫停接單）；「在池」是候選人快照自己的欄位，讀它不算判斷資格
        foreach (var banned in new[] { ".Paused", ".DispatchPaused" })
            if (code.Contains(banned, StringComparison.Ordinal))
                violations.Add($"就緒檢查不得自己寫派工資格條件：出現 {banned}");

        // 必須呼叫既有判定
        foreach (var required in new[] { "_candidates.Build()", "WorkOrderDispatcher.IneligibleReason(", "ScheduleCalculator.HasAnyWindow(",
                     "MailNotificationService.EligibleEmail(", "AdminMembersResolver.GetAdminMembers(" })
            if (!code.Contains(required, StringComparison.Ordinal))
                violations.Add($"就緒檢查沒有呼叫既有判定：{required}");
        return violations;
    }

    [Fact]
    public void 就緒檢查只呼叫既有判定_沒有自己的可見範圍或資格判斷()
    {
        var violations = Violations(ServiceSource());
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void 反例_自己寫可見範圍或資格判斷_會被抓到()
    {
        var source = ServiceSource();
        Assert.Empty(Violations(source));   // 基準：現況通過

        // 反例一：自己用候選人的可見主機集合判斷
        var ownVisibility = source.Replace("var anyActiveMember =",
            "var own = pool.ByUserId.Values.Any(c => c.VisibleHostIds.Count > 0); var anyActiveMember =");
        Assert.NotEqual(source, ownVisibility);
        Assert.Contains(Violations(ownVisibility), v => v.Contains("VisibleHostIds"));

        // 反例二：自己寫「在池且未暫停」的資格條件
        var ownEligibility = source.Replace("var anyActiveMember =",
            "var own2 = pool.ByUserId.Values.Any(c => c.InPool && !c.Paused); var anyActiveMember =");
        Assert.NotEqual(source, ownEligibility);
        Assert.Contains(Violations(ownEligibility), v => v.Contains(".Paused"));

        // 反例三：拿掉對資格函式的呼叫（改成自己算）
        var noCall = source.Replace("WorkOrderDispatcher.IneligibleReason(", "OwnReason(");
        Assert.Contains(Violations(noCall), v => v.Contains("IneligibleReason"));

        // 反例四：自己用群組角色判斷處理能力
        var ownRole = source.Replace("var anyActiveMember =", "var cap = RoleCapabilityMap.For(UserRole.User); var anyActiveMember =");
        Assert.Contains(Violations(ownRole), v => v.Contains("RoleCapabilityMap"));
    }
}

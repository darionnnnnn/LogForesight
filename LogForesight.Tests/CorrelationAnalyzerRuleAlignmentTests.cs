using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 關聯層事件群組（<see cref="CorrelationAnalyzer"/> 的 internal ID 陣列）是程式碼另外維護的
/// 一份清單，規則表演進（如使用者停用/修改某條規則）後容易與這份清單悄悄漂移不同步。
///
/// 這項檢查原本是 console 專案 <c>SelfTestRunner.CheckCorrelationIdsExistInRules</c> 的一部分
/// （<c>--selftest</c> 手動執行），console 專案隨 Phase 5 退場（docs/archive/WEB-SCHEDULER-PLAN.md §1.5）
/// 一併移除；移到這裡等於是把「手動跑才檢查」升級成「每次建置都檢查」，覆蓋不打折。
///
/// ID 層級的粗略比對（不比對來源字串），用意是抓明顯的漂移，不是精確驗證比對邏輯——
/// 只認「明確列出該 ID」的規則，MatchAllEventIds 規則不算涵蓋（否則任何一條 MatchAll 規則
/// 存在就會讓所有 ID 被誤判為「已涵蓋」，漂移檢查形同虛設）。
/// </summary>
public class CorrelationAnalyzerRuleAlignmentTests
{
    public CorrelationAnalyzerRuleAlignmentTests()
    {
        // 顯式用完整種子初始化，不依賴其他測試對 KnownIssueCatalog 的執行順序副作用
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    }

    private static void AssertAllIdsCoveredByRules(int[] ids)
    {
        var missing = ids.Where(id => !KnownIssueCatalog.Rules.Any(r => r.EventIds.Contains(id))).ToList();
        Assert.True(missing.Count == 0, $"規則表未涵蓋：{string.Join(",", missing)}");
    }

    [Fact]
    public void AccountChangeIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.AccountChangeIds);

    [Fact]
    public void PersistenceSecurityIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.PersistenceSecurityIds);

    [Fact]
    public void AuditTamperIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.AuditTamperIds);

    [Fact]
    public void PermissionChangeIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.PermissionChangeIds);

    [Fact]
    public void DiskErrorIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.DiskErrorIds);

    [Fact]
    public void NtfsErrorIds_全數存在於規則表() => AssertAllIdsCoveredByRules(CorrelationAnalyzer.NtfsErrorIds);

    /// <summary>種子規則一律含 Defender 規則（非舊版 seed 的操作面情境），這裡不需要
    /// SelfTestRunner 那個「規則表整批沒有 Defender 規則就跳過」的分支。</summary>
    [Fact]
    public void DefenderMalwareIds_全數存在於規則表()
    {
        Assert.True(KnownIssueCatalog.HasWatchlist("Microsoft-Windows-Windows Defender"));
        AssertAllIdsCoveredByRules(CorrelationAnalyzer.DefenderMalwareIds);
    }

    [Fact]
    public void DefenderProtectionOffIds_全數存在於規則表()
    {
        Assert.True(KnownIssueCatalog.HasWatchlist("Microsoft-Windows-Windows Defender"));
        AssertAllIdsCoveredByRules(CorrelationAnalyzer.DefenderProtectionOffIds);
    }

    // ── PatternId 目錄完整性（回饋十五輪 A-5）───────────────────────────
    // CorrelationFinding.PatternId 是 required 屬性，已經在編譯期保證每個 findings.Add
    // 呼叫端都有指定；這裡額外釘住目錄本身（CorrelationPatternIds.All）沒有拼字重複——
    // 拼字重複的後果是兩個不同模式共用同一個抑制開關，抑制其中一個會誤傷另一個。

    [Fact]
    public void CorrelationPatternIds目錄內全部識別碼皆非空且不重複()
    {
        Assert.All(CorrelationPatternIds.All, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(CorrelationPatternIds.All.Length, CorrelationPatternIds.All.Distinct().Count());
    }
}

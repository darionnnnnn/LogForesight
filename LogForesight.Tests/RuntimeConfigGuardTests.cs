using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// `LogForesight.Web/runtimeconfig.template.json` 的 GC 設定守門。
///
/// runtime 遇到認不得的 `System.GC.*` 鍵不會報錯，只會靜默忽略——錯鍵的設定看起來有、實際沒有生效，
/// 而且站台不讀回設定就永遠不會發現（`System.GC.Conserve` 就是這樣沉默了許多輪）。
/// 所以這裡以鍵名白名單＋值區間守住：出現白名單外的 `System.GC.*` 鍵、少了必要鍵、或值超出區間都紅。
/// 生效值另由健康頁記憶體卡與啟動 log（`GC.GetConfigurationVariables()`）讀回。
/// </summary>
public class RuntimeConfigGuardTests
{
    /// <summary>runtime 文件認得、本專案使用的 GC 鍵（新增前先確認鍵名，見 docs/README 升級注意事項）</summary>
    private static readonly HashSet<string> AllowedGcKeys = new(StringComparer.Ordinal)
    {
        "System.GC.Server",
        "System.GC.ConserveMemory",
        "System.GC.RetainVM",
        "System.GC.HeapHardLimitPercent",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 LogForesight.sln");
    }

    private static Dictionary<string, JsonElement> ReadConfigProperties()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "runtimeconfig.template.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("configProperties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    [Fact]
    public void GC鍵名全部在白名單內_沒有runtime認不得的錯鍵()
    {
        var unknown = ReadConfigProperties().Keys
            .Where(k => k.StartsWith("System.GC.", StringComparison.Ordinal) && !AllowedGcKeys.Contains(k))
            .ToList();
        Assert.True(unknown.Count == 0, $"runtimeconfig 有白名單外的 GC 鍵（runtime 會靜默忽略）：{string.Join("、", unknown)}");
    }

    [Fact]
    public void 必要的GC鍵都存在且值在合理區間()
    {
        var props = ReadConfigProperties();
        foreach (var key in AllowedGcKeys)
            Assert.True(props.ContainsKey(key), $"runtimeconfig 缺少 {key}");

        Assert.True(props["System.GC.Server"].GetBoolean());
        Assert.False(props["System.GC.RetainVM"].GetBoolean());
        var conserve = props["System.GC.ConserveMemory"].GetInt32();
        Assert.InRange(conserve, 0, 9);
        var hardLimitPercent = props["System.GC.HeapHardLimitPercent"].GetInt32();
        Assert.InRange(hardLimitPercent, 30, 90);
    }
}

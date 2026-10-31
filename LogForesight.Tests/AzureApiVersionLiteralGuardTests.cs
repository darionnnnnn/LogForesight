using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Azure OpenAI 預設 API 版本只能有一個來源（<c>AiProviders.DefaultAzureApiVersion</c>）：
/// 設定模型、DTO、服務、畫面預設散落多份字面，改版時就會漏掉其中一處。
/// 視圖與 JS 一律引用常數（JS 經 data-default 屬性），這裡掃全部原始碼確認沒有字面殘留。
/// 測試裡的字串斷言刻意保留字面，用來偵測常數被誤改。
/// </summary>
public class AzureApiVersionLiteralGuardTests
{
    private const string Literal = "2024-10-21";

    [Fact]
    public void 預設API版本字面只出現在AiProviders()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "LogForesight.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var hits = new List<string>();
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, project), "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (ext != ".cs" && ext != ".cshtml" && ext != ".js") continue;
                var rel = Path.GetRelativePath(root.FullName, file).Replace(Path.DirectorySeparatorChar, '/');
                var parts = rel.Split('/');
                if (parts.Contains("bin") || parts.Contains("obj") || parts.Contains("lib") || parts.Contains("node_modules")) continue;
                if (File.ReadAllText(file).Contains(Literal)) hits.Add(rel);
            }
        }

        Assert.Equal(new[] { "LogForesight.Core/Configuration/AiProviders.cs" }, hits.ToArray());
    }
}

using System.Text.RegularExpressions;

namespace LogForesight.Tests;

/// <summary>
/// 測試共用：讀 wwwroot/img/icons.svg 裡所有 symbol 的 id。
/// 章節目錄圖示（HelpManualLayoutTests）與前端寫死的圖示名守門（IconSpriteTests）都用這一份解析。
/// </summary>
internal static class IconSpriteIds
{
    public static HashSet<string> Load(string repoRoot)
    {
        var svg = File.ReadAllText(Path.Combine(repoRoot, "LogForesight.Web", "wwwroot", "img", "icons.svg"));
        return Regex.Matches(svg, "symbol id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
    }
}

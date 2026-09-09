using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端模組用到共用函式卻忘了 import，會在**模組載入當下**擲 ReferenceError，
/// 讓整頁的 JS 全部不執行——而語法檢查與比對字串的 UI 測試都看不出來
/// （語法是合法的、字串也還在）。這一條把它變成建置時就會紅的錯誤。
///
/// 抽出或搬移模組時最容易發生：把一段程式碼搬到新檔，卻沒把它依賴的 import 一起帶過去。
/// </summary>
public class JsModuleImportTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 每個前端模組用到的共用函式都有匯入()
    {
        var root = FindRepoRoot();
        var jsRoot = Path.Combine(root, "LogForesight.Web", "wwwroot", "js");
        var coreDir = Path.Combine(jsRoot, "core");
        Assert.True(Directory.Exists(coreDir), $"找不到目錄: {coreDir}");

        // 1. 蒐集 core/ 各模組匯出的具名函式
        var exported = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var coreFile in Directory.GetFiles(coreDir, "*.js"))
        {
            var text = File.ReadAllText(coreFile);
            foreach (Match m in Regex.Matches(text, @"^export\s+(?:async\s+)?(?:function|const|let)\s+(\w+)", RegexOptions.Multiline))
            {
                exported[m.Groups[1].Value] = Path.GetFileName(coreFile);
            }
        }

        Assert.True(exported.Count > 0, "core/ 沒有解析到任何匯出，測試本身失效");

        // 2. 每個 pages/ 模組：用到了某個共用函式名，就必須在自己的 import 清單裡
        var problems = new List<string>();
        foreach (var pageFile in Directory.GetFiles(Path.Combine(jsRoot, "pages"), "*.js"))
        {
            var text = File.ReadAllText(pageFile);

            var imported = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, @"import\s*\{([^}]*)\}\s*from", RegexOptions.Singleline))
            {
                foreach (var raw in m.Groups[1].Value.Split(','))
                {
                    // `a as b` 取本地名
                    var name = raw.Trim().Split(new[] { " as " }, StringSplitOptions.None)[^1].Trim();
                    if (name.Length > 0) imported.Add(name);
                }
            }

            // 自己也可能定義同名的函式，那不算漏 import。**巢狀宣告也要算**——
            // 只認頂層的話，函式內部定義的同名 helper 會被誤報成漏 import。
            var locallyDefined = new HashSet<string>(
                Regex.Matches(text, @"^\s*(?:export\s+)?(?:async\s+)?(?:function|const|let|var)\s+(\w+)", RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            foreach (var (name, source) in exported)
            {
                if (imported.Contains(name) || locallyDefined.Contains(name)) continue;

                // 以識別字邊界比對，避免 `formatDate` 命中 `formatDateTime`
                if (Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(name)}\s*\("))
                {
                    problems.Add($"{Path.GetFileName(pageFile)} 用了 {source} 的 {name}() 卻沒有 import");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "模組載入時會擲 ReferenceError，整頁 JS 都不會執行：\n" + string.Join("\n", problems));
    }
}

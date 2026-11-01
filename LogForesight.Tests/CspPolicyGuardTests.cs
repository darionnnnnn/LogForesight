using System.Text.RegularExpressions;
using LogForesight.Web.Security;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// CSP 守門：政策與實際資產不能脫鉤。
/// (a) 靜態 CSS 只要用了 data: URI，政策的 img-src 就必須允許 data:；
/// (b) 所有 view 的 inline script 都必須帶 nonce（沒帶的在強制模式下整段被瀏覽器擋掉）；
/// (c) view 與前端 JS 不得寫 style 屬性與 inline 事件屬性（style-src、script-src 都不放行 inline）。
/// 政策字串取自 <see cref="SecurityHeadersPolicy.BuildCsp"/>（中介層實際使用的同一支建構函式），不另抄一份。
/// </summary>
public sealed class CspPolicyGuardTests
{
    [Fact]
    public void CSS使用data_URI時_政策的img_src必須含data冒號()
    {
        var web = Path.Combine(RepoRoot(), "LogForesight.Web", "wwwroot");
        var cssFiles = Directory.EnumerateFiles(Path.Combine(web, "lib"), "*.css", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "css"), "*.css", SearchOption.TopDirectoryOnly))
            .ToList();
        Assert.NotEmpty(cssFiles);

        var dataUri = new Regex(@"url\(\s*[""']?data:", RegexOptions.IgnoreCase);
        var users = cssFiles.Where(f => dataUri.IsMatch(File.ReadAllText(f))).ToList();

        var policy = SecurityHeadersPolicy.BuildCsp("n", "DENY", "");
        var imgSrc = policy.Split("; ").Single(d => d.StartsWith("img-src ", StringComparison.Ordinal));
        if (users.Count > 0)
            Assert.True(imgSrc.Split(' ').Contains("data:"),
                $"下列 CSS 使用 data: URI，但政策的 img-src 不含 data:（{imgSrc}）：{string.Join(", ", users.Select(Path.GetFileName))}");
    }

    [Fact]
    public void 所有view的inline_script都帶nonce()
    {
        var views = Path.Combine(RepoRoot(), "LogForesight.Web", "Views");
        var scriptTag = new Regex(@"<script\b[^>]*>", RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        var inspected = 0;
        foreach (var file in Directory.EnumerateFiles(views, "*.cshtml", SearchOption.AllDirectories))
        {
            foreach (Match m in scriptTag.Matches(File.ReadAllText(file)))
            {
                if (Regex.IsMatch(m.Value, @"\bsrc\s*=", RegexOptions.IgnoreCase)) continue;   // 外部腳本由 script-src 'self' 放行
                inspected++;
                if (!Regex.IsMatch(m.Value, @"\bnonce\s*=", RegexOptions.IgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(views, file)}: {m.Value}");
            }
        }
        Assert.True(inspected >= 4, $"預期至少掃到 4 段 inline script，實際 {inspected}（守門自身失效？）");
        Assert.True(offenders.Count == 0, "下列 inline script 沒有 nonce：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void view與前端JS沒有style屬性與inline事件屬性()
    {
        var web = Path.Combine(RepoRoot(), "LogForesight.Web");
        var files = Directory.EnumerateFiles(Path.Combine(web, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
            .ToList();
        Assert.True(files.Count >= 20, $"只掃到 {files.Count} 個檔案（守門自身失效？）");

        // 只認 HTML 標籤內的屬性（含 view 的跨行標籤、JS 字串裡的標籤）：<div style="…">、<button onclick="…">。
        // JS 的 element.style.x = …、el.onclick = … 屬性指派與同名變數不受 CSP 限制，不算
        var attr = new Regex(@"<[a-zA-Z][^<>]*?\s(style|on[a-z]+)\s*=", RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (Match m in attr.Matches(text))
            {
                var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(web, f)}:{line}: {m.Value.ReplaceLineEndings(" ")}");
            }
        }
        Assert.True(offenders.Count == 0, "下列位置有 inline style 或事件屬性，強制 CSP 下會被擋：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }
}

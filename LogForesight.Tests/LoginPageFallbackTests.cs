using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

public class LoginPageFallbackTests
{
    [Fact]
    public void LoginForm_ShouldHaveMethodPost()
    {
        var content = GetLoginCshtmlContent();
        var formMatch = Regex.Match(content, @"<form\b[^>]*id=""lf-login-form""[^>]*>");
        Assert.True(formMatch.Success, "Login.cshtml 應包含 id=\"lf-login-form\" 的 form 標籤");
        Assert.Contains("method=\"post\"", formMatch.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 降級區塊的三件事都要在「module 未就緒」這個條件內：顯示錯誤、擋下原生送出、停用登入鈕。
    /// 只斷言「檔案裡有 LF_LOGIN_READY 這個字」是同義反覆——把區塊內容整段刪掉仍會綠。
    /// 專案沒有瀏覽器端測試管線，靜態檢查是這裡能做到的上限，因此至少要驗到區塊內容與守衛方向。
    /// </summary>
    [Fact]
    public void LoginCshtml_ShouldContainLfLoginReadyDetection()
    {
        var content = GetLoginCshtmlContent();

        var guard = Regex.Match(content, @"if\s*\(\s*window\.LF_LOGIN_READY\s*!==\s*true\s*\)\s*\{(.+?)\n\s*\}\s*\}\s*\);", RegexOptions.Singleline);
        Assert.True(guard.Success, "Login.cshtml 應有 window.LF_LOGIN_READY !== true 的降級守衛區塊（守衛方向不可相反）");

        var body = guard.Groups[1].Value;
        Assert.Contains("頁面資源載入失敗", body);
        Assert.Contains("preventDefault", body);
        Assert.Contains("disabled", body);
    }

    [Fact]
    public void LoginJs_ShouldSetLfLoginReadyAfterInitBrandAlign()
    {
        var jsPath = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "login.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");

        var lines = File.ReadAllLines(jsPath);
        int brandAlignLineIndex = -1;
        int readyLineIndex = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("initBrandAlign()"))
            {
                brandAlignLineIndex = i;
            }
            if (lines[i].Contains("window.LF_LOGIN_READY = true"))
            {
                readyLineIndex = i;
            }
        }

        Assert.True(brandAlignLineIndex >= 0, "login.js 應包含 initBrandAlign()");
        Assert.True(readyLineIndex >= 0, "login.js 應包含 window.LF_LOGIN_READY = true");
        Assert.True(readyLineIndex > brandAlignLineIndex, "window.LF_LOGIN_READY = true 應出現在 initBrandAlign() 之後");
    }

    [Fact]
    public void LoginCshtml_ShouldContainFallbackPromptText()
    {
        var content = GetLoginCshtmlContent();
        Assert.Contains("頁面資源載入失敗", content);
    }

    private static string GetLoginCshtmlContent()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Login.cshtml");
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

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
}

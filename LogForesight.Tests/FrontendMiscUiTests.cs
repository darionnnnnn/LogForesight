using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪 G4：對話面板換目標殘留、下載走 api 出口等前端小項的守門。
/// 只切出目標函式主體斷言，避免整檔斷言恆真。
/// </summary>
public class FrontendMiscUiTests
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

    private static string JsRoot() =>
        Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");

    private static string ReadPage(string file) =>
        File.ReadAllText(Path.Combine(JsRoot(), "pages", file)).Replace("\r\n", "\n");

    /// <summary>切出以 declaration 開頭的函式主體（含大括號配對），並確認非空</summary>
    private static string FunctionBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到函式：{declaration}");
        var open = source.IndexOf('{', source.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }

        throw new InvalidOperationException($"函式未閉合：{declaration}");
    }

    [Fact]
    public void 頁面模組不得直接呼叫fetch或location_assign()
    {
        var pagesDir = Path.Combine(JsRoot(), "pages");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(pagesDir, "*.js"))
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"(?<![\w.])fetch\s*\(") || Regex.IsMatch(text, @"location\.assign\s*\("))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "頁面直接 fetch／location.assign 會繞過逾時、401 導頁與錯誤提示，改走 api.js：" + string.Join("、", offenders));
    }

    [Fact]
    public void 兩處下載都走api_download並以暫時連結儲存()
    {
        var calibration = FunctionBody(ReadPage("prtg-calibration.js"), "async function downloadPackage(");
        Assert.Contains("api.download(", calibration);
        Assert.Contains("saveBlobAsFile(", calibration);
        Assert.Contains("'danger'", calibration);

        var admin = ReadPage("prtg-admin.js");
        var exportIdx = admin.IndexOf("prtg-export?from=", StringComparison.Ordinal);
        Assert.True(exportIdx >= 0, "找不到 PRTG 匯出呼叫");
        var around = admin.Substring(Math.Max(0, exportIdx - 400), 1200);
        Assert.Contains("api.download(", around);
        Assert.Contains("saveBlobAsFile(", around);
        Assert.Contains("'danger'", around);

        var api = File.ReadAllText(Path.Combine(JsRoot(), "core", "api.js")).Replace("\r\n", "\n");
        var save = FunctionBody(api, "export function saveBlobAsFile(");
        Assert.Contains("URL.createObjectURL", save);
        Assert.Contains("URL.revokeObjectURL", save);
        Assert.Contains("removeChild", save);
    }

    [Fact]
    public void 對話面板以世代計數器丟棄舊回覆且收尾依是否已選問題決定輸入框()
    {
        var source = ReadPage("chat-panel.js");
        var reset = FunctionBody(source, "function resetConversation(");
        Assert.Contains("generation++", reset);

        var submit = FunctionBody(source, "async function onSubmit(");
        Assert.Contains("const requestGeneration = generation", submit);
        Assert.Contains("requestGeneration !== generation", submit);
        Assert.Contains("setConversationControlsDisabled(true)", submit);
        Assert.Contains("input.disabled = !hasIssue", submit);
        Assert.DoesNotContain("input.disabled = false;", submit);
    }
}

/// <summary>對話面板世代行為：載入 production 的 chat-panel.js，以替身驅動換問題與回覆時序。</summary>
public class ChatPanelGenerationBehaviorTests
{
    [NodeTheory]
    [InlineData("pendingLocksControls")]
    [InlineData("sameConversationReplyKept")]
    [InlineData("staleReplyDropped")]
    [InlineData("staleFailureDropped")]
    [InlineData("finallyKeepsInputDisabledWithoutIssue")]
    public void 對話面板世代行為案例(string caseName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var scriptDir = Path.Combine(dir!.FullName, "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "chat-panel-generation.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--experimental-default-type=module");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(caseName);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("偵測到 node 卻無法啟動行為測試腳本。", ex);
        }

        Assert.NotNull(process);
        // 先非同步讀輸出、逾時就結束整個程序樹再判失敗，避免實作壞掉時 node 不結束而卡死
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
            Assert.Fail($"node 執行逾時：{caseName}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}

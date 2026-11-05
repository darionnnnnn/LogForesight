using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 共用 node 執行器（<see cref="NodeRuntime"/>）本身的行為，以及「只有一個啟動 node 的地方」與
/// 「behavior 案例必須登錄」兩道守門（第 61 輪 M-12／M-13）。
/// </summary>
public sealed class NodeRuntimeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>在背景執行並加 30 秒保底：執行器壞掉（例如逾時不結束）時讓測試失敗，而不是卡死整個測試程序。</summary>
    private static NodeRunResult RunGuarded(Func<NodeRunResult> run)
    {
        var task = Task.Run(run);
        var finished = Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
        Assert.True(finished == task, "NodeRuntime.Run 超過 30 秒仍未回傳（逾時機制失效）");
        return task.GetAwaiter().GetResult();
    }

    [NodeFact]
    public void 永不結束的腳本在逾時後被結束且標記TimedOut()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = RunGuarded(() => NodeRuntime.Run(
            new[] { "--eval", "console.log(process.pid); setInterval(() => {}, 1000);" },
            timeout: TimeSpan.FromSeconds(2)));
        watch.Stop();

        int pid = 0;
        try
        {
            Assert.True(result.TimedOut, "永不結束的腳本應標記 TimedOut");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"逾時後應在 10 秒內回傳，實際 {watch.Elapsed.TotalSeconds:0.0} 秒");
            Assert.True(int.TryParse(result.Stdout.Trim(), out pid), $"逾時仍應回傳已讀到的輸出（pid），實際：{result.Stdout}");

            // 逾時一定要真的結束 node，而不是只放手不管
            var dead = false;
            for (var i = 0; i < 30 && !dead; i++)
            {
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); dead = p.HasExited; }
                catch (ArgumentException) { dead = true; }
                if (!dead) Thread.Sleep(100);
            }
            Assert.True(dead, $"逾時後 node 程序 {pid} 仍存活（沒有 Kill）");
        }
        finally
        {
            if (pid != 0)
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* 已結束 */ }
            }
        }
    }

    [NodeFact]
    public void 先寫滿一百萬字元到stderr再寫stdout不會死鎖()
    {
        var result = RunGuarded(() => NodeRuntime.Run(
            new[] { "--eval", "process.stderr.write('x'.repeat(1024 * 1024)); console.log('ok');" },
            timeout: TimeSpan.FromSeconds(20)));

        Assert.False(result.TimedOut, "stdout／stderr 應同時讀取，不得因 stderr 塞滿而逾時");
        Assert.Contains("ok", result.Stdout);
        Assert.True(result.Stderr.Length >= 1024 * 1024, $"stderr 應完整讀到，實際 {result.Stderr.Length}");
    }

    [NodeFact]
    public void stdin傳入的腳本會被執行()
    {
        var result = RunGuarded(() => NodeRuntime.Run(
            new[] { "--input-type=module" }, stdin: "console.log(1+1)", timeout: TimeSpan.FromSeconds(20)));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2", result.Stdout.Trim());
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("1", true, false)]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("0", false, false)]
    public void LF_REQUIRE_NODE只有設為1且偵測不到node時才要求失敗(string? envValue, bool available, bool expected)
    {
        Assert.Equal(expected, NodeRuntime.ShouldFailWhenMissing(envValue, available));
    }

    [Theory]
    [InlineData("v22.7.0", true)]
    [InlineData("v22.23.4\n", true)]
    [InlineData("v26.13.0", true)]
    [InlineData("v23.0.0-nightly20240101", true)]
    [InlineData("v22.6.9", false)]
    [InlineData("v20.19.0", false)]
    [InlineData("", false)]
    [InlineData("not a version", false)]
    [InlineData(null, false)]
    public void 版本下限判定_不封頂_解析不了不算(string? output, bool expected)
    {
        Assert.Equal(expected, NodeRuntime.IsSupportedVersion(output));
    }

    [Fact]
    public void 不可用原因區分沒有node與版本太舊()
    {
        Assert.Contains("偵測不到", NodeRuntime.DescribeUnavailable(null));
        var tooOld = NodeRuntime.DescribeUnavailable("v20.19.0");
        Assert.Contains("v20.19.0", tooOld);
        Assert.Contains(NodeRuntime.MinimumVersion.ToString(), tooOld);
    }

    [Fact]
    public void 找node先看PATH_找不到才探固定位置_都沒有回node()
    {
        var pathDir = Path.Combine("tools", "bin");
        var pathEnv = pathDir + Path.PathSeparator + "other";
        var onPath = Path.Combine(pathDir, OperatingSystem.IsWindows() ? "node.exe" : "node");
        var shimScript = Path.Combine(pathDir, "node");

        Assert.Equal(onPath, NodeRuntime.ResolveExecutable(pathEnv, f => f == onPath || f == "/opt/homebrew/bin/node"));
        // Windows 的 shim 目錄同時有 sh 腳本 node 與 node.exe：要挑 exe，不然 Process.Start 起不來
        if (OperatingSystem.IsWindows())
            Assert.Equal(onPath, NodeRuntime.ResolveExecutable(pathEnv, f => f == onPath || f == shimScript));
        Assert.Equal("/opt/homebrew/bin/node", NodeRuntime.ResolveExecutable(pathEnv, f => f == "/opt/homebrew/bin/node"));
        Assert.Equal("/usr/local/bin/node", NodeRuntime.ResolveExecutable(null, f => f == "/usr/local/bin/node"));
        Assert.Equal("node", NodeRuntime.ResolveExecutable(pathEnv, _ => false));
    }

    [Fact]
    public void 測試設定檔把LF_REQUIRE_NODE帶進測試環境()
    {
        // dotnet test 與 IDE 都讀 csproj 指定的 test.runsettings；讀不到時前端測試會靜默略過
        Assert.Equal("1", Environment.GetEnvironmentVariable("LF_REQUIRE_NODE"));
    }

    [Fact]
    public void 測試專案只有NodeRuntime可以啟動node()
    {
        var root = Path.Combine(FindRepoRoot(), "LogForesight.Tests");
        // 以串接組字，避免本檔自己的守門字串觸發守門
        var needles = new[] { "Process" + "StartInfo", "FileName = \"node\"" };
        var sep = Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Where(f => !string.Equals(Path.GetFileName(f), "NodeRuntime.cs", StringComparison.Ordinal))
            .Where(f => needles.Any(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "下列測試檔自己啟動 node，請改用 NodeRuntime.Run／RunOrFail：" + string.Join("、", offenders));
    }

    [Fact]
    public void 測試不得依賴node實驗旗標()
    {
        // 實驗旗標會隨 node 大版本移除，一移除就是整批行為測試同時紅。以串接組字，避免本檔自己觸發守門
        var needle = "--experimental" + "-";
        var root = Path.Combine(FindRepoRoot(), "LogForesight.Tests");
        var sep = Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".mjs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Where(f => File.ReadAllText(f).Contains(needle, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "下列測試檔仍依賴 node 實驗旗標：" + string.Join("、", offenders));
    }

    private static readonly Regex InlineDataPattern = new(
        "\\[InlineData\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"(?:\\s*,\\s*\"((?:[^\"\\\\]|\\\\.)*)\")?\\s*\\)\\]",
        RegexOptions.Compiled);

    [NodeFact]
    public void 每個behavior腳本的案例都已登錄為InlineData且沒有多登錄()
    {
        var root = Path.Combine(FindRepoRoot(), "LogForesight.Tests");
        var scriptDir = Path.Combine(root, "JsBehavior");
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText).ToList();

        var problems = new List<string>();
        foreach (var script in Directory.EnumerateFiles(scriptDir, "*.behavior.mjs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var scriptName = Path.GetFileName(script);

            // 不帶案例名：各腳本慣例是印出所有案例名
            var run = NodeRuntime.RunOrFail(
                new[] { script }, scriptDir,
                timeout: TimeSpan.FromSeconds(15), label: scriptName);
            Assert.True(run.ExitCode == 0, $"{scriptName} 不帶參數應列出案例名並正常結束：{run.Stdout}{run.Stderr}");
            var actual = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
            Assert.True(actual.Count > 0, $"{scriptName} 沒有列出任何案例名");

            // 登錄：每個 [NodeTheory] 區段內，InlineData 兩參數者看第一參數是否指向該腳本，單參數者看區段是否引用該腳本
            var registered = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            foreach (var source in sources.Where(s => s.Contains(scriptName, StringComparison.Ordinal)))
            {
                foreach (var chunk in source.Split("[NodeTheory]").Skip(1))
                {
                    var method = chunk.IndexOf("public void", StringComparison.Ordinal);
                    var header = method >= 0 ? chunk[..method] : chunk;
                    var body = method >= 0 ? chunk[method..] : string.Empty;
                    var nextMember = body.IndexOf("[Fact]", StringComparison.Ordinal);
                    if (nextMember >= 0) body = body[..nextMember];
                    var chunkRefsScript = (header + body).Contains(scriptName, StringComparison.Ordinal);
                    if (!chunkRefsScript) continue;
                    found = true;
                    foreach (Match m in InlineDataPattern.Matches(header))
                    {
                        if (m.Groups[2].Success)
                        {
                            if (m.Groups[1].Value == scriptName) registered.Add(m.Groups[2].Value);
                        }
                        else
                        {
                            registered.Add(m.Groups[1].Value);
                        }
                    }
                }
            }
            if (!found) { problems.Add($"{scriptName}：找不到執行它的 [NodeTheory] 測試"); continue; }

            var missing = actual.Except(registered).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extra = registered.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (missing.Count > 0) problems.Add($"{scriptName}：腳本有案例但沒有登錄 InlineData（從沒執行過）：{string.Join("、", missing)}");
            if (extra.Count > 0) problems.Add($"{scriptName}：InlineData 登錄了腳本沒有的案例（名稱打錯？）：{string.Join("、", extra)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

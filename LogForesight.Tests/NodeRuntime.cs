using System.Diagnostics;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>一次 node 執行的結果。逾時時 <see cref="TimedOut"/> 為 true，輸出仍是逾時前已讀到的內容。</summary>
public sealed record NodeRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// 前端行為測試需要的 Node.js 執行環境偵測與共用執行器（回饋第 45 輪 B7、第 61 輪 M-12）。
///
/// 本專案至今沒有任何 node 依賴（連 package.json 都沒有），而 CLAUDE.md 明寫
/// 「部署前驗證＝跑 dotnet test」。若沒有 node 就讓那幾題紅，等於在部署機上製造
/// **環境造成的假警報**，「全綠」這個訊號就失去意義——因此偵測不到時是「略過」。
/// 例外：環境變數 <c>LF_REQUIRE_NODE=1</c>（CI 用）表示「這台一定要有 node」，此時偵測不到改為失敗，
/// 免得 CI 的 node 壞掉時整批前端測試靜默變成略過。
///
/// 偵測只做一次並快取（<see cref="Lazy{T}"/>）：每題各 spawn 一次 node 去探測，
/// 光探測就比測試本身還貴。注意「偵測不到 → 略過」與「跑得起來但失敗 → 紅」是兩件事，
/// 後者絕不可以被吞成略過。
///
/// 全專案唯一允許啟動 node 的地方：stdout／stderr 同時非同步讀（循序讀會在 stderr 寫滿管線緩衝時死鎖）、
/// 一律有逾時並結束整個程序樹（node 不結束時測試要失敗而不是卡死）。
/// </summary>
public static class NodeRuntime
{
    /// <summary>偵測到的版本字串（如 <c>v22.23.4</c>）；跑不起來時為 null。</summary>
    private static readonly Lazy<string?> DetectedVersion = new(Probe, isThreadSafe: true);

    private static readonly Lazy<string> Executable = new(
        () => ResolveExecutable(Environment.GetEnvironmentVariable("PATH"), File.Exists), isThreadSafe: true);

    /// <summary>
    /// 先看 PATH；找不到再探 Homebrew 與 /usr/local 的固定位置。macOS 從 IDE 測試總管啟動時
    /// 不繼承 shell 的 PATH，只靠 PATH 會讓前端測試找不到 node。都沒有就回 "node"，交由探測判定不可用。
    /// </summary>
    internal static string ResolveExecutable(string? pathEnv, Func<string, bool> fileExists)
    {
        // Windows 只認 node.exe：Scoop 這類 shim 目錄同時放 sh 腳本 node 與 node.exe，無副檔名那個 Process.Start 跑不起來
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : new[] { "node" };
        foreach (var dir in (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (fileExists(candidate)) return candidate;
            }
        }

        foreach (var candidate in new[] { "/opt/homebrew/bin/node", "/usr/local/bin/node" })
        {
            if (fileExists(candidate)) return candidate;
        }

        return "node";
    }

    /// <summary>
    /// 行為腳本不帶旗標直接以 ES Modules 執行，靠 22.7 起預設開啟的 ESM 語法偵測（專案沒有 package.json）。
    /// 全專案只在這裡判定版本；低於下限視同不可用，分流同「沒有 node」。
    /// </summary>
    internal static readonly Version MinimumVersion = new(22, 7);

    public static bool IsAvailable => IsSupportedVersion(DetectedVersion.Value);

    /// <summary><c>node --version</c> 的輸出是否達到 <see cref="MinimumVersion"/>；解析不了一律不算。</summary>
    internal static bool IsSupportedVersion(string? versionOutput)
    {
        var text = (versionOutput ?? "").Trim().TrimStart('v').Split('-')[0];
        return Version.TryParse(text, out var version) && version >= MinimumVersion;
    }

    /// <summary>不可用的原因：區分「沒有 node」與「版本太舊」，訊息才對得到要做的事。</summary>
    internal static string DescribeUnavailable(string? detectedVersion) =>
        detectedVersion == null
            ? $"偵測不到可執行的 node（需要 {MinimumVersion} 以上）"
            : $"偵測到 node {detectedVersion}，低於最低需求 {MinimumVersion}";

    public const string RequireNodeVariable = "LF_REQUIRE_NODE";

    public static string SkipReason =>
        "這題需要 Node.js 執行前端行為測試腳本（LogForesight.Tests/JsBehavior/），" +
        $"{DescribeUnavailable(DetectedVersion.Value)}，因此略過。要執行請安裝 Node.js（https://nodejs.org）並確認 node 在 PATH 上。";

    /// <summary>已設定 LF_REQUIRE_NODE=1 但偵測不到 node 時要讓測試失敗（而不是略過）。</summary>
    public static bool ShouldFailWhenMissing(string? envValue, bool available) =>
        !available && envValue == "1";

    /// <summary>目前行程的 LF_REQUIRE_NODE 是否要求一定要有 node。</summary>
    public static bool RequireNode => Environment.GetEnvironmentVariable(RequireNodeVariable) == "1";

    /// <summary>已設定 LF_REQUIRE_NODE=1 卻偵測不到 node → 直接讓測試失敗。</summary>
    public static void EnsureAvailableOrFail()
    {
        if (ShouldFailWhenMissing(Environment.GetEnvironmentVariable(RequireNodeVariable), IsAvailable))
            Assert.Fail($"已設定 LF_REQUIRE_NODE=1 但{DescribeUnavailable(DetectedVersion.Value)}：前端行為測試無法執行。請安裝 Node.js 並確認 node 在 PATH 上。");
    }

    /// <summary>
    /// 執行 node。stdout／stderr 同時非同步讀；寫完 stdin 後關閉；逾時（預設 60 秒）會結束整個程序樹，
    /// <see cref="NodeRunResult.TimedOut"/> 為 true 並仍回傳已讀到的輸出。
    /// </summary>
    public static NodeRunResult Run(
        IReadOnlyList<string> args, string? workingDirectory = null, string? stdin = null, TimeSpan? timeout = null)
    {
        EnsureAvailableOrFail();
        try
        {
            return RunCore(args, workingDirectory, stdin, timeout ?? TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 走到這裡表示偵測時 node 還在、真正要跑時卻啟動不了——那是環境在測試中途壞掉，不是「沒有 node」，照常紅
            throw new InvalidOperationException("偵測到 node 卻無法啟動它（前端行為測試腳本因此沒有跑到）。", ex);
        }
    }

    /// <summary>
    /// 同 <see cref="Run"/>，但逾時直接讓測試失敗（訊息含已讀到的 stdout／stderr）。所有測試呼叫端都走這支，
    /// 逾時處理因此一致。<paramref name="label"/> 用來說明是哪個案例逾時。
    /// </summary>
    public static NodeRunResult RunOrFail(
        IReadOnlyList<string> args, string? workingDirectory = null, string? stdin = null,
        TimeSpan? timeout = null, string? label = null)
    {
        var result = Run(args, workingDirectory, stdin, timeout);
        if (result.TimedOut)
        {
            Assert.Fail($"node 執行逾時（{(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds:0} 秒）：{label}" +
                        $"{Environment.NewLine}{result.Stdout}{result.Stderr}");
        }
        return result;
    }

    private static NodeRunResult RunCore(IReadOnlyList<string> args, string? workingDirectory, string? stdin, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Executable.Value)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 回傳 null");
        // 兩條管線同時讀：循序讀時 stderr 寫滿管線緩衝會讓 node 卡住，stdout 永遠讀不完
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            try
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            catch (IOException) { /* node 已先結束（例如語法錯誤），不再讀 stdin */ }
        }

        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
            process.WaitForExit(5_000);
        }

        // 程序結束後管線很快就讀完；有孫程序抓著管線時最多多等 5 秒，不無限卡住
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5));
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        return new NodeRunResult(exited ? process.ExitCode : -1, stdout, stderr, TimedOut: !exited);
    }

    private static string? Probe()
    {
        try
        {
            var result = RunCore(new[] { "--version" }, null, null, TimeSpan.FromSeconds(15));
            return !result.TimedOut && result.ExitCode == 0 ? result.Stdout.Trim() : null;
        }
        catch
        {
            // 找不到執行檔、被政策擋下、權限不足——一律視為「這台機器沒有 node」
            return null;
        }
    }
}

/// <summary>
/// 需要 node 的 <see cref="TheoryAttribute"/>：偵測不到 node 時設定 Skip，
/// 由 xUnit 顯示為略過（同 <see cref="ScaleFactAttribute"/> 的既有慣例）。
/// 設了 LF_REQUIRE_NODE=1 則不略過，改由 <see cref="NodeRuntime.EnsureAvailableOrFail"/> 讓測試失敗。
/// </summary>
public sealed class NodeTheoryAttribute : TheoryAttribute
{
    public NodeTheoryAttribute()
    {
        if (!NodeRuntime.IsAvailable && !NodeRuntime.RequireNode) Skip = NodeRuntime.SkipReason;
    }
}

/// <summary>需要 node 的 <see cref="FactAttribute"/>，規則同 <see cref="NodeTheoryAttribute"/>。</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (!NodeRuntime.IsAvailable && !NodeRuntime.RequireNode) Skip = NodeRuntime.SkipReason;
    }
}

using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端行為測試需要的 Node.js 執行環境偵測（回饋第 45 輪 B7）。
///
/// 本專案至今沒有任何 node 依賴（連 package.json 都沒有），而 CLAUDE.md 明寫
/// 「部署前驗證＝跑 dotnet test」。若沒有 node 就讓那幾題紅，等於在部署機上製造
/// **環境造成的假警報**，「全綠」這個訊號就失去意義——因此偵測不到時是「略過」。
///
/// 偵測只做一次並快取（<see cref="Lazy{T}"/>）：每題各 spawn 一次 node 去探測，
/// 光探測就比測試本身還貴。注意「偵測不到 → 略過」與「跑得起來但失敗 → 紅」是兩件事，
/// 後者絕不可以被吞成略過。
/// </summary>
public static class NodeRuntime
{
    private static readonly Lazy<bool> Detected = new(Probe, isThreadSafe: true);

    public static bool IsAvailable => Detected.Value;

    public const string SkipReason =
        "這題需要 Node.js 執行前端行為測試腳本（LogForesight.Tests/JsBehavior/），" +
        "本機偵測不到可執行的 node 因此略過。要執行請安裝 Node.js（https://nodejs.org）並確認 node 在 PATH 上。";

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就算了 */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            // 找不到執行檔、被政策擋下、權限不足——一律視為「這台機器沒有 node」
            return false;
        }
    }
}

/// <summary>
/// 需要 node 的 <see cref="TheoryAttribute"/>：偵測不到 node 時設定 Skip，
/// 由 xUnit 顯示為略過（同 <see cref="ScaleFactAttribute"/> 的既有慣例）。
/// </summary>
public sealed class NodeTheoryAttribute : TheoryAttribute
{
    public NodeTheoryAttribute()
    {
        if (!NodeRuntime.IsAvailable) Skip = NodeRuntime.SkipReason;
    }
}

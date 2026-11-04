using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 取數趟開始時的憑證前置檢查：對「這一趟會用到」的密文先試解一次，解不開的在執行紀錄與輸出各留一行，
/// 讓使用者在這趟一開始就看到原因，而不是等 Sentinel 回 401、PRTG 寫「未啟用」才倒推。
///
/// **只寫 Warn、不寫 Error**：失敗的那一路由該路自己的結果表達（NetIQ 計失敗主機日、PRTG 早退），
/// 前置檢查若寫 Error，憑證壞掉會讓只跑本機分析的趟也被判成 failed（<c>BatchRunStatus.Compute</c>）。
/// 檢查本身擲例外一律吞掉只記 Warn，不得中斷分析（WEB-SPEC §11-4）。
/// </summary>
public static class CredentialPreflight
{
    /// <summary>固定 logger 名稱：執行詳情的 Logger 欄位可據此過濾</summary>
    public const string LoggerName = "CredentialPreflight";

    private static readonly Logger Log = LogManager.GetLogger(LoggerName);

    /// <summary>
    /// 取出「這趟用得到且解不開」的項目顯示名稱（不含任何值）。空密文跳過（沒填不是解不開）。
    /// 解密走 <see cref="CryptoHelper.TryDecrypt"/>，它會設 <see cref="CryptoHelper.DecryptFailureSeen"/>。
    /// </summary>
    public static IReadOnlyList<string> FindUndecryptable(IEnumerable<SecretEntry> inventory)
    {
        var names = new List<string>();
        foreach (var entry in inventory)
        {
            if (!entry.NeededThisRun || string.IsNullOrEmpty(entry.Ciphertext)) continue;
            if (!CryptoHelper.TryDecrypt(entry.Ciphertext, out _))
                names.Add(entry.DisplayName);
        }
        return names;
    }

    /// <summary>
    /// 執行檢查並輸出。<paramref name="inventory"/> 以委派傳入，讓「建立清單」本身的失敗也被這裡吞掉。
    /// </summary>
    /// <returns>解不開的項目顯示名稱；檢查失敗回空清單</returns>
    public static IReadOnlyList<string> Run(IRunConsole console, Func<IReadOnlyList<SecretEntry>> inventory)
    {
        try
        {
            var bad = FindUndecryptable(inventory());
            if (bad.Count == 0) return bad;

            var joined = string.Join("、", bad);
            Log.Warn("取數前憑證檢查：下列憑證無法用目前金鑰解密，本趟略過：{0}（請到對應設定頁重新輸入）", joined);
            console.WriteLine($"\n  ⚠ 下列憑證無法解密，本趟略過：{joined}；請到對應設定頁重新輸入");
            return bad;
        }
        catch (Exception ex)
        {
            // 檢查只是提早提示，失敗不可影響分析
            Log.Warn(ex, "取數前憑證檢查失敗（不影響本次分析）：{0}", ex.Message);
            return Array.Empty<string>();
        }
    }
}

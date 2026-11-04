using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 運行中的憑證復原：儲存任何會寫密文的設定後，重新用目前金鑰試解 <see cref="SecretInventory"/> 全部密文，
/// 全部解得開就清掉 <see cref="CryptoHelper.DecryptFailureSeen"/>，健康頁不必重啟站台即恢復。
/// 只處理「部分憑證無法解密」旗標；<see cref="CryptoKeyBootstrapper.KeyMismatch"/> 只在啟動時處理。
/// </summary>
public static class CryptoRecovery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 重新評估解密失敗旗標。任何例外都只記 Warn、不外拋——儲存動作已成功，不得因此失敗。
    /// </summary>
    public static void Reevaluate(ISystemSettingsStore settingsStore, ISentinelStore sentinelStore)
    {
        try
        {
            var entries = SecretInventory.Build(settingsStore.Get(), sentinelStore.GetAll());
            CryptoHelper.ReevaluateDecryptFailure(entries.Where(e => !e.Retired).Select(e => e.Ciphertext));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Crypto] 儲存後重新評估憑證解密狀態失敗（不影響儲存）：{0}", ex.Message);
        }
    }
}

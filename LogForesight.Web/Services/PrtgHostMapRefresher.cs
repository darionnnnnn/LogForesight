using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 「重算今天的 PRTG 主機對應」的單一入口（docs/PRTG-SPEC.md §4）。
///
/// 對應的兩半分別來自 PRTG 鏡像與主機主檔，任一半改動了都要重算，
/// 否則要等到隔天夜間批次才對得上。呼叫端有四類：人工對應變更、IP 排除清單變更、
/// 主機主檔變更（新增／改 IP／停用／合併）、手動同步結構與對應。
///
/// **失敗不擲例外**：重算是附加動作，它壞掉不該讓「儲存主機」或「新增人工對應」跟著失敗。
/// 回傳警告字串讓呼叫端夾帶在回應裡，使用者才知道對應還是舊的。
/// </summary>
public interface IPrtgHostMapRefresher
{
    /// <summary>重算今天的對應；成功或不需要重算時回 null，失敗回警告字串。</summary>
    string? TryRefreshToday();

    /// <summary>
    /// 對應規則升級（<see cref="PrtgHostMapper.RuleVersion"/>）：已套用、PRTG 未啟用時直接返回 null；
    /// 否則以本機鏡像重算今天的對應，成功後刪除其他日期的舊對應並記下版本。鏡像沒有裝置或任何一步失敗時
    /// 不刪、不記版本（下次啟動再試），回說明字串。
    /// </summary>
    string? TryApplyRuleUpgrade();
}

public class PrtgHostMapRefresher : IPrtgHostMapRefresher
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StorageBackend? _backend;
    private readonly ISystemSettingsStore _settings;

    public PrtgHostMapRefresher(ISystemSettingsStore settings, StorageBackend? backend = null)
    {
        _settings = settings;
        _backend = backend;
    }

    /// <summary>
    /// 重算今天的對應。成功或「不需要重算」時回 null，失敗時回警告字串。
    /// PRTG 未啟用時零成本直接返回——沒有鏡像資料，重算只會把空的對應寫一次。
    /// </summary>
    public string? TryRefreshToday()
    {
        if (_backend == null) return null;

        try
        {
            if (!_settings.Get().PrtgEnabled) return null;

            var hostStore = new HostStore(_backend.Blob("hosts"));
            var mapper = new PrtgHostMapper(
                _backend.PrtgStore(), hostStore, new SilentRunConsole(), new PrtgAddressResolver());
            mapper.MapForDate(DateTime.Today);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "重算今日 PRTG 對應失敗");
            return $"重算今日 PRTG 對應失敗: {ex.Message}";
        }
    }

    public string? TryApplyRuleUpgrade()
    {
        if (_backend == null) return null;

        try
        {
            if (!_settings.Get().PrtgEnabled) return null;

            var versionBlob = _backend.Blob(PrtgHostMapper.RuleVersionBlobKey);
            if (int.TryParse(versionBlob.Read(), out var applied) && applied >= PrtgHostMapper.RuleVersion) return null;

            var store = _backend.PrtgStore();
            if (store.GetAllDevices().Count == 0)
            {
                return "PRTG 主機對應規則已更新，但裝置鏡像是空的；待下次同步結構後再重算。";
            }

            var today = DateTime.Today;
            var mapper = new PrtgHostMapper(store, new HostStore(_backend.Blob("hosts")), new SilentRunConsole(), new PrtgAddressResolver());
            var result = mapper.MapForDate(today);
            var deleted = store.DeleteHostMapsExceptDate(today);
            if (deleted < 0)
            {
                return "PRTG 主機對應規則已更新，但今天的對應沒有寫入任何列；舊對應保留，下次啟動再試。";
            }

            versionBlob.Mutate(_ => (PrtgHostMapper.RuleVersion.ToString(), true));
            Log.Info("PRTG 主機對應規則升級到第 {0} 版：重算 {1:yyyy-MM-dd} 對應（已對應主機 {2} 台），刪除舊規則產生的對應 {3} 列；過去日改沿用新對應。",
                PrtgHostMapper.RuleVersion, today, result.MappedHosts, deleted);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "PRTG 主機對應規則升級失敗");
            return $"PRTG 主機對應規則升級失敗：{ex.Message}";
        }
    }

    /// <summary>對應過程的輸出在這條路徑上沒有讀取端（不是批次執行，沒有執行詳情可看）。</summary>
    private sealed class SilentRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}

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

    /// <summary>對應過程的輸出在這條路徑上沒有讀取端（不是批次執行，沒有執行詳情可看）。</summary>
    private sealed class SilentRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}

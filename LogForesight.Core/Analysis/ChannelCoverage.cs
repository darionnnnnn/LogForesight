namespace LogForesight.Core.Analysis;

/// <summary>
/// 判斷某筆歷史紀錄當天是否讀取了某個頻道——趨勢層與慢速趨勢層用它把「當天沒讀到該頻道」的
/// 日子排除在該頻道簽章的基準之外（假性零會把平均墊低，造成頻道恢復/上線後的正常量被誤判成
/// 「首次出現」或「頻率上升」）。這是既有 Security 特例（<c>SecurityLogAvailable != false</c>）
/// 的一般化，讓新頻道（Defender/RDP）自動享有同一套保護。
/// </summary>
internal static class ChannelCoverage
{
    /// <summary>
    /// 新頻道要累積幾天可靠歷史後，趨勢層才開始對它產生 New/Rising 告警與嚴重度升級（暖身期）。
    /// 防的是「頻道上線第一天所有簽章都是首次出現」的切換日告警風暴——偵測（規則層/關聯層）不受
    /// 影響，只是趨勢雜訊延後幾天再吵。既有頻道的可靠歷史遠多於此值，行為零改變。
    /// </summary>
    public const int WarmupDays = 3;

    /// <summary>該日紀錄是否讀取了指定頻道。</summary>
    public static bool WasRead(DailyAnalysisRecord record, string logName)
    {
        // Linux（docs/FEEDBACK-12-PLAN.md §4.2）一律視為已讀：NetIQ pipeline 傳入
        // BuildStatisticalRecordAsync 的 channels 恆為 null（Linux 取數是整批查詢，成敗與否
        // 影響的是整個主機日有沒有紀錄，不是單一頻道有沒有讀到），若不特判，下面的
        // ChannelsRead==null fallback 只認 System/Application/Security 三個 Windows 頻道名，
        // "Linux" 一律落到最後的 return false——慢速趨勢層會把每一天都當成「頻道未讀」排除，
        // 暖身期永遠跑不完，Linux 簽章的趨勢比對形同永久停擺。
        if (logName.Equals(LogAggregator.LinuxLogName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 新紀錄（本欄位問世後）以實際讀取清單為準。
        if (record.ChannelsRead != null)
        {
            return record.ChannelsRead.Any(c => c.Equals(logName, StringComparison.OrdinalIgnoreCase));
        }

        // 舊紀錄 fallback（同 HostId=0 的降級慣例）：本欄位問世前只掃三個傳統日誌，
        // System/Application 當年一定有讀；Security 沿用既有的 SecurityLogAvailable；
        // 新頻道（Defender/RDP）當年根本不存在 → 一律不算入基準，切換日不會被假性零污染。
        if (logName.Equals(ChannelCatalog.SystemChannel, StringComparison.OrdinalIgnoreCase) ||
            logName.Equals(ChannelCatalog.ApplicationChannel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (logName.Equals(ChannelCatalog.SecurityChannel, StringComparison.OrdinalIgnoreCase))
        {
            return record.SecurityLogAvailable != false;
        }
        return false;
    }
}

namespace LogForesight;

/// <summary>
/// 判斷某筆歷史紀錄當天是否讀取了某個頻道——趨勢層與慢速趨勢層用它把「當天沒讀到該頻道」的
/// 日子排除在該頻道簽章的基準之外（假性零會把平均墊低，造成頻道恢復/上線後的正常量被誤判成
/// 「首次出現」或「頻率上升」）。這是既有 Security 特例（<c>SecurityLogAvailable != false</c>）
/// 的一般化，讓新頻道（Defender/RDP）自動享有同一套保護。
/// </summary>
public static class ChannelCoverage
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

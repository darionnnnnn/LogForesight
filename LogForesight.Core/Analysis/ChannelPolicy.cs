namespace LogForesight.Core.Analysis;

/// <summary>
/// 一個 Event Log 頻道的事件納入政策。決定 <see cref="EventLogService"/> 掃描該頻道時
/// 哪些等級/事件要收進分析——不同頻道的雜訊特性差很多，用同一套規則會不是漏抓就是灌爆。
/// </summary>
internal enum ChannelInclusionKind
{
    /// <summary>傳統 System/Application：只收 Error/Warning/Critical，Information 一律不收（量太大且多為正常運作紀錄）。</summary>
    ErrorWarningOnly,

    /// <summary>Security：FailureAudit 全收；SuccessAudit 只收 watchlist 內的高價值事件（登入成功量極大）。</summary>
    SecurityAudit,

    /// <summary>
    /// Operational 頻道（Defender／RDP）：Error/Warning/Critical 全收；Information 只收 watchlist 內的事件。
    /// 這類頻道的關鍵訊號（Defender 5001 防護關閉、RDP 21/1149 登入）多是 Information 等級，
    /// 靠 watchlist 精挑，避免把整個頻道的正常運作紀錄都吸進來。
    /// </summary>
    OperationalWatchlist
}

/// <summary>單一頻道的名稱、納入政策與 watchlist 推導用的 provider 探測字串。</summary>
internal sealed class ChannelPolicy
{
    /// <summary>Event Log 頻道全名（如 "Security"、"Microsoft-Windows-Windows Defender/Operational"）。</summary>
    public string ChannelName { get; init; } = string.Empty;

    public ChannelInclusionKind Kind { get; init; }

    /// <summary>
    /// 該頻道事件的 provider 名稱（<see cref="System.Diagnostics.Eventing.Reader.EventRecord.ProviderName"/>），
    /// 供 <see cref="KnownIssueCatalog"/> 推導此頻道的 watchlist：凡 SourcePattern 命中此探測字串的
    /// 啟用規則，其 EventIds 聯集即為 watchlist。ErrorWarningOnly 頻道不需 watchlist，留空字串。
    /// </summary>
    public string ProviderProbe { get; init; } = string.Empty;

    /// <summary>
    /// 明列「資訊等級也收」清單：不論頻道政策為何，清單內（提供者＋事件編號）的非錯誤警告事件一律收進分析。
    /// 刻意手工明列、不由規則的事件編號推導——同編號在別的提供者或別的版本可能是每天都有的健康訊息
    /// （例如 NTFS 98 的資訊等級版本），自動推導會把它們變成每天的假告警。空清單＝行為與純政策相同。
    /// </summary>
    public IReadOnlyList<InformationalAllowEntry> InformationalAllowList { get; init; } = Array.Empty<InformationalAllowEntry>();

    /// <summary>事件的提供者名是否命中明列清單（比對語意與規則 SourcePattern 相同：不分大小寫的子字串）。</summary>
    public bool IsInformationalAllowed(string source, int eventId)
    {
        foreach (var entry in InformationalAllowList)
        {
            if (entry.EventId == eventId && source.Contains(entry.ProviderPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>明列清單的一筆：提供者名樣式（照種子規則的 SourcePattern）＋事件編號。</summary>
internal sealed record InformationalAllowEntry(string ProviderPattern, int EventId);

/// <summary>
/// 預設頻道目錄。三個傳統日誌 + Defender / RDP 兩類 Operational 頻道。
/// appsettings.json 的 <c>Analysis.Channels</c> 未設定時即用這份預設。
/// </summary>
internal static class ChannelCatalog
{
    public const string SystemChannel = "System";
    public const string ApplicationChannel = "Application";
    public const string SecurityChannel = "Security";
    public const string DefenderChannel = "Microsoft-Windows-Windows Defender/Operational";
    public const string RdpLsmChannel = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";
    public const string RdpRcmChannel = "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational";

    /// <summary>Security log 事件的 provider 名稱——也是原本寫死在 KnownIssueCatalog 的探測字串。</summary>
    public const string SecurityProbe = "Microsoft-Windows-Security-Auditing";

    /// <summary>
    /// System／Application 的資訊等級必收清單：安裝新服務 7045（Service Control Manager）、
    /// 事件日誌被清除 104（Eventlog）。這兩個事件的提供者中繼資料等級是資訊，頻道政策只收錯誤警告就收不到；
    /// 提供者名照種子規則的 SourcePattern。新增項目要先以 Windows 專屬測試
    /// （<c>EventProviderMetadataWindowsTests</c>）確認等級與訊息樣板，不得整個編號收。
    /// </summary>
    public static readonly IReadOnlyList<InformationalAllowEntry> LegacyInformationalAllowList = new[]
    {
        new InformationalAllowEntry("Service Control Manager", 7045),
        new InformationalAllowEntry("EventLog", 104)
    };

    /// <summary>
    /// Security 的稽核清除 1102 必收清單：1102 是成功稽核，原本靠規則推導的 watchlist 才收得到，
    /// 而該 watchlist 只認來源樣式命中 Security-Auditing 的規則（真實發出者 Eventlog 的那條不貢獻）。
    /// 明列後停用或刪除任何一條 1102 規則都不會讓本機收不到它。
    /// </summary>
    public static readonly IReadOnlyList<InformationalAllowEntry> SecurityInformationalAllowList = new[]
    {
        new InformationalAllowEntry("Eventlog", 1102),
        new InformationalAllowEntry("Security-Auditing", 1102)
    };

    public static readonly IReadOnlyList<ChannelPolicy> Defaults = new List<ChannelPolicy>
    {
        new() { ChannelName = SystemChannel, Kind = ChannelInclusionKind.ErrorWarningOnly, InformationalAllowList = LegacyInformationalAllowList },
        new() { ChannelName = ApplicationChannel, Kind = ChannelInclusionKind.ErrorWarningOnly, InformationalAllowList = LegacyInformationalAllowList },
        new() { ChannelName = SecurityChannel, Kind = ChannelInclusionKind.SecurityAudit, ProviderProbe = SecurityProbe, InformationalAllowList = SecurityInformationalAllowList },
        new() { ChannelName = DefenderChannel, Kind = ChannelInclusionKind.OperationalWatchlist, ProviderProbe = "Microsoft-Windows-Windows Defender" },
        new() { ChannelName = RdpLsmChannel, Kind = ChannelInclusionKind.OperationalWatchlist, ProviderProbe = "Microsoft-Windows-TerminalServices-LocalSessionManager" },
        new() { ChannelName = RdpRcmChannel, Kind = ChannelInclusionKind.OperationalWatchlist, ProviderProbe = "Microsoft-Windows-TerminalServices-RemoteConnectionManager" }
    };

    public static string[] DefaultChannelNames => Defaults.Select(p => p.ChannelName).ToArray();

    /// <summary>找出頻道的政策；未知頻道（使用者在設定裡自行加的）保守以 ErrorWarningOnly 處理。</summary>
    public static ChannelPolicy Resolve(string channelName) =>
        Defaults.FirstOrDefault(p => p.ChannelName.Equals(channelName, StringComparison.OrdinalIgnoreCase))
        ?? new ChannelPolicy { ChannelName = channelName, Kind = ChannelInclusionKind.ErrorWarningOnly };
}

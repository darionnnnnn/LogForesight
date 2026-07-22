namespace LogForesight;

/// <summary>
/// 一個 Event Log 頻道的事件納入政策。決定 <see cref="EventLogService"/> 掃描該頻道時
/// 哪些等級/事件要收進分析——不同頻道的雜訊特性差很多，用同一套規則會不是漏抓就是灌爆。
/// </summary>
public enum ChannelInclusionKind
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
public sealed class ChannelPolicy
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
}

/// <summary>
/// 預設頻道目錄。三個傳統日誌 + Defender / RDP 兩類 Operational 頻道。
/// appsettings.json 的 <c>Analysis.Channels</c> 未設定時即用這份預設。
/// </summary>
public static class ChannelCatalog
{
    public const string SystemChannel = "System";
    public const string ApplicationChannel = "Application";
    public const string SecurityChannel = "Security";
    public const string DefenderChannel = "Microsoft-Windows-Windows Defender/Operational";
    public const string RdpLsmChannel = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";
    public const string RdpRcmChannel = "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational";

    /// <summary>Security log 事件的 provider 名稱——也是原本寫死在 KnownIssueCatalog 的探測字串。</summary>
    public const string SecurityProbe = "Microsoft-Windows-Security-Auditing";

    public static readonly IReadOnlyList<ChannelPolicy> Defaults = new List<ChannelPolicy>
    {
        new() { ChannelName = SystemChannel, Kind = ChannelInclusionKind.ErrorWarningOnly },
        new() { ChannelName = ApplicationChannel, Kind = ChannelInclusionKind.ErrorWarningOnly },
        new() { ChannelName = SecurityChannel, Kind = ChannelInclusionKind.SecurityAudit, ProviderProbe = SecurityProbe },
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

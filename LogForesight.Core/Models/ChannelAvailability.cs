namespace LogForesight;

/// <summary>
/// 本次執行各頻道的讀取狀態三態分類，供 <see cref="LogAnalysisService"/> 誠實申報未檢查項目、
/// 並寫入 <see cref="DailyAnalysisRecord.ChannelsRead"/> 供暖身與趨勢基準判斷。
///
/// 「不存在」（主機未安裝該角色）與「被拒」（有頻道但權限不足）刻意分開：
/// 被拒是應該有卻讀不到的偵測盲區，值得申報；不存在則是該偵測本來就不適用於這台主機。
/// </summary>
public class ChannelAvailability
{
    /// <summary>本次成功讀取的頻道名稱。</summary>
    public List<string> Read { get; init; } = new();

    /// <summary>存在但存取被拒的頻道（權限不足）——偵測盲區。</summary>
    public List<string> Denied { get; init; } = new();

    /// <summary>主機上不存在的頻道（未安裝對應角色）——該偵測不適用。</summary>
    public List<string> Missing { get; init; } = new();

    public bool WasRead(string channelName) => Read.Any(c => c.Equals(channelName, StringComparison.OrdinalIgnoreCase));
    public bool WasDenied(string channelName) => Denied.Any(c => c.Equals(channelName, StringComparison.OrdinalIgnoreCase));
    public bool WasMissing(string channelName) => Missing.Any(c => c.Equals(channelName, StringComparison.OrdinalIgnoreCase));
}

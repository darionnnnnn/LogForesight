using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// 未回報主機的 PRTG 現況提示：用 PRTG 鏡像裡 sensor 的現況狀態，
/// 分辨「主機真的離線」與「主機在線但日誌取數端出問題」。全站唯一的判定。
/// </summary>
public static class PrtgPresenceHint
{
    /// <summary>主機沒有任何 ok 的 PRTG 對應</summary>
    public const string NoMap = "no-map";

    public const string Down = "down";
    public const string Unknown = "unknown";
    public const string Up = "up";

    /// <summary>
    /// 有 availability 分類的 sensor 時只看它們（Ping 通不通才是「主機在不在」的直接證據，
    /// 流量掉線不代表主機離線）；沒有時退而看全部 sensor。
    /// 判定順序：空 → unknown；任一 Down → down；全部 Unknown／空值 → unknown；否則 up。
    /// </summary>
    public static string Classify(IReadOnlyList<(string? Status, string? Category)> sensors)
    {
        var availability = sensors
            .Where(s => string.Equals(s.Category, PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase))
            .ToList();
        IReadOnlyList<(string? Status, string? Category)> basis = availability.Count > 0 ? availability : sensors;

        if (basis.Count == 0) return Unknown;
        if (basis.Any(s => PrtgSensorStatuses.IsDown(s.Status))) return Down;
        if (basis.All(s => PrtgSensorStatuses.IsUnknownOrEmpty(s.Status))) return Unknown;
        return Up;
    }
}

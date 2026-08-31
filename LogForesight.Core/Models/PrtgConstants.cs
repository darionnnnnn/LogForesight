namespace LogForesight.Core.Models;

/// <summary>
/// PRTG 資料品質常數。
/// 這五個值必須互相可分辨——<see cref="Paused"/>（PRTG 上被暫停）、<see cref="Unknown"/>（PRTG 回報 unknown 狀態）、
/// <see cref="NoData"/>（該時段根本沒有資料）是三種不同狀態，任何統計與基線計算都不得把它們混為一談；
/// <see cref="Untrusted"/> 是 probe 斷線期間取得的資料。
/// </summary>
public static class PrtgDataQuality
{
    public const string Ok = "ok";
    public const string Paused = "paused";
    public const string Unknown = "unknown";
    public const string NoData = "nodata";
    public const string Untrusted = "untrusted";
}

/// <summary>
/// PRTG 主機對應狀態常數。
/// </summary>
public static class PrtgMapStatus
{
    public const string Ok = "ok";
    public const string Conflict = "conflict";
    public const string Unmatched = "unmatched";
}

/// <summary>
/// PRTG 認證方式常數。
/// <see cref="Token"/>（token）走 apitoken 參數；
/// <see cref="Password"/>（password）走 PRTG 的 username＋passhash 流程
/// （密碼只在換取 passhash 時使用一次，不會出現在後續請求的 URL）。
/// </summary>
public static class PrtgAuthModes
{
    public const string Token = "token";
    public const string Password = "password";

    /// <summary>
    /// 判斷是否為合法的 PRTG 認證方式（Token 或 Password）。
    /// 全站唯一的合法值判定。
    /// </summary>
    public static bool IsValid(string? mode) =>
        mode is Token or Password;
}
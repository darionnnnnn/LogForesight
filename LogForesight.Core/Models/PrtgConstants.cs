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

/// <summary>sensor 語意分類值（lf_prtg_sensors.category）。</summary>
public static class PrtgSensorCategories
{
    public const string Traffic = "traffic";
    public const string Disk = "disk";
    public const string Cpu = "cpu";
    public const string Memory = "memory";
}

/// <summary>分類來源（lf_prtg_sensors.category_source），欄長上限 16。</summary>
public static class PrtgCategorySources
{
    /// <summary>由 type 對照表自動判定</summary>
    public const string Auto = "auto";
}

/// <summary>PRTG sensor type 對語意分類的對照表（不分大小寫）。
/// 內容依實機探測的 type 分布挑選，未列出的 type 不自動分類（留 null）。</summary>
public static class PrtgSensorTypeCategoryMap
{
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SNMP Traffic 64bit"] = PrtgSensorCategories.Traffic,
            ["SNMP Traffic 32bit"] = PrtgSensorCategories.Traffic,
            ["Windows Network Card"] = PrtgSensorCategories.Traffic,
            ["SNMP Disk Free"] = PrtgSensorCategories.Disk,
            ["WMI Free Disk Space (Multi Disk)"] = PrtgSensorCategories.Disk,
            ["SNMP CPU Load"] = PrtgSensorCategories.Cpu,
            ["SNMP Memory"] = PrtgSensorCategories.Memory,
            ["SNMP Linux Meminfo"] = PrtgSensorCategories.Memory,
        };
}

/// <summary>
/// PRTG 認證方式常數。
/// <see cref="Token"/>（token）走 apitoken 參數；
/// <see cref="Password"/>（password）走 PRTG 的 username＋passhash 流程
/// （密碼只在換取 passhash 時使用一次，不會出現在後續請求的 URL）；
/// <see cref="Passhash"/>（passhash）模式由使用者自行提供 passhash，系統不呼叫 getpasshash.htm。
/// </summary>
public static class PrtgAuthModes
{
    public const string Token = "token";
    public const string Password = "password";
    public const string Passhash = "passhash";

    /// <summary>
    /// 判斷是否為合法的 PRTG 認證方式（Token、Password 或 Passhash）。
    /// 全站唯一的合法值判定。
    /// </summary>
    public static bool IsValid(string? mode) =>
        mode is Token or Password or Passhash;
}
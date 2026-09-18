namespace LogForesight.Core.Models;

/// <summary>
/// PRTG sensor 狀態字串（原樣取自 PRTG，未正規化）。實務上會出現帶括號的變體
/// （如 "Down (Acknowledged)"／"Down (Partial)"），因此比對一律用前綴且不分大小寫。
/// </summary>
public static class PrtgSensorStatuses
{
    public const string Up = "Up";
    public const string Down = "Down";
    public const string Warning = "Warning";
    public const string Unknown = "Unknown";
    public const string Paused = "Paused";

    /// <summary>是否為 Down 系列狀態（含 "Down (Acknowledged)" 等變體）</summary>
    public static bool IsDown(string? status) =>
        status != null && status.StartsWith(Down, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否為已於 PRTG 確認的 Down 狀態（IsDown 成立且含 "(Acknowledged)"，不分大小寫）</summary>
    public static bool IsAcknowledged(string? status) =>
        IsDown(status) && status!.Contains("(Acknowledged)", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否為 Up 狀態</summary>
    public static bool IsUp(string? status) =>
        status != null && status.StartsWith(Up, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否為 Warning 狀態</summary>
    public static bool IsWarning(string? status) =>
        status != null && status.StartsWith(Warning, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否為 Unknown 或空值（判定沉默 device 用）</summary>
    public static bool IsUnknownOrEmpty(string? status) =>
        string.IsNullOrWhiteSpace(status) ||
        status.StartsWith(Unknown, StringComparison.OrdinalIgnoreCase);
}

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

    /// <summary>
    /// 站台定時快照自行平均得到的小時值，精度低於 PRTG 的真平均，
    /// Coverage 是「實得樣本數 ÷ 期望樣本數 × 100」。
    /// </summary>
    public const string Sampled = "sampled";
}

/// <summary>
/// PRTG 數值可用性常數。
/// </summary>
public static class PrtgValueUsability
{
    /// <summary>
    /// 取樣列（sampled）算作可用列的 coverage 下限（百分比）。
    /// 保守策略 15 分鐘一次，一小時期望 4 個樣本，要有 3 個（75%）；
    /// 激進 5 分鐘一次期望 12 個，要有 9 個（75%）。
    /// 兩個樣本的平均當一小時的代表值太薄。
    /// </summary>
    public const double SampledMinCoverage = 75.0;
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
    public const string Availability = "availability";
    public const string Hardware = "hardware";

    /// <summary>全部合法分類值（供錯誤訊息列出）。</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Traffic, Disk, Cpu, Memory, Availability, Hardware };

    /// <summary>判斷是否為合法分類值（不分大小寫）。全站唯一的分類合法值判定。</summary>
    public static bool IsValid(string? category) =>
        category != null && All.Contains(category, StringComparer.OrdinalIgnoreCase);
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
            ["Ping"] = PrtgSensorCategories.Availability,
            // 連通性類：服務端點回不回應，down 的語意與 Ping 相同
            ["Port"] = PrtgSensorCategories.Availability,
            ["HTTP"] = PrtgSensorCategories.Availability,
            ["SNTP"] = PrtgSensorCategories.Availability,
            ["DNS (DEPRECATED)"] = PrtgSensorCategories.Availability,
            ["FTP"] = PrtgSensorCategories.Availability,
            ["RDP (Remote Desktop)"] = PrtgSensorCategories.Availability,
            ["Cisco IP SLA"] = PrtgSensorCategories.Availability,
            // 硬體健康：實機清單中唯一的硬體健康 type（溫度、風扇、電源），多半掛在網路設備上
            ["SNMP Cisco System Health"] = PrtgSensorCategories.Hardware,
            // 刻意不列：SNMP Linux Load Average（不是百分比，歸 cpu 會讓資源守門以百分比門檻誤判）、
            // SNMP Custom*／SNMP Library／SSH Script／EXE/Script Advanced／Sensor Factory（內容因環境而異）、
            // System／Probe／Core Health（PRTG 自身健康，守門另以 corehealth 判定）、Uptime 類（時間長度，down 與 Ping 重複）。
            // 需要時由補充對照 PrtgSensorTypeCategoryOverrides 指定。
        };

    /// <summary>
    /// 解析 sensor type 分類。補充對照表優先，再查內建表，都沒有回 null。
    /// </summary>
    public static string? Resolve(string? sensorType, IReadOnlyDictionary<string, string> overrides)
    {
        if (sensorType == null) return null;
        if (overrides.TryGetValue(sensorType, out var fromOverride)) return fromOverride;
        return Map.TryGetValue(sensorType, out var builtIn) ? builtIn : null;
    }

    /// <summary>
    /// 解析補充對照表設定（一行一筆「type=分類」）。全站唯一的解析與驗證實作。
    /// 空白行略過；同 type 重複時後者覆蓋前者。回傳的 Map 不分大小寫，分類值一律轉小寫；
    /// 錯誤行不進 Map，錯誤訊息格式「第 N 行「原文」：原因」。
    /// </summary>
    public static (Dictionary<string, string> Map, List<string> Errors) ParseOverrides(IEnumerable<string>? lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        if (lines == null) return (map, errors);

        var validList = string.Join("、", PrtgSensorCategories.All);
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var eq = raw.IndexOf('=');
            if (eq < 0)
            {
                errors.Add($"第 {lineNo} 行「{raw}」：缺少「=」，格式應為「type=分類」，分類可用 {validList}");
                continue;
            }

            var type = raw[..eq].Trim();
            var category = raw[(eq + 1)..].Trim();
            if (type.Length == 0)
            {
                errors.Add($"第 {lineNo} 行「{raw}」：「=」左邊的 sensor type 不可空白，分類可用 {validList}");
                continue;
            }
            if (!PrtgSensorCategories.IsValid(category))
            {
                errors.Add($"第 {lineNo} 行「{raw}」：分類「{category}」不合法，可用 {validList}");
                continue;
            }

            map[type] = category.ToLowerInvariant();
        }

        return (map, errors);
    }
}

/// <summary>
/// PRTG 流量型感測器類型（值經過每小時流量正規化）。
/// </summary>
public static class PrtgVolumeSensorTypes
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "SNMP Traffic 64bit",
        "SNMP Traffic 32bit",
        "Windows Network Card"
    };

    public static bool IsVolume(string? sensorType) =>
        sensorType != null && Types.Contains(sensorType);
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
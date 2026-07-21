using System.Net;

namespace LogForesight;

/// <summary>
/// NetIQ 主機清單的領域規則（純函數）。
///
/// **為什麼放在 Core 而不是 Web**：批次要用這裡的 <see cref="Pollable"/> 決定今晚去 Sentinel
/// 查哪些主機，Web 要用同一組規則在畫面上標示「這台為什麼沒有被輪巡」。兩邊各寫一份的話，
/// 使用者會看到畫面說正常、實際卻沒查——而那正是本系統最不能有的失敗方式（沒查 ≠ 沒事）。
/// 與 <see cref="RecordStorageShaper"/> 的單點化是同一個理由。
/// </summary>
public static class NetiqHostList
{
    public const string NetiqSource = "netiq";

    /// <summary>清單上「還算數」的 NetIQ 主機：來源為 netiq、啟用中、且不是墓碑列</summary>
    public static IEnumerable<WebHost> Listed(IEnumerable<WebHost> allHosts) =>
        allHosts.Where(h =>
            string.Equals(h.Source, NetiqSource, StringComparison.OrdinalIgnoreCase) &&
            h.Active &&
            h.MergedInto == null);

    /// <summary>
    /// 待歸屬：尚未確定在哪一台 Sentinel 上。登錄時允許不填，由批次自動確認
    /// （見 docs/NETIQ-HOSTLIST-WEB-PLAN.md「Sentinel 歸屬自動確認」），確認前不進日常輪巡。
    /// </summary>
    public static IEnumerable<WebHost> PendingAssignment(IEnumerable<WebHost> allHosts) =>
        Listed(allHosts).Where(h => string.IsNullOrWhiteSpace(h.NetiqServer));

    /// <summary>
    /// IP 衝突組：同一個 IP 有兩台以上的活躍 NetIQ 主機。
    ///
    /// **衝突是導出狀態，不是欄位**——沒有要維護的旗標、沒有狀態機，處置完（改 IP／停用／合併）
    /// 衝突自然消失。每組依 HostId 升冪，第一個就是實際會被輪巡的那台（見 <see cref="Pollable"/>）。
    /// </summary>
    public static List<List<WebHost>> IpConflicts(IEnumerable<WebHost> allHosts) =>
        Listed(allHosts)
            .Where(h => !string.IsNullOrWhiteSpace(h.IpAddress))
            .GroupBy(h => h.IpAddress!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(h => h.HostId).ToList())
            .OrderBy(g => g[0].IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 今晚實際要向 Sentinel 查詢的主機：已歸屬、有 IP、且未落在 IP 衝突的「非首位」。
    ///
    /// 衝突時只輪巡最早建立（HostId 最小）的那台，行為才可預測；被跳過的那些不是靜默忽略——
    /// 呼叫端負責把它們列進機房總覽的「來源狀態」，理由與「無資料主機」一樣：
    /// 沒查到不等於沒事，畫面上必須看得出來。
    /// </summary>
    public static List<WebHost> Pollable(IEnumerable<WebHost> allHosts)
    {
        var hosts = allHosts as IReadOnlyList<WebHost> ?? allHosts.ToList();

        var skipped = IpConflicts(hosts)
            .SelectMany(group => group.Skip(1))
            .Select(h => h.HostId)
            .ToHashSet();

        return Listed(hosts)
            .Where(h => !string.IsNullOrWhiteSpace(h.NetiqServer) &&
                        !string.IsNullOrWhiteSpace(h.IpAddress) &&
                        !skipped.Contains(h.HostId))
            .OrderBy(h => h.HostId)
            .ToList();
    }

    /// <summary>
    /// 未分組主機：沒有任何主機群組，依授權模型**只有 admin 看得到**。
    /// 這是新登錄主機的安全預設（不會意外曝光給錯的部門），但也因此必須有人去補分組，
    /// 所以要能列得出來——否則就成了沒人記得的盲區。來源不限（本機主機同樣適用）。
    /// </summary>
    public static IEnumerable<WebHost> Ungrouped(IEnumerable<WebHost> allHosts) =>
        allHosts.Where(h => h.Active && h.MergedInto == null && h.GroupIds.Count == 0);

    /// <summary>
    /// IP 格式驗證。刻意不接受 <c>IPAddress.TryParse</c> 全部放行的簡寫形式
    /// （"10.1" 會被解讀成 10.0.0.1）——清單上的 IP 是實際要送去 Sentinel 篩選的條件，
    /// 打錯字的結果是「這台主機永遠查無資料」，不如當場擋下來。
    /// </summary>
    public static bool IsValidIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!IPAddress.TryParse(value.Trim(), out var parsed)) return false;

        return parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
               value.Trim().Count(c => c == '.') == 3;
    }

    /// <summary>
    /// 批次貼上的一行：<c>IP[,角色描述]</c>。`#` 開頭為註解、空行忽略（沿用 txt 清單格式，
    /// 讓既有的 txt 內容可以直接貼進來）。回傳 null 代表這一行沒有內容需要處理。
    /// </summary>
    public static NetiqHostLine? ParseLine(string rawLine, int lineNumber)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;

        var parts = line.Split(',', 2);
        var ip = parts[0].Trim();
        var roleDesc = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        return new NetiqHostLine
        {
            LineNumber = lineNumber,
            RawLine = line,
            IpAddress = ip,
            RoleDesc = roleDesc,
            Error = IsValidIp(ip) ? null : $"「{ip}」不是有效的 IP 位址"
        };
    }
}

/// <summary>批次貼上解析後的一行</summary>
public class NetiqHostLine
{
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string RoleDesc { get; set; } = string.Empty;

    /// <summary>不合格的原因；null = 這一行可以匯入</summary>
    public string? Error { get; set; }
}

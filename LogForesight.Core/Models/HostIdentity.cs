namespace LogForesight;

/// <summary>
/// 主機的識別組合：<see cref="HostId"/> 是關聯鍵，<see cref="HostName"/> 僅供
/// 舊紀錄（<see cref="DailyAnalysisRecord.HostId"/> 尚未寫入＝0）的 fallback 比對。
///
/// 兩個欄位一起傳遞的理由：儲存層不認識主機清單（那是 <see cref="IHostStore"/> 的事），
/// 只給 id 就無法比對舊紀錄、只給名稱則改名與合併會斷鏈。由呼叫端一次解析好，
/// 儲存層照著比對即可，不需要反過來依賴主機清單。
/// </summary>
public class HostKey
{
    public long HostId { get; set; }

    public string HostName { get; set; } = string.Empty;

    public static HostKey Of(WebHost host) => new() { HostId = host.HostId, HostName = host.HostName };
}

/// <summary>
/// 「這筆紀錄屬不屬於這組主機識別」的比對規則，單點定義——未來 DB 後端把它翻成
/// <c>WHERE host_id IN (...) OR (host_id = 0 AND host_name IN (...))</c>，語意同一份。
/// 大量紀錄逐筆比對，所以先建索引而不是每筆線性搜尋識別清單。
/// </summary>
public sealed class HostMatcher
{
    private readonly HashSet<long> _hostIds;
    private readonly HashSet<string> _hostNames;

    public HostMatcher(IEnumerable<HostKey> keys)
    {
        var list = keys as IReadOnlyList<HostKey> ?? keys.ToList();
        _hostIds = list.Select(k => k.HostId).ToHashSet();
        _hostNames = list.Select(k => k.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **PK 優先**：紀錄有 HostId 就只認 HostId，主機改名後舊名的紀錄仍歸戶正確；
    /// 只有 HostId 為 0 的舊紀錄才退回名稱比對。
    ///
    /// 刻意不是「id 或名稱任一命中」：那會讓 id 已經對不上的紀錄又從名稱溜回來，
    /// 而查詢的可見範圍正是靠這個比對決定的——寧可嚴格。
    /// </summary>
    public bool Matches(DailyAnalysisRecord record) =>
        record.HostId != 0
            ? _hostIds.Contains(record.HostId)
            : _hostNames.Contains(record.Host);
}

/// <summary>
/// 主機識別的解析：別名展開與合併鏈跟隨。
/// 純函數（吃主機清單、不碰 store），Web 與未來的 DB 後端共用同一份規則。
/// </summary>
public static class HostIdentityResolver
{
    /// <summary>
    /// 別名展開：主機本身 ＋ 所有最終併入它的墓碑列。
    ///
    /// 併入代表「這兩列是同一台實體機器」，所以查詢目標主機時必須一併涵蓋合併前
    /// 寫在舊識別下的歷史——否則 Merge 之後那段歷史就從畫面上消失了。
    /// 回傳的第一個永遠是主機本身，呼叫端（GetOne）可依序擇一。
    ///
    /// **依存活主機判斷而不是只看直接的 MergedInto**：A→B→C 這種鏈上，A 也必須算進 C 的
    /// 識別集合。寫入端雖然擋掉了新的鏈（見 HostAdminService.MergeHost），但那個守則是後來
    /// 才加的，既有資料可能已經有鏈——查詢端自己認得，就不必賭資料乾淨。
    /// </summary>
    public static List<HostKey> Expand(IEnumerable<WebHost> allHosts, long hostId)
    {
        var hosts = allHosts as IReadOnlyList<WebHost> ?? allHosts.ToList();

        var self = hosts.FirstOrDefault(h => h.HostId == hostId);
        if (self == null) return new List<HostKey>();

        var keys = new List<HostKey> { HostKey.Of(self) };
        keys.AddRange(hosts
            .Where(h => h.HostId != hostId && Surviving(hosts, h).HostId == hostId)
            .Select(HostKey.Of));
        return keys;
    }

    /// <summary>
    /// 跟隨 MergedInto 鏈找出存活主機（併入的目標若自己也被併入就繼續往下）。
    /// 步數上限＝主機數：資料異常（兩列互指）時停在目前位置，不會無窮迴圈；
    /// 目標已不存在時留在墓碑本身，至少畫面還指得出東西。
    /// </summary>
    public static WebHost Surviving(IEnumerable<WebHost> allHosts, WebHost host)
    {
        var hosts = allHosts as IReadOnlyList<WebHost> ?? allHosts.ToList();

        var current = host;
        for (int i = 0; i < hosts.Count && current.MergedInto != null; i++)
        {
            var next = hosts.FirstOrDefault(h => h.HostId == current.MergedInto);
            if (next == null) break;
            current = next;
        }
        return current;
    }
}

/// <summary>
/// 紀錄 → 存活主機的解析索引。清單畫面一頁可能有數百筆紀錄，逐筆線性搜尋主機清單
/// 在 2000 台規模下會退化，所以先把「id/名稱 → 存活主機」建好，之後每筆紀錄都是 O(1)。
///
/// 解析到**存活**主機而不是原主機：合併之後，舊識別下的紀錄要顯示成存活主機，
/// 使用者點進去才連得到有效的詳情頁，處理狀態也才對得上（處理狀態以現行主機名稱為鍵）。
/// </summary>
public sealed class HostLookup
{
    private readonly Dictionary<long, WebHost> _byId = new();
    private readonly Dictionary<string, WebHost> _byName = new(StringComparer.OrdinalIgnoreCase);

    public HostLookup(IEnumerable<WebHost> allHosts)
    {
        var hosts = allHosts as IReadOnlyList<WebHost> ?? allHosts.ToList();

        foreach (var host in hosts)
        {
            var surviving = HostIdentityResolver.Surviving(hosts, host);
            _byId[host.HostId] = surviving;
            _byName[host.HostName] = surviving;
        }
    }

    /// <summary>
    /// 紀錄所屬的存活主機；主機清單中查無對應時回 null
    /// （紀錄比主機列更早存在、或該主機列已被刪除，畫面退回顯示紀錄自帶的名稱快照）。
    /// 比對規則與 <see cref="HostMatcher"/> 一致：PK 優先、舊紀錄退回名稱。
    /// </summary>
    public WebHost? For(DailyAnalysisRecord record)
    {
        if (record.HostId != 0)
        {
            return _byId.TryGetValue(record.HostId, out var byId) ? byId : null;
        }

        return _byName.TryGetValue(record.Host, out var byName) ? byName : null;
    }
}

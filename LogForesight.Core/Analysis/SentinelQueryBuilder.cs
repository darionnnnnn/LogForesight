namespace LogForesight;

/// <summary>
/// 規則表→Lucene filter 的純函數產生器（docs/NETIQ-API-REFERENCE.md §4「watchlist→Lucene 產生器」）。
///
/// 只有 Windows 分支：Linux 那台 Sentinel 尚未接入 LogForesight，沒有任何真實 probe 樣本可依據
/// （docs/BACKLOG.md 閘門）。強行寫一份沒有實據的 Linux 產生器只是把猜測換個地方藏，
/// 等 Linux Sentinel 接入、對它跑過 probe 後再補這個分支，屆時介面照這份的形狀加，不是新設計。
/// </summary>
public static class SentinelQueryBuilder
{
    /// <summary>
    /// generic 錯誤等級收集的 sev 下限（候選值，見 <see cref="SentinelFieldMap.MapEntryType"/> 同一份
    /// 未實證說明）。第三輪 probe 量級實測：單主機 sev&gt;=3 全頻道 3872/日、鎖 System+Application 後
    /// 155/日，量級可行；sev&gt;=2 的量尚未實測，先取 2（覆蓋面較保守的一側，寧可稍多不要漏），
    /// 試點時依實測耗時與 Warning/Error 門檻確認一併調整。
    /// </summary>
    public const int GenericErrorSeverityMin = 2;

    /// <summary>
    /// 建立 Q1（主聚合查詢）的 filter：`(IP 批次) AND (規則事件ID聯集 OR generic 錯誤收集)`。
    /// </summary>
    /// <param name="hostIps">本批要查詢的主機 IP（<see cref="SentinelFieldMap.HostIp"/> 篩選對象），
    /// 批次大小上限由呼叫端控制（第一、二輪 probe 實證 IP 篩選 10/50/100 個子句皆被接受）。</param>
    /// <param name="rules">目前啟用的規則表（呼叫端傳入 <c>KnownIssueCatalog.Rules</c> 或測試用清單，
    /// 本函數不讀任何全域可變狀態，維持純函數）。</param>
    /// <exception cref="ArgumentException"><paramref name="hostIps"/> 為空——沒有主機就不該建查詢，
    /// 呼叫端應該在更早的地方擋下（清單空＝零副作用，見 pipeline 既定原則），這裡用例外守住
    /// 「不小心傳空清單會建出一個只有 AND 右半邊、等於全站掃描的危險 filter」的風險。</exception>
    public static string BuildWindowsFilter(IReadOnlyList<string> hostIps, IEnumerable<KnownIssueRule> rules)
    {
        if (hostIps.Count == 0)
        {
            throw new ArgumentException("主機 IP 清單不可為空——會產生出等同全站掃描的 filter。", nameof(hostIps));
        }

        var ipClause = BuildIpClause(hostIps);
        var contentClause = BuildContentClause(rules);
        return $"{ipClause} AND {contentClause}";
    }

    /// <summary>單獨曝露 IP 批次子句，供 Q3（風險主機原始 log，單台小查詢）等其他查詢重用同一組寫法。</summary>
    public static string BuildIpClause(IReadOnlyList<string> hostIps) =>
        $"({string.Join(" OR ", hostIps.Select(ip => $"{SentinelFieldMap.HostIp}:{ip}"))})";

    /// <summary>
    /// 事件內容子句：規則事件 ID 聯集 OR generic 錯誤收集。兩者是 OR，任一命中即收——
    /// 規則命中的事件走 <see cref="KnownIssueCatalog.Classify"/> 精準分類，generic 分支撈的是
    /// 規則表還沒覆蓋到的 System/Application 錯誤/警告事件（對齊本機 <c>ErrorWarningOnly</c>
    /// 頻道的既有偵測面，也是 <see cref="KnownIssueRule.MatchAllEventIds"/> 類規則
    /// 【WHEA-Logger／Resource-Exhaustion／VSS，皆為 System 來源】事件的收集路徑——
    /// 這類規則沒有具體 EventId 可下推 Lucene，改由 generic 分支撈進來後本地 Classify 精準比對）。
    /// </summary>
    private static string BuildContentClause(IEnumerable<KnownIssueRule> rules)
    {
        var eventIds = WindowsRuleEventIds(rules);
        var genericClause =
            $"(({SentinelFieldMap.LogName}:System OR {SentinelFieldMap.LogName}:Application) " +
            $"AND {SentinelFieldMap.Severity}:[{GenericErrorSeverityMin} TO 5])";

        if (eventIds.Count == 0)
        {
            return genericClause;
        }

        var idClause = $"{SentinelFieldMap.EventId}:({string.Join(" OR ", eventIds)})";
        return $"({idClause} OR {genericClause})";
    }

    /// <summary>
    /// 啟用中的 Windows 規則、且非 <see cref="KnownIssueRule.MatchAllEventIds"/> 的 EventIds 聯集，
    /// 升冪排序去重——與 <see cref="KnownIssueCatalog"/> 既有的 watchlist 推導同一套篩選條件
    /// （<c>Platform=="windows"</c>、非 MatchAllEventIds），只是這裡聯集的是「全部規則」而非
    /// 單一頻道，因為 Sentinel Q1 是一次查詢涵蓋所有頻道，不像本機逐頻道分開讀取。
    /// </summary>
    internal static List<int> WindowsRuleEventIds(IEnumerable<KnownIssueRule> rules) =>
        rules
            .Where(r => r.Enabled && r.Platform == "windows" && !r.MatchAllEventIds)
            .SelectMany(r => r.EventIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    // ── 網段範圍探索（新增主機，2026-07-29 定案）───────────────────────────────
    //
    // docs/NETIQ-API-REFERENCE.md §3.4：使用者在 Sentinel Web UI 實測 repip 支援前綴萬用字元
    // （repip:10.1.2.* 有效過濾，近 1h 從全站 154 萬筆縮到 2.4 萬筆），探索因此改走
    // 「網段範圍掃描」而不是 ESM 物件 API（一直被權限拒絕）——輸入網段前綴，
    // 依實測量級自動決定窗口，投影兩欄本地 distinct 出主機清單。

    /// <summary>網段前綴最少要幾個八位組——只給一段（如「10」）等於半個全站掃描，
    /// 量級與全站同一數量級，不值得假裝是「網段」查詢。</summary>
    internal const int MinPrefixOctets = 2;

    /// <summary>
    /// 把使用者輸入的網段字串（前綴「192.168.0」或 CIDR「192.168.0.0/24」）正規化成
    /// Lucene 前綴萬用字元查詢（<c>repip:192.168.0.*</c>）。純格式驗證，不含任何比對
    /// Sentinel 實際資料的邏輯。不帶 CIDR 的完整四段 IP **不接受**，理由見
    /// <see cref="NormalizeSubnetPrefix"/>。
    /// </summary>
    /// <exception cref="ArgumentException">輸入不是合法的 IPv4 前綴／CIDR，或段數不足
    /// <see cref="MinPrefixOctets"/>（避免建出等同全站掃描的查詢）。</exception>
    public static string BuildSubnetDiscoveryFilter(string subnetInput)
    {
        var prefix = NormalizeSubnetPrefix(subnetInput);
        return $"{SentinelFieldMap.HostIp}:{prefix}.*";
    }

    /// <summary>
    /// 正規化網段輸入為前綴八位組陣列的字串形式（如「192.168.0」，供
    /// <see cref="BuildSubnetDiscoveryFilter"/> 接上 <c>.*</c> 組成前綴萬用字元查詢）。
    /// 只允許數字與點——**天然免疫 Lucene 注入**，不需要另外跳脫特殊字元，因為根本不存在
    /// 會被當成語法的字元能通過這道白名單。
    ///
    /// **只接受 2～3 段前綴**（選填 /16、/24 CIDR 寫法）：這是「掃一個網段」的探索功能，
    /// 不是「查一台主機」——完整四段 IP 沒有對應的前綴萬用字元寫法（`repip:x.x.x.x.*`
    /// 語法不合法，若改成精確比對又是另一種查詢語意），需要查單台主機請用主機頁/CSV。
    ///
    /// **public**（不是 internal）：Web 層（探索 client／精靈顯示）需要正規化後的人看字串
    /// 本身（不只是組好的 filter），例如涵蓋範圍說明文字要顯示「192.168.0.*」而非原始輸入
    /// 「192.168.0.0/24」，這是合法的獨立使用情境，不是內部實作細節外洩。
    ///
    /// **例外訊息刻意不帶 paramName**：這些訊息是設計來原樣顯示給管理員的（Web 層
    /// <c>NetiqDiscoveryException</c> 直接轉傳 <c>ex.Message</c>），而
    /// <c>ArgumentException(message, paramName)</c> 會在 Message 後面自動附上
    /// 「(Parameter 'subnetInput')」——參數名對管理員毫無意義，只是把實作細節漏到畫面上。
    /// 型別維持 <see cref="ArgumentException"/>（呼叫端的 catch 與既有測試都以它為準）。
    /// </summary>
    public static string NormalizeSubnetPrefix(string subnetInput)
    {
        var trimmed = subnetInput?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("請輸入網段（如 192.168.0 或 192.168.0.0/24）。");
        }

        // CIDR：只接受 /16、/24（能對齊八位組邊界才轉得成前綴；/8 太籠統會被下面的量級守則
        // 一併擋下，/25 這類跨八位組的遮罩沒有對應的前綴萬用字元寫法，此版本不支援）
        var cidrSplit = trimmed.Split('/', 2);
        var ipPart = cidrSplit[0];
        int? cidrBits = null;
        if (cidrSplit.Length == 2)
        {
            if (!int.TryParse(cidrSplit[1], out var bits) || bits is not (16 or 24))
            {
                throw new ArgumentException(
                    $"CIDR 遮罩「/{cidrSplit[1]}」不支援，僅接受 /16、/24（能對齊八位組邊界）。");
            }
            cidrBits = bits;
        }

        var octets = ipPart.Split('.');
        if (octets.Length is < 1 or > 4 || octets.Any(o => o.Length == 0))
        {
            throw new ArgumentException($"網段格式不正確：「{subnetInput}」。");
        }

        var parsed = new List<int>();
        foreach (var octet in octets)
        {
            if (!int.TryParse(octet, out var value) || value is < 0 or > 255)
            {
                throw new ArgumentException($"網段格式不正確：「{subnetInput}」（「{octet}」不是合法的 0~255 數字）。");
            }
            parsed.Add(value);
        }

        if (cidrBits.HasValue)
        {
            // CIDR 指定的位元數決定要留幾段：/16 留 2 段、/24 留 3 段——
            // 多打的段數（如 10.1.2.5/24）直接截斷，少打的視為輸入錯誤
            var wantedOctets = cidrBits.Value / 8;
            if (parsed.Count < wantedOctets)
            {
                throw new ArgumentException(
                    $"「{subnetInput}」的位址段數不足以對應 /{cidrBits.Value}。");
            }
            parsed = parsed.Take(wantedOctets).ToList();
        }
        else if (parsed.Count == 4)
        {
            // 沒帶 CIDR 卻打了完整四段：這是單一 IP 而不是網段，不在這個函數的支援範圍——
            // 明確拒絕比默默截成 /24 安全，使用者原意可能真的只想查一台
            throw new ArgumentException(
                $"「{subnetInput}」是完整的單一 IP，不是網段——查單一台主機請用主機頁或 CSV 匯入；" +
                "要掃描它所在的網段請改輸入前三段（如 192.168.0 或 192.168.0.0/24）。");
        }

        if (parsed.Count < MinPrefixOctets)
        {
            throw new ArgumentException(
                $"網段太籠統：「{subnetInput}」只有 {parsed.Count} 段，至少需要 {MinPrefixOctets} 段" +
                "（如 192.168），避免掃描範圍等同全站。");
        }

        return string.Join('.', parsed);
    }
}

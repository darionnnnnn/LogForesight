namespace LogForesight.Core.Analysis;

/// <summary>
/// 規則表→Lucene filter 的純函數產生器（docs/NETIQ-API-REFERENCE.md §4「watchlist→Lucene 產生器」）。
/// Windows／Linux 各自的 <c>Build{Os}Filter</c> 分支，皆依真實 probe 樣本定案
/// （docs/archive/FEEDBACK-12-PLAN.md §4.0，四輪 probe，Sentinel「118_linux」）。
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

    // ── Linux（docs/archive/FEEDBACK-12-PLAN.md §4.4，四輪 probe 實證定案）──────────────────

    /// <summary>generic 高嚴重度收集的 sev 下限（輪 B 第 3/4 項定案，全站 2,403 筆/日，極便宜）。
    /// 與 <see cref="GenericErrorSeverityMin"/> 數值相同、各自獨立宣告：Windows 版仍是未實證的
    /// 候選值，Linux 版已由完整的 sev 分佈量測定案，兩者的實證基礎不同，不該共用同一個常數
    /// 讓其中一邊的未來調整意外牽動另一邊。</summary>
    public const int LinuxGenericSeverityMin = 2;

    /// <summary>
    /// 吵 program 常數集（輪 B 第 5 項量級實證的環境事實）：sshd（244k/日）／sudo（219k/日）／
    /// su（52k/日）／kernel（305k/日）／systemd（1.96M/日）遠超單一 job 的 100k 截斷線，
    /// 必須靠 msg 下推壓量；其餘 program 最大量級（chronyd 3.4k/日）整拉仍在安全範圍內，
    /// 順便避開片語標點（chronyd「Can't synchronise」的撇號、CRON「(CRON) ERROR」的括號）
    /// 下推時的殘餘風險。這是環境實測結果，不是理論推導——量級分佈若日後改變需要重新以
    /// probe 驗證，不建議憑感覺調整這個集合。
    /// </summary>
    internal static readonly HashSet<string> LinuxNoisyPrograms =
        new(StringComparer.OrdinalIgnoreCase) { "sshd", "sudo", "su", "kernel", "systemd" };

    /// <summary>
    /// 建立 Linux Q1 的 filter：`(IP 批次) AND (規則子句聯集 OR generic 高嚴重度收集)`。
    /// 形狀比照 <see cref="BuildWindowsFilter"/>；內容子句是 program／message 混合下推
    /// （見 <see cref="LinuxRuleProgramClauses"/>）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hostIps"/> 為空，理由同 <see cref="BuildWindowsFilter"/>。</exception>
    public static string BuildLinuxFilter(IReadOnlyList<string> hostIps, IEnumerable<KnownIssueRule> rules)
    {
        if (hostIps.Count == 0)
        {
            throw new ArgumentException("主機 IP 清單不可為空——會產生出等同全站掃描的 filter。", nameof(hostIps));
        }

        var ipClause = BuildIpClause(hostIps);
        var contentClause = BuildLinuxContentClause(rules);
        return $"{ipClause} AND {contentClause}";
    }

    private static string BuildLinuxContentClause(IEnumerable<KnownIssueRule> rules)
    {
        var genericClause = $"{SentinelFieldMap.Severity}:[{LinuxGenericSeverityMin} TO 5]";

        var programClauses = LinuxRuleProgramClauses(rules);
        return programClauses.Count == 0
            ? genericClause
            : $"(({string.Join(" OR ", programClauses)}) OR {genericClause})";
    }

    /// <summary>
    /// 依 program 分組產生規則子句：吵 program（<see cref="LinuxNoisyPrograms"/>）帶該 program
    /// 全部啟用規則的 <see cref="KnownIssueRule.MessagePatterns"/> 聯集下推
    /// （<c>(sp:{program}* AND msg:("片語1" OR "片語2" …))</c>），其餘 program 整拉
    /// （<c>sp:{program}*</c>，不論該規則有沒有 MessagePatterns——量級夠小，整拉比冒險下推更安全，
    /// 見 <see cref="LinuxNoisyPrograms"/> 的說明）。internal 供測試直接驗證分組結果，
    /// 不必逐次解析組出來的完整 filter 字串。
    /// </summary>
    internal static List<string> LinuxRuleProgramClauses(IEnumerable<KnownIssueRule> rules)
    {
        var byProgram = rules
            .Where(r => r.Enabled && r.Platform == "linux" && r.ProgramPattern.Length > 0)
            .GroupBy(r => r.ProgramPattern, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase); // 穩定順序：方便測試斷言與人工核對輸出

        var clauses = new List<string>();
        foreach (var group in byProgram)
        {
            var program = group.Key;
            var programClause = $"{SentinelFieldMap.LinuxProgram}:{program}*";

            if (!LinuxNoisyPrograms.Contains(program))
            {
                clauses.Add(programClause);
                continue;
            }

            var phrases = group
                .SelectMany(r => r.MessagePatterns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 吵 program 目前 17 條種子都有 MessagePatterns，這個分支理論上不會被走到；
            // 萬一日後有人新增一條吵 program 規則卻忘記填片語，寧可安全退回整拉也不要
            // 產生語法不完整的 filter（如 "(sp:x* AND msg:())"）
            clauses.Add(phrases.Count == 0
                ? programClause
                : $"({programClause} AND {SentinelFieldMap.Message}:({string.Join(" OR ", phrases.Select(EscapeLucenePhrase))}))");
        }

        return clauses;
    }

    /// <summary>把規則的 MessagePatterns 片語包成帶跳脫的 Lucene 完整片語查詢——內容來自 Web
    /// 規則維護頁（Maintain 權限，信任邊界內），但仍跳脫 `"`／`\` 避免管理者填入的片語意外
    /// 破壞 filter 語法（同 <see cref="NormalizeSubnetPrefix"/> 對不受信輸入的白名單精神，
    /// 這裡輸入來源更受信任，做的是語法正確性防禦而非安全性沙盒）。</summary>
    private static string EscapeLucenePhrase(string phrase) =>
        $"\"{phrase.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // ── 網段範圍探索（新增主機，2026-07-29 定案）───────────────────────────────
    //
    // docs/NETIQ-API-REFERENCE.md §3.4：使用者在 Sentinel Web UI 實測 repip 支援前綴萬用字元
    // （repip:192.168.0.* 有效過濾，近 1h 從全站 154 萬筆縮到 2.4 萬筆），探索因此改走
    // 「網段範圍掃描」而不是 ESM 物件 API（一直被權限拒絕）——輸入網段前綴，
    // 依實測量級自動決定窗口，投影兩欄本地 distinct 出主機清單。

    /// <summary>網段前綴最少要幾個八位組——只給一段（如「10」）等於半個全站掃描，
    /// 量級與全站同一數量級，不值得假裝是「網段」查詢。</summary>
    internal const int MinPrefixOctets = 2;

    /// <summary>
    /// 將字串形式的粒度（如 "24"、"/24"、"Slash24"）解析為 <see cref="ScanGranularity"/>。
    /// 無法解析、空值或未支援的值一律安全退回 <see cref="ScanGranularity.Slash24"/>（粒度為最佳化選項，不擲例外）。
    /// </summary>
    public static ScanGranularity ParseGranularity(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return ScanGranularity.Slash24;

        var trimmed = input.Trim().TrimStart('/');
        if (trimmed.Equals("24", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Slash24", StringComparison.OrdinalIgnoreCase))
        {
            return ScanGranularity.Slash24;
        }
        if (trimmed.Equals("26", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Slash26", StringComparison.OrdinalIgnoreCase))
        {
            return ScanGranularity.Slash26;
        }

        return ScanGranularity.Slash24;
    }

    /// <summary>
    /// 把使用者輸入的網段字串（前綴「192.168.0」或 CIDR「192.168.0.0/24」）正規化成
    /// Lucene 前綴萬用字元查詢（<c>repip:192.168.0.*</c>）。純格式驗證，不含任何比對
    /// Sentinel 實際資料的邏輯。不帶 CIDR 的完整四段 IP **不接受**，理由見
    /// <see cref="NormalizeSubnetPrefix"/>。
    /// </summary>
    /// <exception cref="ArgumentException">輸入不是合法的 IPv4 前綴／CIDR，或段數不足
    /// <see cref="MinPrefixOctets"/>（避免建出等同全站掃描的查詢）。</exception>
    public static string BuildSubnetDiscoveryFilter(string subnetInput) =>
        BuildSubnetDiscoveryFilter(subnetInput, null);

    /// <inheritdoc cref="BuildSubnetDiscoveryFilter(string)"/>
    /// <param name="excludeIps">要排除的 IP（<c>AND NOT (repip:a OR repip:b …)</c>）。
    /// null／空＝不排除。見 <see cref="BuildExclusionSuffix"/> 的用途說明。</param>
    public static string BuildSubnetDiscoveryFilter(string subnetInput, IReadOnlyCollection<string>? excludeIps)
    {
        var prefix = NormalizeSubnetPrefix(subnetInput);
        return BuildSubnetDiscoveryFilter(new ScanSegment(prefix, prefix, null), excludeIps);
    }

    /// <summary>
    /// 依 <see cref="ScanSegment"/> 建構探索補充掃描的 filter。
    /// 若分段帶有 <see cref="ScanSegment.Ips"/>（/25、/26），以逐 IP 明列 OR 子句表達；
    /// 若為 null（/24），使用前綴萬用字元查詢。
    /// 
    /// <para><b>子句數申報</b>：/26 為 64 個位址子句，加上排除最多 64 個，合計最多 128 個 OR/NOT 子句。
    /// Lucene 預設 maxClauseCount 是 1024，結構上安全但 128 子句未經實測，試點時要核對是否被 Sentinel 拒絕。</para>
    /// </summary>
    public static string BuildSubnetDiscoveryFilter(ScanSegment segment, IReadOnlyCollection<string>? excludeIps = null)
    {
        var addressClause = BuildAddressClause(segment);
        return $"{addressClause}{BuildExclusionSuffix(excludeIps)}";
    }

    /// <summary>
    /// 探索主掃描的 filter：網段前綴**再限定低量頻道**
    /// （docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.1）。
    ///
    /// **為什麼要窄化**：探索要的是「每台主機至少一筆事件」，不是「所有事件」。
    /// 第三輪 probe 實測單台主機日量約 31 萬筆，其中 Security 佔 99.95%
    /// （System=3、Application=152）——把 Security 排除在外，每台主機只貢獻約 155 筆/日。
    /// 取回量因此**從正比於「事件量」變成正比於「主機數」**：前者不可控且與探索目的無關，
    /// 後者可預期，也才是我們真正想量的東西。
    ///
    /// 直接後果是掃描窗口不必再為了控制筆數而縮短——原設計「事件越多窗口越短」
    /// 會把安靜主機連同時間一起裁掉，而那正是最需要被發現的那種主機。
    /// </summary>
    /// <param name="excludeIps">要排除的 IP（殘差輪掃的已見主機、重掃的已登錄主機）。</param>
    /// <param name="os">依 <see cref="Sentinel.Os"/> 決定內容子句（docs/archive/FEEDBACK-12-PLAN.md §4.4）：
    /// windows（預設，既有行為不變）用頻道子句；linux 退回 <c>sev:[0 TO 5]</c>——輪 A 實證
    /// <see cref="SentinelFieldMap.LogName"/>（`rv150`）在 Linux 事件上承載 facility
    /// （`DAEMON`／`KERNEL`）而不是頻道名，頻道子句在 Linux Sentinel 上會恆為 0 台。</param>
    public static string BuildSubnetProbeFilter(
        string subnetInput, IReadOnlyCollection<string>? excludeIps = null, string os = WebHost.OsWindows)
    {
        var prefix = NormalizeSubnetPrefix(subnetInput);
        return BuildSubnetProbeFilter(new ScanSegment(prefix, prefix, null), excludeIps, os);
    }

    /// <summary>
    /// 依 <see cref="ScanSegment"/> 建構探索主掃描的 filter（位址子句 ＋ 頻道窄化 ＋ 排除後綴）。
    /// 若分段帶有 <see cref="ScanSegment.Ips"/>（/25、/26），以逐 IP 明列 OR 子句表達；
    /// 若為 null（/24），使用前綴萬用字元查詢。
    /// 
    /// <para><b>子句數申報</b>：/26 為 64 個位址子句，加上排除最多 64 個，合計最多 128 個 OR/NOT 子句。
    /// Lucene 預設 maxClauseCount 是 1024，結構上安全但 128 子句未經實測，試點時要核對是否被 Sentinel 拒絕。</para>
    /// </summary>
    public static string BuildSubnetProbeFilter(
        ScanSegment segment, IReadOnlyCollection<string>? excludeIps = null, string os = WebHost.OsWindows)
    {
        var addressClause = BuildAddressClause(segment);
        var contentClause = BuildProbeContentClause(os);
        return $"({addressClause} AND {contentClause}){BuildExclusionSuffix(excludeIps)}";
    }

    private static string BuildAddressClause(ScanSegment segment) =>
        segment.Ips is { Count: > 0 }
            ? BuildIpClause(segment.Ips)
            : $"{SentinelFieldMap.HostIp}:{segment.Prefix}.*";

    private static string BuildProbeContentClause(string os) =>
        os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase)
            ? $"{SentinelFieldMap.Severity}:[0 TO 5]"
            : $"({SentinelFieldMap.LogName}:System OR {SentinelFieldMap.LogName}:Application)";

    /// <summary>
    /// 排除子句 <c> AND NOT (repip:a OR repip:b …)</c>——空清單回空字串。
    ///
    /// **兩個用途，同一個機制**（docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.1／§3.3）：
    ///   1. **殘差輪掃**：單輪取回筆數觸頂時，把已發現的主機排除後重查，
    ///      下一輪取回的就只有還沒見過的主機——這是「上限觸頂不會靜默漏機」的關鍵，
    ///      取代原設計「超量就縮短窗口」（縮掉的時間裡安靜主機的事件一併消失，且無警告）。
    ///   2. **重掃增量**：已登錄的主機不需要被重新「發現」，直接在 server 端濾掉，
    ///      沒有新主機時 found=0，一次查詢就結束。
    ///
    /// IP 逐個過白名單驗證（僅數字與點、四段、0~255）——與
    /// <see cref="NormalizeSubnetPrefix"/> 同一個理由：**天然免疫 Lucene 注入**，
    /// 不合格的直接略過而不是擲例外（呼叫端傳進來的是既有資料，
    /// 一筆髒資料不該讓整趟探索失敗；漏排除只會多取回一點，不影響正確性）。
    /// </summary>
    public static string BuildExclusionSuffix(IReadOnlyCollection<string>? excludeIps)
    {
        if (excludeIps is null || excludeIps.Count == 0) return string.Empty;

        var valid = excludeIps
            .Where(IsValidIpv4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (valid.Count == 0) return string.Empty;

        return $" AND NOT ({string.Join(" OR ", valid.Select(ip => $"{SentinelFieldMap.HostIp}:{ip}"))})";
    }

    /// <summary>完整四段 IPv4 且每段 0~255。刻意不接受簡寫或前綴——排除清單的每一筆
    /// 都該是一台明確的主機，模糊的排除會讓涵蓋保證失去意義。</summary>
    private static bool IsValidIpv4(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;

        var octets = ip.Split('.');
        return octets.Length == 4 &&
               octets.All(o => o.Length > 0 && int.TryParse(o, out var v) && v is >= 0 and <= 255);
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
            // 多打的段數（如 192.168.0.5/24）直接截斷，少打的視為輸入錯誤
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

    /// <summary>
    /// 把使用者輸入的網段前綴（如「192.168」或「192.168.1」或 CIDR「192.168.0.0/16」）
    /// 展開成逐段掃描的 /24 前綴清單。
    /// <list type="bullet">
    ///   <item>正規化後已是三段（如「192.168.1」）→ 回傳單元素清單 [192.168.1]。</item>
    ///   <item>正規化後是兩段（如「192.168」）→ 展開為「192.168.0」～「192.168.255」，共 256 個子網段。</item>
    /// </list>
    /// 純函數，驗證與格式例外完全沿用 <see cref="NormalizeSubnetPrefix"/>。
    /// </summary>
    /// <exception cref="ArgumentException">輸入不是合法的 IPv4 前綴／CIDR，或段數不足／超出規範。</exception>
    public static IReadOnlyList<string> ExpandToSlash24Prefixes(string subnetInput)
    {
        var normalized = NormalizeSubnetPrefix(subnetInput);
        var octets = normalized.Split('.');

        if (octets.Length == 3)
        {
            return new[] { normalized };
        }

        if (octets.Length == 2)
        {
            var prefixes = new List<string>(256);
            for (var i = 0; i <= 255; i++)
            {
                prefixes.Add($"{normalized}.{i}");
            }
            return prefixes;
        }

        // NormalizeSubnetPrefix 已透過 MinPrefixOctets 擋下 < 2 段，且不接受 4 段；此處為防禦性回傳
        return new[] { normalized };
    }

    /// <summary>
    /// 依指定的掃描粒度將使用者輸入的網段展開成分段清單：
    /// <list type="bullet">
    ///   <item><see cref="ScanGranularity.Slash24"/>：每 /24 產生 1 個分段，使用前綴萬用字元查詢（Ips 為 null）。</item>
    ///   <item><see cref="ScanGranularity.Slash26"/>：每 /24 產生 4 個分段（.0~.63、.64~.127、.128~.191、.192~.255），以逐 IP 明列 OR 子句查詢。</item>
    /// </list>
    /// IP 清單包含該範圍內的全部位址（含網路與廣播位址，以實際事件 repip 為準）。
    /// 驗證與格式例外完全沿用 <see cref="ExpandToSlash24Prefixes"/>。
    /// </summary>
    /// <exception cref="ArgumentException">輸入不是合法的 IPv4 前綴／CIDR，或段數不足／超出規範。</exception>
    public static IReadOnlyList<ScanSegment> ExpandToSegments(string subnetInput, ScanGranularity granularity)
    {
        var prefixes = ExpandToSlash24Prefixes(subnetInput);

        if (granularity == ScanGranularity.Slash24)
        {
            return prefixes.Select(p => new ScanSegment(p, p, null)).ToList();
        }

        // /26＝每 64 個位址一段（每個 /24 拆四段）。
        // **刻意不提供 /25**：/25 一次要送 128 個位址子句，加上段內排除最多再 128 個，
        // 合計 256 子句——而本環境的 probe 只實證過 OR 50~100 個。把 /25 拆成兩個 64 子句的
        // 查詢送出後，它與 /26 在查詢層面完全等價，多這個選項只是讓人以為有第三種涵蓋粒度。
        const int segmentSize = 64;
        var segments = new List<ScanSegment>(prefixes.Count * (256 / segmentSize));

        foreach (var prefix in prefixes)
        {
            for (var start = 0; start < 256; start += segmentSize)
            {
                var ips = Enumerable.Range(start, segmentSize).Select(i => $"{prefix}.{i}").ToList();
                segments.Add(new ScanSegment(prefix, $"{prefix}.{start}/26", ips));
            }
        }

        return segments;
    }
}

/// <summary>掃描粒度：一次查詢涵蓋多大的位址範圍。/24 用前綴萬用字元，
/// /25 與 /26 無法對齊字串前綴，改以逐 IP 明列 OR 子句表達。</summary>
public enum ScanGranularity { Slash24, Slash26 }

/// <summary>一個掃描分段。Prefix 是顯示與歸屬用的 /24 前綴；
/// Ips 非 null 時代表這一段要以逐 IP 明列 OR 子句查詢（/25、/26），
/// null 代表用 Prefix 的前綴萬用字元查詢（/24）。</summary>
public sealed record ScanSegment(string Prefix, string Label, IReadOnlyList<string>? Ips);


namespace LogForesight;

public class CorrelationFinding
{
    public IssueSeverity Severity { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// LogAnalysisService 條件式撈取 4624（成功登入）後，與當日 4625（失敗）比對出的重疊帳號/IP。
/// 這個比對需要原始事件內容（帳號/IP 從訊息全文抽取），CorrelationAnalyzer 只吃聚合後的簽章，
/// 所以比對本身在 Service 層做、結果以這個小物件傳入，Detect 本身維持純函數、不做 I/O。
/// </summary>
public class SuccessfulLogonMatch
{
    public List<string> MatchedAccounts { get; init; } = new();
    public List<string> MatchedIps { get; init; } = new();

    /// <summary>成功登入面是否包含 RDP 工作階段登入（LSM 21/25、RCM 1149）而不只是 4624——用於在
    /// 【破解得手】描述中附註「含 RDP 工作階段登入」，讓調查者知道得手途徑可能是遠端桌面。</summary>
    public bool IncludesRdp { get; init; }
}

/// <summary>
/// 跨 log 關聯分析：偵測「多個獨立訊號的已知組合模式」（攻擊鏈、故障連鎖）。
/// 單一事件各自看都不嚴重、組合起來卻是明確的入侵或故障故事——這種關聯判讀
/// 正是小模型最容易漏掉的部分，所以由程式確定性比對，AI 只負責解讀比對結果。
/// 比對範圍：當日各事件簽章的共現、時間先後（FirstSeen），以及與前一日紀錄的跨日組合。
/// </summary>
public static class CorrelationAnalyzer
{
    // 事件群組定義（與 KnownIssueCatalog 的規則對齊）。internal（非 private）是刻意的：
    // SelfTestRunner 會逐一驗證這些 ID 都存在於目前生效的規則表，防止規則表演進後兩邊悄悄漂移
    // ——關聯層的組合模式仍是程式碼邏輯、不搬進 rules.json（見 docs/RULES-PLAN.md 語意邊界），
    // 但它引用的事件 ID 應該要跟規則表對得上。
    internal static readonly int[] AccountChangeIds = { 4720, 4722, 4724, 4728, 4732, 4756 };
    internal static readonly int[] PersistenceSecurityIds = { 4697, 4698 };
    internal static readonly int[] AuditTamperIds = { 1102, 4719, 4907 };
    internal static readonly int[] PermissionChangeIds = { 4670, 4703, 4704, 4705, 4717, 4718 };
    internal static readonly int[] DiskErrorIds = { 7, 11, 51, 52, 153 };
    internal static readonly int[] NtfsErrorIds = { 55, 98, 130, 140, 141 };
    internal static readonly int[] DefenderMalwareIds = { 1006, 1116, 1007, 1117, 1008, 1118, 1119 };
    internal static readonly int[] DefenderProtectionOffIds = { 5001, 5010, 5012 };

    public static List<CorrelationFinding> Detect(List<LogIssueSignature> issues,
        List<DailyAnalysisRecord> history, DateTime targetDate, SuccessfulLogonMatch? successfulLogonMatch = null)
    {
        var findings = new List<CorrelationFinding>();

        LogIssueSignature? Find(string sourcePattern, params int[] ids) =>
            issues.FirstOrDefault(i =>
                i.Source.Contains(sourcePattern, StringComparison.OrdinalIgnoreCase) &&
                (ids.Length == 0 || ids.Contains(i.EventId)));

        bool Has(string sourcePattern, params int[] ids) => Find(sourcePattern, ids) != null;

        int TotalCount(string sourcePattern, params int[] ids) => issues
            .Where(i => i.Source.Contains(sourcePattern, StringComparison.OrdinalIgnoreCase) &&
                        (ids.Length == 0 || ids.Contains(i.EventId)))
            .Sum(i => i.Count);

        // ── 安全：入侵鏈（同日）───────────────────────────────────────

        var bruteForce = Find("Security-Auditing", 4625);
        bool heavyBruteForce = bruteForce != null && bruteForce.Count >= 10;
        var accountChange = Find("Security-Auditing", AccountChangeIds);

        // 今日的 RDP 成功登入簽章（LSM 21/25、RCM 1149）——本身不是告警，只在與攻擊錨點交集時才成為訊號
        var rdpSuccess = issues.Where(i =>
            (i.Source.Contains("TerminalServices-LocalSessionManager", StringComparison.OrdinalIgnoreCase) && (i.EventId is 21 or 25)) ||
            (i.Source.Contains("TerminalServices-RemoteConnectionManager", StringComparison.OrdinalIgnoreCase) && i.EventId == 1149))
            .ToList();
        var newService = Find("Service Control Manager", 7045) ?? Find("Security-Auditing", PersistenceSecurityIds);

        if (heavyBruteForce && accountChange != null)
        {
            var ordering = HappenedAfter(accountChange, bruteForce!)
                ? "，且帳號操作發生在登入失敗開始之後，時序符合攻擊得手的推進順序"
                : "";
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【入侵鏈】同日出現大量登入失敗（x{bruteForce!.Count}）與帳號建立/提權操作（EventId {accountChange.EventId}）" +
                              $"——暴力破解得手後建立立足點的典型組合{ordering}，應立即調查該帳號的所有活動"
            });
        }

        if (heavyBruteForce && successfulLogonMatch != null &&
            (successfulLogonMatch.MatchedAccounts.Count > 0 || successfulLogonMatch.MatchedIps.Count > 0))
        {
            var accountsText = successfulLogonMatch.MatchedAccounts.Count > 0
                ? $"帳號：{string.Join("、", successfulLogonMatch.MatchedAccounts.Take(5))}" : "";
            var ipsText = successfulLogonMatch.MatchedIps.Count > 0
                ? $"來源IP：{string.Join("、", successfulLogonMatch.MatchedIps.Take(5))}" : "";
            var matchedText = string.Join("；", new[] { accountsText, ipsText }.Where(s => s.Length > 0));
            var rdpNote = successfulLogonMatch.IncludesRdp ? "（含 RDP 工作階段登入）" : "";

            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【破解得手】同日大量登入失敗（x{bruteForce!.Count}）後，相同帳號/IP 出現成功登入{rdpNote}（{matchedText}）" +
                              "——暴力破解極可能已得手，應立即鎖定該帳號、強制改密碼並全面稽查其後續活動"
            });
        }

        if ((heavyBruteForce || accountChange != null) && newService != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = "【持久化】帳號異動或攻擊嘗試與新服務/排程任務同日出現" +
                              $"（{newService.Source} EventId {newService.EventId}）——入侵後植入持久化後門的典型組合，" +
                              "請確認該服務/任務的執行檔來源與簽章"
            });
        }

        var auditTamper = Find("Security-Auditing", AuditTamperIds);
        if (auditTamper != null && issues.Any(i => i.Category == IssueCategory.Security && i != auditTamper))
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【滅跡】稽核記錄被清除/變更（EventId {auditTamper.EventId}）且同日有其他安全事件" +
                              "——高度疑似入侵者在清除操作痕跡，被清除前的稽核內容可能已遺失，應以其他來源（防火牆、EDR）交叉調查"
            });
        }

        var permissionChange = Find("Security-Auditing", PermissionChangeIds);
        if (permissionChange != null && newService != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【提權→植入】權限/特權異動（EventId {permissionChange.EventId}）與新服務/排程任務同日出現" +
                              "——先取得權限再植入執行體的攻擊推進模式"
            });
        }

        // ── 安全：Microsoft Defender 關聯（同日）──────────────────────────
        // Defender 事件天生低誤報，但單獨的 5001（管理員關防護）只走規則層 High、不觸發關聯——
        // 要有「防護關閉 + 惡意程式/攻擊訊號」的組合才升級為關聯，避免把正常維運誤判成攻擊。

        var defenderMalware = Find("Windows Defender", DefenderMalwareIds);
        var defenderProtectionOff = Find("Windows Defender", DefenderProtectionOffIds);

        if (defenderProtectionOff != null && (defenderMalware != null || heavyBruteForce || accountChange != null))
        {
            var trigger = defenderMalware != null ? "同日偵測到惡意程式"
                : heavyBruteForce ? "同日出現大量登入失敗"
                : "同日出現帳號建立/提權操作";
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【防護遭關閉→惡意程式】防毒防護被關閉/停用（EventId {defenderProtectionOff.EventId}）且{trigger}" +
                              "——入侵者常在植入惡意程式前先解除防護，應立即重啟防護、全機掃描並調查關閉來源"
            });
        }

        if (defenderMalware != null && newService != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【惡意程式→持久化】偵測到惡意程式（EventId {defenderMalware.EventId}）與新服務/排程任務" +
                              $"同日出現（{newService.Source} EventId {newService.EventId}）——惡意程式建立持久化立足點的典型組合，" +
                              "請確認該服務/任務的執行檔來源與簽章，並確認惡意程式已徹底清除"
            });
        }

        // ── 儲存/硬體：故障連鎖（同日）─────────────────────────────────

        var storageSignals = new[]
        {
            Find("disk", DiskErrorIds),
            Find("Ntfs", NtfsErrorIds),
            Find("stor", 129)
        }.Where(s => s != null).Cast<LogIssueSignature>().ToList();

        if (storageSignals.Count >= 2)
        {
            var parts = string.Join("、", storageSignals.Select(s => $"{s.Source}#{s.EventId} x{s.Count}"));
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = $"【儲存連鎖】多個儲存層訊號同日出現（{parts}）——磁碟 I/O、檔案系統、控制器同時異常是" +
                              "硬碟故障連鎖反應的訊號，故障可能迫在眉睫，應立即備份並安排更換"
            });
        }

        var unexpectedShutdown = Find("Kernel-Power", 41) ?? Find("EventLog", 6008);
        if (storageSignals.Count > 0 && unexpectedShutdown != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = "【儲存→當機】磁碟/檔案系統錯誤與非預期關機同日出現——儲存故障可能已導致系統崩潰，" +
                              "下次崩潰可能無法開機，備份的優先度最高"
            });
        }

        if (Has("WHEA-Logger") && unexpectedShutdown != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Critical,
                Description = "【硬體不穩】WHEA 硬體錯誤與非預期重開同日出現——硬體劣化已實際影響系統穩定性，" +
                              "而不只是 corrected error 統計上升"
            });
        }

        // ── 服務/資源：崩潰循環（同日）─────────────────────────────────

        var appCrash = Find("Application Error", 1000) ?? Find(".NET Runtime", 1026);
        var serviceFailure = Find("Service Control Manager", 7031, 7034);

        if (appCrash != null && serviceFailure != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.High,
                Description = $"【崩潰→服務失敗】應用程式崩潰（{appCrash.Source} x{appCrash.Count}）與服務異常終止" +
                              $"（x{serviceFailure.Count}）同日出現——可能為同一應用程式的崩潰導致服務失敗，" +
                              "請比對兩者的範例訊息是否指向同一程式"
            });
        }

        int serviceFailureCount = TotalCount("Service Control Manager", 7031, 7034);
        if (serviceFailureCount >= 100 && Has("Resource-Exhaustion"))
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.High,
                Description = $"【崩潰循環→資源耗盡】服務高頻異常終止（x{serviceFailureCount}）與系統資源耗盡同日出現" +
                              "——服務陷入「崩潰→自動重啟→再崩潰」的循環正在耗盡系統資源，放任將拖垮整機，" +
                              "應先停用該服務的自動重啟再排查根因"
            });
        }

        if (Has("Time-Service") && bruteForce != null)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.High,
                Description = "【時間偏移→驗證失敗】時間同步失敗與登入失敗同日出現——時鐘偏移會導致 Kerberos 驗證大量失敗，" +
                              "登入失敗可能是假性攻擊訊號；但仍需先修復時間同步後確認登入失敗是否消失，不可直接排除攻擊"
            });
        }

        // ── 跨日組合（比對前一日的歷史紀錄）────────────────────────────

        var previousDay = history.FirstOrDefault(h => h.Date.Date == targetDate.Date.AddDays(-1));
        if (previousDay != null)
        {
            bool yesterdayBruteForce = previousDay.TopIssues
                .Any(i => i.EventId == 4625 && i.Count >= 10);
            bool todayFoothold = accountChange != null || newService != null || permissionChange != null;

            if (yesterdayBruteForce && todayFoothold)
            {
                findings.Add(new CorrelationFinding
                {
                    Severity = IssueSeverity.Critical,
                    Description = "【跨日入侵鏈】昨日大量登入失敗、今日出現帳號/權限/服務異動——攻擊者跨日推進的典型模式" +
                                  "（先暴力破解、隔日建立立足點），比單日訊號更值得警戒，應立即調查"
                });
            }

            bool yesterdayStorage = previousDay.TopIssues.Any(i => i.Category == IssueCategory.Storage);
            if (yesterdayStorage && storageSignals.Count > 0)
            {
                findings.Add(new CorrelationFinding
                {
                    Severity = IssueSeverity.Critical,
                    Description = "【儲存持續劣化】儲存層錯誤連續兩日出現——不是偶發抖動而是持續劣化中，" +
                                  "硬碟剩餘壽命可能以天計，備份與更換不應再等待"
                });
            }

            bool yesterdayProtectionOff = previousDay.TopIssues.Any(i =>
                i.Source.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) &&
                DefenderProtectionOffIds.Contains(i.EventId));
            if (yesterdayProtectionOff && defenderMalware != null)
            {
                findings.Add(new CorrelationFinding
                {
                    Severity = IssueSeverity.Critical,
                    Description = "【防護遭關閉→惡意程式】昨日防毒防護被關閉、今日偵測到惡意程式——攻擊者先解除防護、" +
                                  "隔日植入惡意程式的跨日推進模式，應立即隔離主機並全面稽查"
                });
            }

            // 【暴力破解→RDP 得手】需錨點：昨日大量登入失敗的來源 IP ∩ 今日 RDP 成功登入的來源 IP 非空。
            // 純以 IP 交集判定（跨日只有歷史簽章的 KeyDetails 可用），解析不到 IP 就不觸發、不臆測——
            // 正常的每日 RDP 維運不會與昨日暴力破解的 IP 重疊，因此不會誤報。
            if (rdpSuccess.Count > 0)
            {
                var yesterdayBruteIps = previousDay.TopIssues
                    .Where(i => i.EventId == 4625 && i.Count >= 10 && i.KeyDetails != null)
                    .SelectMany(i => ExtractIps(i.KeyDetails!))
                    .ToHashSet();
                var todayRdpIps = rdpSuccess
                    .Where(i => i.KeyDetails != null)
                    .SelectMany(i => ExtractIps(i.KeyDetails!))
                    .ToHashSet();
                var overlapIps = yesterdayBruteIps.Intersect(todayRdpIps).ToList();

                if (overlapIps.Count > 0)
                {
                    findings.Add(new CorrelationFinding
                    {
                        Severity = IssueSeverity.Critical,
                        Description = $"【暴力破解→RDP 得手】昨日大量登入失敗的來源 IP，今日以遠端桌面成功登入" +
                                      $"（來源IP：{string.Join("、", overlapIps.Take(5))}）——暴力破解跨日以 RDP 得手的跡象，" +
                                      "應立即鎖定該來源、檢查其遠端工作階段的所有活動並強制改密碼"
                    });
                }
            }
        }

        return findings;
    }

    private static readonly System.Text.RegularExpressions.Regex Ipv4Regex =
        new(@"\b\d{1,3}(\.\d{1,3}){3}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>從簽章的 KeyDetails 字串（如「來源IP(2個): 1.2.3.4, 5.6.7.8」）解析出 IPv4 位址，供跨日 IP 交集比對。</summary>
    private static IEnumerable<string> ExtractIps(string keyDetails) =>
        Ipv4Regex.Matches(keyDetails).Select(m => m.Value);

    /// <summary>依 FirstSeen (HH:mm) 判斷 later 是否在 earlier 之後開始發生，無法解析時回傳 false（不做臆測）</summary>
    private static bool HappenedAfter(LogIssueSignature later, LogIssueSignature earlier) =>
        TimeSpan.TryParse(later.FirstSeen, out var l) &&
        TimeSpan.TryParse(earlier.FirstSeen, out var e) &&
        l >= e;
}

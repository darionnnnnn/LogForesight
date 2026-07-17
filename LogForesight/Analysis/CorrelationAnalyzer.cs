namespace LogForesight;

public class CorrelationFinding
{
    public IssueSeverity Severity { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// 跨 log 關聯分析：偵測「多個獨立訊號的已知組合模式」（攻擊鏈、故障連鎖）。
/// 單一事件各自看都不嚴重、組合起來卻是明確的入侵或故障故事——這種關聯判讀
/// 正是小模型最容易漏掉的部分，所以由程式確定性比對，AI 只負責解讀比對結果。
/// 比對範圍：當日各事件簽章的共現、時間先後（FirstSeen），以及與前一日紀錄的跨日組合。
/// </summary>
public static class CorrelationAnalyzer
{
    // 事件群組定義（與 KnownIssueCatalog 的規則對齊）
    private static readonly int[] AccountChangeIds = { 4720, 4722, 4724, 4728, 4732, 4756 };
    private static readonly int[] PersistenceSecurityIds = { 4697, 4698 };
    private static readonly int[] AuditTamperIds = { 1102, 4719, 4907 };
    private static readonly int[] PermissionChangeIds = { 4670, 4703, 4704, 4705, 4717, 4718 };
    private static readonly int[] DiskErrorIds = { 7, 11, 51, 52, 153 };
    private static readonly int[] NtfsErrorIds = { 55, 98, 130, 140, 141 };

    public static List<CorrelationFinding> Detect(List<LogIssueSignature> issues,
        List<DailyAnalysisRecord> history, DateTime targetDate)
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
        }

        return findings;
    }

    /// <summary>依 FirstSeen (HH:mm) 判斷 later 是否在 earlier 之後開始發生，無法解析時回傳 false（不做臆測）</summary>
    private static bool HappenedAfter(LogIssueSignature later, LogIssueSignature earlier) =>
        TimeSpan.TryParse(later.FirstSeen, out var l) &&
        TimeSpan.TryParse(earlier.FirstSeen, out var e) &&
        l >= e;
}

namespace LogForesight;

public enum IssueCategory
{
    Hardware,   // CPU / 記憶體 / 電源
    Storage,    // 磁碟 / 檔案系統
    Security,   // 入侵跡象
    Service,    // 服務異常
    Resource,   // 資源耗盡
    Backup,     // 備份 / 還原能力
    Config,     // 設定性風險：憑證、時間同步、群組原則、網域連線
    Other
}

public enum IssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public class KnownIssueRule
{
    public string SourcePattern { get; init; } = string.Empty;
    public int[] EventIds { get; init; } = Array.Empty<int>();
    public IssueCategory Category { get; init; }
    public IssueSeverity Severity { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>當日發生次數達到此值才算完整嚴重度，未達則降一級（例如零星的登入失敗屬正常雜訊）</summary>
    public int CountThreshold { get; init; } = 1;
}

/// <summary>
/// 已知危險訊號的規則表。偵測「已知模式」交給確定性規則，不依賴 AI 模型的召回率；
/// AI 負責綜合判讀、趨勢比對與規則未涵蓋的新型態問題。
/// </summary>
public static class KnownIssueCatalog
{
    /// <summary>Security log 的 SuccessAudit 量極大，只挑這些高價值事件納入分析</summary>
    public static readonly HashSet<int> SecurityAuditWatchlist = new()
    {
        1102, // 稽核日誌被清除
        4719, // 稽核原則被變更
        4720, // 建立使用者帳戶
        4722, // 帳戶被啟用
        4724, // 重設他人密碼
        4728, 4732, 4756, // 加入特權群組
        4729, 4733, 4757, // 移出特權群組
        4697, // 安裝服務
        4698, // 建立排程任務
        4740, // 帳戶鎖定
        4670, // 物件權限 (ACL) 被變更
        4907, // 物件稽核設定 (SACL) 被變更
        4717, 4718, // 系統存取權限被授予/移除
        4704, 4705, // 使用者權限指派被新增/移除
        4703, // 權杖特殊權限於執行期間被調整
        4735, // 安全群組內容被變更
        4739, // 網域原則被變更
        4731, 4734, // 本機安全群組建立/刪除
    };

    public static readonly List<KnownIssueRule> Rules = new()
    {
        // ── 儲存裝置（硬碟故障最重要的前兆訊號）─────────────────────────
        new() { SourcePattern = "disk", EventIds = new[] { 7, 11, 51, 52, 153 },
                Category = IssueCategory.Storage, Severity = IssueSeverity.Critical,
                Description = "磁碟 I/O 錯誤或壞軌前兆，硬碟可能即將故障，應盡快備份並安排更換" },
        new() { SourcePattern = "Ntfs", EventIds = new[] { 55, 98, 130, 140, 141 },
                Category = IssueCategory.Storage, Severity = IssueSeverity.Critical,
                Description = "NTFS 檔案系統損毀跡象，需執行 chkdsk 並檢查底層磁碟健康" },
        new() { SourcePattern = "stor", EventIds = new[] { 129 },
                Category = IssueCategory.Storage, Severity = IssueSeverity.High,
                Description = "儲存控制器逾時重置 (storahci/stornvme 129)，常見於硬碟劣化、線材或背板異常" },
        new() { SourcePattern = "srv", EventIds = new[] { 2013 },
                Category = IssueCategory.Resource, Severity = IssueSeverity.Medium,
                Description = "磁碟空間即將不足" },

        // ── 硬體（CPU / 記憶體 / 電源）───────────────────────────────
        new() { SourcePattern = "WHEA-Logger",
                Category = IssueCategory.Hardware, Severity = IssueSeverity.Critical,
                Description = "WHEA 硬體錯誤 (CPU/記憶體/PCIe)。corrected error 次數上升是硬體劣化的典型前兆" },
        new() { SourcePattern = "Kernel-Power", EventIds = new[] { 41 },
                Category = IssueCategory.Hardware, Severity = IssueSeverity.Critical,
                Description = "非預期斷電或當機重開 (Kernel-Power 41)，可能為電源、過熱或硬體不穩" },
        new() { SourcePattern = "EventLog", EventIds = new[] { 6008 },
                Category = IssueCategory.Hardware, Severity = IssueSeverity.High,
                Description = "非預期關機" },
        new() { SourcePattern = "Resource-Exhaustion",
                Category = IssueCategory.Resource, Severity = IssueSeverity.High,
                Description = "系統資源（虛擬記憶體）即將耗盡，可能有程式記憶體洩漏" },

        // ── 服務 ────────────────────────────────────────────────
        new() { SourcePattern = "Service Control Manager", EventIds = new[] { 7031, 7034 },
                Category = IssueCategory.Service, Severity = IssueSeverity.Medium, CountThreshold = 3,
                Description = "服務異常終止；反覆發生代表服務不穩定，需檢查應用程式錯誤" },
        new() { SourcePattern = "Service Control Manager", EventIds = new[] { 7000, 7001 },
                Category = IssueCategory.Service, Severity = IssueSeverity.Medium,
                Description = "服務啟動失敗" },
        new() { SourcePattern = "Service Control Manager", EventIds = new[] { 7045 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "系統安裝了新服務——若非管理員預期的操作，可能是入侵者植入後門" },

        // ── 營運健康（備份、時間、憑證、網域設定）────────────────────
        new() { SourcePattern = "Backup", EventIds = new[] { 517 },
                Category = IssueCategory.Backup, Severity = IssueSeverity.High,
                Description = "Windows Server Backup 備份失敗——備份損壞往往到需要還原時才發現，應立即檢查" },
        new() { SourcePattern = "VSS",
                Category = IssueCategory.Backup, Severity = IssueSeverity.Medium,
                Description = "磁碟區陰影複製 (VSS) 錯誤，會導致備份失敗或不完整" },
        new() { SourcePattern = "Time-Service", EventIds = new[] { 29, 36, 47, 50 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Medium,
                Description = "時間同步失敗；時鐘偏移超過 5 分鐘會導致 Kerberos 驗證與網域登入全面失敗" },
        new() { SourcePattern = "AutoEnrollment", EventIds = new[] { 64 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Medium,
                Description = "憑證即將到期——過期會造成 TLS 或服務中斷，是最容易預防的停機原因之一" },
        new() { SourcePattern = "Schannel", EventIds = new[] { 36870 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Medium,
                Description = "TLS 憑證私鑰存取失敗，常見於憑證過期或權限異常" },
        new() { SourcePattern = "GroupPolicy", EventIds = new[] { 1030, 1058 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Medium, CountThreshold = 3,
                Description = "群組原則套用失敗，可能為 SYSVOL 或網域控制站連線問題" },
        new() { SourcePattern = "NETLOGON", EventIds = new[] { 5719 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Medium, CountThreshold = 3,
                Description = "無法連上網域控制站，網域驗證將受影響" },
        new() { SourcePattern = "DhcpServer", EventIds = new[] { 1020 },
                Category = IssueCategory.Resource, Severity = IssueSeverity.Medium,
                Description = "DHCP 位址池即將耗盡，新裝置將無法取得 IP（僅 DHCP 伺服器角色會出現）" },
        new() { SourcePattern = "Application Error", EventIds = new[] { 1000 },
                Category = IssueCategory.Service, Severity = IssueSeverity.Medium, CountThreshold = 3,
                Description = "應用程式反覆崩潰——服務完全掛掉之前通常先出現這個訊號" },
        new() { SourcePattern = ".NET Runtime", EventIds = new[] { 1026 },
                Category = IssueCategory.Service, Severity = IssueSeverity.Medium, CountThreshold = 3,
                Description = ".NET 應用程式未處理例外反覆發生" },
        new() { SourcePattern = "WindowsUpdateClient", EventIds = new[] { 20 },
                Category = IssueCategory.Config, Severity = IssueSeverity.Low,
                Description = "Windows Update 安裝失敗，持續失敗會累積未修補的安全風險" },

        // ── 安全 / 入侵跡象（Security log）──────────────────────────
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4625 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High, CountThreshold = 10,
                Description = "登入失敗；短時間大量發生代表帳號密碼暴力破解攻擊" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4740 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "帳戶被鎖定，通常是暴力破解的結果" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 1102 },
                Category = IssueCategory.Security, Severity = IssueSeverity.Critical,
                Description = "安全稽核日誌被清除——入侵者滅跡的典型行為，應立即調查" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4719 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "稽核原則被變更，可能是入侵者關閉記錄以躲避偵測" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4720, 4722, 4724 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "帳戶建立/啟用/密碼被重設——若非預期操作可能為入侵者建立立足點" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4728, 4732, 4756 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "帳戶被加入特權群組（如 Administrators），需確認是否為授權操作——典型提權手法" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4729, 4733, 4757 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "帳戶被移出特權群組，需確認是否為授權操作——也可能是入侵者提權得手後移除紀錄以滅跡" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4697, 4698 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "安裝服務或建立排程任務——攻擊者常見的持久化手法" },

        // ── 權限與角色異動（無論授予或移除都要關注）──────────────────────
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4670 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "檔案／資料夾／登錄物件的權限 (ACL) 被變更，需確認是否為授權操作" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4907 },
                Category = IssueCategory.Security, Severity = IssueSeverity.Critical,
                Description = "物件的稽核設定 (SACL) 被變更——可能是入侵者針對特定物件關閉稽核記錄以躲避偵測" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4717, 4718 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "系統存取權限被授予/移除（User Rights Assignment），需確認是否為授權操作" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4704, 4705 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "使用者權限指派被新增/移除，影響帳戶可執行的系統層級操作範圍" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4703 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "權杖 (token) 特殊權限於執行期間被調整——常見的權限提升攻擊手法" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4735 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "安全群組的內容或權限被變更" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4739 },
                Category = IssueCategory.Security, Severity = IssueSeverity.High,
                Description = "網域原則被變更（僅網域控制站），影響範圍可能擴及整個網域" },
        new() { SourcePattern = "Security-Auditing", EventIds = new[] { 4731, 4734 },
                Category = IssueCategory.Security, Severity = IssueSeverity.Medium,
                Description = "本機安全群組被建立/刪除，需確認是否為授權操作" },
    };

    /// <summary>用規則表為聚合後的事件簽章標記類別與嚴重度</summary>
    public static void Classify(LogIssueSignature signature)
    {
        foreach (var rule in Rules)
        {
            bool sourceMatch = signature.Source.Contains(rule.SourcePattern, StringComparison.OrdinalIgnoreCase);
            bool idMatch = rule.EventIds.Length == 0 || rule.EventIds.Contains(signature.EventId);

            if (sourceMatch && idMatch)
            {
                signature.Category = rule.Category;
                signature.Severity = signature.Count >= rule.CountThreshold
                    ? rule.Severity
                    : Downgrade(rule.Severity);
                signature.KnownIssue = rule.Description;
                return;
            }
        }

        signature.Category = IssueCategory.Other;
        signature.Severity = IssueSeverity.Low;
    }

    private static IssueSeverity Downgrade(IssueSeverity s) =>
        s == IssueSeverity.Low ? IssueSeverity.Low : s - 1;
}

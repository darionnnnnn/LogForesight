using System.Diagnostics;

namespace LogForesight.Core.Analysis;

/// <summary>
/// Sentinel 事件欄位鍵的單一事實來源（docs/NETIQ-API-REFERENCE.md §3.3、§9）。
///
/// 這些鍵名是三輪 <c>--netiq-probe</c> 真實環境輸出實證定案的（元大環境，Sentinel「162」，
/// 2026-07-29），**不是**官方文件推測——欄位「鍵」是 Sentinel schema 固定的，隨 collector 變的
/// 只是「值」，所以做成 Core 常數表而不是使用者可設定項（守「有設定無行為」紅線；
/// 真遇到不同 schema 的環境，probe 是驗收工具，此檔屆時再修）。
/// </summary>
public static class SentinelFieldMap
{
    /// <summary>Windows Event ID（如 4624、4771）。第一、二、三輪共 8 種 ID 交叉實證。</summary>
    public const string EventId = "rv40";

    /// <summary>事件來源／provider（如 <c>Microsoft-Windows-Security-Auditing</c>），
    /// 對應本機 <c>EventRecord.ProviderName</c>，規則 <c>SourcePattern</c> 的比對對象。
    /// **term 欄位、不斷詞**（第三輪步驟 12 實證：完整片語 found=142205，部分詞 found=0）——
    /// 子字串查詢無法下推到這個欄位，只能用完整值查詢或本地比對。</summary>
    public const string Source = "obssvcname";

    /// <summary>頻道／LogName（<c>Security</c>／<c>System</c>／<c>Application</c>），
    /// 與本機頻道名逐字相同。</summary>
    public const string LogName = "rv150";

    /// <summary>事件時間，ISO-8601 UTC。</summary>
    public const string Timestamp = "dt";

    /// <summary>嚴重度 0～5。已實證：0＝Security 成功稽核、1＝Information、4＝稽核失敗（Kerberos
    /// 預先驗證失敗 4771）。**2／3／5 的確切語意未實證**，留待試點核對（見 <see cref="MapEntryType"/>）。</summary>
    public const string Severity = "sev";

    /// <summary>訊息全文，繁中。</summary>
    public const string Message = "msg";

    /// <summary>事件名稱（人看的敘述，繁中翻譯），非結構化代碼，可當 Message 的 fallback。</summary>
    public const string EventName = "evt";

    /// <summary>
    /// **主機歸屬鍵**（第二輪定案）：這筆事件所屬主機的 IP。四台網域控制站對到四個各自不同的值
    /// （一對一，非共用的 collector 代理 IP），watchlist 查詢的 IP 批次以此欄位篩選。
    /// </summary>
    public const string HostIp = "repip";

    /// <summary>回報此事件的主機名稱（與 <see cref="HostIp"/> 成對的觀察者/自身識別）。
    /// 對應本機情境的 <c>Environment.MachineName</c>，供 <c>DisplayName</c> 回填。</summary>
    public const string HostName = "sn";

    /// <summary>事件描述的目的地主機名稱——跨主機事件時可能與 <see cref="HostName"/> 不同
    /// （如某帳戶對 DC 發起的驗證，dhn 可能仍是 DC 自己，見 xdastaxname 語意）。</summary>
    public const string DestinationHostName = "dhn";

    /// <summary>發起這次操作的帳號（第二、三輪定案：具名帳號如 <c>vtit.brk</c>、
    /// 員工編號式數字帳號、或 <c>-</c>＝無）。</summary>
    public const string InitiatorAccount = "sun";

    /// <summary>發起端來源 IP（**不是**主機自身，是連線的遠端來源；第一輪誤判為「不存在」，
    /// 其實登出事件不帶而已）。</summary>
    public const string InitiatorIp = "sip";

    /// <summary>發起端機器名稱（第三輪實證存在：跨主機驗證事件出現 <c>shn=vm-sps-web-02</c>）。</summary>
    public const string InitiatorHostName = "shn";

    /// <summary>XDAS 分類法的結果碼（0＝成功／XDAS_OUT_SUCCESS，其餘為失敗/未知等），
    /// 用於 Security 頻道的成功/失敗稽核判定，比 <see cref="Severity"/> 的門檻更明確
    /// （sev 的 2/3/5 語意未實證，xdasoutcome 是廠商定義的固定分類）。</summary>
    public const string XdasOutcome = "xdasoutcome";

    /// <summary>Q1 查詢要投影的欄位（只取分析與顯示需要的，不拉全欄位——降低傳輸量）。</summary>
    public static readonly IReadOnlyList<string> Q1ProjectionFields = new[]
    {
        HostIp, HostName, EventId, Source, LogName, Timestamp, Severity, Message, EventName,
        InitiatorAccount, XdasOutcome
    };

    // ── Linux（docs/archive/FEEDBACK-12-PLAN.md §4.0/§4.4，四輪 probe 實證定案，Sentinel「118_linux」）──

    /// <summary>syslog program／process 名稱（如 <c>sshd</c>、<c>kernel</c>）。**term 欄位、
    /// 大小寫不敏感、支援前綴萬用字元**（輪 B 第 1 項：<c>sp:networkmanager</c> 與
    /// <c>sp:NetworkManager</c> found 相同；<c>sp:user*</c> 命中 <c>sp:user</c> 查不到的事件）——
    /// 與 <see cref="Source"/>（Windows）的「term 但不斷詞」語意相近，但 Linux 這邊額外確認
    /// 前綴萬用字元有效，filter 產生器可放心用 <c>sp:{program}*</c>。</summary>
    public const string LinuxProgram = "sp";

    /// <summary>CEF collector（Universal Common Event Format）路徑的事件 program 落點——
    /// 第二次 probe 實證：這類事件 <see cref="LinuxProgram"/> 缺席，program 改落在這裡
    /// （如 <c>obssvcname=conmon</c>）。受監控主機目前全走 NetIQ Universal Event collector
    /// （輪 B 第 7 項：欄位聯集無此欄），這是 <see cref="SentinelEventMapper"/> 的第二順位
    /// fallback，不是主要取值來源。</summary>
    public const string LinuxObsSvcName = "obssvcname";

    /// <summary>syslog facility（如 <c>DAEMON</c>、<c>KERNEL</c>、<c>USER</c>）——輪 A 實證：
    /// 同名欄位在 Windows 事件上承載頻道名（<see cref="LogName"/>），Linux 事件上是不同語意，
    /// 兩者共用同一個 Sentinel 欄位鍵但意義不同（collector 差異）。第一版投影帶回，不參與比對。</summary>
    public const string LinuxFacility = "rv150";

    /// <summary>Linux Q1 查詢投影欄位。不含 <see cref="EventName"/>（`evt` 在此環境是
    /// 「NetIQ Universal Event {program} Event」樣板字串，無正規化語意，輪 A 定案不使用）、
    /// 不含 <see cref="InitiatorAccount"/>／<see cref="InitiatorIp"/>／<see cref="XdasOutcome"/>／
    /// <see cref="EventId"/>（Linux 事件無此四欄，輪 A 實證）。含 <see cref="LinuxObsSvcName"/>：
    /// 短欄位，頻寬成本可忽略，供 mapper 的 Source 三段 fallback 鏈使用。</summary>
    public static readonly IReadOnlyList<string> LinuxQ1ProjectionFields = new[]
    {
        HostIp, HostName, LinuxProgram, LinuxObsSvcName, LinuxFacility, Timestamp, Severity, Message
    };

    /// <summary>
    /// Linux sev→<see cref="EventLogEntryType"/> 的推導（輪 B 第 3/4 項定案）：
    /// <c>0~1→Information、2→Warning、3~5→Error</c>。**這是計數用途的務實選擇，不是 syslog
    /// priority 語意的還原**——輪 B 實證 sev 不可靠承載該語意（NetworkManager 的 <c>&lt;warn&gt;</c>
    /// 與 dockerd 的 <c>level=error</c> 皆落在 sev=1；「pam_unix session opened」這類正常訊息
    /// 反而落在 sev3-5）。誤差只影響錯誤/警告計數與 generic 收集門檻，不影響規則命中
    /// （program＋message 比對，見 <see cref="KnownIssueCatalog.FindLinuxRule"/>，與 EntryType 無關）。
    /// </summary>
    public static EventLogEntryType MapEntryTypeLinux(int severity) => severity switch
    {
        <= 1 => EventLogEntryType.Information,
        2 => EventLogEntryType.Warning,
        _ => EventLogEntryType.Error
    };

    /// <summary>
    /// 嚴重度→<see cref="EventLogEntryType"/> 的推導。**部分門檻是候選值，未經試點實證**：
    /// Security 頻道靠 <see cref="XdasOutcome"/>（廠商固定分類，比 sev 門檻可靠）判斷成功/失敗稽核；
    /// 非 Security 頻道目前只有 sev=1（Information）與 sev=4（對應本機 Error 等級的失敗事件）兩個錨點，
    /// 2／3 的 Warning/Error 分界是依常見遞增嚴重度量表（0-1 低、2-3 中、4-5 高）的合理猜測，
    /// **需要試點階段用該主機本機 Event Viewer 對照確認**（見 docs/BACKLOG.md）。
    /// 猜錯的後果僅止於報告上的等級徽章與分組粒度顯示不夠精確，不影響規則命中（規則比對的是
    /// Source＋EventId，與 EntryType 無關）。
    /// </summary>
    public static EventLogEntryType MapEntryType(string logName, int severity, int? xdasOutcome)
    {
        if (string.Equals(logName, "Security", StringComparison.OrdinalIgnoreCase))
        {
            // xdasOutcome 缺席時保守当失敗處理——稽核事件寧可多算一筆失敗也不要漏算
            return xdasOutcome == 0 ? EventLogEntryType.SuccessAudit : EventLogEntryType.FailureAudit;
        }

        return severity switch
        {
            <= 1 => EventLogEntryType.Information,
            <= 3 => EventLogEntryType.Warning,          // 候選門檻，未實證
            _ => EventLogEntryType.Error                 // sev>=4 已實證對應失敗/錯誤事件
        };
    }
}

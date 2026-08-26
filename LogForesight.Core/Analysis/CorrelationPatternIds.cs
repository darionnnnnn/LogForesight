namespace LogForesight.Core.Analysis;

/// <summary>
/// 跨 log 關聯模式的穩定識別碼目錄（回饋十五輪 A-5）：<see cref="CorrelationFinding.PatternId"/>
/// 是 required 屬性，新增模式時編譯期就會強制指定，這份目錄是唯一權威來源——供
/// <see cref="CorrelationAnalyzer"/>／<see cref="LinuxCorrelationAnalyzer"/> 標記，也供
/// Web 層驗證 <c>RuleSuppression.CorrelationPatternId</c>（TargetType=Correlation）是否為
/// 已知模式（Core 的 CorrelationFinding/CorrelationAnalyzer 是 internal，Web 看不到，
/// 這份目錄故意是 public，是兩邊唯一共用的介面）。
///
/// public 但不代表模式的組合邏輯外露——這裡只有識別碼字串，實際觸發條件仍完全在
/// CorrelationAnalyzer／LinuxCorrelationAnalyzer 內部。
/// </summary>
public static class CorrelationPatternIds
{
    // ── Windows：同日組合 ──────────────────────────────────────────
    public const string IntrusionChain = "intrusion-chain";
    public const string BruteSuccess = "brute-success";
    public const string Persistence = "persistence";
    public const string AuditTamper = "audit-tamper";
    public const string PrivImplant = "priv-implant";
    public const string AvOffMalware = "av-off-malware";
    public const string MalwarePersistence = "malware-persistence";
    public const string StorageChain = "storage-chain";
    public const string StorageCrash = "storage-crash";
    public const string HwUnstable = "hw-unstable";
    public const string CrashServiceFail = "crash-service-fail";
    public const string CrashLoopResource = "crash-loop-resource";
    public const string TimeSkewAuth = "time-skew-auth";
    /// <summary>密碼噴灑偵測（同時適用 Windows 與 Linux 簽章）</summary>
    public const string PasswordSpray = "password-spray";

    // ── Windows：跨日組合 ──────────────────────────────────────────
    public const string XdayIntrusion = "xday-intrusion";
    public const string XdayStorage = "xday-storage";
    public const string XdayAvOffMalware = "xday-av-off-malware";
    public const string XdayBruteRdp = "xday-brute-rdp";

    // ── Linux ──────────────────────────────────────────────────────
    public const string LinuxSshBruteSuccess = "linux-ssh-brute-success";
    public const string LinuxSshBruteUncertain = "linux-ssh-brute-uncertain";

    public static readonly string[] All =
    {
        IntrusionChain, BruteSuccess, Persistence, AuditTamper, PrivImplant,
        AvOffMalware, MalwarePersistence, StorageChain, StorageCrash, HwUnstable,
        CrashServiceFail, CrashLoopResource, TimeSkewAuth, PasswordSpray,
        XdayIntrusion, XdayStorage, XdayAvOffMalware, XdayBruteRdp,
        LinuxSshBruteSuccess, LinuxSshBruteUncertain
    };

    public static bool IsValid(string patternId) => All.Contains(patternId);
}

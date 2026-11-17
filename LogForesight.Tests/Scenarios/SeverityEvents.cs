namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 第 76 輪 E4：規則嚴重度情境用的真實格式事件片段。來源名、事件編號、等級、頻道照真實 Windows／Linux 事件，
/// 訊息主體是單行（欄位以空白分隔）。寫法的原則是「事件照真實格式」，不為了命中規則而遷就規則。
/// </summary>
public static class SeverityEvents
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // ── 時間分布 ──────────────────────────────────────────────────────────

    /// <summary>在 <paramref name="start"/> 起的 <paramref name="window"/> 內均勻分布 N 筆（確定性）</summary>
    public static List<ScenarioEvent> Spread(int count, TimeSpan start, TimeSpan window, Func<int, ScenarioEvent> make)
    {
        var list = new List<ScenarioEvent>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = count <= 1 ? TimeSpan.Zero : TimeSpan.FromSeconds(window.TotalSeconds * i / count);
            list.Add(make(i) with { At = start + offset });
        }
        return list;
    }

    public static ScenarioEvent One(TimeSpan at, ScenarioEvent e) => e with { At = at };

    // ── Windows：系統 ─────────────────────────────────────────────────────

    public static ScenarioEvent KernelPower41(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-Kernel-Power", 41, ScenarioEventLevel.Error,
            "The system has rebooted without cleanly shutting down first. This error could be caused if the system stopped responding, crashed, or lost power unexpectedly.");

    public static ScenarioEvent Unexpected6008(TimeSpan at) =>
        new(at, "System", "EventLog", 6008, ScenarioEventLevel.Error,
            "The previous system shutdown at 3:12:45 AM on 11/10/2026 was unexpected.");

    public static ScenarioEvent EventLogStopped6006(TimeSpan at) =>
        new(at, "System", "EventLog", 6006, ScenarioEventLevel.Information, "The Event log service was stopped.");

    public static ScenarioEvent EventLogStarted6005(TimeSpan at) =>
        new(at, "System", "EventLog", 6005, ScenarioEventLevel.Information, "The Event log service was started.");

    /// <summary>1074 的真實來源是 User32（不是 EventLog）</summary>
    public static ScenarioEvent PlannedRestart1074(TimeSpan at) =>
        new(at, "System", "User32", 1074, ScenarioEventLevel.Information,
            "The process C:\\Windows\\system32\\shutdown.exe (WIN-E4) has initiated the restart of computer WIN-E4 on behalf of user CORP\\admin1 for the following reason: Operating System: Recovery (Planned) Reason Code: 0x80020002 Shutdown Type: restart Comment: Monthly patch window");

    public static ScenarioEvent BugCheck1001(TimeSpan at) =>
        new(at, "System", "BugCheck", 1001, ScenarioEventLevel.Error,
            "The computer has rebooted from a bugcheck. The bugcheck was: 0x0000009f (0x0000000000000003, 0xffffe0012a3b4060, 0xfffff80123456780, 0xffffe0012c5d8a10). A dump was saved in: C:\\Windows\\MEMORY.DMP. Report Id: 5b2f0f6e-1c3a-4a0e-9d55-3f1c1d0b7a11.");

    public static ScenarioEvent Whea19Corrected(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-WHEA-Logger", 19, ScenarioEventLevel.Warning,
            "A corrected hardware error has occurred. Reported by component: Processor Core Error Source: Corrected Machine Check Error Type: Cache Hierarchy Error Processor APIC ID: 4 The details view of this entry contains further information.");

    public static ScenarioEvent Whea47Corrected(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-WHEA-Logger", 47, ScenarioEventLevel.Warning,
            "A corrected hardware error has occurred. Reported by component: PCI Express Root Port Error Source: Advanced Error Reporting (PCI Express) Primary Bus:Device:Function: 0x0:0x1C:0x0 The details view of this entry contains further information.");

    public static ScenarioEvent Whea18Fatal(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-WHEA-Logger", 18, ScenarioEventLevel.Error,
            "A fatal hardware error has occurred. Reported by component: Processor Core Error Source: Machine Check Exception Error Type: Cache Hierarchy Error Processor APIC ID: 2 The details view of this entry contains further information.");

    /// <summary>disk 153 的真實等級是 Warning（I/O 重試）</summary>
    public static ScenarioEvent Disk153Retry(TimeSpan at, int n = 0) =>
        new(at, "System", "disk", 153, ScenarioEventLevel.Warning,
            $"The IO operation at logical block address 0x{0x2c4a10 + n * 8:x} for Disk 0 (PDO name: \\Device\\0000026) was retried.");

    public static ScenarioEvent Disk7BadBlock(TimeSpan at, int n = 0) =>
        new(at, "System", "disk", 7, ScenarioEventLevel.Error, "The device, \\Device\\Harddisk0\\DR0, has a bad block.");

    public static ScenarioEvent Ntfs55(TimeSpan at) =>
        new(at, "System", "Ntfs", 55, ScenarioEventLevel.Error,
            "A corruption was discovered in the file system structure on volume C:. The exact nature of the corruption is unknown. The file system structures need to be scanned and repaired.");

    public static ScenarioEvent ServiceState7036(TimeSpan at, int n = 0) =>
        new(at, "System", "Service Control Manager", 7036, ScenarioEventLevel.Information,
            $"The {(n % 2 == 0 ? "Windows Update" : "Background Intelligent Transfer Service")} service entered the {(n % 3 == 0 ? "stopped" : "running")} state.");

    public static ScenarioEvent ServiceInstalled7045(TimeSpan at) =>
        new(at, "System", "Service Control Manager", 7045, ScenarioEventLevel.Information,
            "A service was installed in the system. Service Name: WinSvcUpdater Service File Name: C:\\ProgramData\\svc\\updater.exe -k netsvcs Service Type: user mode service Service Start Type: auto start Service Account: LocalSystem");

    public static ScenarioEvent DistributedCom10016(TimeSpan at, int n = 0) =>
        new(at, "System", "Microsoft-Windows-DistributedCOM", 10016, ScenarioEventLevel.Error,
            "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {D63B10C5-BB46-4990-A94F-E40B9D520160} and APPID {9CA88EE3-ACB7-47C8-AFC4-AB702511C276} to the user NT AUTHORITY\\SYSTEM SID (S-1-5-18) from address LocalHost (Using LRPC) running in the application container Unavailable SID (Unavailable). This security permission can be modified using the Component Services administrative tool.");

    public static ScenarioEvent WindowsUpdateInstalled19(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-WindowsUpdateClient", 19, ScenarioEventLevel.Information,
            "Installation Successful: Windows successfully installed the following update: 2026-10 Cumulative Update for Windows Server 2022 (KB5099999)");

    public static ScenarioEvent DnsTimeout1014(TimeSpan at, int n = 0) =>
        new(at, "System", "Microsoft-Windows-DNS-Client", 1014, ScenarioEventLevel.Warning,
            $"Name resolution for the name wpad{n}.corp.example.com timed out after none of the configured DNS servers responded.");

    public static ScenarioEvent TimeService36(TimeSpan at) =>
        new(at, "System", "Microsoft-Windows-Time-Service", 36, ScenarioEventLevel.Warning,
            "The time service has not been able to synchronize the system time for 86400 seconds because none of the time providers has been able to provide a usable time sample. The time service will continue to retry and will log this message again in 86400 seconds if the problem persists.");

    /// <summary>Windows Server Backup 517（來源 Microsoft-Windows-Backup）</summary>
    public static ScenarioEvent BackupFailed517(TimeSpan at, int n = 0) =>
        new(at, "Application", "Microsoft-Windows-Backup", 517, ScenarioEventLevel.Error,
            "The backup operation that started at '2026-11-10T01:00:00.000000000Z' has failed with following error code '0x80780119' (The backup failed because the volume shadow copy service could not be started).");

    // ── Windows：安全性稽核 ────────────────────────────────────────────────
    // NetIQ／Sentinel 送來的訊息主體是單行：區段標題與欄位之間是連續空白（這裡用 3 個），值內只有單一空白。
    // 這是 LogAggregator 取標籤值的邊界（連續兩個以上空白結束一個值），照真實格式寫才取得到帳號與來源。

    private const string Auditing = "Microsoft-Windows-Security-Auditing";

    private static string Fields(params string[] parts) => string.Join("   ", parts);

    public static ScenarioEvent Logon4625(TimeSpan at, string account, string sourceIp, int n = 0, string workstation = "kali") =>
        new(at, "Security", Auditing, 4625, ScenarioEventLevel.FailureAudit,
            Fields("An account failed to log on.", "Subject:", "Security ID: S-1-0-0", "Account Name: -", "Account Domain: -", "Logon ID: 0x0", "Logon Type: 3",
                "Account For Which Logon Failed:", "Security ID: S-1-0-0", $"Account Name: {account}", "Account Domain: CORP",
                "Failure Information:", "Failure Reason: Unknown user name or bad password.", "Status: 0xC000006D", "Sub Status: 0xC000006A",
                "Process Information:", "Caller Process ID: 0x0", "Caller Process Name: -",
                "Network Information:", $"Workstation Name: {workstation}", $"Source Network Address: {sourceIp}", $"Source Port: {49152 + n}",
                "Detailed Authentication Information:", "Logon Process: NtLmSsp", "Authentication Package: NTLM", "Transited Services: -", "Package Name (NTLM only): -", "Key Length: 0"));

    public static ScenarioEvent Logon4624(TimeSpan at, string account, string sourceIp, int n = 0, int logonType = 3, string workstation = "kali") =>
        new(at, "Security", Auditing, 4624, ScenarioEventLevel.SuccessAudit,
            Fields("An account was successfully logged on.", "Subject:", "Security ID: S-1-0-0", "Account Name: -", "Account Domain: -", "Logon ID: 0x0",
                "Logon Information:", $"Logon Type: {logonType}", "Restricted Admin Mode: -", "Virtual Account: No", "Elevated Token: Yes",
                "New Logon:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-1105", $"Account Name: {account}", "Account Domain: CORP", $"Logon ID: 0x{0x5A3C10 + n:X}",
                "Process Information:", "Process ID: 0x0", "Process Name: -",
                "Network Information:", $"Workstation Name: {workstation}", $"Source Network Address: {sourceIp}", $"Source Port: {50011 + n}",
                "Detailed Authentication Information:", "Logon Process: NtLmSsp", "Authentication Package: NTLM", "Key Length: 128"));

    public static ScenarioEvent AccountCreated4720(TimeSpan at, string actor, string newAccount) =>
        new(at, "Security", Auditing, 4720, ScenarioEventLevel.SuccessAudit,
            Fields("A user account was created.", "Subject:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-500", $"Account Name: {actor}", "Account Domain: CORP", "Logon ID: 0x3E7A1",
                "New Account:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-2201", $"Account Name: {newAccount}", "Account Domain: CORP",
                "Attributes:", "SAM Account Name: svc", "Primary Group ID: 513", "Account Expires: <never>"));

    public static ScenarioEvent GroupMemberAdded4732(TimeSpan at, string actor, string member, string groupName, string groupSid) =>
        new(at, "Security", Auditing, 4732, ScenarioEventLevel.SuccessAudit,
            Fields("A member was added to a security-enabled local group.", "Subject:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-500", $"Account Name: {actor}", "Account Domain: CORP", "Logon ID: 0x3E7A1",
                "Member:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-2201", $"Account Name: CN={member},CN=Users,DC=corp,DC=example,DC=com",
                "Group:", $"Security ID: {groupSid}", $"Group Name: {groupName}", "Group Domain: Builtin"));

    /// <summary>1102 的真實來源是 Microsoft-Windows-Eventlog（不是 Security-Auditing）</summary>
    public static ScenarioEvent AuditCleared1102(TimeSpan at, string actor, string source = "Microsoft-Windows-Eventlog") =>
        new(at, "Security", source, 1102, ScenarioEventLevel.SuccessAudit,
            Fields("The audit log was cleared.", "Subject:", "Security ID: S-1-5-21-1004336348-1177238915-682003330-500", $"Account Name: {actor}", "Domain Name: CORP", "Logon ID: 0x3E7A1"));

    // ── Windows Defender ──────────────────────────────────────────────────

    private const string Defender = "Microsoft-Windows-Windows Defender";
    private const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";

    public static ScenarioEvent Defender1116(TimeSpan at) =>
        new(at, DefenderLog, Defender, 1116, ScenarioEventLevel.Warning,
            "Microsoft Defender Antivirus has detected malware or other potentially unwanted software. For more information please see the following: https://go.microsoft.com/fwlink/?linkid=37020&name=Trojan:Win32/Wacatac.B!ml&threatid=2147735503 Name: Trojan:Win32/Wacatac.B!ml ID: 2147735503 Severity: Severe Category: Trojan Path: file:_C:\\Users\\Public\\Downloads\\invoice.exe Detection Origin: Internet Detection Type: Concrete Detection Source: Real-Time Protection User: CORP\\user01 Process Name: C:\\Windows\\explorer.exe");

    public static ScenarioEvent Defender1117(TimeSpan at) =>
        new(at, DefenderLog, Defender, 1117, ScenarioEventLevel.Information,
            "Microsoft Defender Antivirus has taken action to protect this machine from malware or other potentially unwanted software. For more information please see the following: Name: Trojan:Win32/Wacatac.B!ml ID: 2147735503 Severity: Severe Category: Trojan Path: file:_C:\\Users\\Public\\Downloads\\invoice.exe Action: Quarantine Action Status: No additional actions required Error Code: 0x00000000");

    public static ScenarioEvent Defender1118(TimeSpan at) =>
        new(at, DefenderLog, Defender, 1118, ScenarioEventLevel.Warning,
            "Microsoft Defender Antivirus has encountered a non-critical error when taking action on malware or other potentially unwanted software. Name: Trojan:Win32/Wacatac.B!ml ID: 2147735503 Severity: Severe Category: Trojan Path: file:_C:\\Users\\Public\\Downloads\\invoice.exe Action: Quarantine Error Code: 0x80508023 Error description: The program could not find the malware and other potentially unwanted software on this device.");

    // ── Linux ────────────────────────────────────────────────────────────

    public static ScenarioEvent Linux(TimeSpan at, string program, string message, ScenarioEventLevel level = ScenarioEventLevel.Information) =>
        new(at, "Linux", program, 0, level, message) { LinuxProgram = program };

    public static ScenarioEvent LinuxKernel(TimeSpan at, string message, ScenarioEventLevel level = ScenarioEventLevel.Error) =>
        Linux(at, "kernel", message, level);

    public static ScenarioEvent LinuxIoError(TimeSpan at, string dev, int n = 0) =>
        LinuxKernel(at, $"blk_update_request: I/O error, dev {dev}, sector {1953525168 + n * 8} op 0x0:(READ) flags 0x0 phys_seg 1 prio class 0");

    public static ScenarioEvent LinuxSshFailed(TimeSpan at, string sourceIp, int n = 0) =>
        Linux(at, "sshd", $"Failed password for root from {sourceIp} port {40000 + n} ssh2");

    public static ScenarioEvent LinuxSshAccepted(TimeSpan at, string account, string sourceIp, int n = 0, string method = "password") =>
        Linux(at, "sshd", $"Accepted {method} for {account} from {sourceIp} port {50022 + n} ssh2");
}

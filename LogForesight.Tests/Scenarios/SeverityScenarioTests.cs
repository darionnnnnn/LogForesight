using System.Text;
using LogForesight.Core.Models;
using Xunit;
using static LogForesight.Tests.Scenarios.SeverityEvents;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 第 76 輪 E4：規則嚴重度情境庫（26 個典型日）。預期定稿在 <c>docs/archive/FEEDBACK-76-EVIDENCE.md</c>
/// 「E4 規則嚴重度 → 預期標註」表（H1～H12、I1～I11、N1～N4），這裡照表寫成預期、不改預期。
/// 用預設種子規則（<c>KnownIssueSeed</c>）與預設門檻，事件照真實格式合成；實際與預期不一致的列入已知差異，
/// 鍵 <c>E4-{情境編號}</c>。多日情境每個被標註的天各一個預期。
/// 每個情境跑完把判定日的風險、判定依據、命中的問題、趨勢與關聯寫進測試輸出
/// （環境變數 <c>LF_E4_DUMP</c> 指定檔案時一併附加到該檔），供對照表使用。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed partial class SeverityScenarioTests
{
    private const string Win = "WIN-E4";
    private const string Lnx = "LNX-E4";

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SeverityScenarioTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static Scenario NewScenario(string id, string title, bool linux = false)
    {
        var scenario = new Scenario($"{id} {title}")
            .WithSettings(new ScenarioSettings { AutoDispatchEnabled = false, WorkOrderMailQueue = false })
            .WithHost(new HostSpec(linux ? Lnx : Win) { IsLinux = linux, Groups = new[] { "G1" } })
            .WithUser(new UserSpec("admin1", UserRole.Admin));
        return scenario;
    }

    private static string HostOf(Scenario s) => s.Hosts[0].Name;

    /// <summary>登記「某天主機日風險應為 X」的預期（鍵 D{天}.risk.{主機}）</summary>
    private static void ExpectRisk(Scenario s, int day, string risk) => s.Expect(Expect.HostRisk(day, HostOf(s), risk));

    /// <summary>某情境的已知差異：指向 <see cref="ExpectRisk"/> 登記的預期</summary>
    private static void Known(Scenario s, string id, params int[] days)
    {
        foreach (var day in days) s.KnownDifference($"E4-{id}", $"D{day}.risk.{HostOf(s)}");
    }

    private void RunAndAssert(Scenario scenario, params int[] reportDays)
    {
        var result = ScenarioDriver.Run(scenario);
        var dump = Dump(scenario, result, reportDays.Length == 0 ? Enumerable.Range(1, scenario.TotalDays).ToArray() : reportDays);
        _output.WriteLine(dump);
        var path = Environment.GetEnvironmentVariable("LF_E4_DUMP");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, dump + "\n");
        foreach (var line in result.Verdict.StillKnown) _output.WriteLine("已知差異：" + line);
        result.AssertExpectations();
    }

    private static string Dump(Scenario scenario, ScenarioResult result, IEnumerable<int> days)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {scenario.Name}");
        var host = HostOf(scenario);
        foreach (var d in days)
        {
            var day = result.Day(d);
            var h = day.Host(host);
            sb.AppendLine($"第{d}天 風險={(h.HasRecord ? h.RiskLevel : "無紀錄")}｜依據={h.RiskBasis}｜錯誤{h.ErrorCount}／警告{h.WarningCount}");
            foreach (var i in day.Issues.Where(x => x.Host == host))
                sb.AppendLine($"  問題：{i.Label}｜{i.Severity}｜x{i.Count}｜重大={(i.ElevatesDayRisk ? "是" : "否")}｜抑制={(i.Suppressed ? "是" : "否")}｜趨勢={i.Trend}");
            foreach (var a in day.Alerts.Where(x => x.Host == host))
                sb.AppendLine($"  {a.Kind}：{a.Text}{(a.Suppressed ? "（抑制）" : "")}");
        }
        return sb.ToString().TrimEnd();
    }

    // ══ 硬體 ═══════════════════════════════════════════════════════════════

    [Fact]
    public void H1_維護窗口正常重開()
    {
        var s = NewScenario("H1", "維護窗口內正常重開");
        s.Day(1, d => d.Events(Win, new[]
        {
            PlannedRestart1074(TimeSpan.FromHours(3)), EventLogStopped6006(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(40)),
            EventLogStarted6005(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(4))
        }));
        ExpectRisk(s, 1, RiskLevels.Low);
        RunAndAssert(s);
    }

    [Fact]
    public void H2_單次非預期重開()
    {
        var s = NewScenario("H2", "單次非預期重開");
        s.Day(1, d => d.Events(Win, new[] { KernelPower41(TimeSpan.FromHours(3)), Unexpected6008(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(3)) }));
        s.Days(3);
        ExpectRisk(s, 1, RiskLevels.Medium);
        // 已知差異 E4-H2：實際 高、預期 中：builtin-hardware-kernel-power-41 是 High 且重大旗標，單筆 41 就把當天判成高風險，規則沒有「單次／反覆」之分
        Known(s, "H2", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void H3_一週內三次非預期重開()
    {
        var s = NewScenario("H3", "一週內 41+6008 出現在第 1、3、5 天");
        foreach (var day in new[] { 1, 3, 5 })
            s.Day(day, d => d.Events(Win, new[] { KernelPower41(TimeSpan.FromHours(3)), Unexpected6008(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(3)) }));
        s.Days(7);
        ExpectRisk(s, 5, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void H4_單次WHEA已修正錯誤()
    {
        var s = NewScenario("H4", "單次 WHEA 已修正錯誤");
        s.Day(1, d => d.Events(Win, new[] { Whea19Corrected(TimeSpan.FromHours(14)) }));
        s.Days(3);
        ExpectRisk(s, 1, RiskLevels.Medium);
        // 已知差異 E4-H4：實際 高、預期 中：builtin-hardware-whea-error 對 WHEA-Logger 全部事件編號（含已修正的 19／47）一律 High＋重大，不分已修正與致命
        Known(s, "H4", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void H5_WHEA已修正錯誤逐日增加()
    {
        var s = NewScenario("H5", "WHEA 已修正錯誤第 1～4 天 1、3、8、20 筆");
        var counts = new[] { 1, 3, 8, 20 };
        for (var i = 0; i < counts.Length; i++)
        {
            var n = counts[i];
            s.Day(i + 1, d => d.Events(Win, Spread(n, TimeSpan.FromHours(2), TimeSpan.FromHours(18),
                k => k % 2 == 0 ? Whea19Corrected(TimeSpan.Zero) : Whea47Corrected(TimeSpan.Zero))));
        }
        ExpectRisk(s, 4, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void H6_WHEA致命錯誤()
    {
        var s = NewScenario("H6", "WHEA 致命錯誤一筆");
        s.Day(1, d => d.Events(Win, new[] { Whea18Fatal(TimeSpan.FromHours(10)) }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void H7_磁碟逐步故障七日()
    {
        var s = NewScenario("H7", "磁碟逐步故障七日");
        s.Day(1, d => d.Events(Win, Spread(2, TimeSpan.FromHours(9), TimeSpan.FromHours(6), k => Disk153Retry(TimeSpan.Zero, k))));
        s.Day(2, d => d.Events(Win, Spread(3, TimeSpan.FromHours(9), TimeSpan.FromHours(6), k => Disk153Retry(TimeSpan.Zero, k))));
        for (var day = 3; day <= 7; day++)
        {
            var n = day;
            s.Day(day, d =>
            {
                d.Events(Win, Spread(2, TimeSpan.FromHours(9), TimeSpan.FromHours(6), k => Disk7BadBlock(TimeSpan.Zero, k)));
                d.Events(Win, Spread(3, TimeSpan.FromHours(10), TimeSpan.FromHours(6), k => Disk153Retry(TimeSpan.Zero, k + n)));
                if (n == 5) d.Events(Win, new[] { Ntfs55(TimeSpan.FromHours(16)) });
            });
        }
        ExpectRisk(s, 3, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void H8_磁碟IO重試每天少量連續七天()
    {
        var s = NewScenario("H8", "disk 153 每天 1～2 筆連續 7 天");
        for (var day = 1; day <= 7; day++)
        {
            var n = day % 2 == 1 ? 1 : 2;
            s.Day(day, d => d.Events(Win, Spread(n, TimeSpan.FromHours(11), TimeSpan.FromHours(4), k => Disk153Retry(TimeSpan.Zero, k))));
        }
        for (var day = 3; day <= 7; day++) ExpectRisk(s, day, RiskLevels.Medium);
        // 已知差異 E4-H8：實際 高、預期 中：builtin-storage-disk-io 對 disk 153 單筆即 High＋重大，每天都高；沒有「低量持續」的中度層級
        Known(s, "H8", 3, 4, 5, 6, 7);
        RunAndAssert(s);
    }

    [Fact]
    public void H9_單次藍屏()
    {
        var s = NewScenario("H9", "單次藍屏（BugCheck 1001+41+6008）");
        s.Day(1, d => d.Events(Win, new[]
        {
            KernelPower41(TimeSpan.FromHours(15)), Unexpected6008(TimeSpan.FromHours(15) + TimeSpan.FromMinutes(4)),
            BugCheck1001(TimeSpan.FromHours(15) + TimeSpan.FromMinutes(5))
        }));
        s.Days(3);
        ExpectRisk(s, 1, RiskLevels.Medium);
        // 已知差異 E4-H9：實際 高、預期 中：builtin-hardware-bugcheck-1001 與 kernel-power-41 皆 High＋重大，單次藍屏判高
        Known(s, "H9", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void H10_Linux插拔USB隨身碟的IO錯誤()
    {
        var s = NewScenario("H10", "Linux 插拔 USB 隨身碟產生 I/O error", linux: true);
        var t = TimeSpan.FromHours(14);
        s.Day(1, d => d.Events(Lnx, new[]
        {
            LinuxKernel(t, "usb 1-1: new high-speed USB device number 4 using xhci_hcd", ScenarioEventLevel.Information),
            LinuxKernel(t + TimeSpan.FromSeconds(2), "sd 6:0:0:0: [sdb] 30031872 512-byte logical blocks: (15.4 GB/14.3 GiB)", ScenarioEventLevel.Information),
            LinuxKernel(t + TimeSpan.FromMinutes(20), "usb 1-1: USB disconnect, device number 4", ScenarioEventLevel.Information),
            LinuxIoError(t + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(1), "sdb", 0),
            LinuxIoError(t + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(1), "sdb", 1),
            LinuxKernel(t + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(2), "Buffer I/O error on dev sdb1, logical block 0, lost async page write")
        }));
        ExpectRisk(s, 1, RiskLevels.Low);
        // 已知差異 E4-H10：實際 高、預期 低：builtin-linux-storage-io 只比對 kernel 程式名加「I/O error」字樣，看不出裝置是可移除的 USB 隨身碟
        Known(s, "H10", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void H11_Linux系統碟錯誤唯讀重掛陣列降級()
    {
        var s = NewScenario("H11", "Linux 系統碟 I/O error + 唯讀重掛 + md 降級", linux: true);
        var t = TimeSpan.FromHours(4);
        s.Day(1, d => d.Events(Lnx, new[]
        {
            LinuxIoError(t, "sda", 0), LinuxIoError(t + TimeSpan.FromSeconds(1), "sda", 1),
            LinuxKernel(t + TimeSpan.FromSeconds(3), "EXT4-fs (sda1): Remounting filesystem read-only"),
            LinuxKernel(t + TimeSpan.FromSeconds(5), "md/raid1:md0: Disk failure on sdb1, disabling device."),
            LinuxKernel(t + TimeSpan.FromSeconds(5), "md/raid1:md0: Operation continuing on 1 devices."),
            Linux(t + TimeSpan.FromSeconds(8), "mdadm", "Fail event detected on md device /dev/md0, component device /dev/sdb1", ScenarioEventLevel.Warning),
            Linux(t + TimeSpan.FromSeconds(9), "mdadm", "DegradedArray event detected on md device /dev/md0", ScenarioEventLevel.Warning)
        }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void H12_LinuxSAN多路徑短暫切換()
    {
        var s = NewScenario("H12", "Linux SAN 多路徑 path failed 後 reinstated，伴隨一筆 I/O error", linux: true);
        var t = TimeSpan.FromHours(13);
        s.Day(1, d => d.Events(Lnx, new[]
        {
            Linux(t, "multipathd", "8:32: mark as failed", ScenarioEventLevel.Warning),
            Linux(t + TimeSpan.FromSeconds(0), "multipathd", "mpatha: remaining active paths: 1", ScenarioEventLevel.Warning),
            LinuxIoError(t + TimeSpan.FromSeconds(1), "sdc", 0),
            Linux(t + TimeSpan.FromSeconds(6), "multipathd", "mpatha: sdc - tur checker reports path is up", ScenarioEventLevel.Information),
            Linux(t + TimeSpan.FromSeconds(6), "multipathd", "8:32: reinstated", ScenarioEventLevel.Information),
            Linux(t + TimeSpan.FromSeconds(6), "multipathd", "mpatha: remaining active paths: 2", ScenarioEventLevel.Information)
        }));
        ExpectRisk(s, 1, RiskLevels.Medium);
        // 已知差異 E4-H12：實際 高、預期 中：builtin-linux-storage-io 命中那一筆 I/O error 即 High＋重大；multipathd 的 failed／reinstated 沒有規則，備援生效的資訊用不上
        Known(s, "H12", 1);
        RunAndAssert(s);
    }

    // ══ 入侵 ═══════════════════════════════════════════════════════════════

    private static List<ScenarioEvent> Brute4625(int count = 200) =>
        Spread(count, TimeSpan.FromHours(10), TimeSpan.FromHours(1), k => Logon4625(TimeSpan.Zero, "administrator", "203.0.113.9", k));

    [Fact]
    public void I1_單一來源一小時兩百次登入失敗沒有成功()
    {
        var s = NewScenario("I1", "單一外部來源 1 小時內 200 次 4625，沒有成功登入");
        s.Day(1, d => d.Events(Win, Brute4625()));
        ExpectRisk(s, 1, RiskLevels.Medium);
        RunAndAssert(s);
    }

    [Fact]
    public void I2_暴力破解後成功登入()
    {
        var s = NewScenario("I2", "200 次 4625 後同來源同帳號 4624 成功");
        var events = Brute4625();
        events.Add(Logon4624(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(1), "administrator", "203.0.113.9", 0));
        s.Day(1, d => d.Events(Win, events));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void I3_低速密碼噴灑連續三天()
    {
        var s = NewScenario("I3", "同一來源每天對 30 個帳號各失敗 1 次，連續 3 天");
        for (var day = 1; day <= 3; day++)
        {
            var n = day;
            s.Day(day, d => d.Events(Win, Spread(30, TimeSpan.FromHours(1), TimeSpan.FromHours(20),
                k => Logon4625(TimeSpan.Zero, $"user{k + 1:00}", "198.51.100.77", k + n * 100))));
        }
        ExpectRisk(s, 1, RiskLevels.Medium);
        ExpectRisk(s, 3, RiskLevels.High);
        // 已知差異 E4-I3：實際 高、預期 中：PasswordSprayDetector 只看單日同來源 10 個以上帳號、每帳號 3 次內，命中即 High＋重大，第 1 天就高，沒有「首日中、持續多日升高」的層次
        Known(s, "I3", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void I4_內部工作站偶發輸入錯誤密碼()
    {
        var s = NewScenario("I4", "一天 12 次 4625 分散全天，同一帳號、內部工作站");
        s.Day(1, d => d.Events(Win, Spread(12, TimeSpan.FromHours(1), TimeSpan.FromHours(20),
            k => Logon4625(TimeSpan.Zero, "wang.xm", "10.20.30.41", k, "PC-WANG"))));
        ExpectRisk(s, 1, RiskLevels.Low);
        // 已知差異 E4-I4：實際 中、預期 低：builtin-security-bruteforce-4625 只看單日 4625 總數（門檻 10），12 筆分散全天的內部工作站打錯密碼也 High，來源是否內部與是否集中時段不參與判定
        Known(s, "I4", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void I5_稽核紀錄被清除()
    {
        var s = NewScenario("I5", "稽核紀錄被清除 1102 一筆（真實來源 Microsoft-Windows-Eventlog）");
        s.Day(1, d => d.Events(Win, new[] { AuditCleared1102(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), "admin1") }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);

        // 命中重大規則且只計一次（兩條同義 1102 規則不得對同一筆各計一次）
        var issues = ScenarioDriver.Run(s).Day(1).Issues.Where(i => i.Host == Win && i.Label.Contains("1102", StringComparison.Ordinal)).ToList();
        var issue = Assert.Single(issues);
        Assert.True(issue.ElevatesDayRisk);
        Assert.Equal(1, issue.Count);
    }

    /// <summary>真實來源的 1102 加同日帳號建立：滅跡關聯成立（錨點不看來源名，但要先被規則判成安全事件）</summary>
    [Fact]
    public void I5_真實來源的1102加同日帳號建立_滅跡關聯成立()
    {
        var s = NewScenario("I5c", "1102（Microsoft-Windows-Eventlog）＋同日 4720");
        s.Day(1, d => d.Events(Win, new[]
        {
            AuditCleared1102(TimeSpan.FromHours(2), "admin1"),
            AccountCreated4720(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5), "admin1", "svc-x"),
        }));
        ExpectRisk(s, 1, RiskLevels.High);
        var result = ScenarioDriver.Run(s);
        result.AssertExpectations();
        Assert.Contains(result.Day(1).Alerts, a => a.Host == Win && a.Kind == "關聯" && a.Text.Contains("【滅跡】", StringComparison.Ordinal) && !a.Suppressed);
    }

    /// <summary>對照：把 1102 的來源改成規則假定的 Security-Auditing，同一規則就命中並判高（證明差異出在來源名不一致）。</summary>
    [Fact]
    public void I5_對照_以規則假定的來源名稱則命中判高()
    {
        var s = NewScenario("I5b", "稽核紀錄被清除 1102（來源名改為 Microsoft-Windows-Security-Auditing，非真實格式）");
        s.Day(1, d => d.Events(Win, new[] { AuditCleared1102(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), "admin1", "Microsoft-Windows-Security-Auditing") }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void I6_上班時間管理者帳號異動加新服務()
    {
        var s = NewScenario("I6", "上班時間同一管理者：4720、4732 一般群組、7045");
        s.Day(1, d => d.Events(Win, new[]
        {
            AccountCreated4720(TimeSpan.FromHours(10), "admin1", "svc-report"),
            GroupMemberAdded4732(TimeSpan.FromHours(10) + TimeSpan.FromMinutes(2), "admin1", "svc-report", "Remote Desktop Users", "S-1-5-32-555"),
            ServiceInstalled7045(TimeSpan.FromHours(10) + TimeSpan.FromMinutes(20))
        }));
        ExpectRisk(s, 1, RiskLevels.Medium);
        // 已知差異 E4-I6：實際 高、預期 中：CorrelationAnalyzer 的【持久化】模式只要帳號異動（4720／4732）與 7045 同日就命中並判高，不看時段與群組是否高權限
        Known(s, "I6", 1);
        RunAndAssert(s);
    }

    [Fact]
    public void I7_半夜建立管理員帳號並安裝服務()
    {
        var s = NewScenario("I7", "半夜：4720、4732 加入 Administrators、7045");
        s.Day(1, d => d.Events(Win, new[]
        {
            AccountCreated4720(TimeSpan.FromHours(3), "admin1", "svc-report"),
            GroupMemberAdded4732(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1), "admin1", "svc-report", "Administrators", "S-1-5-32-544"),
            ServiceInstalled7045(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(5))
        }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    [Fact]
    public void I8_Defender偵測並成功處置()
    {
        var s = NewScenario("I8", "Defender 1116＋1117");
        s.Day(1, d => d.Events(Win, new[] { Defender1116(TimeSpan.FromHours(11)), Defender1117(TimeSpan.FromHours(11) + TimeSpan.FromSeconds(20)) }));
        ExpectRisk(s, 1, RiskLevels.Medium);
        RunAndAssert(s);
    }

    [Fact]
    public void I9_Defender處置失敗()
    {
        var s = NewScenario("I9", "Defender 1116＋1118");
        s.Day(1, d => d.Events(Win, new[] { Defender1116(TimeSpan.FromHours(11)), Defender1118(TimeSpan.FromHours(11) + TimeSpan.FromSeconds(20)) }));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    private static List<ScenarioEvent> LinuxBrute() =>
        Spread(300, TimeSpan.FromHours(10), TimeSpan.FromHours(1), k => LinuxSshFailed(TimeSpan.Zero, "203.0.113.9", k));

    [Fact]
    public void I10_Linux單一來源一小時三百次SSH失敗()
    {
        var s = NewScenario("I10", "Linux 單一來源 1 小時內 300 次 sshd Failed password，沒有成功", linux: true);
        s.Day(1, d => d.Events(Lnx, LinuxBrute()));
        ExpectRisk(s, 1, RiskLevels.Medium);
        RunAndAssert(s);
    }

    [Fact]
    public void I11_Linux暴力破解後成功登入()
    {
        var s = NewScenario("I11", "300 次 sshd Failed password 後同來源 Accepted password", linux: true);
        var events = LinuxBrute();
        events.Add(LinuxSshAccepted(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(1), "root", "203.0.113.9"));
        s.Day(1, d => d.Events(Lnx, events));
        ExpectRisk(s, 1, RiskLevels.High);
        RunAndAssert(s);
    }

    // ══ 雜訊 ═══════════════════════════════════════════════════════════════

    [Fact]
    public void N1_Windows正常營運日()
    {
        var s = NewScenario("N1", "Windows 正常營運日");
        s.Day(1, d =>
        {
            d.Events(Win, Spread(15, TimeSpan.FromHours(1), TimeSpan.FromHours(20), k => ServiceState7036(TimeSpan.Zero, k)));
            d.Events(Win, Spread(4, TimeSpan.FromHours(2), TimeSpan.FromHours(18), k => DistributedCom10016(TimeSpan.Zero, k)));
            d.Events(Win, Spread(3, TimeSpan.FromHours(8), TimeSpan.FromHours(8), k => DnsTimeout1014(TimeSpan.Zero, k)));
            d.Events(Win, Spread(20, TimeSpan.FromHours(0), TimeSpan.FromHours(23),
                k => Logon4624(TimeSpan.Zero, "svc-backup", "10.20.30.5", k, 3, "FILESRV01")));
            d.Events(Win, new[] { WindowsUpdateInstalled19(TimeSpan.FromHours(3)) });
        });
        ExpectRisk(s, 1, RiskLevels.Low);
        RunAndAssert(s);
    }

    [Fact]
    public void N2_中度規則連續五天每天出現()
    {
        var s = NewScenario("N2", "時間同步警告（Time-Service 36，Medium 規則）連續 5 天每天 1 筆");
        for (var day = 1; day <= 5; day++) s.Day(day, d => d.Events(Win, new[] { TimeService36(TimeSpan.FromHours(6)) }));
        for (var day = 3; day <= 5; day++) ExpectRisk(s, day, RiskLevels.Medium);
        // 已知差異 E4-N2：實際 低、預期 中：builtin-config-time-sync-failure 是 Medium 但無重大旗標，風險判定只認 High 或重大，持續 Recurring 的 Medium 問題不會升級
        Known(s, "N2", 3, 4, 5);
        RunAndAssert(s);
    }

    [Fact]
    public void N3_高規則平常每天一筆某天五筆()
    {
        var s = NewScenario("N3", "備份失敗（Backup 517，High 規則）平常每天 1 筆、第 15 天 5 筆");
        for (var day = 1; day <= 14; day++) s.Day(day, d => d.Events(Win, new[] { BackupFailed517(TimeSpan.FromHours(2)) }));
        s.Day(15, d => d.Events(Win, Spread(5, TimeSpan.FromHours(1), TimeSpan.FromHours(4), k => BackupFailed517(TimeSpan.Zero, k))));
        ExpectRisk(s, 15, RiskLevels.Medium);
        // 已知差異 E4-N3：實際 高、預期 中：趨勢層把 517 由 1 筆升到 5 筆判為 Rising，趨勢升高讓問題變重大並判高；小幅波動沒有最低量級門檻
        Known(s, "N3", 15);
        RunAndAssert(s, 1, 14, 15);
    }

    [Fact]
    public void N4_Linux正常營運日()
    {
        var s = NewScenario("N4", "Linux 正常營運日", linux: true);
        s.Day(1, d =>
        {
            d.Events(Lnx, Spread(3, TimeSpan.FromHours(8), TimeSpan.FromHours(9), k => LinuxSshAccepted(TimeSpan.Zero, "ops", "10.20.30.7", k, "publickey")));
            d.Events(Lnx, Spread(30, TimeSpan.FromHours(0), TimeSpan.FromHours(23),
                k => Linux(TimeSpan.Zero, "CRON", $"(root) CMD (/usr/lib/sa/sa1 1 1 #{k})")));
            d.Events(Lnx, Spread(10, TimeSpan.FromHours(1), TimeSpan.FromHours(20),
                k => Linux(TimeSpan.Zero, "systemd", $"Started Session {100 + k} of user root.")));
            d.Events(Lnx, Spread(5, TimeSpan.FromHours(9), TimeSpan.FromHours(8),
                k => Linux(TimeSpan.Zero, "sudo", "ops : TTY=pts/0 ; PWD=/home/ops ; USER=root ; COMMAND=/usr/bin/systemctl status nginx")));
        });
        ExpectRisk(s, 1, RiskLevels.Low);
        RunAndAssert(s);
    }
}

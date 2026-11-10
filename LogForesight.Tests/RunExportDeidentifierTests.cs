using System.Reflection;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 排程作業診斷匯出的去識別化遮罩器：B-1 取代順序、B-2 字典邊界、B-3 樣式、B-4 代號、B-5 堆疊、B-6 結果掃描。
/// 全部以記憶體來源資料建立遮罩器，不需要資料庫。
/// </summary>
public class RunExportDeidentifierTests
{
    // ===== 建構輔助 =====

    private static DeidentifierHost H(long id, string name, string? display = null, string? ip = null,
        string? netiq = null, string? orphan = null, string? role = null)
        => new(id, name, display, ip, netiq, orphan, role);

    private static RunExportDeidentifierSource Src(
        DeidentifierHost[]? hosts = null,
        string?[]? groups = null,
        DeidentifierUser[]? users = null,
        DeidentifierSentinel[]? sentinels = null,
        DeidentifierPrtgDevice[]? devices = null,
        DeidentifierSettings? settings = null,
        string[]? secrets = null)
        => new()
        {
            Hosts = hosts ?? Array.Empty<DeidentifierHost>(),
            HostGroupNames = groups ?? Array.Empty<string?>(),
            Users = users ?? Array.Empty<DeidentifierUser>(),
            Sentinels = sentinels ?? Array.Empty<DeidentifierSentinel>(),
            PrtgDevices = devices ?? Array.Empty<DeidentifierPrtgDevice>(),
            Settings = settings ?? new DeidentifierSettings(),
            Secrets = secrets ?? Array.Empty<string>()
        };

    private static RunExportDeidentifier Make(RunExportDeidentifierSource? src = null)
        => RunExportDeidentifier.Create(src ?? Src());

    private static RunExportDeidentifier MakeWithHosts(params DeidentifierHost[] hosts) => Make(Src(hosts: hosts));

    // ===== 63-2 批次F：短詞、登錄機碼、DC 標籤、Basic 與表單編碼 =====

    [Fact]
    public void F1_群組與角色的短詞不進字典_長名稱照遮()
    {
        var m = Make(Src(groups: new[] { "成功", "成功廠區", "IT" }, hosts: new[] { H(1, "WEB01", role: "DB"), H(2, "WEB02", role: "DBA 組") }));

        Assert.Equal("診斷成功，IT 部門", m.Mask("診斷成功，IT 部門"));
        var generic = Make(Src(groups: new[] { "Core", "Linux" }, hosts: new[] { H(1, "WEB01", role: "Web") }));
        const string frame = "at LogForesight.Core.Service.Run() in builtin-linux-ssh-accept Web";
        Assert.Equal(frame, generic.Mask(frame));
        // 只放過描述性名稱：主機真的叫 Core 時仍要遮
        Assert.Equal("host-001", Make(Src(hosts: new[] { H(1, "Core") })).Mask("Core"));
        Assert.Equal("group-001", m.Mask("成功廠區"));
        Assert.Equal("DB 連線", m.Mask("DB 連線"));
        Assert.Equal("role-001", m.Mask("DBA 組"));
    }

    [Fact]
    public void F1_兩字的主機名與使用者顯示名稱仍要遮()
    {
        var m = Make(Src(hosts: new[] { H(1, "資料") }, users: new[] { new DeidentifierUser(1, "wang", "王明", null) }));

        Assert.Equal("host-001", m.Mask("資料"));
        Assert.Equal("user-001 登入", m.Mask("王明 登入"));
    }

    [Theory]
    [InlineData("HKLM\\Software\\Microsoft\\EventSystem")]
    [InlineData("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet")]
    [InlineData("HKLM\\Software")]
    [InlineData("hkcu\\Environment")]
    [InlineData("SOFTWARE\\Microsoft\\Windows")]
    public void F2_登錄機碼路徑原樣(string path)
    {
        Assert.Equal(path, Make().Mask(path));
    }

    [Fact]
    public void F2_一般的網域帳號仍要遮()
    {
        Assert.Equal("domain-001\\user-001 登入", Make().Mask("CORP\\alice 登入"));
    }

    [Fact]
    public void F3_AD搜尋基準的網域名進字典_頂級網域不單獨收()
    {
        var settings = DeidentifierSettings.From(new SystemSettings { AdSearchBase = "OU=Users,DC=brk,DC=local" });
        var m = Make(Src(settings: settings));

        Assert.DoesNotContain("BRK", m.Mask("無法在網域 BRK 上設定工作階段"));
        Assert.DoesNotContain("brk.local", m.Mask("連線 brk.local 失敗"));
        Assert.Equal("local 模式", m.Mask("local 模式"));
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: Basic secret")]
    [InlineData("Basic YWRtaW46MTIzNA==", "Basic secret")]
    [InlineData("Basic configuration loaded", "Basic configuration loaded")]
    [InlineData("Basic Authentication failed", "Basic Authentication failed")]
    public void F5_Basic憑證判定(string text, string expected)
    {
        Assert.Equal(expected, Make().Mask(text));
    }

    [Fact]
    public void F5_密鑰的表單編碼形式也要遮()
    {
        var m = Make(Src(secrets: new[] { "p@ss w0rd" }));

        Assert.DoesNotContain("w0rd", m.Mask("body=p%40ss+w0rd&x=1"));
        Assert.DoesNotContain("w0rd", m.Mask("body=p%40ss%20w0rd&x=1"));
    }

    // ===== 體檢輪：邊界、IP 前綴、代號字彙密鑰、國碼網域 =====

    [Theory]
    [InlineData("D:\\logs\\WEBSRV01_20261103.log")]
    [InlineData("job WEBSRV01-nightly aborted")]
    [InlineData("connect WEBSRV01.contoso.eu refused")]
    [InlineData("backup_10.1.2.3.tmp")]
    [InlineData("x_10.1.2.3 與 host.10.1.2.3 與 ver10.1.2.3")]
    public void 體檢_字典詞與IP貼著底線連字號點號仍要遮_且掃描抓得到(string text)
    {
        var m = MakeWithHosts(H(1, "WEBSRV01", ip: "10.1.2.3"));

        var masked = m.Mask(text);

        Assert.DoesNotContain("WEBSRV01", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.1.2.3", masked);
        // 掃描與遮罩同一套邊界：遮罩前的原文一定被掃出殘留，遮罩後乾淨
        Assert.NotEmpty(m.ScanResidual(System.Text.Json.JsonSerializer.Serialize(new { text })));
        Assert.Empty(m.ScanResidual(System.Text.Json.JsonSerializer.Serialize(new { text = masked })));
    }

    [Fact]
    public void 體檢_IPv6貼著冒號前綴也要遮()
    {
        var m = Make();
        Assert.Equal("addr:ip-001 down", m.Mask("addr:fe80::1 down"));
        Assert.Equal("RemoteAddress:ip-002 x", m.Mask("RemoteAddress:2001:db8::5 x"));
        Assert.Equal("於 12:34:56 開始", m.Mask("於 12:34:56 開始"));
    }

    [Fact]
    public void 體檢_與代號字彙重疊的密鑰值略過比對_匯出不會永遠失敗()
    {
        var m = Make(Src(hosts: new[] { H(1, "WEBSRV01") }, secrets: new[] { "secret", "host", "user-0", "Pa55-word" }));

        Assert.Equal("host-001 failed secret", m.Mask("WEBSRV01 failed Pa55-word"));
        Assert.Empty(m.ScanResidual("{\"a\":\"host-001 failed secret user-001\"}"));
        Assert.Equal(new Dictionary<string, int> { ["secret"] = 1 }, m.ScanResidual("{\"a\":\"Pa55-word\"}"));
    }

    [Theory]
    [InlineData("svc.contoso.eu down", "domain-001 down")]
    [InlineData("db01.contoso.co failed", "domain-001 failed")]
    [InlineData("mail.example.cloud", "domain-001")]
    [InlineData("System.Net.Http.HttpClient", "System.Net.Http.HttpClient")]
    [InlineData("LogForesight.Core.Service.X", "LogForesight.Core.Service.X")]
    public void 體檢_國碼與常見頂級網域都當FQDN_程式識別字不誤判(string text, string expected)
    {
        Assert.Equal(expected, Make().Mask(text));
    }

    // ===== 反例表（文件 §批次B「容易誤判的反例」逐條）=====

    [Fact]
    public void 反例_命名空間與類別名稱不被當成FQDN()
    {
        var m = Make();
        const string text = "System.Net.Http.HttpClient 逾時；LogForesight.Core.Service.PrtgFetchService 失敗；NLog.Logger.Info；ASP.NET Core";
        Assert.Equal(text, m.Mask(text));
    }

    [Fact]
    public void 反例_真正的FQDN會被遮()
    {
        var m = Make();
        var masked = m.Mask("連線 srv99.corp.local 與 MAIL.EXAMPLE.COM 失敗");
        Assert.DoesNotContain("corp.local", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("domain-001", masked);
        Assert.Contains("domain-002", masked);
    }

    [Fact]
    public void 反例_帳號dev_不切壞device與dev_server()
    {
        var m = Make(Src(users: new[] { new DeidentifierUser(1, "dev", null, null) }));
        var masked = m.Mask("dev 登入；device 正常；dev-server 重啟；結尾是 dev.");
        // 體檢輪推翻規劃的「dev-server 不被切開」：連字號與底線都是邊界，寧可多遮——
        // 否則 WEBSRV01_20261103.log、WEBSRV01-nightly 裡的主機名會原樣流出，結果掃描也看不到
        Assert.Equal("user-001 登入；device 正常；user-001-server 重啟；結尾是 user-001.", masked);
    }

    [Fact]
    public void 反例_SRV01與SRV010各自對應不互相截斷()
    {
        var m = MakeWithHosts(H(1, "SRV01"), H(2, "SRV010"));
        Assert.Equal("host-001 host-002 host-001 host-002", m.Mask("SRV01 SRV010 srv01 Srv010"));
    }

    [Fact]
    public void 反例_保留詞不遮_其餘常見詞寧可多遮()
    {
        var m = MakeWithHosts(H(1, "AI"), H(2, "PRTG"), H(3, "NetIQ"), H(4, "Sentinel"), H(5, "web"));
        Assert.Equal("AI PRTG NetIQ Sentinel 皆保留，host-001 會被遮", m.Mask("AI PRTG NetIQ Sentinel 皆保留，web 會被遮"));
    }

    [Fact]
    public void 反例_四段版本號當IP遮_三段日期不動()
    {
        var m = Make();
        Assert.Equal("版本 ip-001 日期 2026.11.03", m.Mask("版本 8.0.12.1 日期 2026.11.03"));
    }

    [Fact]
    public void 反例_中文緊鄰主機名視為邊界()
    {
        var m = MakeWithHosts(H(1, "SRV01"));
        Assert.Equal("主機host-001失敗", m.Mask("主機SRV01失敗"));
    }

    [Fact]
    public void 反例_字典為空_樣式比對照常生效()
    {
        var m = Make();
        var masked = m.Mask("來自 10.1.2.3 的信件 bob@example.org 連到 https://x.example.org/a 以 CORP\\alice 身分");
        Assert.DoesNotContain("10.1.2.3", masked);
        Assert.DoesNotContain("bob@example.org", masked);
        Assert.DoesNotContain("x.example.org", masked);
        Assert.DoesNotContain("alice", masked);
        Assert.Contains("ip-001", masked);
        Assert.Contains("email-001", masked);
        Assert.Contains("url-001", masked);
        Assert.Contains("domain-001\\user-001", masked);
    }

    [Fact]
    public void 反例_保留清單不算殘留_不得讓匯出失敗()
    {
        var m = MakeWithHosts(H(1, "AI"), H(2, "PRTG"));
        Assert.Empty(m.ScanResidual("{\"note\":\"AI 與 PRTG 與 NetIQ 與 Sentinel\"}"));
    }

    [Fact]
    public void 字典空值與僅空白不進字典()
    {
        var m = Make(Src(
            hosts: new[] { H(1, "  ", display: "", ip: null), H(2, "") },
            groups: new string?[] { null, "   " },
            users: new[] { new DeidentifierUser(1, " ", "", null) }));
        const string text = "一般文字 with spaces  and  tabs";
        Assert.Equal(text, m.Mask(text));
        Assert.Empty(m.ScanResidual("{\"a\":\"  \"}"));
    }

    // ===== 事件常見的身分格式 =====

    [Fact]
    public void Windows事件_AccountName與SID被遮_欄位名保留_帳號不在WebUser也遮得掉()
    {
        var m = Make();
        const string msg =
            "An account failed to log on.\r\nSubject:\r\n\tSecurity ID:\t\tS-1-5-18\r\n\tAccount Name:\t\tSRV99$\r\n\tAccount Domain:\t\tCORP\r\n" +
            "Account For Which Logon Failed:\r\n\tSecurity ID:\t\tS-1-5-21-3623811015-3361044348-30300820-1013\r\n\tAccount Name:\t\talice\r\n\tAccount Domain:\t\tCORP\r\n" +
            "Network Information:\r\n\tWorkstation Name:\tWKS-77\r\n\tSource Network Address:\t10.9.8.7";
        var masked = m.Mask(msg);

        foreach (var leaked in new[] { "alice", "SRV99", "3623811015", "30300820", "WKS-77", "10.9.8.7", "CORP" })
            Assert.DoesNotContain(leaked, masked, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Account Name:\t\tuser-001", masked);
        Assert.Contains("Account Name:\t\tuser-002", masked);
        Assert.Contains("Account Domain:\t\tdomain-001", masked);
        Assert.Contains("S-1-5-18", masked); // 內建 SID 不是識別資訊
        Assert.Contains("sid-001", masked);
        Assert.Contains("Workstation Name:\thost-001", masked);
        Assert.Contains("Source Network Address:\tip-001", masked);
        // 同一個帳號同一個代號
        Assert.Equal(2, masked.Split("domain-001").Length - 1);
    }

    [Fact]
    public void Windows事件_內建主體與空值保留()
    {
        var m = Make();
        Assert.Equal("Account Name:\t\t-\r\n\tAccount Name:\t\tSYSTEM", m.Mask("Account Name:\t\t-\r\n\tAccount Name:\t\tSYSTEM"));
    }

    [Fact]
    public void Windows事件_中文欄位名()
    {
        var m = Make();
        var masked = m.Mask("帳戶名稱:\t\tcarol\r\n帳戶網域:\t\tCORP2");
        Assert.DoesNotContain("carol", masked);
        Assert.DoesNotContain("CORP2", masked);
        Assert.Contains("帳戶名稱:\t\tuser-001", masked);
        Assert.Contains("帳戶網域:\t\tdomain-001", masked);
    }

    [Fact]
    public void Linux_invalid_user_bob_from_IP()
    {
        var m = Make();
        Assert.Equal(
            "Failed password for invalid user user-001 from ip-001 port 22 ssh2",
            m.Mask("Failed password for invalid user bob from 1.2.3.4 port 22 ssh2"));
        Assert.Equal("Invalid user user-001 from ip-001", m.Mask("Invalid user bob from 1.2.3.4"));
        Assert.Equal("Accepted publickey for user-002 from ip-002", m.Mask("Accepted publickey for root from 5.6.7.8"));
        Assert.Equal("authentication failure; rhost=ip-002  user=user-001", m.Mask("authentication failure; rhost=5.6.7.8  user=bob"));
        Assert.Equal("session opened for user user-002(uid=0)", m.Mask("session opened for user root(uid=0)"));
    }

    [Fact]
    public void 使用者家目錄_與UNC_與DomainUser()
    {
        var m = Make();
        var masked = m.Mask(@"讀取 C:\Users\dave\AppData\x.log 與 /home/erin/.ssh 與 \\fs01\share$\a.txt 以 ACME\frank 身分");
        foreach (var leaked in new[] { "dave", "erin", "fs01", "ACME", "frank" })
            Assert.DoesNotContain(leaked, masked);
        Assert.Contains(@"C:\Users\user-002\AppData\x.log", masked);
        Assert.Contains("/home/user-003/.ssh", masked);
        Assert.Contains(@"\\host-001\share$\a.txt", masked);
        Assert.Contains(@"domain-001\user-001", masked);
        // 一般路徑與公用設定檔不被誤判成 DOMAIN\user
        Assert.Equal(@"C:\Windows\System32\drivers 與 C:\Users\Public\x", m.Mask(@"C:\Windows\System32\drivers 與 C:\Users\Public\x"));
    }

    // ===== IPv6：時間不是位址 =====

    [Fact]
    public void IPv6_時間與MAC與冒號識別字不誤判()
    {
        var m = Make();
        const string text = "時間 12:34:56 與 2026-11-03T12:34:56.789 與 12:34 與 MAC 00:1A:2B:3C:4D:5E 與 std::vector 與 port:8080";
        Assert.Equal(text, m.Mask(text));
    }

    [Fact]
    public void IP緊鄰中文也要遮()
    {
        var m = Make();
        Assert.Equal("主機ip-001失敗", m.Mask("主機10.1.2.3失敗"));
        Assert.Equal("與ip-002連線", m.Mask("與fe80::2連線"));
    }

    [Fact]
    public void IPv6_含雙冒號或完整八段才算_且不同寫法同代號()
    {
        var m = Make();
        var masked = m.Mask("來源 fe80::1 與 2001:db8:0:0:0:0:0:1 與 2001:DB8::1 與 [::1]:443 與 fe80:0:0:0:0:0:0:1。");
        Assert.DoesNotContain("fe80", masked);
        Assert.DoesNotContain("2001", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("來源 ip-001 與 ip-002 與 ip-002 與 [ip-003]:443 與 ip-001。", masked);
    }

    // ===== 每個來源欄位 =====

    [Theory]
    [InlineData("Host.HostName")]
    [InlineData("Host.DisplayName")]
    [InlineData("Host.IpAddress")]
    [InlineData("Host.NetiqServer")]
    [InlineData("Host.OrphanedFromSentinel")]
    [InlineData("Host.RoleDesc")]
    [InlineData("HostGroup.GroupName")]
    [InlineData("User.Account")]
    [InlineData("User.Account.Alias")]
    [InlineData("User.Account.Domain")]
    [InlineData("User.DisplayName")]
    [InlineData("User.Email")]
    [InlineData("Sentinel.Name")]
    [InlineData("Sentinel.BaseUrl")]
    [InlineData("Sentinel.BaseUrl.Host")]
    [InlineData("Sentinel.Username")]
    [InlineData("Prtg.Name")]
    [InlineData("Prtg.Ip")]
    [InlineData("Prtg.GroupPath.Segment1")]
    [InlineData("Prtg.GroupPath.Segment2")]
    [InlineData("Settings.BrandName")]
    [InlineData("Settings.ServerDescription")]
    [InlineData("Settings.PrtgUrl")]
    [InlineData("Settings.PrtgUrl.Host")]
    [InlineData("Settings.AiBaseUrl")]
    [InlineData("Settings.SmtpServer")]
    [InlineData("Settings.SmtpAccount")]
    [InlineData("Settings.MailFrom")]
    [InlineData("Settings.MailRecipients")]
    [InlineData("Settings.AdServers")]
    [InlineData("Settings.AdSearchBase")]
    [InlineData("Settings.PrtgUsername")]
    public void 每種來源欄位的原值都被遮掉(string field)
    {
        var (src, original, kind) = SourceFieldCase(field);
        var m = Make(src);

        var masked = m.Mask($"前 {original} 後");

        Assert.DoesNotContain(original, masked, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("前 ", masked, StringComparison.Ordinal);
        Assert.EndsWith(" 後", masked, StringComparison.Ordinal);
        Assert.Contains($"{kind}-001", masked);
    }

    private static (RunExportDeidentifierSource Src, string Original, string Kind) SourceFieldCase(string field)
    {
        static DeidentifierSettings Cfg(Action<SystemSettings> set)
        {
            var s = new SystemSettings();
            set(s);
            return DeidentifierSettings.From(s);
        }

        return field switch
        {
            "Host.HostName" => (Src(hosts: new[] { H(1, "SRV-ALPHA") }), "SRV-ALPHA", "host"),
            "Host.DisplayName" => (Src(hosts: new[] { H(1, "SRV-A", display: "Alpha Display") }), "Alpha Display", "host"),
            "Host.IpAddress" => (Src(hosts: new[] { H(1, "SRV-A", ip: "10.20.30.40") }), "10.20.30.40", "host"),
            "Host.NetiqServer" => (Src(hosts: new[] { H(1, "SRV-A", netiq: "SENT-ONE") }), "SENT-ONE", "sentinel"),
            "Host.OrphanedFromSentinel" => (Src(hosts: new[] { H(1, "SRV-A", orphan: "OLD-SENT") }), "OLD-SENT", "sentinel"),
            "Host.RoleDesc" => (Src(hosts: new[] { H(1, "SRV-A", role: "Finance DB role") }), "Finance DB role", "role"),
            "HostGroup.GroupName" => (Src(groups: new string?[] { "Finance Group" }), "Finance Group", "group"),
            "User.Account" => (Src(users: new[] { new DeidentifierUser(1, "alice", null, null) }), "alice", "user"),
            "User.Account.Alias" => (Src(users: new[] { new DeidentifierUser(1, @"CORP\bob", null, null) }), "bob", "user"),
            "User.Account.Domain" => (Src(users: new[] { new DeidentifierUser(1, @"CORP\bob", null, null) }), "CORP", "domain"),
            "User.DisplayName" => (Src(users: new[] { new DeidentifierUser(1, "u1", "Alice Chen", null) }), "Alice Chen", "user"),
            "User.Email" => (Src(users: new[] { new DeidentifierUser(1, "u1", null, "alice.chen@example.org") }), "alice.chen@example.org", "user"),
            "Sentinel.Name" => (Src(sentinels: new[] { new DeidentifierSentinel(1, "SENT-TWO", null, null) }), "SENT-TWO", "sentinel"),
            "Sentinel.BaseUrl" => (Src(sentinels: new[] { new DeidentifierSentinel(1, "S", "https://sent2.example.org:8443/", null) }), "https://sent2.example.org:8443/", "url"),
            "Sentinel.BaseUrl.Host" => (Src(sentinels: new[] { new DeidentifierSentinel(1, "S", "https://sent2.example.org:8443/", null) }), "sent2.example.org", "sentinel"),
            "Sentinel.Username" => (Src(sentinels: new[] { new DeidentifierSentinel(1, "S", null, "svc-sentinel") }), "svc-sentinel", "user"),
            "Prtg.Name" => (Src(devices: new[] { new DeidentifierPrtgDevice(9, "PRTG-DEV-9", null, null) }), "PRTG-DEV-9", "device"),
            "Prtg.Ip" => (Src(devices: new[] { new DeidentifierPrtgDevice(9, "dev9", "10.9.9.9", null) }), "10.9.9.9", "device"),
            "Prtg.GroupPath.Segment1" => (Src(devices: new[] { new DeidentifierPrtgDevice(9, "dev9", null, "Root/Datacenter-X/Rack-7") }), "Datacenter-X", "group"),
            "Prtg.GroupPath.Segment2" => (Src(devices: new[] { new DeidentifierPrtgDevice(9, "dev9", null, "Root/Datacenter-X/Rack-7") }), "Rack-7", "group"),
            "Settings.BrandName" => (Src(settings: Cfg(s => s.BrandName = "Acme Corp")), "Acme Corp", "domain"),
            "Settings.ServerDescription" => (Src(settings: Cfg(s => s.ServerDescription = "Acme primary server")), "Acme primary server", "domain"),
            "Settings.PrtgUrl" => (Src(settings: Cfg(s => s.PrtgUrl = "https://prtg.acme.example/")), "https://prtg.acme.example/", "url"),
            "Settings.PrtgUrl.Host" => (Src(settings: Cfg(s => s.PrtgUrl = "https://prtg.acme.example/")), "prtg.acme.example", "domain"),
            "Settings.AiBaseUrl" => (Src(settings: Cfg(s => s.AiBaseUrl = "http://ai.acme.example:8080")), "http://ai.acme.example:8080", "url"),
            "Settings.SmtpServer" => (Src(settings: Cfg(s => s.SmtpServer = "smtp.acme.example")), "smtp.acme.example", "domain"),
            "Settings.SmtpAccount" => (Src(settings: Cfg(s => s.SmtpAccount = "mailer")), "mailer", "user"),
            "Settings.MailFrom" => (Src(settings: Cfg(s => s.MailFrom = "noreply@acme.example")), "noreply@acme.example", "email"),
            "Settings.MailRecipients" => (Src(settings: Cfg(s => s.MailRecipients = new List<string> { "ops@acme.example" })), "ops@acme.example", "email"),
            "Settings.AdServers" => (Src(settings: Cfg(s => s.AdServers = new List<string> { "dc01.acme.example" })), "dc01.acme.example", "domain"),
            "Settings.AdSearchBase" => (Src(settings: Cfg(s => s.AdSearchBase = "DC=acme,DC=example")), "DC=acme,DC=example", "domain"),
            "Settings.PrtgUsername" => (Src(settings: Cfg(s => s.PrtgUsername = "prtgadmin")), "prtgadmin", "user"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    [Fact]
    public void PRTG群組路徑的Root不是識別資訊()
    {
        var m = Make(Src(devices: new[] { new DeidentifierPrtgDevice(9, "dev9", null, "Root/Datacenter-X") }));
        Assert.Equal("Root/group-001", m.Mask("Root/Datacenter-X"));
    }

    [Fact]
    public void 預設的產品名稱與副標題不進字典_改過的才算()
    {
        var plain = Make(Src(settings: DeidentifierSettings.From(new SystemSettings())));
        Assert.Equal("LogForesight 事件日誌預警 啟動", plain.Mask("LogForesight 事件日誌預警 啟動"));

        var custom = Make(Src(settings: DeidentifierSettings.From(new SystemSettings { BrandName = "Acme 預警" })));
        Assert.Equal("domain-001 啟動", custom.Mask("Acme 預警 啟動"));
    }

    [Fact]
    public void 迴圈位址的AI服務不把localhost遮光()
    {
        var m = Make(Src(settings: DeidentifierSettings.From(new SystemSettings { AiBaseUrl = "http://localhost:8080" })));
        Assert.Equal("localhost 沒有回應", m.Mask("localhost 沒有回應"));
    }

    [Fact]
    public void FQDN主機名的短名稱也進字典()
    {
        var m = MakeWithHosts(H(1, "srv01.corp.local"));
        Assert.Equal("host-001 與 host-001 與 host-001:443", m.Mask("srv01 與 SRV01.CORP.LOCAL 與 srv01:443"));
        // 未登錄的網域後綴若接在已知短名稱後面，整串對到同一台主機
        Assert.Equal("host-001", m.Mask("srv01.example.com"));
    }

    // ===== 代號穩定性 =====

    [Fact]
    public void 同值同代號_不同值不同代號_依首次出現配發()
    {
        var m = MakeWithHosts(H(1, "SRV01"), H(2, "SRV02"));
        // 先出現的 SRV02 配到 host-001（依首次出現配發，不是依字典順序）
        Assert.Equal("host-001 host-002 host-001 host-002", m.Mask("SRV02 SRV01 srv02 Srv01"));
        Assert.Equal("host-002", m.Mask("srv01"));
    }

    [Fact]
    public void 不同實例的代號互不相干_且不是雜湊()
    {
        var a = MakeWithHosts(H(1, "SRV01"), H(2, "SRV02"));
        var b = MakeWithHosts(H(1, "SRV01"), H(2, "SRV02"));
        Assert.Equal("host-001", a.Mask("SRV01"));
        Assert.Equal("host-001", b.Mask("SRV02"));
    }

    [Fact]
    public void 各類代號計數()
    {
        var m = MakeWithHosts(H(1, "SRV01"), H(2, "SRV02"));
        m.Mask("SRV01 SRV02 srv01 來自 9.9.9.9 與 8.8.8.8 mail a@b.org");
        var counts = m.TokenCounts;
        Assert.Equal(2, counts["host"]);
        Assert.Equal(2, counts["ip"]);
        Assert.Equal(1, counts["email"]);
        Assert.False(counts.ContainsKey("user"));
    }

    [Fact]
    public void 主機IP與NetIQ位址對應到該主機的host代號()
    {
        var m = Make(Src(
            hosts: new[]
            {
                H(1, "SRV01", ip: "10.1.2.3"),
                H(2, "10.5.5.5", display: "NETIQ-HOST-5", netiq: "SENT-A"),
                H(3, "SRV03", ip: "fd00::5")
            },
            sentinels: new[] { new DeidentifierSentinel(1, "SENT-A", "https://sent-a.example.org/", null) }));

        Assert.Equal("host-001 host-001 host-001", m.Mask("SRV01 10.1.2.3 010.001.002.003"));
        Assert.Equal("host-002 host-002", m.Mask("10.5.5.5 NETIQ-HOST-5"));
        Assert.Equal("host-002:8443", m.Mask("10.5.5.5:8443"));
        Assert.Equal("host-003 host-003 host-003", m.Mask("SRV03 fd00::5 fd00:0:0:0:0:0:0:5"));
        Assert.Equal("sentinel-001 sentinel-001", m.Mask("SENT-A sent-a.example.org"));
        // 沒登錄的 IP 另配 ip 代號，同一個 IP 同代號
        Assert.Equal("ip-001 ip-002 ip-001", m.Mask("9.9.9.9 9.9.9.8 9.9.9.9"));
    }

    [Fact]
    public void MaskHost_已知主機名取該主機代號_未知的另配_空值原樣()
    {
        var m = MakeWithHosts(H(1, "SRV01", display: "Alpha", ip: "10.1.2.3"));
        Assert.Equal("host-001", m.MaskHost("SRV01"));
        Assert.Equal("host-001", m.MaskHost(" alpha "));
        Assert.Equal("host-001", m.MaskHost("10.1.2.3"));
        Assert.Equal("host-002", m.MaskHost("GONE-HOST"));
        Assert.Equal("host-002", m.MaskHost("gone-host"));
        Assert.Equal("AI", m.MaskHost("AI"));
        Assert.Null(m.MaskHost(null));
        Assert.Equal("  ", m.MaskHost("  "));
        Assert.Equal("ip-001", m.MaskHost("7.7.7.7"));
    }

    [Fact]
    public void Mask_空值回空字串()
    {
        var m = Make();
        Assert.Equal("", m.Mask(null));
        Assert.Equal("", m.Mask(""));
        Assert.Equal("", m.MaskStack(null));
    }

    // ===== 密鑰（B-1 第一階段）=====

    [Fact]
    public void 密鑰實際值_不在URL內也被遮()
    {
        var m = Make(Src(secrets: new[] { "Sup3r-Secret!Value", "tok_ABC123xyz" }));
        var masked = m.Mask("連線失敗，密碼 Sup3r-Secret!Value；token tok_ABC123xyz 無效；編碼後 " + Uri.EscapeDataString("Sup3r-Secret!Value"));
        Assert.DoesNotContain("Sup3r", masked);
        Assert.DoesNotContain("ABC123", masked);
        Assert.Equal("連線失敗，密碼 secret；token secret 無效；編碼後 secret", masked);
        Assert.Equal(3, m.TokenCounts["secret"]);
    }

    [Fact]
    public void B1順序_密鑰先於字典_含主機名的密碼不會只被切掉一半()
    {
        var m = Make(Src(hosts: new[] { H(1, "SRV01") }, secrets: new[] { "SRV01!Passw0rd" }));
        var masked = m.Mask("登入 SRV01!Passw0rd 失敗，主機 SRV01");
        Assert.Equal("登入 secret 失敗，主機 host-001", masked);
    }

    [Fact]
    public void B1順序_URL先於字典_整串URL一個代號_主機代號依後續首次出現配發()
    {
        var m = MakeWithHosts(H(1, "SRV01"), H(2, "SRV02"));
        var masked = m.Mask("呼叫 https://SRV01/api/x 後 SRV02 回應");
        Assert.Equal("呼叫 url-001 後 host-001 回應", masked);
        Assert.Equal(1, m.TokenCounts["host"]);
    }

    [Fact]
    public void B1順序_字典先於樣式_主機IP是host代號不是ip代號()
    {
        var m = MakeWithHosts(H(1, "SRV01", ip: "10.1.2.3"));
        Assert.Equal("host-001", m.Mask("10.1.2.3"));
        Assert.False(m.TokenCounts.ContainsKey("ip"));
    }

    [Fact]
    public void 字典長字串優先_不被短字串先切壞()
    {
        var m = Make(Src(
            hosts: new[] { H(1, "corp", display: "corp lab") },
            groups: new string?[] { "corp lab east" }));
        // 同一位置有 corp、corp lab、corp lab east 三個候選：取最長者
        Assert.Equal("group-001 與 host-001", m.Mask("corp lab east 與 corp lab"));
    }

    [Fact]
    public void 參數名形式_password_Bearer_URL查詢字串都處理()
    {
        var m = Make();
        var masked = m.Mask("password=hunter22 pwd: \"abc def\" Authorization: Bearer abc.def-ghi_jkl== GET https://h.example.org/p?x=1&passhash=ZZZ999&y=2 db_password=p@ss word 到此");
        Assert.DoesNotContain("hunter22", masked);
        Assert.DoesNotContain("abc def", masked);
        Assert.DoesNotContain("abc.def", masked);
        Assert.DoesNotContain("ZZZ999", masked);
        Assert.DoesNotContain("p@ss", masked);
        Assert.Contains("password=secret", masked);
        Assert.Contains("Bearer secret", masked);
    }

    [Fact]
    public void 一般英文詞組不被當成密鑰()
    {
        var m = Make();
        const string text = "Basic configuration loaded; tokens refreshed; secrets 數量 3";
        Assert.Equal(text, m.Mask(text));
    }

    // ===== 堆疊（B-5）=====

    [Fact]
    public void 堆疊_框架保留_路徑只留檔名與行號_連續框架收成略過N層_例外訊息走完整遮罩()
    {
        var m = MakeWithHosts(H(1, "SRV01"));
        const string stack =
            "System.InvalidOperationException: failed on SRV01 at 10.1.1.1\r\n" +
            "   at LogForesight.Web.Services.Foo.Bar(String host) in C:\\src\\LogForesight.Web\\Services\\Foo.cs:line 42\r\n" +
            "   at System.Linq.Enumerable.Select()\r\n" +
            "   at System.Threading.Tasks.Task.Run()\r\n" +
            "   at Microsoft.AspNetCore.Hosting.X()\r\n" +
            "   at LogForesight.Core.Baz.Run() in /home/build/src/Baz.cs:line 7\r\n" +
            "--- End of stack trace from previous location ---\r\n" +
            "   at System.Foo.Bar()";
        var masked = m.MaskStack(stack);
        var lines = masked.Split('\n');

        Assert.Equal("System.InvalidOperationException: failed on host-001 at ip-001", lines[0]);
        Assert.Equal("   at LogForesight.Web.Services.Foo.Bar(String host) in Foo.cs:line 42", lines[1]);
        Assert.Equal("   ... 略過 3 層框架呼叫", lines[2]);
        Assert.Equal("   at LogForesight.Core.Baz.Run() in Baz.cs:line 7", lines[3]);
        Assert.Equal("--- End of stack trace from previous location ---", lines[4]);
        Assert.Equal("   ... 略過 1 層框架呼叫", lines[5]);
        Assert.DoesNotContain("C:\\src", masked);
        Assert.DoesNotContain("/home/build", masked);
    }

    [Fact]
    public void 堆疊_看起來像框架但含可遮罩內容的行_退回完整遮罩()
    {
        var m = Make();
        var masked = m.MaskStack("   at 10.9.9.9.Run(x)\n   at 這是一般敘述 (see log) 在 SRV99");
        Assert.DoesNotContain("10.9.9.9", masked);
        Assert.Contains("ip-001", masked);
    }

    // ===== 結果掃描（B-6）=====

    [Fact]
    public void 掃描_遮罩後的內容是乾淨的()
    {
        var m = MakeWithHosts(H(1, "SRV01", ip: "10.1.2.3"));
        var json = JsonSerializer.Serialize(new { msg = m.Mask("SRV01 10.1.2.3 bob@example.org"), n = 3 });
        Assert.Empty(m.ScanResidual(json));
        m.EnsureNoResidual(json);
    }

    [Fact]
    public void 掃描_字典原值殘留_不分大小寫_屬性名稱也掃()
    {
        var m = MakeWithHosts(H(1, "SRV01"));
        var r1 = m.ScanResidual("{\"a\":\"x srv01 y\"}");
        Assert.Equal(1, r1[RunExportDeidentifier.ResidualKinds.Dictionary]);
        var r2 = m.ScanResidual("{\"SRV01\":1}");
        Assert.Equal(1, r2[RunExportDeidentifier.ResidualKinds.Dictionary]);
        // 邊界同遮罩：SRV010 不是 SRV01
        Assert.Empty(m.ScanResidual("{\"a\":\"SRV010x\"}"));
    }

    [Fact]
    public void 掃描_JSON跳脫過的非ASCII原值也找得到()
    {
        var m = MakeWithHosts(H(1, "主機甲"));
        var json = JsonSerializer.Serialize(new { a = "連到主機甲失敗" });
        Assert.Contains("\\u", json); // 預設編碼器會把中文跳脫成 \uXXXX
        Assert.Equal(1, m.ScanResidual(json)[RunExportDeidentifier.ResidualKinds.Dictionary]);
    }

    [Fact]
    public void 掃描_IPv4與Email與密鑰殘留()
    {
        var m = Make(Src(secrets: new[] { "TopSecretValue" }));
        var r = m.ScanResidual("{\"a\":\"from 1.2.3.4\",\"b\":\"bob@example.org\",\"c\":\"x TopSecretValue y\",\"d\":[\"9.9.9.9\"]}");
        Assert.Equal(2, r[RunExportDeidentifier.ResidualKinds.Ipv4]);
        Assert.Equal(1, r[RunExportDeidentifier.ResidualKinds.Email]);
        Assert.Equal(1, r[RunExportDeidentifier.ResidualKinds.Secret]);
        Assert.False(r.ContainsKey(RunExportDeidentifier.ResidualKinds.Dictionary));
    }

    [Fact]
    public void 掃描_刻意不遮罩的欄位可整串放行()
    {
        var m = Make();
        const string json = "{\"appVersion\":\"1.0.0.0\",\"note\":\"ok\"}";
        Assert.Equal(1, m.ScanResidual(json)[RunExportDeidentifier.ResidualKinds.Ipv4]);
        Assert.Empty(m.ScanResidual(json, new[] { "1.0.0.0" }));
    }

    [Fact]
    public void 掃描_不是合法JSON_視為無法確認()
    {
        var m = Make();
        Assert.Equal(1, m.ScanResidual("{not json")[RunExportDeidentifier.ResidualKinds.Unparsable]);
    }

    [Fact]
    public void 掃描有殘留就丟專屬例外_訊息與屬性不含原值()
    {
        var m = MakeWithHosts(H(1, "SRV-LEAK-77", ip: "10.77.77.77"));
        var ex = Assert.Throws<RunExportDeidentificationException>(
            () => m.EnsureNoResidual("{\"a\":\"SRV-LEAK-77 at 10.77.77.77\"}"));

        Assert.Equal(DeidentificationFailureKind.ResidualFound, ex.Kind);
        Assert.Equal(2, ex.Residual[RunExportDeidentifier.ResidualKinds.Dictionary]);
        Assert.Equal(1, ex.Residual[RunExportDeidentifier.ResidualKinds.Ipv4]);
        var everything = ex.ToString() + string.Join(",", ex.Residual.Keys);
        Assert.DoesNotContain("SRV-LEAK-77", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10.77.77.77", everything);
    }

    // ===== 規模（唯一接受的效能基準，預設略過）=====

    [ScaleFact]
    public void 規模_2000台字典與一萬則訊息在五秒內()
    {
        var hosts = Enumerable.Range(1, 2000)
            .Select(i => H(i, $"SRV{i:D4}", display: $"Display-Host-{i}", ip: $"10.{i / 250}.{i % 250}.{i % 200 + 1}"))
            .ToArray();
        var m = Make(Src(hosts: hosts, users: Enumerable.Range(1, 200).Select(i => new DeidentifierUser(i, $"user{i}", $"User Name {i}", $"user{i}@example.org")).ToArray()));

        var messages = Enumerable.Range(1, 10_000)
            .Select(i => $"[{i}] 連線 SRV{i % 2000 + 1:D4} 失敗，來源 10.{i % 8}.{i % 250}.{i % 200 + 1} 帳號 user{i % 200 + 1} 錯誤 System.Net.Http.HttpClient 逾時 12:34:{i % 60:D2} 位於 https://svc{i % 30}.example.org/api/x?id={i}")
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var masked = messages.Select(m.Mask).ToList();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"耗時 {sw.Elapsed.TotalSeconds:F2} 秒");
        Assert.All(masked.Take(200), x => Assert.DoesNotContain("SRV0", x));
    }
}

/// <summary>去識別化失敗的錯誤碼登錄與 ApiExceptionFilter 對應；log 只含類別與次數。</summary>
[Collection("NLogGlobalConfig")]
public class RunExportDeidentifierTestsFilter
{
    [Fact]
    public void 錯誤碼已登錄_狀態碼500()
    {
        Assert.Equal("export_deidentification_failed", ApiErrorCodes.ExportDeidentificationFailed);
        Assert.Equal(500, ApiErrorCodes.StatusOf(ApiErrorCodes.ExportDeidentificationFailed));
        Assert.Equal(500, ApiErrorCodes.Table[ApiErrorCodes.ExportDeidentificationFailed]);
    }

    [Fact]
    public void WebSpec登錄了這個錯誤碼()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var spec = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "WEB-SPEC.md"));
        Assert.Contains("`export_deidentification_failed` | 500 |", spec);
    }

    [Fact]
    public void Filter把去識別化失敗對應成500加錯誤碼_回應與log都不含原值()
    {
        var target = new MemoryTarget("lf-deid-" + Guid.NewGuid().ToString("N"))
        {
            Layout = "${level}|${logger}|${message}|${exception:format=ToString}"
        };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
        LogManager.ReconfigExistingLoggers();

        try
        {
            var m = RunExportDeidentifier.Create(new RunExportDeidentifierSource
            {
                Hosts = new[] { new DeidentifierHost(1, "SRV-LEAK-88", null, null, null, null, null) }
            });

            RunExportDeidentificationException? thrown = null;
            try
            {
                m.EnsureNoResidual("{\"a\":\"SRV-LEAK-88\"}");
            }
            catch (RunExportDeidentificationException ex)
            {
                thrown = ex;
            }

            Assert.NotNull(thrown);

            var http = new DefaultHttpContext();
            http.Request.Method = "GET";
            http.Request.Path = "/api/test";
            var ctx = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>())
            {
                Exception = thrown!
            };
            new ApiExceptionFilter().OnException(ctx);

            var result = Assert.IsType<ObjectResult>(ctx.Result);
            Assert.Equal(500, result.StatusCode);
            var body = Assert.IsType<ApiResponse<object>>(result.Value);
            Assert.Equal("export_deidentification_failed", body.Error!.Code);
            Assert.DoesNotContain("SRV-LEAK-88", body.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(ctx.ExceptionHandled);

            var logs = target.Logs.ToList();
            Assert.Contains(logs, l => l.Contains("dictionary=1"));
            Assert.DoesNotContain(logs, l => l.Contains("SRV-LEAK-88", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }
}

/// <summary>
/// 工廠從各 store 讀字典來源：每個欄位進得去、密文密鑰解得開、任一來源丟例外就中止。
/// 觸碰 <see cref="CryptoHelper"/> 的全域狀態（解密失敗旗標），與其他金鑰測試放同一個集合序列化。
/// </summary>
[Collection("CryptoKeyState")]
public class RunExportDeidentifierTestsSource : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public RunExportDeidentifierTestsSource() => CryptoHelper.ResetForTests();

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>讓指定方法丟例外、其餘轉給真正的 store 的代理（IHostStore 等介面很大，不手寫替身）</summary>
    public class ThrowingProxy<T> : DispatchProxy where T : class
    {
        public T Inner { get; set; } = null!;

        public string ThrowOn { get; set; } = "GetAll";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == ThrowOn)
                throw new InvalidOperationException("底層失敗含機敏字樣 sk-live-DO-NOT-LEAK");
            return targetMethod.Invoke(Inner, args);
        }
    }

    private static T Throwing<T>(T inner, string method = "GetAll") where T : class
    {
        var proxy = DispatchProxy.Create<T, ThrowingProxy<T>>();
        ((ThrowingProxy<T>)(object)proxy).Inner = inner;
        ((ThrowingProxy<T>)(object)proxy).ThrowOn = method;
        return proxy;
    }

    private sealed class Stores
    {
        public IHostStore Hosts = null!;
        public IHostGroupStore Groups = null!;
        public IUserStore Users = null!;
        public ISentinelStore Sentinels = null!;
        public ISystemSettingsStore Settings = null!;
        public List<PrtgDeviceRow> Devices = new();
        public Func<IReadOnlyList<PrtgDeviceRow>> PrtgSource = null!;
    }

    private Stores Build()
    {
        var s = new Stores
        {
            Hosts = new HostStore(_fx.Blob("hosts")),
            Groups = new HostGroupStore(_fx.Blob("host_groups")),
            Users = new UserStore(_fx.Blob("users"), _fx.Blob("user_last_login")),
            Sentinels = new SentinelStore(_fx.Blob("sentinels")),
            Settings = new SystemSettingsStore(_fx.Blob("system_settings"))
        };
        s.PrtgSource = () => s.Devices;
        return s;
    }

    private static RunExportDeidentifierFactory Factory(Stores s) =>
        new(s.Hosts, s.Groups, s.Users, s.Sentinels, s.PrtgSource, s.Settings);

    private static void Seed(Stores s)
    {
        s.Hosts.Upsert(new WebHost { HostName = "SRV-REAL-1", DisplayName = "Real One", IpAddress = "10.8.8.8", RoleDesc = "資料庫" });
        s.Groups.Upsert(new HostGroup { GroupName = "財務部" });
        s.Users.Upsert(new WebUser { Account = "alice", DisplayName = "Alice Real", Email = "alice@real.example" });
        s.Sentinels.Upsert(new Sentinel
        {
            Name = "SENT-REAL", BaseUrl = "https://sent-real.example.org/", Username = "svc-real",
            PasswordEnc = CryptoHelper.Encrypt("sentinel-pass-123")
        });
        s.Settings.Update(x =>
        {
            x.AiApiKeyEnc = CryptoHelper.Encrypt("ai-key-ABCDEF");
            x.SmtpPasswordEnc = CryptoHelper.Encrypt("smtp-pass-XYZ789");
            x.PrtgApiTokenEnc = CryptoHelper.Encrypt("prtg-token-QWERTY");
            x.PrtgPasswordEnc = CryptoHelper.Encrypt("prtg-pass-0000");
            x.PrtgPasshashEnc = CryptoHelper.Encrypt("1234567890");
            x.PrtgEnabled = true;
        });
        s.Devices.Add(new PrtgDeviceRow { Objid = 7, Name = "PRTG-DEV-REAL", Ip = "10.7.7.7", GroupPath = "Root/DC-REAL" });
    }

    [Fact]
    public void ReadSource_各來源欄位與密鑰實際值都讀進來()
    {
        var s = Build();
        Seed(s);

        var src = Factory(s).ReadSource();

        Assert.Equal("SRV-REAL-1", Assert.Single(src.Hosts).HostName);
        Assert.Equal("財務部", Assert.Single(src.HostGroupNames));
        Assert.Equal("alice", Assert.Single(src.Users).Account);
        Assert.Equal("svc-real", Assert.Single(src.Sentinels).Username);
        Assert.Equal("PRTG-DEV-REAL", Assert.Single(src.PrtgDevices).Name);
        Assert.Equal(
            new[] { "ai-key-ABCDEF", "smtp-pass-XYZ789", "prtg-token-QWERTY", "prtg-pass-0000", "1234567890", "sentinel-pass-123" }.OrderBy(x => x),
            src.Secrets.OrderBy(x => x));
    }

    [Fact]
    public void Create_端到端_字典與密鑰都生效()
    {
        var s = Build();
        Seed(s);
        var m = Factory(s).Create();

        var masked = m.Mask("SRV-REAL-1 (10.8.8.8) 以 alice 連 https://sent-real.example.org/x 失敗 密碼 sentinel-pass-123 與 ai-key-ABCDEF；裝置 PRTG-DEV-REAL 於 DC-REAL");
        foreach (var leaked in new[] { "SRV-REAL-1", "10.8.8.8", "alice", "sent-real", "sentinel-pass-123", "ai-key-ABCDEF", "PRTG-DEV-REAL", "DC-REAL" })
            Assert.DoesNotContain(leaked, masked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", masked);
    }

    [Fact]
    public void 沒有任何資料的全新站台_仍能建立_PRTG鏡像為空屬正常()
    {
        var s = Build();
        var m = Factory(s).Create();
        Assert.Equal("ip-001", m.Mask("1.2.3.4"));
    }

    [Fact]
    public void 體檢_PRTG額外取數主機與最後修改帳號也進字典()
    {
        var s = Build();
        s.Settings.Update(x =>
        {
            x.PrtgValueFetchExtraHosts = new List<string> { "EXTRA-NAS01", "10.9.9.9" };
            x.UpdatedByAccount = "opsadmin";
        });

        var m = Factory(s).Create();

        Assert.Equal("host-001 host-002 user-001", m.Mask("EXTRA-NAS01 10.9.9.9 opsadmin"));
    }

    [Fact]
    public void PRTG未啟用_鏡像讀取失敗不算來源失敗()
    {
        var s = Build();
        s.Settings.Update(x => x.PrtgEnabled = false);
        s.PrtgSource = () => throw new InvalidOperationException("table missing");
        var m = Factory(s).Create();
        Assert.Equal("ip-001", m.Mask("1.2.3.4"));
    }

    [Theory]
    [InlineData("主機清單")]
    [InlineData("主機群組")]
    [InlineData("使用者清單")]
    [InlineData("Sentinel 設定")]
    [InlineData("系統設定")]
    [InlineData("PRTG 裝置鏡像")]
    public void 任一來源讀取例外就中止_不以殘缺字典繼續_訊息不含底層內容(string source)
    {
        var s = Build();
        Seed(s);
        switch (source)
        {
            case "主機清單": s.Hosts = Throwing(s.Hosts); break;
            case "主機群組": s.Groups = Throwing(s.Groups); break;
            case "使用者清單": s.Users = Throwing(s.Users); break;
            case "Sentinel 設定": s.Sentinels = Throwing(s.Sentinels); break;
            case "系統設定": s.Settings = Throwing(s.Settings, "Get"); break;
            case "PRTG 裝置鏡像": s.PrtgSource = () => throw new InvalidOperationException("底層失敗含機敏字樣 sk-live-DO-NOT-LEAK"); break;
        }

        var ex = Assert.Throws<RunExportDeidentificationException>(() => Factory(s).Create());

        Assert.Equal(DeidentificationFailureKind.SourceUnavailable, ex.Kind);
        Assert.Equal(source, ex.SourceName);
        Assert.NotNull(ex.InnerException);
        Assert.DoesNotContain("sk-live", ex.Message);
        Assert.Contains(source, ex.Message);
    }

    [Fact]
    public void 密文解不開不中止匯出_記下名稱且樣式遮罩照常()
    {
        const string Undecryptable = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var s = Build();
        s.Settings.Update(x =>
        {
            x.PrtgPasswordEnc = Undecryptable;
            x.AiApiKeyEnc = Undecryptable;
        });

        var masker = Factory(s).Create();

        Assert.Equal(new[] { "AI 金鑰" }, masker.UndecryptableSecrets);
        Assert.Equal(new[] { "PRTG 密碼" }, masker.RetiredUndecryptableSecrets);
        Assert.DoesNotContain("plain-value-1", masker.Mask("連線失敗 password=plain-value-1"));
    }

    [Fact]
    public void Secret_retired解不開只進retired清單_非retired只進原清單_解得開皆空()
    {
        const string Bad = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var secrets = new List<string>();
        var u = new List<string>();
        var r = new List<string>();

        RunExportDeidentifierFactory.Secret(secrets, u, "舊", Bad, retired: true, retiredUndecryptable: r);
        Assert.Empty(u);
        Assert.Equal(new[] { "舊" }, r);

        u.Clear(); r.Clear();
        RunExportDeidentifierFactory.Secret(secrets, u, "現", Bad, retired: false, retiredUndecryptable: r);
        Assert.Equal(new[] { "現" }, u);
        Assert.Empty(r);

        u.Clear(); r.Clear();
        RunExportDeidentifierFactory.Secret(secrets, u, "好", "plain-ok", retired: true, retiredUndecryptable: r);
        Assert.Empty(u);
        Assert.Empty(r);
    }

    [Fact]
    public void 其他來源讀取失敗仍然中止_解不開的憑證不放寬字典來源()
    {
        var s = Build();
        s.Settings.Update(x =>
        {
            x.PrtgEnabled = true;
            x.PrtgPasswordEnc = "enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        });
        s.PrtgSource = () => throw new InvalidOperationException("壞了");

        var ex = Assert.Throws<RunExportDeidentificationException>(() => Factory(s).Create());
        Assert.Equal("PRTG 裝置鏡像", ex.SourceName);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 排程作業診斷匯出的去識別化遮罩器（一次匯出一個實例）。
///
/// <b>契約</b>
/// <list type="bullet">
/// <item>取代順序固定：密鑰 → URL → 已知字典（長字串優先）→ 樣式。順序不可對調——
/// 密鑰先於字典，否則含主機名的密碼只會被切掉一半；字典先於樣式，否則主機 IP 會被當成一般 IP 而不是該主機的代號。</item>
/// <item>代號（<c>host-001</c>、<c>user-001</c>…）依首次出現配發，只在同一個實例內穩定，**不用雜湊**（雜湊可被字典反推）。
/// 檔案內不放對照表。</item>
/// <item>主機的 IP 與名稱對應到同一個 host 代號；Sentinel 位址對應到 sentinel 代號。</item>
/// <item>字典比對不分大小寫；前後不是英數才算邊界——<c>_</c>、<c>-</c>、<c>.</c> 都是邊界，
/// <c>WEBSRV01_20261103.log</c>、<c>WEBSRV01-nightly</c>、<c>WEBSRV01.contoso.eu</c> 裡的主機名都要遮（寧可多遮）；中文相鄰視為邊界。保留詞（AI、PRTG、NetIQ、Sentinel）不進字典。</item>
/// <item>IP 判斷一律走 <see cref="IpSyntax"/>；IPv6 只認含 <c>::</c> 或完整 8 段者，避免把 <c>12:34:56</c> 這類時間當位址。</item>
/// <item>序列化完成後以 <see cref="ScanResidual"/>／<see cref="EnsureNoResidual"/> 掃整份 JSON，有殘留就中止匯出。
/// 伺服器 log 只記類別與次數，不記原值。</item>
/// </list>
/// 單一執行緒使用為主；代號配發內部有鎖，同一實例被並行呼叫時代號仍一致。
/// </summary>
public sealed class RunExportDeidentifier : IRunExportMasker
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>保留詞：產品與整合系統的通用名稱，不遮（暫定清單）。主機剛好叫這些名字時也不遮</summary>
    public static readonly IReadOnlySet<string> KeepWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AI", "PRTG", "NetIQ", "Sentinel" };

    /// <summary>代號類別（代號格式為 <c>{類別}-{三位數}</c>，密鑰固定為 <c>secret</c>）</summary>
    public static class Kinds
    {
        public const string Host = "host";
        public const string User = "user";
        public const string Sentinel = "sentinel";
        public const string Device = "device";
        public const string Group = "group";
        public const string Ip = "ip";
        public const string Url = "url";
        public const string Domain = "domain";
        public const string Email = "email";
        public const string Sid = "sid";
        public const string Role = "role";
        public const string Secret = "secret";
    }

    /// <summary>殘留掃描的類別</summary>
    public static class ResidualKinds
    {
        public const string Dictionary = "dictionary";
        public const string Ipv4 = "ipv4";
        public const string Email = "email";
        public const string Secret = "secret";

        /// <summary>內容不是合法 JSON，無法確認是否乾淨</summary>
        public const string Unparsable = "unparsable";
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(3);

    private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const RegexOptions OptI = Opt | RegexOptions.IgnoreCase;

    // ---- 樣式 ----

    private static readonly Regex BearerRx = new(@"(?<k>\bBearer\s+)[A-Za-z0-9\-._~+/]+=*", OptI, RegexTimeout);

    private static readonly Regex BasicRx = new(@"(?<k>\bBasic\s+)(?<v>[A-Za-z0-9+/]{8,}={0,2})", OptI, RegexTimeout);

    // 密鑰類參數名＝值（URL 查詢字串、設定行、JSON 片段都涵蓋）。值為引號包起來的整段，或到空白／分隔符為止
    private static readonly Regex SecretKvRx = new(
        @"(?<k>(?<![A-Za-z0-9])(?:[A-Za-z0-9]+[_.-])?(?:password|passwd|pwd|passphrase|passhash|apitoken|api_key|api-key|apikey|access_token|refresh_token|client_secret|token|secret)(?:[_.-]?(?:enc|hash|value|key))?[""']?[ \t]*[=:][ \t]*)(?<v>""[^""\r\n]*""|'[^'\r\n]*'|[^\s&;,""')\]]+)",
        OptI, RegexTimeout);

    private static readonly Regex UrlRx = new(
        @"(?<![A-Za-z0-9+.\-])(?:https?|ftps?|ldaps?|wss?|sftp)://[^\s""'<>\\^`{|}\u0080-￿]+", OptI, RegexTimeout);

    private static readonly Regex EmailRx = new(
        @"(?<![A-Za-z0-9._%+'\-])[A-Za-z0-9._%+'\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*", Opt, RegexTimeout);

    private static readonly Regex UncRx = new(
        @"\\\\(?<s>[A-Za-z0-9_.$\-]+)(?<r>(?:\\[^\s\\""'<>|:*?]*)*)", Opt, RegexTimeout);

    // 帳號段不可回溯：HKLM\Software\Microsoft 若能回溯，會被切成 HKLM＋Softwar 而把路徑遮壞
    private static readonly Regex DomainUserRx = new(
        @"(?<![A-Za-z0-9_\\:/.$\-])(?<d>[A-Za-z][A-Za-z0-9_\-]{1,30})\\(?<u>(?>[A-Za-z0-9_$][A-Za-z0-9_.$\-]{0,63}))(?![\\/])", Opt, RegexTimeout);

    private static readonly Regex UserProfileRx = new(
        @"(?<k>(?<![A-Za-z0-9])[A-Za-z]:\\Users\\|/home/|/Users/)(?<n>[^\\/\s:*?""<>|]+)", OptI, RegexTimeout);

    private static readonly Regex SidRx = new(@"(?<![A-Za-z0-9_\-])S-1-[0-9]+(?:-[0-9]+)+(?![A-Za-z0-9_\-])", Opt, RegexTimeout);

    // 前後只排除數字：x_10.1.2.3、host.10.1.2.3、ver10.1.2.3 裡的位址都要遮（五段以上的版本號會被遮掉後四段，接受）
    private static readonly Regex Ipv4Rx = new(@"(?<![0-9])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?![0-9]|\.[0-9])", Opt, RegexTimeout);

    // 前面可以是冒號：addr:fe80::1、RemoteAddress:2001:db8::5 的位址要遮；時間 12:34:56 由「含 :: 或完整 8 段」擋掉
    private static readonly Regex Ipv6Rx = new(@"(?<![A-Za-z0-9_.])[0-9A-Fa-f:.]{3,}(?![A-Za-z0-9_:])", Opt, RegexTimeout);

    private static readonly Regex FqdnRx = new(
        @"(?<![A-Za-z0-9_@.\-])[A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+)+(?![A-Za-z0-9_\-])", Opt, RegexTimeout);

    private const string TokenPattern = @"(?:(?:host|user|sentinel|device|group|ip|url|domain|email|sid|role)-[0-9]{3,}|secret)";

    // 整串就是代號（例如已被字典換掉的 user-001）
    private static readonly Regex TokenLikeRx = new("^" + TokenPattern + "$", Opt, RegexTimeout);

    // 以代號開頭（例如機器帳戶 host-001$：前面的主機名已被字典換掉，不該再被當成帳號重配一個代號）
    private static readonly Regex TokenPrefixRx = new("^" + TokenPattern + "(?![A-Za-z0-9])", Opt, RegexTimeout);

    // 事件與 syslog 的身分欄位：標籤保留，值換代號
    private static readonly (Regex Rx, string Kind)[] LabelRules =
    {
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:account name|target user name|subject user name|caller user name|user name|username|logon account|user account|帳戶名稱|使用者名稱|使用者帳戶)[ \t]*[:：=][ \t]*)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.User),
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:account domain|target domain name|subject domain name|caller domain|logon domain|帳戶網域)[ \t]*[:：=][ \t]*)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.Domain),
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:workstation name|source workstation|client name|computer name|machine name|工作站名稱|電腦名稱|來源工作站)[ \t]*[:：=][ \t]*)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.Host),
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:user|ruser|logname|acct)=)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.User),
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:Failed|Accepted)\s+\S+\s+for\s+(?:invalid user\s+)?)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.User),
        (new Regex(@"(?<k>(?<![A-Za-z0-9])(?:invalid user|authenticating user|disconnected from user|for user|from user)\s+)(?<v>[^\s,;|()""']+)", OptI, RegexTimeout), Kinds.User),
    };

    // 這些值不是識別資訊（內建主體、空值），保留下來分析才看得出「是系統帳號在動」
    private static readonly HashSet<string> KeepPrincipals =
        new(StringComparer.OrdinalIgnoreCase) { "-", "SYSTEM", "N/A", "ANONYMOUS", "ANONYMOUS LOGON", "LOCAL", "NETWORK" };

    private static readonly HashSet<string> KeepDomains =
        new(StringComparer.OrdinalIgnoreCase) { "NT", "AUTHORITY", "BUILTIN", "SERVICE", "WORKGROUP", "MANAGER", "DRIVER", "HOST", "-", "N/A" };

    private static readonly HashSet<string> KeepProfiles =
        new(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users", "Shared" };

    // 只認這些頂級網域（FQDN 判定）：System.Net.Http.HttpClient 這類命名空間因最後一段不是頂級網域而不誤判
    private static readonly HashSet<string> Tlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "edu", "gov", "mil", "biz", "info", "local", "lan", "corp", "internal",
        "intranet", "home", "localdomain", "tw", "cn", "jp", "hk", "sg", "us", "uk", "de", "fr", "kr",
        "au", "ca", "io", "ru", "nl", "eu", "co", "cloud", "dev", "app", "site", "online", "tech", "xyz",
        "me", "cc", "tv", "asia", "int", "pro", "mobi", "name", "ai", "ltd", "inc", "ngo"
    };

    /// <summary>兩個字母、全小寫或全大寫的最後一段視為國碼網域（清單列不完，漏遮比多遮糟）</summary>
    private static bool LooksLikeCountryCode(string tld) => tld.Length == 2 && tld.All(char.IsAsciiLetter);

    private static readonly HashSet<string> NotFqdn = new(StringComparer.OrdinalIgnoreCase) { "asp.net", "vb.net" };

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "txt", "json", "xml", "dll", "exe", "config", "ini", "cs", "csv", "db", "sql", "bak", "evtx", "yml", "yaml",
        "js", "mjs", "cjs", "ts", "py", "go", "rb", "sh", "md", "pl", "vb", "fs", "rs", "kt", "ps1", "psm1", "css", "htm", "html",
        "cshtml", "csproj", "sln", "pdb", "pdf", "zip", "png", "jpg", "gif", "svg", "ico", "cfg", "tmp", "dat", "bin", "old", "map"
    };

    // ---- 字典 ----

    private readonly record struct Entry(string Kind, string Key);

    private sealed class Node
    {
        public Dictionary<char, Node>? Next;
        public bool HasEntry;
        public Entry Entry;
    }

    private readonly Node _root = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _ipMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _secrets;

    /// <summary>代號字彙：密鑰值若是其中任一字串的子字串（例如密碼就叫 secret、host），結果掃描會把自己產生的代號當殘留、
    /// 讓匯出永遠失敗；這種值略過實際值比對，只靠樣式遮罩。</summary>
    private const string TokenVocabulary = "secret host-000 user-000 sentinel-000 device-000 group-000 ip-000 url-000 domain-000 email-000 sid-000 role-000";

    /// <inheritdoc />
    public IReadOnlyList<string> UndecryptableSecrets { get; }

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private int _secretReplacements;

    private RunExportDeidentifier(RunExportDeidentifierSource source)
    {
        UndecryptableSecrets = source.UndecryptableSecrets;
        foreach (var h in source.Hosts) AddHost(h);
        foreach (var u in source.Users) AddUser(u);
        foreach (var s in source.Sentinels) AddSentinel(s);
        foreach (var g in source.HostGroupNames) AddGroupName(g);
        foreach (var d in source.PrtgDevices) AddDevice(d);
        AddSettings(source.Settings);

        // 密鑰：4 字元以下的值不放（比對會把一般文字大量誤遮，且這種長度的密鑰本身就不成立）；長字串優先
        var secrets = new List<string>();
        foreach (var raw in source.Secrets)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length < 4) continue;
            if (TokenVocabulary.Contains(raw, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("診斷匯出：有一組密鑰值與代號字彙重疊，略過實際值比對（只靠樣式遮罩）。");
                continue;
            }
            secrets.Add(raw);
            var escaped = Uri.EscapeDataString(raw);
            if (!string.Equals(escaped, raw, StringComparison.Ordinal)) secrets.Add(escaped);
            // 表單編碼（空白→+）與 URL 編碼只差在這裡
            var formEncoded = System.Net.WebUtility.UrlEncode(raw);
            if (!string.Equals(formEncoded, raw, StringComparison.Ordinal)) secrets.Add(formEncoded);
        }

        _secrets = secrets.Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ToList();
    }

    /// <summary>以來源資料建立遮罩器（測試與 <see cref="RunExportDeidentifierFactory"/> 共用）</summary>
    public static RunExportDeidentifier Create(RunExportDeidentifierSource source) => new(source);

    // ===== 字典建構 =====

    /// <summary>主機、帳號、顯示名稱的最短長度：兩字中文姓名與 DB 這種主機名都是識別資訊，要遮</summary>
    private const int MinIdentifierLength = 2;

    /// <summary>群組、角色、站台描述的最短長度：這些是自由取的名稱，「成功」「DB」這類短詞多半是一般用語，
    /// 進字典會把訊息裡的常見詞整批換掉（看不懂比漏遮這種非身分資訊更傷）</summary>
    private const int MinDescriptiveLength = 3;

    private static string Lower(string s) => s.ToLowerInvariant();

    private void AddHost(DeidentifierHost h)
    {
        var name = h.HostName?.Trim();
        var keyName = !string.IsNullOrWhiteSpace(name) ? name : h.DisplayName?.Trim() ?? h.IpAddress?.Trim() ?? "";
        var hostEntry = new Entry(Kinds.Host, h.HostId != 0 ? $"h:{h.HostId}" : $"h:n:{Lower(keyName)}");

        AddNameWithAliases(h.HostName, hostEntry);
        AddNameWithAliases(h.DisplayName, hostEntry);
        AddIp(h.HostName, hostEntry);
        AddIp(h.IpAddress, hostEntry);

        var sentinelEntry = (string? n) => new Entry(Kinds.Sentinel, $"s:{Lower(n!.Trim())}");
        if (!string.IsNullOrWhiteSpace(h.NetiqServer)) AddText(h.NetiqServer, sentinelEntry(h.NetiqServer));
        if (!string.IsNullOrWhiteSpace(h.OrphanedFromSentinel)) AddText(h.OrphanedFromSentinel, sentinelEntry(h.OrphanedFromSentinel));
        if (!string.IsNullOrWhiteSpace(h.RoleDesc)) AddText(h.RoleDesc, new Entry(Kinds.Role, $"r:{Lower(h.RoleDesc.Trim())}"), MinDescriptiveLength);
    }

    private void AddUser(DeidentifierUser u)
    {
        var entry = new Entry(Kinds.User, u.UserId != 0 ? $"u:{u.UserId}" : $"u:n:{Lower((u.Account ?? u.DisplayName ?? u.Email ?? "").Trim())}");
        AddAccount(u.Account, entry);
        AddText(u.DisplayName, entry);
        AddText(u.Email, entry);
    }

    private void AddSentinel(DeidentifierSentinel s)
    {
        var name = s.Name?.Trim();
        var entry = new Entry(Kinds.Sentinel, $"s:{Lower(name ?? "")}");
        AddText(name, entry);
        AddUrl(s.BaseUrl, entry);
        AddAccount(s.Username, new Entry(Kinds.User, $"su:{Lower((s.Username ?? "").Trim())}"));
    }

    private void AddGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        AddText(name, new Entry(Kinds.Group, $"g:{Lower(name.Trim())}"), MinDescriptiveLength);
    }

    private void AddDevice(DeidentifierPrtgDevice d)
    {
        var entry = new Entry(Kinds.Device, $"d:{d.Objid}");
        AddNameWithAliases(d.Name, entry);
        AddIp(d.Ip, entry);

        // 群組路徑切段後逐段比對；PRTG 最上層固定叫 Root，不是識別資訊
        if (string.IsNullOrWhiteSpace(d.GroupPath)) return;
        var segments = d.GroupPath.Split(new[] { '/', '\\', '>' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i == 0 && segments[i].Equals("Root", StringComparison.OrdinalIgnoreCase)) continue;
            AddGroupName(segments[i]);
        }
    }

    private void AddSettings(DeidentifierSettings s)
    {
        foreach (var n in s.Names)
            if (!string.IsNullOrWhiteSpace(n)) AddText(n, new Entry(Kinds.Domain, $"dom:{Lower(n.Trim())}"), MinDescriptiveLength);
        foreach (var d in s.Domains)
            if (!string.IsNullOrWhiteSpace(d)) AddText(d, new Entry(Kinds.Domain, $"dom:{Lower(d.Trim())}"));
        foreach (var a in s.Accounts)
            if (!string.IsNullOrWhiteSpace(a)) AddAccount(a, new Entry(Kinds.User, $"su:{Lower(a.Trim())}"));
        foreach (var e in s.Emails)
            if (!string.IsNullOrWhiteSpace(e)) AddText(e, new Entry(Kinds.Email, $"e:{Lower(e.Trim())}"));
        foreach (var u in s.Urls)
        {
            if (string.IsNullOrWhiteSpace(u)) continue;
            var host = HostOfUrl(u);
            AddUrl(u, new Entry(Kinds.Domain, "dom:" + Lower(host ?? u.Trim())));
        }
    }

    private static string? HostOfUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;
        // 迴圈位址不是識別資訊，也會把 http://localhost:8080 這類本機服務的線索遮光
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]") return null;
        return host;
    }

    /// <summary>網址整串進字典；主機部分以 <paramref name="hostEntry"/> 進字典（Sentinel 位址對應到 sentinel 代號）</summary>
    private void AddUrl(string? url, Entry hostEntry)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var trimmed = url.Trim();
        AddText(trimmed, new Entry(Kinds.Url, $"u:{Lower(trimmed)}"));
        var host = HostOfUrl(trimmed);
        if (host is null) return;
        AddText(host, hostEntry);
        AddIp(host, hostEntry);
    }

    /// <summary>帳號進字典；<c>DOMAIN\user</c> 與 <c>user@domain</c> 另把單獨的 user 部分當別名，網域部分遮成 domain</summary>
    private void AddAccount(string? account, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(account)) return;
        var a = account.Trim();
        AddText(a, entry);

        var slash = a.LastIndexOf('\\');
        if (slash > 0 && slash < a.Length - 1)
        {
            AddText(a[(slash + 1)..], entry);
            var domain = a[..slash];
            AddText(domain, new Entry(Kinds.Domain, $"d:{Lower(domain)}"));
        }

        var at = a.IndexOf('@');
        if (at > 0 && at < a.Length - 1)
        {
            AddText(a[..at], entry);
            var domain = a[(at + 1)..];
            AddText(domain, new Entry(Kinds.Domain, $"d:{Lower(domain)}"));
        }
    }

    /// <summary>名稱進字典；含點的 FQDN 另把第一段（短名稱）當別名，事件常只印短名稱</summary>
    private void AddNameWithAliases(string? name, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var n = name.Trim();
        AddText(n, entry);

        if (IpSyntax.IsStrictIpv4(n)) return;
        var dot = n.IndexOf('.');
        if (dot > 1 && !n.Contains(':'))
        {
            var label = n[..dot];
            if (!label.All(char.IsAsciiDigit)) AddText(label, entry);
        }
    }

    /// <summary>值若是 IP（經 <see cref="IpSyntax"/> 驗證），登記標準寫法 → 代號，讓不同寫法（前導零、IPv6 縮寫）也對得回同一台</summary>
    private void AddIp(string? value, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        var canonical = CanonicalIp(v);
        if (canonical is null) return;
        _ipMap.TryAdd(canonical, entry);
        AddText(v, entry);
    }

    private static string? CanonicalIp(string value)
    {
        if (IpSyntax.CanonicalIpv4(value) is { } v4) return v4;
        if (value.Contains(':') && IpSyntax.TryParse(value, out var addr) && addr is not null &&
            addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return addr.ToString();
        return null;
    }

    /// <summary>放進字典（先登錄者優先；空白、單一字元、保留詞不進）</summary>
    private void AddText(string? text, Entry entry, int minLength = MinIdentifierLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (t.Length < minLength || KeepWords.Contains(t)) return;

        var key = Lower(t);
        if (!_entries.TryAdd(key, entry)) return;

        var node = _root;
        foreach (var c in key)
        {
            node.Next ??= new Dictionary<char, Node>();
            if (!node.Next.TryGetValue(c, out var next))
            {
                next = new Node();
                node.Next[c] = next;
            }

            node = next;
        }

        node.HasEntry = true;
        node.Entry = entry;
    }

    // ===== 代號 =====

    private string Token(Entry e) => Token(e.Kind, e.Key);

    private string Token(string kind, string key)
    {
        var id = kind + "\u0001" + key;
        lock (_gate)
        {
            if (_tokens.TryGetValue(id, out var existing)) return existing;
            var n = _counters.GetValueOrDefault(kind) + 1;
            _counters[kind] = n;
            var token = $"{kind}-{n:D3}";
            _tokens[id] = token;
            return token;
        }
    }

    /// <summary>各類代號已配發的數量（密鑰為取代次數）</summary>
    public IReadOnlyDictionary<string, int> TokenCounts
    {
        get
        {
            lock (_gate)
            {
                var result = new Dictionary<string, int>(_counters);
                if (_secretReplacements > 0) result[Kinds.Secret] = _secretReplacements;
                return result;
            }
        }
    }

    // ===== 對外方法 =====

    /// <summary>遮罩一段自由文字；null 或空字串回空字串</summary>
    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = MaskSecrets(text);
        s = MaskUrls(s);
        s = MaskDictionary(s);
        return MaskPatterns(s);
    }

    /// <summary>
    /// 給已知是主機名的欄位（例如執行紀錄的站台名稱、主機欄）取代號：
    /// 名稱／顯示名／IP 對得到字典就回該主機的代號，查不到（已刪除的主機）則依名稱另配 host 代號。
    /// 保留詞與空值原樣回傳。
    /// </summary>
    public string? MaskHost(string? hostText)
    {
        if (string.IsNullOrWhiteSpace(hostText)) return hostText;
        var t = hostText.Trim();
        if (KeepWords.Contains(t)) return hostText;

        if (_entries.TryGetValue(Lower(t), out var entry)) return Token(entry);
        if (CanonicalIp(t) is { } ip)
            return _ipMap.TryGetValue(ip, out var mapped) ? Token(mapped) : Token(Kinds.Ip, ip);
        return Token(Kinds.Host, $"n:{Lower(t)}");
    }

    /// <summary>
    /// 堆疊處理：<c>at Namespace.Type.Method(...)</c> 保留；<c>in 路徑:line N</c> 只留檔名與行號；
    /// 連續的 <c>System.*</c>／<c>Microsoft.*</c> 框架層收成「略過 N 層」；其他行（例外訊息等）走完整遮罩。
    /// </summary>
    public string MaskStack(string? stack)
    {
        if (string.IsNullOrEmpty(stack)) return "";

        var output = new List<string>();
        var skipped = 0;
        void FlushSkipped()
        {
            if (skipped > 0) output.Add($"   ... 略過 {skipped} 層框架呼叫");
            skipped = 0;
        }

        foreach (var rawLine in stack.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = FrameRx.Match(line);
            if (!m.Success)
            {
                FlushSkipped();
                output.Add(Mask(line));
                continue;
            }

            var method = m.Groups["m"].Value;
            // 保險：保留下來的方法文字若仍含字典詞、IP 或其他可遮罩的樣式，退回整行完整遮罩
            if (Mask(method) != method)
            {
                FlushSkipped();
                output.Add(Mask(line));
                continue;
            }

            if (method.StartsWith("System.", StringComparison.Ordinal) || method.StartsWith("Microsoft.", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            FlushSkipped();
            var sb = new StringBuilder();
            sb.Append(m.Groups["ind"].Value).Append("at ");
            if (m.Groups["async"].Success) sb.Append("async ");
            sb.Append(method);
            if (m.Groups["p"].Success)
            {
                var path = m.Groups["p"].Value.TrimEnd();
                var file = path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
                sb.Append(" in ").Append(Mask(file));
                if (m.Groups["n"].Success) sb.Append(":line ").Append(m.Groups["n"].Value);
            }

            output.Add(sb.ToString());
        }

        FlushSkipped();
        return string.Join("\n", output);
    }

    private static readonly Regex FrameRx = new(
        @"^(?<ind>\s*)at\s+(?<async>async\s+)?(?<m>[\w.`<>+\[\]$|=&*,\-]*\.[\w.`<>+\[\]$|=&*,\-]*\([^()\r\n]*\))(?:\s+in\s+(?<p>.+?)(?::line\s+(?<n>\d+))?)?\s*$",
        Opt, RegexTimeout);

    // ===== 結果掃描 =====

    /// <summary>
    /// 掃整份 JSON（逐一解開字串值與屬性名稱，涵蓋跳脫與 \uXXXX）：字典原值、IPv4、Email、密鑰實際值。
    /// 回傳類別 → 次數（不含原值）；空字典＝乾淨。內容不是合法 JSON 時回 <c>unparsable</c>。
    /// <paramref name="allowedLiterals"/> 是整串相等時放行的值（例如 <c>appVersion</c> 這類刻意不遮罩的欄位）。
    /// 保留詞不在字典內，因此不算殘留。
    /// <paramref name="scanPropertyNames"/> 為 false 時只掃值、不掃屬性名稱：匯出的欄位名是程式固定的結構，
    /// 站台的字典卻可能有 <c>status</c>、<c>count</c> 這類常見詞（帳號、群組名稱都可以這樣取），
    /// 連欄位名一起掃，該站台每次匯出都會失敗。呼叫端用 false 時，**動態鍵只能是程式自己產生的值**，
    /// 不得把資料內容當成屬性名稱。預設 true（逐一掃描屬性名稱），既有呼叫端行為不變。
    /// </summary>
    public IReadOnlyDictionary<string, int> ScanResidual(string json, IEnumerable<string>? allowedLiterals = null,
        bool scanPropertyNames = true)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var allowed = allowedLiterals is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(allowedLiterals.Where(x => !string.IsNullOrEmpty(x)), StringComparer.OrdinalIgnoreCase);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException)
        {
            counts[ResidualKinds.Unparsable] = 1;
            return counts;
        }

        using (doc)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(doc.RootElement);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                switch (el.ValueKind)
                {
                    case JsonValueKind.String:
                        ScanString(el.GetString() ?? "", allowed, counts);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray()) stack.Push(item);
                        break;
                    case JsonValueKind.Object:
                        foreach (var p in el.EnumerateObject())
                        {
                            if (scanPropertyNames) ScanString(p.Name, allowed, counts);
                            stack.Push(p.Value);
                        }

                        break;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// 殘留掃描失敗即停：有任何殘留就丟 <see cref="RunExportDeidentificationException"/>，呼叫端不得輸出任何檔案。
    /// 伺服器 log 只記類別與次數。
    /// </summary>
    public void EnsureNoResidual(string json, IEnumerable<string>? allowedLiterals = null, bool scanPropertyNames = true)
    {
        var residual = ScanResidual(json, allowedLiterals, scanPropertyNames);
        if (residual.Count == 0) return;

        Log.Warn("匯出去識別化結果掃描發現殘留（{0}），已中止匯出。", string.Join("、", residual.Select(kv => $"{kv.Key}={kv.Value}")));
        throw new RunExportDeidentificationException(residual);
    }

    private void ScanString(string s, HashSet<string> allowed, Dictionary<string, int> counts)
    {
        if (s.Length == 0 || allowed.Contains(s)) return;

        void Add(string kind, int n = 1) => counts[kind] = counts.GetValueOrDefault(kind) + n;

        var i = 0;
        while (i < s.Length)
        {
            if (TryMatch(s, i, out var end, out _))
            {
                Add(ResidualKinds.Dictionary);
                i = end;
            }
            else
            {
                i++;
            }
        }

        if (s.Contains('.'))
        {
            foreach (Match m in Ipv4Rx.Matches(s))
                if (IpSyntax.IsStrictIpv4(m.Value) && !allowed.Contains(m.Value)) Add(ResidualKinds.Ipv4);
        }

        if (s.Contains('@'))
            Add(ResidualKinds.Email, EmailRx.Matches(s).Count);

        foreach (var secret in _secrets)
        {
            var from = 0;
            while ((from = s.IndexOf(secret, from, StringComparison.Ordinal)) >= 0)
            {
                Add(ResidualKinds.Secret);
                from += secret.Length;
            }
        }
    }

    // ===== 階段 1：密鑰 =====

    private string MaskSecrets(string s)
    {
        foreach (var secret in _secrets)
        {
            var from = 0;
            StringBuilder? sb = null;
            var last = 0;
            while ((from = s.IndexOf(secret, from, StringComparison.Ordinal)) >= 0)
            {
                sb ??= new StringBuilder(s.Length);
                sb.Append(s, last, from - last).Append(Kinds.Secret);
                from += secret.Length;
                last = from;
                lock (_gate) _secretReplacements++;
            }

            if (sb is not null)
            {
                sb.Append(s, last, s.Length - last);
                s = sb.ToString();
            }
        }

        s = UrlSecrets.Mask(s);

        if (s.IndexOf("earer", StringComparison.OrdinalIgnoreCase) >= 0)
            s = BearerRx.Replace(s, m => { CountSecret(); return m.Groups["k"].Value + Kinds.Secret; });
        if (s.IndexOf("asic", StringComparison.OrdinalIgnoreCase) >= 0)
            s = BasicRx.Replace(s, m =>
            {
                if (!LooksLikeBase64Credential(m.Groups["v"].Value)) return m.Value;
                CountSecret();
                return m.Groups["k"].Value + Kinds.Secret;
            });

        if (s.IndexOfAny(new[] { '=', ':' }) >= 0)
        {
            s = SecretKvRx.Replace(s, m =>
            {
                var v = m.Groups["v"].Value;
                if (v == Kinds.Secret) return m.Value;
                CountSecret();
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
                    return m.Groups["k"].Value + v[0] + Kinds.Secret + v[0];
                return m.Groups["k"].Value + Kinds.Secret;
            });
        }

        return s;
    }

    /// <summary>
    /// Basic 後面接的是不是憑證：含數字或 +/= 就是；純字母時要長度是 4 的倍數且大小寫混合
    /// （<c>dXNlcjpwYXNz</c> 是，<c>configuration</c>、<c>Authentication</c> 這類英文詞組不是）。
    /// </summary>
    private static bool LooksLikeBase64Credential(string value)
    {
        if (value.Any(c => char.IsAsciiDigit(c) || c is '+' or '/' or '=')) return true;
        return value.Length % 4 == 0 && value.Skip(1).Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiLetterLower);
    }

    private void CountSecret()
    {
        lock (_gate) _secretReplacements++;
    }

    // ===== 階段 2：URL =====

    private string MaskUrls(string s)
    {
        if (s.IndexOf("://", StringComparison.Ordinal) < 0) return s;
        return UrlRx.Replace(s, m =>
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            var trailing = m.Value[url.Length..];
            return Token(Kinds.Url, $"u:{Lower(url)}") + trailing;
        });
    }

    // ===== 階段 3：字典（單次掃描，最長優先）=====

    private string MaskDictionary(string s)
    {
        if (_root.Next is null) return s;

        StringBuilder? sb = null;
        var last = 0;
        var i = 0;
        while (i < s.Length)
        {
            if (TryMatch(s, i, out var end, out var entry))
            {
                sb ??= new StringBuilder(s.Length + 16);
                sb.Append(s, last, i - last).Append(Token(entry));
                last = end;
                i = end;
            }
            else
            {
                i++;
            }
        }

        if (sb is null) return s;
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }

    private bool TryMatch(string s, int start, out int end, out Entry entry)
    {
        end = 0;
        entry = default;
        if (_root.Next is null) return false;
        if (!_root.Next.ContainsKey(char.ToLowerInvariant(s[start]))) return false;
        if (!StartBoundary(s, start)) return false;

        var node = _root;
        var bestEnd = -1;
        var best = default(Entry);
        for (var j = start; j < s.Length; j++)
        {
            if (node.Next is null || !node.Next.TryGetValue(char.ToLowerInvariant(s[j]), out var next)) break;
            node = next;
            if (node.HasEntry && EndBoundary(s, j + 1))
            {
                bestEnd = j + 1;
                best = node.Entry;
            }
        }

        if (bestEnd < 0) return false;
        end = bestEnd;
        entry = best;
        return true;
    }

    private static bool IsWordChar(char c) => c < 128 && char.IsAsciiLetterOrDigit(c);

    /// <summary>開頭邊界：前一字元不是英數。底線、連字號、點都算邊界——檔名 WEBSRV01_20261103.log、工作名 WEBSRV01-nightly
    /// 裡的主機名一樣要遮；結果掃描用同一套邊界，這裡放過的掃描也看不到。中文相鄰視為邊界</summary>
    private static bool StartBoundary(string s, int i)
    {
        if (i == 0) return true;
        if (!IsWordChar(s[i])) return true;
        return !IsWordChar(s[i - 1]);
    }

    /// <summary>結尾邊界：後一字元不是英數（SRV01 不會吃掉 SRV010 的一部分）</summary>
    private static bool EndBoundary(string s, int end)
    {
        if (end >= s.Length) return true;
        if (!IsWordChar(s[end - 1])) return true;
        return !IsWordChar(s[end]);
    }

    // ===== 階段 4：樣式 =====

    private string MaskPatterns(string s)
    {
        if (s.Contains('@')) s = EmailRx.Replace(s, m => Token(Kinds.Email, $"e:{Lower(m.Value)}"));

        if (s.Contains("\\\\")) s = UncRx.Replace(s, ReplaceUnc);
        if (s.Contains('\\')) s = DomainUserRx.Replace(s, ReplaceDomainUser);
        if (s.Contains("Users", StringComparison.OrdinalIgnoreCase) || s.Contains("/home/", StringComparison.Ordinal))
            s = UserProfileRx.Replace(s, ReplaceProfile);

        foreach (var (rx, kind) in LabelRules)
            s = rx.Replace(s, m => ReplaceLabeled(m, kind));

        if (s.Contains("S-1-", StringComparison.Ordinal)) s = SidRx.Replace(s, ReplaceSid);
        if (s.Contains(':')) s = Ipv6Rx.Replace(s, ReplaceIpv6);
        if (s.Contains('.'))
        {
            s = Ipv4Rx.Replace(s, ReplaceIpv4);
            s = FqdnRx.Replace(s, ReplaceFqdn);
        }

        return s;
    }

    private static bool IsTokenLike(string value) => TokenLikeRx.IsMatch(value);

    private static bool StartsWithToken(string value) => TokenPrefixRx.IsMatch(value);

    private string ReplaceUnc(Match m)
    {
        var server = m.Groups["s"].Value;
        if (StartsWithToken(server) || IpSyntax.IsStrictIpv4(server) || KeepWords.Contains(server)) return m.Value;
        return @"\\" + Token(Kinds.Host, $"n:{Lower(server)}") + m.Groups["r"].Value;
    }

    /// <summary>登錄機碼的根（HKLM\Software）不是網域\帳號</summary>
    private static bool IsRegistryHive(string value) =>
        value.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
        || value.ToUpperInvariant() is "HKLM" or "HKCU" or "HKCR" or "HKU" or "HKCC";

    private string ReplaceDomainUser(Match m)
    {
        var d = m.Groups["d"].Value;
        var u = m.Groups["u"].Value;
        var trailing = "";
        while (u.EndsWith('.')) { trailing += "."; u = u[..^1]; }

        if (u.Length == 0 || u.All(char.IsAsciiDigit)) return m.Value;
        var ext = u.LastIndexOf('.');
        if (ext > 0 && FileExtensions.Contains(u[(ext + 1)..])) return m.Value;

        if (IsRegistryHive(d)) return m.Value;

        var maskedDomain = IsTokenLike(d) || KeepDomains.Contains(d) || KeepWords.Contains(d)
            ? d
            : Token(Kinds.Domain, $"d:{Lower(d)}");
        var maskedUser = IsTokenLike(u) || KeepPrincipals.Contains(u) || KeepWords.Contains(u)
            ? u
            : Token(Kinds.User, $"n:{Lower(u)}");
        return maskedDomain + "\\" + maskedUser + trailing;
    }

    private string ReplaceProfile(Match m)
    {
        var name = m.Groups["n"].Value;
        if (IsTokenLike(name) || KeepProfiles.Contains(name)) return m.Value;
        return m.Groups["k"].Value + Token(Kinds.User, $"n:{Lower(name)}");
    }

    private string ReplaceLabeled(Match m, string kind)
    {
        var raw = m.Groups["v"].Value;
        var value = raw.TrimEnd('.');
        var trailing = raw[value.Length..];
        if (value.Length == 0 || StartsWithToken(value) || KeepWords.Contains(value)) return m.Value;
        if (kind == Kinds.User && KeepPrincipals.Contains(value)) return m.Value;
        if (kind == Kinds.Domain && KeepDomains.Contains(value)) return m.Value;
        // 值是 DOMAIN\user：上一個階段已處理（兩段都換成代號），這裡看到的是代號
        return m.Groups["k"].Value + Token(kind, kind == Kinds.Domain ? $"d:{Lower(value)}" : $"n:{Lower(value)}") + trailing;
    }

    private string ReplaceSid(Match m)
    {
        // S-1-5-21-…（網域／本機帳號）與其他帶 3 段以上子授權者才遮；S-1-5-18、S-1-5-32-544 這類內建 SID 不是識別資訊
        var parts = m.Value.Split('-');
        var subAuthorities = parts.Length - 3;
        if (subAuthorities < 3) return m.Value;
        return Token(Kinds.Sid, $"sid:{Lower(m.Value)}");
    }

    private string ReplaceIpv4(Match m)
    {
        var canonical = IpSyntax.CanonicalIpv4(m.Value);
        if (canonical is null) return m.Value;
        return _ipMap.TryGetValue(canonical, out var mapped) ? Token(mapped) : Token(Kinds.Ip, canonical);
    }

    private string ReplaceIpv6(Match m)
    {
        var v = m.Value;
        var core = v.TrimEnd('.');
        var trailing = v[core.Length..];
        var colons = core.Count(c => c == ':');
        // 只認含 :: 或完整 8 段者：12:34:56 這類時間、MAC 位址都不是
        if (!core.Contains("::", StringComparison.Ordinal) && colons != 7) return v;

        if (!IpSyntax.TryParse(core, out var addr) || addr is null ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return v;

        var canonical = addr.ToString();
        return (_ipMap.TryGetValue(canonical, out var mapped) ? Token(mapped) : Token(Kinds.Ip, canonical)) + trailing;
    }

    private string ReplaceFqdn(Match m)
    {
        var v = m.Value;
        if (NotFqdn.Contains(v)) return v;

        var labels = v.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0 || l[0] == '-' || l[^1] == '-')) return v;

        var tld = labels[^1];
        // 頂級網域必須是全小寫或全大寫：Pascal Case（System.Net、Logger.Info）是程式識別字，不是網域
        var isCasedUniformly = tld == tld.ToLowerInvariant() || tld == tld.ToUpperInvariant();
        // 檔名（Foo.cs、site.js）不是網域，即使副檔名長得像國碼
        if (FileExtensions.Contains(tld)) return v;
        if (!isCasedUniformly || !(Tlds.Contains(tld) || LooksLikeCountryCode(tld))) return v;

        // 第一段是已知主機（短名稱）→ 整串對到同一個 host 代號；字典階段已先把短名稱換成代號時，同樣只留主機代號
        if (_entries.TryGetValue(Lower(labels[0]), out var first) && first.Kind == Kinds.Host) return Token(first);
        if (IsTokenLike(labels[0]) && labels[0].StartsWith(Kinds.Host + "-", StringComparison.Ordinal)) return labels[0];
        return Token(Kinds.Domain, $"d:{Lower(v)}");
    }
}

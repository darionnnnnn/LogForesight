using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LogForesight.Core.Analysis;

public class LoginFailureDetail
{
    public string Account { get; set; } = string.Empty;
    public bool IsComputerAccount { get; set; }
    public string Source { get; set; } = string.Empty;
    public int? LogonType { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class LogIssueSignature
{
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EventLogEntryType EntryType { get; set; }

    /// <summary>
    /// Linux 簽章聚合鍵的第五段（docs/archive/FEEDBACK-12-PLAN.md §4.2，實作 docs/LINUX-RULES.md §131-146
    /// 的設計文，語意較該文簡化）。Windows 事件恆為空字串——既有的 (LogName, Source, EventId,
    /// EntryType) 四元組鍵字串完全不變，零遷移。Linux 事件沒有 EventId（恆 0），只在規則命中時
    /// 填規則 Id（<see cref="RuleId"/> 的值），用來把「同一個 program 命中不同規則」
    /// （如 sshd 底下的 ssh-bruteforce 與 ssh-accept）在簽章、處理狀態、案件鍵上分開；
    /// 未命中規則的 Linux 事件維持空字串，跟四元組聚合成 Other 類，語意與 Windows Other 一致。
    /// </summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>
    /// 顯示用的「來源＋事件識別」文字（docs/archive/FEEDBACK-12-PLAN.md §4.3）：Windows 顯示既有的
    /// 「{Source} EventId {EventId}」；Linux 命中規則時 EventId 恆 0、顯示這個數字沒有意義，
    /// 改顯示「{Source}（{EventKey}）」（規則 Id）。未命中規則的 Linux 事件（EventKey 空）
    /// 仍維持「EventId 0」——沒有更好的識別依據，誠實顯示比假裝有意義更好。
    /// 不序列化——由 <see cref="Source"/>／<see cref="EventId"/>／<see cref="EventKey"/> 算出，
    /// 避免另存一份可能漂移的值（同 <see cref="DailyAnalysisRecord.HasCoverageGap"/> 的既有慣例）。
    /// </summary>
    [JsonIgnore]
    public string SourceEventLabel => EventId == 0 && EventKey.Length > 0
        ? $"{Source}（{EventKey}）"
        : $"{Source} EventId {EventId}";

    public int Count { get; set; }

    /// <summary>當日首次/最後發生時間 (HH:mm)，用於判斷是集中爆發還是全天零星</summary>
    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;

    /// <summary>最多 3 則內容相異的範例訊息（各截 200 字）</summary>
    public List<string> SampleMessages { get; set; } = new();

    /// <summary>群組內相異訊息內容的數量——區分「同一服務掛 10 次」與「10 個服務各掛一次」</summary>
    public int DistinctMessageCount { get; set; }

    /// <summary>Security 事件從全部訊息彙總出的相關帳號與來源 IP（入侵分析關鍵資訊），非安全事件為 null</summary>
    public string? KeyDetails { get; set; }

    /// <summary>
    /// Security 與 RDP 事件從全部訊息抽出的完整帳號集合（不分大小寫去重且保持出現順序，封頂 200 個）。
    /// 非 Security／RDP 頻道為 null。
    /// 這是資料契約（供判定端如 ResidualCredentialDetector 比對使用），KeyDetails 是顯示字串，兩者不可互相取代。
    /// </summary>
    public List<string>? KeyAccounts { get; set; }

    /// <summary>
    /// <see cref="KeyAccounts"/> 是否因達到封頂上限（200 個）而截斷。
    /// 這是資料契約，KeyDetails 是顯示字串，兩者不可互相取代。
    /// </summary>
    public bool KeyAccountsTruncated { get; set; }

    /// <summary>
    /// Security 與 RDP 事件從全部訊息抽出的完整來源 IP 集合（去重且保持出現順序，封頂 200 個）。
    /// 非 Security／RDP 頻道為 null。
    /// 這是資料契約（供跨日關聯比對來源 IP 使用），KeyDetails 是顯示字串，兩者不可互相取代
    /// ——顯示字串只列前 5 個，第 6 個以後的 IP 在那裡永遠看不到。
    /// </summary>
    public List<string>? KeyIps { get; set; }

    /// <summary><see cref="KeyIps"/> 是否因達到封頂上限（200 個）而截斷。</summary>
    public bool KeyIpsTruncated { get; set; }

    /// <summary>登入失敗事件（4625／4771／Linux ssh 認證失敗）的結構化明細，
    /// 依 (帳號, 來源, 登入類型, 失敗原因) 分組計數。非登入失敗簽章為 null。
    /// **封頂 50 組**，被截掉的部分見 <see cref="LoginFailureTotalCount"/>／
    /// <see cref="LoginFailureDetailsTruncated"/>。</summary>
    public List<LoginFailureDetail>? LoginFailureDetails { get; set; }

    /// <summary>
    /// 截斷前的明細總次數。<see cref="LoginFailureDetails"/> 封頂 50 組時被丟掉的都是尾巴小組，
    /// 判定若拿保留下來的組數當分母，集中度會被系統性抬高——本來分散（多帳號多來源＝攻擊形狀）
    /// 的簽章會因此被誤判成殘留而靜音，是往「少報攻擊」的方向錯。集中度一律用這個值當分母。
    /// </summary>
    public int LoginFailureTotalCount { get; set; }

    /// <summary>明細是否因封頂而截斷——截斷時不足以判斷集中度，判定端一律不標殘留。</summary>
    public bool LoginFailureDetailsTruncated { get; set; }

    /// <summary>true = 此登入失敗簽章判定為「疑似殘留憑證重試」（機械性重複，非攻擊）。
    /// 由 ResidualCredentialDetector 填入，判定依據見 ResidualCredentialBasis。</summary>
    public bool ResidualCredentialRetry { get; set; }

    /// <summary>殘留憑證重試的判定依據白話說明（供畫面與報告呈現），未命中為 null。</summary>
    public string? ResidualCredentialBasis { get; set; }

    // 以下由 KnownIssueCatalog.Classify 填入
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueCategory Category { get; set; } = IssueCategory.Other;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueSeverity Severity { get; set; } = IssueSeverity.Low;

    /// <summary>
    /// 命中即列為高風險日（docs/archive/HISTORY.md #1，B1 三級化）：由
    /// <see cref="KnownIssueCatalog.Classify"/> 依命中規則的旗標帶入，或由
    /// <see cref="TrendAnalyzer"/> 在 High 嚴重度問題頻率上升時設為 true
    /// （舊制下這種情況會把嚴重度升到 Critical，直接讓當天判定為高風險日；
    /// 三級化後嚴重度封頂 High，改用這個旗標達成同樣效果——LogAnalysisService.ComputeRuleBasedRisk
    /// 據此判定高風險日，行為與三級化前完全一致）。
    /// </summary>
    public bool ElevatesDayRisk { get; set; }

    /// <summary>命中規則表時的已知問題說明，未命中為 null</summary>
    public string? KnownIssue { get; set; }

    /// <summary>命中規則的穩定 Id（KnownIssueRule.Id），未命中為 null。落紀錄供未來管理頁的
    /// 頻率報表與抑制比對使用（用 Id 查詢，不依賴 (Source, EventId) 反推，規則演進後仍對得上）</summary>
    public string? RuleId { get; set; }

    /// <summary>true = 此簽章命中的規則已被本機的 suppressions 設定抑制——只影響「要不要吵」
    /// （通知、風險升級），偵測與紀錄照常，見 docs/RULES-SPEC.md 的語意邊界</summary>
    public bool Suppressed { get; set; }

    // 以下由 TrendAnalyzer.Apply 填入（與歷史紀錄比對後的頻率趨勢）
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueTrend Trend { get; set; } = IssueTrend.Unknown;

    /// <summary>前一日發生次數；前一日有紀錄但無此事件為 0，完全無前一日紀錄為 null</summary>
    public int? PreviousDayCount { get; set; }

    /// <summary>近期歷史中有出現的日子的平均每日次數，從未出現為 null</summary>
    public double? HistoryDailyAverage { get; set; }

    /// <summary>近期歷史中出現過的天數</summary>
    public int DaysSeenInHistory { get; set; }
}

internal static class LogAggregator
{
    private static readonly Regex Ipv4Regex = new(@"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Security 與 RDP 事件訊息中帳號欄位的標籤（中英文系統皆支援）。
    /// "Account Name:" 為 Security 4625/4624；"User:" 為 TerminalServices 工作階段事件（21/25/1149）。
    /// </summary>
    private static readonly string[] AccountLabels = { "Account Name:", "帳戶名稱:", "帳戶名稱：", "User:", "使用者:", "使用者：" };

    /// <summary>Linux 事件的 LogName（docs/LINUX-RULES.md §131-146 定案）——sentinel 端映射器
    /// 與這裡的分組/分類判斷都用這個常數，避免兩處字面值漂移。</summary>
    internal const string LinuxLogName = "Linux";

    /// <summary>
    /// 依 (LogName, Source, EventId, EntryType, EventKey) 五元組分組統計並用規則表分類。
    /// 聚合是小模型策略的核心：把上千筆原始 log 壓成數十行統計，AI 只看摘要不看原文。
    /// 上限 50：超出主 prompt 呈現上限的部分會走前置掃描（分批給 AI 篩選），
    /// 不會直接消失，所以這裡不需要砍太兇；50 之後的極端尾巴才截斷。
    ///
    /// EventKey（第五段，docs/archive/FEEDBACK-12-PLAN.md §4.2）：Windows 事件恆空字串，
    /// 既有四元組鍵字串完全不變。Linux 事件在分組**之前**逐事件呼叫
    /// <see cref="KnownIssueCatalog.FindLinuxRule"/>——訊息全文比對（MessagePatterns）
    /// 是這裡唯一能拿到完整訊息的地方，聚合後只剩截斷過的 SampleMessages，先比對後聚合才不漏。
    /// </summary>
    public static List<LogIssueSignature> Aggregate(List<EventLogEntryData> logs, int top = 50)
    {
        var signatures = logs
            .GroupBy(GroupKeyFor)
            .Select(g =>
            {
                var distinctMessages = g
                    .Select(e => TextTruncation.Truncate(CleanMessage(e.Message), 200))
                    .Distinct()
                    .ToList();

                var loginFailures = ExtractLoginFailureDetailsForGroup(g.Key, g.Select(e => e.Message));
                var securityDetails = ShouldExtractKeyDetails(g.Key.LogName)
                    ? ExtractSecurityDetails(g.Select(e => e.Message))
                    : default;

                return new LogIssueSignature
                {
                    LogName = g.Key.LogName,
                    Source = g.Key.Source,
                    EventId = g.Key.EventId,
                    EntryType = g.Key.EntryType,
                    EventKey = g.Key.EventKey,
                    Count = g.Count(),
                    FirstSeen = g.Min(e => e.TimeGenerated).ToString("HH:mm"),
                    LastSeen = g.Max(e => e.TimeGenerated).ToString("HH:mm"),
                    SampleMessages = distinctMessages.Take(3).ToList(),
                    DistinctMessageCount = distinctMessages.Count,
                    KeyDetails = securityDetails.KeyDetails,
                    KeyAccounts = securityDetails.KeyAccounts,
                    KeyAccountsTruncated = securityDetails.KeyAccountsTruncated,
                    KeyIps = securityDetails.KeyIps,
                    KeyIpsTruncated = securityDetails.KeyIpsTruncated,
                    LoginFailureDetails = loginFailures.Details,
                    LoginFailureTotalCount = loginFailures.TotalCount,
                    LoginFailureDetailsTruncated = loginFailures.Truncated
                };
            })
            .ToList();

        foreach (var sig in signatures)
        {
            if (sig.LogName.Equals(LinuxLogName, StringComparison.OrdinalIgnoreCase))
            {
                KnownIssueCatalog.ClassifyLinux(sig);
            }
            else
            {
                KnownIssueCatalog.Classify(sig);
            }
        }

        // 嚴重度優先、次數其次，確保截斷 top N 時重大問題一定被保留
        return signatures
            .OrderByDescending(s => s.Severity)
            .ThenByDescending(s => s.Count)
            .Take(top)
            .ToList();
    }

    /// <summary>
    /// 分組鍵計算——Linux 事件的規則比對必須在這裡（分組之前）做，見 <see cref="Aggregate"/> 的說明。
    /// internal 而非 private：<see cref="RiskyEventSelector"/> 從原始 <see cref="EventLogEntryData"/>
    /// 反查對應的簽章時要用同一套鍵計算，否則 ssh-bruteforce／ssh-accept 這類「同 program
    /// 不同規則」的原始事件會在風險 log 暫存裡混在一起（兩個簽章各自撈到同一批原始事件）。
    /// </summary>
    internal static (string LogName, string Source, int EventId, EventLogEntryType EntryType, string EventKey) GroupKeyFor(EventLogEntryData l)
    {
        if (!l.LogName.Equals(LinuxLogName, StringComparison.OrdinalIgnoreCase))
        {
            return (l.LogName, l.Source, l.EventId, l.EntryType, string.Empty);
        }

        var rule = KnownIssueCatalog.FindLinuxRule(l.Source, eventName: null, l.Message);
        return (l.LogName, l.Source, l.EventId, l.EntryType, rule?.Id ?? string.Empty);
    }

    /// <summary>
    /// 從 Security 事件的完整訊息（未截斷）彙總相異的帳號名稱與來源 IP。
    /// 範例訊息截 200 字常截不到這些欄位，但「50 次登入失敗是打同一帳號還是掃多帳號、
    /// 來自單一 IP 還是多個 IP」正是入侵分析最關鍵的判讀依據。公開給
    /// <see cref="LogAnalysisService"/> 重用，用來比對 4625（失敗）與 4624（成功）是否為同一組帳號/IP。
    /// </summary>
    public static (HashSet<string> Accounts, HashSet<string> Ips) ExtractAccountsAndIps(IEnumerable<string> rawMessages)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<string>();

        foreach (var message in rawMessages)
        {
            foreach (Match m in Ipv4Regex.Matches(message))
            {
                if (m.Value != "127.0.0.1" && m.Value != "0.0.0.0")
                {
                    ips.Add(m.Value);
                }
            }

            foreach (var line in message.Split('\n'))
            {
                foreach (var label in AccountLabels)
                {
                    int idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0)
                    {
                        continue;
                    }

                    var value = line[(idx + label.Length)..].Trim();
                    if (value.Length == 0 || value == "-" || value.EndsWith("$"))
                    {
                        continue;
                    }

                    accounts.Add(value);

                    // RDP 的 "User:" 是 DOMAIN\user 格式，4625 的 "Account Name:" 常是純帳號——
                    // 同時收錄 \ 後的純帳號，兩種來源的帳號才對得上（否則交集永遠落空）
                    int slash = value.LastIndexOf('\\');
                    if (slash >= 0 && slash + 1 < value.Length)
                    {
                        var bare = value[(slash + 1)..];
                        if (bare.Length > 0 && !bare.EndsWith("$"))
                        {
                            accounts.Add(bare);
                        }
                    }
                }
            }
        }

        return (accounts, ips);
    }

    /// <summary>
    /// 哪些頻道要抽取帳號/IP 彙總（KeyDetails）：Security 與兩個 RDP TerminalServices 頻道。
    /// RDP 也要，因為【暴力破解→RDP 得手】的跨日比對靠歷史簽章裡存的 KeyDetails IP 集合對照。
    ///
    /// 刻意不含 Linux（docs/archive/FEEDBACK-12-PLAN.md §4.2）：這裡的帳號/IP 抽取邏輯
    /// （<see cref="ExtractAccountsAndIps"/>）是為 Windows「Account Name:」／「User:」等固定
    /// 標籤設計的，Linux syslog 訊息沒有這種標籤格式。sshd 失敗/成功登入的帳號與來源 IP
    /// 解析是 §4.5（SSH 攻擊鏈關聯）獨立的正則，不借用這裡。
    /// </summary>
    private static bool ShouldExtractKeyDetails(string logName) =>
        logName.Equals(ChannelCatalog.SecurityChannel, StringComparison.OrdinalIgnoreCase) ||
        logName.Equals(ChannelCatalog.RdpLsmChannel, StringComparison.OrdinalIgnoreCase) ||
        logName.Equals(ChannelCatalog.RdpRcmChannel, StringComparison.OrdinalIgnoreCase);

    /// <summary>結構化帳號集合封頂上限（200 個）</summary>
    internal const int KeyAccountsCap = 200;

    private static (string? KeyDetails, List<string>? KeyAccounts, bool KeyAccountsTruncated, List<string>? KeyIps, bool KeyIpsTruncated) ExtractSecurityDetails(IEnumerable<string> rawMessages)
    {
        var (accounts, ips) = ExtractAccountsAndIps(rawMessages);

        var parts = new List<string>();
        if (accounts.Count > 0)
        {
            parts.Add($"相關帳號({accounts.Count}個): {string.Join(", ", accounts.Take(5))}{(accounts.Count > 5 ? "…" : "")}");
        }
        if (ips.Count > 0)
        {
            parts.Add($"來源IP({ips.Count}個): {string.Join(", ", ips.Take(5))}{(ips.Count > 5 ? "…" : "")}");
        }

        var keyDetails = parts.Count > 0 ? string.Join("；", parts) : null;
        var truncated = accounts.Count > KeyAccountsCap;
        var keyAccounts = accounts.Take(KeyAccountsCap).ToList();
        var ipsTruncated = ips.Count > KeyAccountsCap;
        var keyIps = ips.Take(KeyAccountsCap).ToList();

        return (keyDetails, keyAccounts, truncated, keyIps, ipsTruncated);
    }

    private static string CleanMessage(string s) =>
        string.Join(' ', s.Split('\r', '\n', '\t').Where(p => p.Length > 0));

    private static readonly string[] LogonTypeLabels = { "Logon Type:", "Logon Type：", "登入類型:", "登入類型：" };
    private static readonly string[] SubStatusLabels = { "Sub Status:", "Sub Status：", "SubStatus:", "SubStatus：", "子狀態:", "子狀態：" };
    private static readonly string[] StatusLabels = { "Status:", "Status：", "狀態:", "狀態：" };
    private static readonly string[] FailureCodeLabels = { "Failure Code:", "Failure Code：", "失敗碼:", "失敗碼：" };
    private static readonly string[] WorkstationLabels = { "Workstation Name:", "Workstation Name：", "工作站名稱:", "工作站名稱：" };
    private static readonly string[] SourceNetworkAddressLabels = { "Source Network Address:", "Source Network Address：", "來源網路位址:", "來源網路位址：" };
    private static readonly string[] ClientAddressLabels = { "Client Address:", "Client Address：", "用戶端位址:", "用戶端位址：" };

    private static readonly string[] SubStatusExcludeSubstrings = { "Sub Status", "SubStatus", "子狀態" };

    private static bool IsLoginFailureEvent(int eventId) => eventId == 4625 || eventId == 4771;

    /// <summary>
    /// 從單筆登入失敗事件（4625／4771）完整訊息中抽取結構化欄位（A1）。
    /// </summary>
    public static LoginFailureDetail ExtractLoginFailureDetail(int eventId, string rawMessage)
    {
        var account = ExtractLastValue(rawMessage, AccountLabels) ?? string.Empty;
        var isComputer = account.EndsWith('$');

        if (eventId == 4771)
        {
            var rawClientAddress = ExtractLastValue(rawMessage, ClientAddressLabels) ?? string.Empty;
            if (string.IsNullOrEmpty(rawClientAddress))
            {
                rawClientAddress = ExtractLastValue(rawMessage, SourceNetworkAddressLabels) ?? string.Empty;
            }
            var source = CleanClientAddress(rawClientAddress);

            var rawFailureCode = ExtractLastValue(rawMessage, FailureCodeLabels) ?? string.Empty;
            var reasonCode = Normalize4771ReasonCode(rawFailureCode);

            return new LoginFailureDetail
            {
                Account = account,
                IsComputerAccount = isComputer,
                Source = source,
                LogonType = null,
                ReasonCode = reasonCode,
                Count = 1
            };
        }
        else
        {
            var rawLogonType = ExtractLastValue(rawMessage, LogonTypeLabels);
            int? logonType = int.TryParse(rawLogonType, out var lt) ? lt : null;

            var subStatus = ExtractLastValue(rawMessage, SubStatusLabels);
            string rawStatus;
            if (!string.IsNullOrWhiteSpace(subStatus) && subStatus != "0x0" && subStatus != "0" && subStatus != "0x00000000")
            {
                rawStatus = subStatus;
            }
            else
            {
                rawStatus = ExtractLastValue(rawMessage, StatusLabels, SubStatusExcludeSubstrings) ?? string.Empty;
            }
            var reasonCode = Normalize4625ReasonCode(rawStatus);

            var workstation = ExtractLastValue(rawMessage, WorkstationLabels) ?? string.Empty;
            var netAddress = ExtractLastValue(rawMessage, SourceNetworkAddressLabels) ?? string.Empty;

            var source = !string.IsNullOrEmpty(workstation) ? workstation : CleanClientAddress(netAddress);

            return new LoginFailureDetail
            {
                Account = account,
                IsComputerAccount = isComputer,
                Source = source,
                LogonType = logonType,
                ReasonCode = reasonCode,
                Count = 1
            };
        }
    }

    private static (List<LoginFailureDetail>? Details, int TotalCount, bool Truncated) ExtractLoginFailureDetailsForGroup(
        (string LogName, string Source, int EventId, EventLogEntryType EntryType, string EventKey) key,
        IEnumerable<string> rawMessages)
    {
        if (key.LogName.Equals(LinuxLogName, StringComparison.OrdinalIgnoreCase))
        {
            return LinuxAuthParser.LoginFailureRuleIds.Contains(key.EventKey)
                ? ExtractLinuxLoginFailureDetails(rawMessages)
                : (null, 0, false);
        }

        return IsLoginFailureEvent(key.EventId)
            ? ExtractLoginFailureDetails(key.EventId, rawMessages)
            : (null, 0, false);
    }

    /// <summary>
    /// 將多筆登入失敗明細依 (Account, Source, LogonType, ReasonCode) 分組計數，
    /// 帳號與來源比對不分大小寫，依 Count 降冪排序，最多保留 50 筆。
    /// Windows 與 Linux 共用此分組邏輯。
    /// </summary>
    /// <summary>封頂組數——超過的尾巴小組會被丟棄，總量與截斷旗標另外保留（見 LoginFailureTotalCount）。
    /// 兩個消費端對截斷的處理刻意不對稱：殘留判定（ResidualCredentialDetector）截斷時**不判定**
    /// （分母縮水會把攻擊形狀誤判成集中，往少報攻擊方向錯）；密碼噴灑（PasswordSprayDetector）
    /// 照常判定——截斷代表 ≥50 組、帳號數遠超噴灑門檻，只會更容易命中，方向是多報攻擊、安全側。</summary>
    internal const int LoginFailureDetailCap = 50;

    private static (List<LoginFailureDetail> Details, int TotalCount, bool Truncated)
        GroupAndAggregateFailureDetails(IEnumerable<LoginFailureDetail> details)
    {
        var groups = new Dictionary<(string AccountKey, string SourceKey, int? LogonType, string ReasonCode), LoginFailureDetail>();

        foreach (var detail in details)
        {
            var key = (
                detail.Account.ToUpperInvariant(),
                detail.Source.ToUpperInvariant(),
                detail.LogonType,
                detail.ReasonCode
            );

            if (groups.TryGetValue(key, out var existing))
            {
                existing.Count += detail.Count;
            }
            else
            {
                groups[key] = new LoginFailureDetail
                {
                    Account = detail.Account,
                    IsComputerAccount = detail.IsComputerAccount,
                    Source = detail.Source,
                    LogonType = detail.LogonType,
                    ReasonCode = detail.ReasonCode,
                    Count = detail.Count
                };
            }
        }

        var totalCount = groups.Values.Sum(d => d.Count);
        var truncated = groups.Count > LoginFailureDetailCap;
        var kept = groups.Values
            .OrderByDescending(d => d.Count)
            .Take(LoginFailureDetailCap)
            .ToList();
        return (kept, totalCount, truncated);
    }

    /// <summary>
    /// 從 Linux 登入/認證失敗事件的多筆原始訊息抽取結構化明細（A2）。
    /// 依 (Account, Source, LogonType, ReasonCode) 分組計數，依 Count 降冪排序，最多保留 50 筆。
    /// </summary>
    public static (List<LoginFailureDetail> Details, int TotalCount, bool Truncated)
        ExtractLinuxLoginFailureDetails(IEnumerable<string> rawMessages)
    {
        var items = new List<LoginFailureDetail>();
        foreach (var message in rawMessages)
        {
            if (LinuxAuthParser.TryParseAuthFailure(message, out var user, out var ip, out var reasonCode))
            {
                items.Add(new LoginFailureDetail
                {
                    Account = user,
                    IsComputerAccount = false,
                    Source = ip,
                    LogonType = null,
                    ReasonCode = reasonCode,
                    Count = 1
                });
            }
        }

        return GroupAndAggregateFailureDetails(items);
    }

    /// <summary>
    /// 從登入失敗事件的多筆原始訊息彙總結構化明細，依 (Account, Source, LogonType, ReasonCode) 分組計數，
    /// 依 Count 降冪排序，最多保留 50 筆。
    /// </summary>
    public static (List<LoginFailureDetail> Details, int TotalCount, bool Truncated)
        ExtractLoginFailureDetails(int eventId, IEnumerable<string> rawMessages) =>
        GroupAndAggregateFailureDetails(rawMessages.Select(m => ExtractLoginFailureDetail(eventId, m)));

    private static string Normalize4625ReasonCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "-" || raw == "0x0" || raw == "0" || raw == "0x00000000")
        {
            return string.Empty;
        }

        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "0xc000006a" => "bad_password",
            "0xc0000071" => "password_expired",
            "0xc0000234" => "account_locked",
            "0xc0000072" => "account_disabled",
            "0xc0000064" => "unknown_user",
            "0xc000006f" => "logon_time_restriction",
            "0xc0000070" => "workstation_restriction",
            "0xc0000193" => "account_expired",
            _ => trimmed
        };
    }

    private static string Normalize4771ReasonCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "-" || raw == "0x0" || raw == "0")
        {
            return string.Empty;
        }

        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "0x18" => "bad_password",
            "0x17" => "password_expired",
            "0x12" => "account_locked_or_disabled",
            "0x6" or "0x06" => "unknown_user",
            _ => trimmed
        };
    }

    private static string CleanClientAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "-")
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var match = Ipv4Regex.Match(trimmed);
        if (match.Success)
        {
            return match.Value;
        }

        if (trimmed.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = trimmed[7..].Trim();
            return stripped == "-" ? string.Empty : stripped;
        }

        return trimmed == "-" ? string.Empty : trimmed;
    }

    private static string? ExtractLastValue(string message, string[] labels, string[]? excludeSubstrings = null)
    {
        string? last = null;
        foreach (var line in message.Split('\n'))
        {
            if (excludeSubstrings != null && excludeSubstrings.Any(ex => line.Contains(ex, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var label in labels)
            {
                int idx = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                var value = line[(idx + label.Length)..].Trim();
                if (value == "-")
                {
                    last = string.Empty;
                }
                else if (value.Length > 0)
                {
                    last = value;
                }
                break;
            }
        }
        return last;
    }
}

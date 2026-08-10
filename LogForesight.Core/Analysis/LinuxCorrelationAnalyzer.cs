using System.Text.RegularExpressions;

namespace LogForesight.Core.Analysis;

/// <summary>
/// Linux 主機的跨事件關聯分析（docs/FEEDBACK-12-PLAN.md §4.5，批 4C）：獨立於
/// <see cref="CorrelationAnalyzer"/>（Windows Event ID 群組比對，兩者比對機制完全不同，
/// 硬塞進同一支會讓兩套規則互相干擾）。目前只有一條【SSH 破解得手】關聯，與 Windows
/// 【破解得手】同構——同日大量 SSH 登入失敗（達 <see cref="KnownIssueRule.CountThreshold"/>）
/// 後，相同帳號或來源 IP 出現成功登入。
///
/// **此環境是 Full Text Parser collector，沒有 <c>sun</c>／<c>sip</c> 這類結構化帳號/IP 欄位**
/// （輪 A 實證，見 docs/NETIQ-API-REFERENCE.md §4a）——帳號/IP 只能從 <c>msg</c> 文字用正則
/// 抽取，抽取樣本與格式由診斷輪 B 第四次 probe（8g）的真實 sshd 事件定案（見下方 regex 說明）。
/// </summary>
internal static class LinuxCorrelationAnalyzer
{
    /// <summary>與 <see cref="KnownIssueSeed"/> 種子的 Id 對應——drift 風險由
    /// <c>LinuxCorrelationAnalyzerTests</c> 的規則對齊測試把關（比照 Windows 版
    /// <see cref="CorrelationAnalyzer"/> 的 <c>CorrelationAnalyzerRuleAlignmentTests</c> precedent，
    /// 這裡只有兩個 Id、不必另開一個測試類別）。</summary>
    internal const string SshBruteforceRuleId = "builtin-linux-ssh-bruteforce";
    internal const string SshAcceptRuleId = "builtin-linux-ssh-accept";

    /// <summary>與規則表的 CountThreshold 數值巧合相同（10），但刻意各自獨立宣告——
    /// 這裡決定的是「值得跨事件關聯」的門檻，規則表的 CountThreshold 決定的是「嚴重度是否打折」，
    /// 兩者語意不同，同 Windows 版 <see cref="CorrelationAnalyzer"/> 對 4625 門檻的既有處理方式
    /// （硬編，不去讀規則表）。</summary>
    private const int HeavyBruteforceThreshold = 10;

    /// <summary>
    /// 失敗登入樣本格式（診斷輪 B 第四次 probe，8g，2026-08-07 真實樣本）：
    /// <c>Failed password for invalid user 1838651 from 10.225.2.219 port 54500 ssh2</c>。
    /// <c>invalid user </c> 是可選段（無效帳號才有，合法帳號密碼打錯不會有這段）。
    /// **不錨定行首**——輪 B 實證部分 sshd 事件的 msg 沒有 <c>program[pid]:</c> 前綴
    /// （<see cref="SentinelEventMapper"/> 保留原文，前綴存在與否不影響這裡的比對）。
    /// </summary>
    private static readonly Regex FailedPasswordRegex = new(
        @"Failed password for (?:invalid user )?(?<user>\S+) from (?<ip>\d{1,3}(?:\.\d{1,3}){3}) port \d+",
        RegexOptions.Compiled);

    /// <summary>
    /// 成功登入樣本格式：OpenSSH 上游標準模板。四輪 probe 皆未直接取樣到這一行（8f 只見
    /// pam_unix session opened／SFTP opendir 這類周邊訊息）——若此環境的 sshd LogLevel
    /// 設定不記錄 Accepted 行，這條 regex 永遠比對不到、<see cref="SshAcceptRuleId"/> 簽章
    /// 恆零，關聯因此永不觸發：這是**誠實的沉默**（沒有可信資料就不下結論），不是誤報，
    /// 試點階段用真實環境核對即可，不值得為了這一行再跑一輪 probe。
    /// </summary>
    private static readonly Regex AcceptedRegex = new(
        @"Accepted (?:password|publickey) for (?<user>\S+) from (?<ip>\d{1,3}(?:\.\d{1,3}){3}) port \d+",
        RegexOptions.Compiled);

    /// <param name="issues">已聚合分類的當日簽章（判斷是否達門檻用 Count，不逐事件解析）。</param>
    /// <param name="logs">當日原始事件（regex 逐事件解析帳號/IP 用；聚合後的 SampleMessages
    /// 只留 3 則，涵蓋不了整批事件的帳號/IP 交集判定）。</param>
    public static List<CorrelationFinding> Detect(List<LogIssueSignature> issues, List<EventLogEntryData> logs)
    {
        var findings = new List<CorrelationFinding>();

        var bruteforceSignature = issues.FirstOrDefault(i => i.EventKey == SshBruteforceRuleId);
        var acceptSignature = issues.FirstOrDefault(i => i.EventKey == SshAcceptRuleId);
        bool heavyBruteforce = bruteforceSignature != null && bruteforceSignature.Count >= HeavyBruteforceThreshold;

        if (!heavyBruteforce || acceptSignature == null)
        {
            return findings; // 沒有錨點（同 Windows 版語意：無大量失敗或無成功登入，不臆測）
        }

        var bruteforceEvents = FilterByEventKey(logs, SshBruteforceRuleId);
        var acceptEvents = FilterByEventKey(logs, SshAcceptRuleId);

        var (bruteforceMatches, bruteforceParseFailures) = ExtractMatches(bruteforceEvents, FailedPasswordRegex);
        var (acceptMatches, acceptParseFailures) = ExtractMatches(acceptEvents, AcceptedRegex);

        var bruteforceUsers = bruteforceMatches.Select(m => m.User).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bruteforceIps = bruteforceMatches.Select(m => m.Ip).ToHashSet();
        var overlapUsers = acceptMatches.Select(m => m.User).Where(u => bruteforceUsers.Contains(u))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var overlapIps = acceptMatches.Select(m => m.Ip).Where(ip => bruteforceIps.Contains(ip))
            .Distinct().ToList();

        if (overlapUsers.Count > 0 || overlapIps.Count > 0)
        {
            var parts = new List<string>();
            if (overlapUsers.Count > 0) parts.Add($"帳號：{string.Join("、", overlapUsers.Take(5))}");
            if (overlapIps.Count > 0) parts.Add($"來源IP：{string.Join("、", overlapIps.Take(5))}");

            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.High, ElevatesDayRisk = true,
                Description = $"【SSH 破解得手】同日大量 SSH 登入失敗（x{bruteforceSignature!.Count}）後，" +
                              $"相同帳號/IP 出現成功登入（{string.Join("；", parts)}）——暴力破解極可能已得手，" +
                              "應立即鎖定該帳號、強制改密碼並全面稽查其後續活動"
            });
            return findings;
        }

        // 個別事件解析失敗時降級（docs/FEEDBACK-12-PLAN.md §4.5）：精確比對沒有交集，但只要
        // 任一邊有解析失敗的事件，就不能把「沒交集」當成「確認沒關聯」——正則格式可能已經
        // 跟不上這批事件的實際寫法，落回主機級粗版比對，明講降級原因，不因格式漂移靜默漏報。
        if (bruteforceParseFailures > 0 || acceptParseFailures > 0)
        {
            findings.Add(new CorrelationFinding
            {
                Severity = IssueSeverity.Medium, ElevatesDayRisk = false,
                Description = $"【SSH 破解得手？】同日大量 SSH 登入失敗（x{bruteforceSignature!.Count}）與成功登入" +
                              $"（x{acceptSignature.Count}）同時存在，但部分事件無法解析帳號/IP" +
                              $"（失敗面 {bruteforceParseFailures} 筆／成功面 {acceptParseFailures} 筆解析不出），" +
                              "已降級為主機級比對，無法確認是否為同一來源——建議人工核對原始訊息"
            });
        }

        return findings;
    }

    private static List<EventLogEntryData> FilterByEventKey(List<EventLogEntryData> logs, string eventKey) =>
        logs.Where(l => LogAggregator.GroupKeyFor(l).EventKey == eventKey).ToList();

    private static (List<(string User, string Ip)> Matches, int ParseFailures) ExtractMatches(
        List<EventLogEntryData> events, Regex pattern)
    {
        var matches = new List<(string User, string Ip)>();
        var failures = 0;

        foreach (var evt in events)
        {
            var match = pattern.Match(evt.Message);
            if (match.Success)
            {
                matches.Add((match.Groups["user"].Value, match.Groups["ip"].Value));
            }
            else
            {
                failures++;
            }
        }

        return (matches, failures);
    }
}

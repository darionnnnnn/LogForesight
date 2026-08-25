using System.Text.RegularExpressions;

namespace LogForesight.Core.Analysis;

/// <summary>
/// Linux 登入與提權認證失敗訊息的共用解析器（A2）。
/// 集中管理 sshd、pam_unix、sudo 等 Linux 服務的登入/認證失敗訊息正則樣式與規則 Id，
/// 供 <see cref="LogAggregator"/> 抽取結構化明細及 <see cref="LinuxCorrelationAnalyzer"/> 跨事件關聯比對複用。
/// </summary>
internal static class LinuxAuthParser
{
    internal const string SudoAuthFailureRuleId = "builtin-linux-sudo-auth-failure";
    internal const string SuAuthFailureRuleId = "builtin-linux-su-auth-failure";

    /// <summary>
    /// Linux 登入失敗家族規則 Id 集合（單一出處）。
    /// 包含 ssh 暴力破解、sudo 認證失敗、su 認證失敗三條規則。
    /// </summary>
    internal static readonly HashSet<string> LoginFailureRuleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        LinuxCorrelationAnalyzer.SshBruteforceRuleId,
        SudoAuthFailureRuleId,
        SuAuthFailureRuleId
    };

    /// <summary>
    /// 失敗登入樣本格式（診斷輪 B 第四次 probe，8g，2026-08-07 真實樣本）：
    /// <c>Failed password for invalid user 1838651 from 10.225.2.219 port 54500 ssh2</c>。
    /// <c>invalid user </c> 是可選段（無效帳號才有，合法帳號密碼打錯不會有這段）。
    /// **不錨定行首**——輪 B 實證部分 sshd 事件的 msg 沒有 <c>program[pid]:</c> 前綴
    /// （<see cref="SentinelEventMapper"/> 保留原文，前綴存在與否不影響這裡的比對）。
    /// </summary>
    internal static readonly Regex FailedPasswordRegex = new(
        @"Failed password for (?:invalid user )?(?<user>\S+) from (?<ip>\d{1,3}(?:\.\d{1,3}){3}) port \d+",
        RegexOptions.Compiled);

    /// <summary>
    /// 成功登入樣本格式：OpenSSH 上游標準模板。四輪 probe 皆未直接取樣到這一行（8f 只見
    /// pam_unix session opened／SFTP opendir 這類周邊訊息）——若此環境的 sshd LogLevel
    /// 設定不記錄 Accepted 行，這條 regex 永遠比對不到、<see cref="LinuxCorrelationAnalyzer.SshAcceptRuleId"/> 簽章
    /// 恆零，關聯因此永不觸發：這是**誠實的沉默**（沒有可信資料就不下結論），不是誤報，
    /// 試點階段用真實環境核對即可，不值得為了這一行再跑一輪 probe。
    /// </summary>
    internal static readonly Regex AcceptedRegex = new(
        @"Accepted (?:password|publickey) for (?<user>\S+) from (?<ip>\d{1,3}(?:\.\d{1,3}){3}) port \d+",
        RegexOptions.Compiled);

    private static readonly Regex InvalidUserRegex = new(
        @"\bInvalid user (?<user>\S+) from (?<ip>\d{1,3}(?:\.\d{1,3}){3})",
        RegexOptions.Compiled);

    private static readonly Regex PamUnixAuthRegex = new(
        @"\bpam_unix(?:\([^)]*\))?:\s*authentication failure;?",
        RegexOptions.Compiled);

    private static readonly Regex PamUserRegex = new(
        @"\buser=(?<user>\S+)",
        RegexOptions.Compiled);

    private static readonly Regex PamRhostRegex = new(
        @"\brhost=(?<rhost>\S*)",
        RegexOptions.Compiled);

    private static readonly Regex SudoIncorrectPasswordRegex = new(
        @"(?:^|:\s*)(?<user>[^\s:]+)\s*:\s*\d+\s+incorrect password attempts?",
        RegexOptions.Compiled);

    /// <summary>
    /// 嘗試解析 Linux 認證/登入失敗訊息，依序比對 (a) sshd Failed password、(b) sshd Invalid user、
    /// (c) pam_unix authentication failure、(d) sudo incorrect password attempt 四種樣式。
    /// </summary>
    public static bool TryParseAuthFailure(string message, out string user, out string ip, out string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            user = string.Empty;
            ip = string.Empty;
            reasonCode = string.Empty;
            return false;
        }

        // (a) sshd 失敗密碼（含 invalid user 可選段）
        var mFailed = FailedPasswordRegex.Match(message);
        if (mFailed.Success)
        {
            user = mFailed.Groups["user"].Value;
            ip = mFailed.Groups["ip"].Value;
            reasonCode = mFailed.Value.Contains("invalid user", StringComparison.OrdinalIgnoreCase)
                ? "unknown_user"
                : "bad_password";
            return true;
        }

        // (b) sshd 無效帳號（無 Failed password 的獨立行）
        var mInvalid = InvalidUserRegex.Match(message);
        if (mInvalid.Success)
        {
            user = mInvalid.Groups["user"].Value;
            ip = mInvalid.Groups["ip"].Value;
            reasonCode = "unknown_user";
            return true;
        }

        // (c) pam_unix 認證失敗（sshd／sudo／su 共用格式）
        if (PamUnixAuthRegex.IsMatch(message))
        {
            var userMatches = PamUserRegex.Matches(message);
            if (userMatches.Count > 0)
            {
                user = userMatches[^1].Groups["user"].Value;
                var rhostMatch = PamRhostRegex.Match(message);
                ip = rhostMatch.Success ? rhostMatch.Groups["rhost"].Value : string.Empty;
                reasonCode = "bad_password";
                return true;
            }
        }

        // (d) sudo incorrect password attempt
        var mSudo = SudoIncorrectPasswordRegex.Match(message);
        if (mSudo.Success)
        {
            user = mSudo.Groups["user"].Value;
            ip = string.Empty;
            reasonCode = "bad_password";
            return true;
        }

        user = string.Empty;
        ip = string.Empty;
        reasonCode = string.Empty;
        return false;
    }
}

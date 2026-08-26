using LogForesight.Core.Models;

namespace LogForesight.Core.Analysis;

/// <summary>
/// 疑似殘留憑證重試判定器（A3）。
/// 實務上大量登入失敗來自「使用動態密碼 (OTP) 登入主機後未登出，隔天密碼變更，
/// 殘留的 session／排程工作／磁碟機對應持續用舊密碼重試」，這些是機械性重複、非攻擊。
/// 採跨日確認制：第一天照常告警，同一 (Account, Source) 組合在近 7 天歷史中跨日重現才標為殘留，
/// 並將 High 嚴重度調降為 Medium、清除 ElevatesDayRisk。
/// </summary>
internal static class ResidualCredentialDetector
{
    private const double ConcentrationThreshold = 0.8;
    private const double MechanicalLogonTypeThreshold = 0.8;
    private const double SingleGroupConcentrationThreshold = 0.8;
    private const int HistoryWindowDays = 7;
    private const int MinDistinctDays = 2;

    /// <summary>
    /// 逐一檢查 issues 裡有 LoginFailureDetails 且非空的簽章，符合四個條件時標記為殘留憑證重試。
    /// </summary>
    public static void Mark(
        List<LogIssueSignature> issues,
        List<DailyAnalysisRecord> history,
        DateTime targetDate)
    {
        if (issues == null || issues.Count == 0)
        {
            return;
        }

        var residualAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一階段：登入失敗簽章的殘留憑證重試判定（既有四條件判定邏輯）
        foreach (var issue in issues)
        {
            if (issue.LoginFailureDetails == null || issue.LoginFailureDetails.Count == 0)
            {
                continue;
            }

            int totalDetailCount = issue.LoginFailureDetails.Sum(d => d.Count);
            if (totalDetailCount == 0)
            {
                continue;
            }

            var topGroup = issue.LoginFailureDetails
                .OrderByDescending(d => d.Count)
                .Take(2)
                .ToList();

            var largest = topGroup[0];
            if (string.IsNullOrWhiteSpace(largest.Account))
            {
                continue;
            }

            // 條件 1（失敗集中）：集中組的 Count 總和 ÷ 明細總數 ≥ 0.8
            int topGroupCountSum = topGroup.Sum(d => d.Count);
            if ((double)topGroupCountSum / totalDetailCount < ConcentrationThreshold)
            {
                continue;
            }

            // 條件 2（型態機械）：
            // - Windows 4625：集中組中 LogonType 為 3、4、5 其中之一的 Count 總和 ÷ 集中組 Count 總和 ≥ 0.8
            // - 其他（4771 與 Linux）：單一最大組的 Count ÷ 明細總數 ≥ 0.8
            if (issue.EventId == 4625)
            {
                int mechanicalCountSum = topGroup
                    .Where(d => d.LogonType is 3 or 4 or 5)
                    .Sum(d => d.Count);

                if ((double)mechanicalCountSum / topGroupCountSum < MechanicalLogonTypeThreshold)
                {
                    continue;
                }
            }
            else
            {
                if ((double)largest.Count / totalDetailCount < SingleGroupConcentrationThreshold)
                {
                    continue;
                }
            }

            // 條件 3（帳號非未知）：集中組中沒有任何一組的 ReasonCode 是 unknown_user
            bool hasUnknownUser = topGroup.Any(d =>
                string.Equals(d.ReasonCode, "unknown_user", StringComparison.OrdinalIgnoreCase));
            if (hasUnknownUser)
            {
                continue;
            }

            // 條件 4（跨日重現）：集中組中最大的那一組的 (Account, Source) 組合，
            // 在 history 裡近 7 天（含 targetDate 當天，即 [targetDate-6, targetDate]）的
            // 登入失敗明細中，出現在 ≥ 2 個不同日期。
            // targetDate 當天算 1 天，因此在歷史中至少要找到 ≥ 1 天。
            if (!HasCrossDayRecurrence(history, targetDate, largest.Account, largest.Source))
            {
                continue;
            }

            // 四條件皆滿足：標記為殘留憑證重試
            issue.ResidualCredentialRetry = true;
            issue.ResidualCredentialBasis = BuildBasis(issue, largest);
            residualAccounts.Add(largest.Account);

            if (issue.Severity == IssueSeverity.High)
            {
                issue.Severity = IssueSeverity.Medium;
                issue.ElevatesDayRisk = false;
            }
        }

        // 第二階段：4740 帳號鎖定與殘留帳號交叉比對（A4c）
        if (residualAccounts.Count == 0)
        {
            return;
        }

        foreach (var issue in issues)
        {
            if (issue.EventId != 4740)
            {
                continue;
            }

            var accounts = ExtractAccountsFromKeyDetails(issue.KeyDetails);
            if (accounts.Count == 0)
            {
                continue;
            }

            var matchedAccounts = accounts
                .Where(a => residualAccounts.Contains(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchedAccounts.Count == 0)
            {
                continue;
            }

            if (issue.Severity == IssueSeverity.High)
            {
                issue.Severity = IssueSeverity.Medium;
            }

            issue.ElevatesDayRisk = false;

            var displayAccounts = string.Join("、", matchedAccounts.Take(3));
            issue.ResidualCredentialBasis =
                $"可能由殘留憑證重試觸發：帳號 {displayAccounts} 當日有疑似殘留的重複登入失敗";
        }
    }

    private static bool HasCrossDayRecurrence(
        List<DailyAnalysisRecord>? history,
        DateTime targetDate,
        string account,
        string source)
    {
        if (history == null || history.Count == 0)
        {
            return false;
        }

        var startDate = targetDate.Date.AddDays(-(HistoryWindowDays - 1));
        var endDate = targetDate.Date;

        var matchingDates = new HashSet<DateTime>();

        foreach (var record in history)
        {
            var recordDate = record.Date.Date;
            if (recordDate < startDate || recordDate >= endDate)
            {
                // 排除 targetDate 當天：當天已由下方 +1 計入。重新分析已分析過的日期時
                // ReadRecent 會撈到該日的舊紀錄（Append 不刪舊列），不排除的話它會被當成
                // 「另一天」而讓首日就達到跨日門檻，跨日確認制形同虛設。
                continue;
            }

            if (record.TopIssues == null || record.TopIssues.Count == 0)
            {
                continue;
            }

            bool matchedInRecord = false;
            foreach (var histIssue in record.TopIssues)
            {
                if (histIssue.LoginFailureDetails == null || histIssue.LoginFailureDetails.Count == 0)
                {
                    continue;
                }

                foreach (var detail in histIssue.LoginFailureDetails)
                {
                    if (string.Equals(detail.Account, account, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(detail.Source, source, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedInRecord = true;
                        break;
                    }
                }

                if (matchedInRecord)
                {
                    break;
                }
            }

            if (matchedInRecord)
            {
                matchingDates.Add(recordDate);
            }
        }

        return (matchingDates.Count + 1) >= MinDistinctDays;
    }

    private static string BuildBasis(LogIssueSignature issue, LoginFailureDetail largest)
    {
        var account = largest.Account;
        var source = string.IsNullOrWhiteSpace(largest.Source) ? "來源不明" : largest.Source;
        var count = largest.Count;
        var reasonText = LoginFailureTextFormatter.FormatReason(largest.ReasonCode);

        string parenDetails;
        if (issue.EventId == 4625 && largest.LogonType.HasValue)
        {
            var typeText = LoginFailureTextFormatter.FormatLogonType(largest.LogonType.Value);
            parenDetails = $"{typeText}，{reasonText}";
        }
        else
        {
            parenDetails = reasonText;
        }

        return $"疑似殘留憑證重試：{account} 自 {source} 重複失敗 {count} 次（{parenDetails}），近 7 天已重複出現";
    }


    /// <summary>
    /// 從簽章的 KeyDetails 字串（如「相關帳號(3個): alice, bob, svc_backup；來源IP(1個): 10.0.0.5」）
    /// 解析出相關帳號清單，供 4740 帳號鎖定比對。
    /// </summary>
    internal static List<string> ExtractAccountsFromKeyDetails(string? keyDetails)
    {
        if (string.IsNullOrWhiteSpace(keyDetails))
        {
            return new List<string>();
        }

        var accountIndex = keyDetails.IndexOf("相關帳號", StringComparison.Ordinal);
        if (accountIndex < 0)
        {
            return new List<string>();
        }

        var colonIndex = keyDetails.IndexOfAny(new[] { ':', '：' }, accountIndex);
        if (colonIndex < 0)
        {
            return new List<string>();
        }

        var startIndex = colonIndex + 1;
        var semicolonIndex = keyDetails.IndexOfAny(new[] { '；', ';' }, startIndex);
        var content = semicolonIndex >= 0
            ? keyDetails.Substring(startIndex, semicolonIndex - startIndex)
            : keyDetails.Substring(startIndex);

        var rawTokens = content.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
        var accounts = new List<string>();

        foreach (var token in rawTokens)
        {
            var cleaned = token.Trim().TrimEnd('…', '.').Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                accounts.Add(cleaned);
            }
        }

        return accounts;
    }
}

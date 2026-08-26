namespace LogForesight.Core.Analysis;

/// <summary>
/// 密碼噴灑（Password Spraying）偵測器（A5）。
/// 偵測「以少數密碼嘗試大量帳號」的攻擊手法——每個帳號失敗次數極少以避開鎖定門檻，
/// 但同一個來源會對大量不同帳號嘗試登入。
/// 適用於 Windows（4625／4771）與 Linux（ssh/sudo/su 認證失敗）簽章。
/// </summary>
internal static class PasswordSprayDetector
{
    private const int MinDistinctAccounts = 10;
    private const int MaxPerAccountAttempts = 3;
    private const int MaxFindings = 5;

    /// <summary>
    /// 逐一檢查 issues 裡有 LoginFailureDetails 且非空的簽章，
    /// 依來源分組、帳號合併計數，符合門檻時產生關聯訊號。
    /// </summary>
    public static List<CorrelationFinding> Detect(List<LogIssueSignature> issues)
    {
        if (issues == null || issues.Count == 0)
        {
            return new List<CorrelationFinding>();
        }

        var candidates = new List<(int DistinctAccounts, CorrelationFinding Finding)>();

        foreach (var issue in issues)
        {
            if (issue.LoginFailureDetails == null || issue.LoginFailureDetails.Count == 0)
            {
                continue;
            }

            var sourceGroups = issue.LoginFailureDetails
                .Where(d => !string.IsNullOrWhiteSpace(d.Source))
                .GroupBy(d => d.Source, StringComparer.OrdinalIgnoreCase);

            foreach (var sourceGroup in sourceGroups)
            {
                var accountGroups = sourceGroup
                    .Where(d => !string.IsNullOrWhiteSpace(d.Account))
                    .GroupBy(d => d.Account, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (accountGroups.Count == 0)
                {
                    continue;
                }

                int distinctAccounts = accountGroups.Count;
                int maxAttempts = accountGroups.Max(g => g.Sum(d => d.Count));

                if (distinctAccounts >= MinDistinctAccounts && maxAttempts <= MaxPerAccountAttempts)
                {
                    var sourceName = sourceGroup.Key;
                    candidates.Add((distinctAccounts, new CorrelationFinding
                    {
                        Severity = IssueSeverity.High,
                        ElevatesDayRisk = true,
                        PatternId = CorrelationPatternIds.PasswordSpray,
                        Description = $"【密碼噴灑】來源 {sourceName} 對 {distinctAccounts} 個不同帳號嘗試登入，每個帳號僅失敗 {maxAttempts} 次以內——刻意避開帳號鎖定門檻的密碼噴灑特徵，應立即封鎖該來源並檢查是否有帳號已被猜中"
                    }));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return new List<CorrelationFinding>();
        }

        return candidates
            .OrderByDescending(c => c.DistinctAccounts)
            .Take(MaxFindings)
            .Select(c => c.Finding)
            .ToList();
    }
}

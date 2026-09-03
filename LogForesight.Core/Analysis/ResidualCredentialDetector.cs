using LogForesight.Core.Models;

namespace LogForesight.Core.Analysis;

/// <summary>
/// 殘留憑證重試判定候選簽章的中間指標（docs/archive/FEEDBACK-37-PLAN.md 批次A，校準數值匯出用）
/// </summary>
internal sealed record ResidualCandidateMetrics
{
    public int EventId { get; init; }
    public string EventKey { get; init; } = string.Empty;
    public int TotalDetailCount { get; init; }
    public int TopTwoCountSum { get; init; }
    public double ConcentrationRatio { get; init; }
    public double? MechanicalLogonTypeRatio { get; init; }
    public double SingleGroupRatio { get; init; }
    public bool IsTruncated { get; init; }
    public bool IsMatch { get; init; }

    /// <summary>跨日重現的相異日期數（含當日）。判定門檻是 MinDistinctDays，
    /// 記錄實際值供校準——「差一天才成立」與「完全沒有」是不同的資訊。</summary>
    public int CrossDayDistinctDays { get; init; }
    internal LoginFailureDetail? LargestDetail { get; init; }
}

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

    /// <summary>回看窗長（校準匯出組 history 時要用同一個值，否則兩邊判定窗不一致）</summary>
    internal const int HistoryWindowDaysForCalibration = HistoryWindowDays;
    private const int MinDistinctDays = 2;

    /// <summary>
    /// 評估單一簽章的殘留憑證判定指標（docs/archive/FEEDBACK-37-PLAN.md 批次A，判定與校準匯出共用同一份）。
    /// 非登入失敗簽章（無 LoginFailureDetails 或為空）回傳 null。
    /// </summary>
    internal static ResidualCandidateMetrics? EvaluateMetrics(
        LogIssueSignature issue,
        List<DailyAnalysisRecord>? history,
        DateTime targetDate)
    {
        if (issue == null || issue.LoginFailureDetails == null || issue.LoginFailureDetails.Count == 0)
        {
            return null;
        }

        // 明細封頂 50 組時被丟掉的都是尾巴小組。若拿保留下來的組數當分母，集中度會被
        // 系統性抬高——本來分散（多帳號多來源＝攻擊形狀）的簽章會被誤判成殘留而靜音，
        // 是往「少報攻擊」的方向錯。截斷時資料不足以判斷集中度，一律不標殘留。
        // 舊資料相容：截斷旗標與總量是後加的欄位，舊 ContentJson 反序列化後
        // 恆為 false／0。舊資料若當初就滿 50 組，很可能實際被截斷過——
        // 保守地把「滿 50 組且無總量」也視為疑似截斷（誤傷的只是「剛好 50 組」的
        // 合法情況，代價是維持既有攻擊語意，方向安全）。
        bool suspectedLegacyTruncation =
            issue.LoginFailureTotalCount == 0 &&
            issue.LoginFailureDetails.Count >= LogAggregator.LoginFailureDetailCap;
        bool isTruncated = issue.LoginFailureDetailsTruncated || suspectedLegacyTruncation;

        int totalDetailCount = issue.LoginFailureTotalCount > 0
            ? issue.LoginFailureTotalCount
            : issue.LoginFailureDetails.Sum(d => d.Count);

        var topGroup = issue.LoginFailureDetails
            .OrderByDescending(d => d.Count)
            .Take(2)
            .ToList();

        var largest = topGroup.Count > 0 ? topGroup[0] : null;
        int topTwoCountSum = topGroup.Sum(d => d.Count);
        double concentrationRatio = totalDetailCount > 0 ? (double)topTwoCountSum / totalDetailCount : 0.0;
        double singleGroupRatio = (largest != null && totalDetailCount > 0) ? (double)largest.Count / totalDetailCount : 0.0;

        double? mechanicalLogonTypeRatio = null;
        if (issue.EventId == 4625)
        {
            int mechanicalCountSum = topGroup
                .Where(d => d.LogonType is 3 or 4 or 5)
                .Sum(d => d.Count);
            mechanicalLogonTypeRatio = topTwoCountSum > 0 ? (double)mechanicalCountSum / topTwoCountSum : 0.0;
        }

        // 四個判定條件（全部滿足才標記為殘留憑證重試）：
        // 1. 失敗集中：前二大組的 Count 總和 ÷ 明細總數 ≥ ConcentrationThreshold。
        //    分母一律用「截斷前總量」——用保留下來的組數會系統性抬高集中度（見上方截斷說明）。
        // 2. 型態機械：
        //    - Windows 4625：集中組中 LogonType 為 3、4、5 其中之一的 Count 總和 ÷ 集中組總和
        //      ≥ MechanicalLogonTypeThreshold（服務／排程／解鎖等非互動登入才算機械）。
        //    - 其他（4771 與 Linux）：單一最大組的 Count ÷ 明細總數 ≥ SingleGroupConcentrationThreshold。
        // 3. 帳號非未知：集中組中沒有任何一組的 ReasonCode 是 unknown_user
        //    （帳號根本不存在＝猜帳號的攻擊形狀，不是殘留憑證）。
        // 4. 跨日重現：最大組的 (Account, Source) 在近 HistoryWindowDays 天內出現於
        //    ≥ MinDistinctDays 個不同日期（targetDate 當天算一天，故歷史中至少要再找到一天）。
        bool isMatch = false;
        if (!isTruncated &&
            totalDetailCount > 0 &&
            largest != null &&
            !string.IsNullOrWhiteSpace(largest.Account) &&
            concentrationRatio >= ConcentrationThreshold)
        {
            bool condition2Passed = false;
            if (issue.EventId == 4625)
            {
                if (mechanicalLogonTypeRatio.HasValue && mechanicalLogonTypeRatio.Value >= MechanicalLogonTypeThreshold)
                {
                    condition2Passed = true;
                }
            }
            else
            {
                if (singleGroupRatio >= SingleGroupConcentrationThreshold)
                {
                    condition2Passed = true;
                }
            }

            if (condition2Passed)
            {
                bool hasUnknownUser = topGroup.Any(d =>
                    string.Equals(d.ReasonCode, "unknown_user", StringComparison.OrdinalIgnoreCase));
                if (!hasUnknownUser)
                {
                    if (HasCrossDayRecurrence(history, targetDate, largest.Account, largest.Source))
                    {
                        isMatch = true;
                    }
                }
            }
        }

        return new ResidualCandidateMetrics
        {
            EventId = issue.EventId,
            EventKey = issue.EventKey ?? string.Empty,
            TotalDetailCount = totalDetailCount,
            TopTwoCountSum = topTwoCountSum,
            ConcentrationRatio = concentrationRatio,
            MechanicalLogonTypeRatio = mechanicalLogonTypeRatio,
            SingleGroupRatio = singleGroupRatio,
            IsTruncated = isTruncated,
            IsMatch = isMatch,
            CrossDayDistinctDays = largest != null && !string.IsNullOrWhiteSpace(largest.Account)
                ? CrossDayDistinctCount(history, targetDate, largest.Account, largest.Source)
                : 0,
            LargestDetail = largest
        };
    }

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

        // 第一階段：登入失敗簽章的殘留憑證重試判定（呼叫 EvaluateMetrics 取得指標後依指標標記）
        foreach (var issue in issues)
        {
            var metrics = EvaluateMetrics(issue, history, targetDate);
            if (metrics == null || !metrics.IsMatch)
            {
                continue;
            }

            var largest = metrics.LargestDetail!;
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

            // 優先讀取未截斷的結構化帳號欄位 KeyAccounts；若為 null 則 fallback 回 KeyDetails 顯示字串剖析。
            // Fallback 理由：舊 ContentJson 沒有 KeyAccounts 欄位，在系統過渡期需相容舊紀錄（會隨保留期自然淘汰）。
            var accounts = issue.KeyAccounts ?? ExtractAccountsFromKeyDetails(issue.KeyDetails);
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

    /// <summary>跨日重現的相異日期數（含 targetDate 當天）。判定用 <see cref="HasCrossDayRecurrence"/>，
    /// 這個回傳實際天數供校準匯出記錄——「差一天才成立」與「完全沒有」在校準時是不同的資訊。</summary>
    private static int CrossDayDistinctCount(
        List<DailyAnalysisRecord>? history,
        DateTime targetDate,
        string account,
        string source)
    {
        if (history == null || history.Count == 0) return 1;

        var startDate = targetDate.Date.AddDays(-(HistoryWindowDays - 1));
        var endDate = targetDate.Date;
        var matchingDates = new HashSet<DateTime>();

        foreach (var record in history)
        {
            var recordDate = record.Date.Date;
            if (recordDate < startDate || recordDate >= endDate) continue;
            if (RecordHasPair(record, account, source)) matchingDates.Add(recordDate);
        }

        return matchingDates.Count + 1;
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

            if (RecordHasPair(record, account, source))
            {
                matchingDates.Add(recordDate);
            }
        }

        return (matchingDates.Count + 1) >= MinDistinctDays;
    }

    /// <summary>某日紀錄中是否出現過同一組 (帳號, 來源) 的登入失敗明細</summary>
    private static bool RecordHasPair(DailyAnalysisRecord record, string account, string source)
    {
        if (record.TopIssues == null || record.TopIssues.Count == 0) return false;

        foreach (var histIssue in record.TopIssues)
        {
            if (histIssue.LoginFailureDetails == null || histIssue.LoginFailureDetails.Count == 0) continue;

            foreach (var detail in histIssue.LoginFailureDetails)
            {
                if (string.Equals(detail.Account, account, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(detail.Source, source, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
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

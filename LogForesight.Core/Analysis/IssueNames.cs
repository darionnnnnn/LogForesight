using LogForesight.Core.Service;
namespace LogForesight;

/// <summary>
/// 問題名稱的唯一出口：只有來源與事件編號（依問題聚合的畫面、人工交辦、夜間派工）時一律呼叫這裡，
/// 不各自拼「來源 + 空白 + 事件編號」。有完整簽章時用 <see cref="LogIssueSignature.SourceEventLabel"/>，
/// 兩者對同一個 Windows 事件輸出相同文字。
/// 規則：
/// - 事件編號大於 0：「{來源} EventId {編號}」（與 SourceEventLabel 的 Windows 格式相同）。
/// - 事件編號為 0（Linux、PRTG 的聚合鍵）：只顯示來源，不顯示「0」；取得得到規則名稱時附在後面，「{來源}（{規則名稱}）」。
/// - 來源是 PRTG 規則（<c>PRTG:{代碼}</c>）：聚合層沒有感測器編號，顯示「PRTG {固定短稱}」（不附規則說明句）。
/// 已存進交辦單的名稱不回寫；顯示時用 <see cref="Display"/>：以單上的來源與事件重新產生，取不到才用存的字串。
/// </summary>
public static class IssueNames
{
    public static string For(string? source, int eventId, string? ruleName = null)
    {
        var src = (source ?? string.Empty).Trim();
        var name = string.IsNullOrWhiteSpace(ruleName) ? null : ruleName.Trim();
        if (PrtgFindingMapper.TryGetRuleCode(src, out var code))
            return $"{PrtgFindingMapper.PrtgLogName} {PrtgKindText(code)}";   // 規則名稱是長說明句，PRTG 用固定短稱
        if (eventId > 0) return $"{src} EventId {eventId}";
        return name == null ? src : $"{src}（{name}）";
    }

    /// <summary>
    /// PRTG 規則代碼（<c>PrtgRuleEvaluator.Rule*</c>）的固定短稱，是全站唯一一份：
    /// <see cref="For"/> 與 <see cref="LogIssueSignature.SourceEventLabel"/> 的 PRTG 分支都呼叫這裡；不認得的代碼原樣顯示。
    /// </summary>
    public static string PrtgKindText(string code) => code.ToLowerInvariant() switch
    {
        PrtgRuleEvaluator.RuleDown => "持續故障",
        PrtgRuleEvaluator.RuleWarning => "持續警告",
        PrtgRuleEvaluator.RuleFlapping => "反覆故障",
        PrtgRuleEvaluator.RuleDiskFreeTrend => "磁碟容量趨勢",
        PrtgRuleEvaluator.RuleMemoryUsedTrend => "記憶體使用趨勢",
        PrtgRuleEvaluator.RuleCpuLoadTrend => "CPU 負載趨勢",
        PrtgRuleEvaluator.RulePingLatencyTrend => "Ping 延遲趨勢",
        PrtgRuleEvaluator.RuleSilent => "長時間無回報",
        _ => code
    };

    /// <summary>
    /// 依問題聚合的畫面一律走這個：事件編號為 0 時以 <see cref="KnownIssueCatalog.RuleNameFor"/> 取規則名稱附上
    /// （恰好一條規則命中才附，多條或沒有就只顯示來源），所有畫面對同一問題輸出相同文字。
    /// </summary>
    public static string ForRules(IReadOnlyList<KnownIssueRule> rules, string? source, int eventId) =>
        For(source, eventId,
            eventId == 0 && !string.IsNullOrWhiteSpace(source) ? KnownIssueCatalog.RuleNameFor(rules, source.Trim(), eventId) : null);

    /// <summary>
    /// 既有交辦單／案件的顯示名稱：來源非空時以來源與事件重新產生（<see cref="ForRules"/>，舊單的「sshd 0」也因此改成同一種寫法），
    /// PRTG 單若存的名稱帶感測器編號（夜間派工建的）保留它（<see cref="PrtgFindingMapper.DisplayStoredLabel"/>）；來源空白才用存的字串。
    /// </summary>
    public static string Display(string? source, int eventId, string? stored, IReadOnlyList<KnownIssueRule> rules)
    {
        if (string.IsNullOrWhiteSpace(source)) return PrtgFindingMapper.DisplayStoredLabel(stored ?? string.Empty);
        if (PrtgFindingMapper.TryGetRuleCode(source, out _))
        {
            // 夜間派工存的 PRTG 名稱帶感測器編號（單上沒有），能讀出來就保留；其餘（人工建立存的舊寫法）重新產生
            var readable = PrtgFindingMapper.DisplayStoredLabel(stored ?? string.Empty);
            if (readable.Contains("感測器 #", StringComparison.Ordinal)) return readable;
        }
        return ForRules(rules, source, eventId);
    }
}

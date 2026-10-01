using LogForesight.Core.Models;
using LogForesight.Core.Service;

namespace LogForesight.Core.Analysis;

/// <summary>
/// PRTG 跨來源佐證（純函式）：同一主機日，事件日誌與 PRTG 兩個獨立來源同時示警時，
/// 在紀錄既有的關聯欄位補一條同日線索，走既有的關聯抑制；配對本身不提高風險。
///
/// 在 PRTG finding **追加時**判定（兩條追加路徑——資料庫補追加與記憶體就地追加——都呼叫這裡），
/// 不把 PRTG 評估搬到分析之前。佐證判定只有這一份。
///
/// 刻意配對：磁碟 I/O 錯誤配硬體健康 sensor、磁碟空間不足配磁碟可用空間 sensor、非預期關機配連通性 sensor。
/// 「I/O 錯誤＋空間快滿」是不同情境，不予配對。
/// </summary>
public static class PrtgCorroboration
{
    private const string StoragePrefix = "【儲存異常同日訊號】";
    private const string CapacityPrefix = "【容量異常同日訊號】";
    private const string OutagePrefix = "【關機與監測異常同日訊號】";

    private sealed record Hit(string PatternId, string Prefix, string Text);

    /// <summary>判定修訂時重建本元件的線索；不動其他事件關聯。未傳抑制集合時沿用既有抑制。</summary>
    public static (string? RiskLevel, string? RiskBasis, int Added) Refresh(
        DailyAnalysisRecord record, IReadOnlySet<string>? suppressedPatternIds = null)
    {
        (string Id, string Prefix)[] owned = [
            (CorrelationPatternIds.PrtgStorageCorroborated, StoragePrefix),
            (CorrelationPatternIds.PrtgCapacityCorroborated, CapacityPrefix),
            (CorrelationPatternIds.PrtgOutageCorroborated, OutagePrefix)];
        var prior = record.CorrelationAlertRefs.Where(r => owned.Any(o => o.Id == r.PatternId))
            .Select(r => r.PatternId).ToHashSet(StringComparer.Ordinal);
        var suppression = suppressedPatternIds ?? owned.Where(o => record.SuppressedCorrelationAlerts
            .Any(t => t.StartsWith(o.Prefix, StringComparison.Ordinal))).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var priorTexts = record.CorrelationAlertRefs.Where(r => prior.Contains(r.PatternId)).Select(r => r.Text).ToHashSet();
        bool Owns(string text) => priorTexts.Contains(text) || owned.Any(o => text.StartsWith(o.Prefix, StringComparison.Ordinal));
        record.CorrelationAlerts.RemoveAll(Owns);
        record.SuppressedCorrelationAlerts.RemoveAll(Owns);
        record.CorrelationAlertRefs.RemoveAll(r => owned.Any(o => o.Id == r.PatternId));
        Apply(record, suppression);
        return (null, null, record.CorrelationAlertRefs.Count(r => owned.Any(o => o.Id == r.PatternId) && !prior.Contains(r.PatternId)));
    }

    /// <summary>
    /// 對紀錄套用佐證判定，就地寫入 <see cref="DailyAnalysisRecord.CorrelationAlerts"/>／
    /// <see cref="DailyAnalysisRecord.CorrelationAlertRefs"/>（未抑制）或
    /// <see cref="DailyAnalysisRecord.SuppressedCorrelationAlerts"/>（模式被抑制）。
    /// 目前只有弱佐證，回傳風險為 null；呼叫端的 PRTG finding 本身仍可依規則影響風險。
    /// </summary>
    /// <param name="record">主機日紀錄（TopIssues 已含本次追加的 PRTG 簽章）。</param>
    /// <param name="suppressedPatternIds">該主機生效中的關聯抑制模式 Id（沒有就傳空集合）。</param>
    /// <returns>
    /// 現有配對只有同日資訊，缺時間與資源身分；只記弱佐證，不以配對提高風險。
    /// 回傳的風險與依據恆為 null；Added 為新加的未抑制線索數。
    /// </returns>
    public static (string? RiskLevel, string? RiskBasis, int Added) Apply(
        DailyAnalysisRecord record, IReadOnlySet<string> suppressedPatternIds)
    {
        var eventIssues = record.TopIssues
            .Where(i => !PrtgFindingMapper.IsPrtg(i) && !i.Suppressed)
            .ToList();
        var prtgIssues = record.TopIssues
            .Where(i => PrtgFindingMapper.IsPrtg(i) && !i.Suppressed)
            .ToList();
        if (eventIssues.Count == 0 || prtgIssues.Count == 0) return (null, null, 0);

        var hits = new List<Hit>();

        // storage：儲存訊號 ≥1 ＋ 硬體健康 sensor 的 warning 或 down
        var storageSignals = CorrelationAnalyzer.StorageSignals(eventIssues);
        var hardwareAlert = FirstPrtg(prtgIssues, PrtgSensorCategories.Hardware,
            PrtgRuleEvaluator.RuleWarning, PrtgRuleEvaluator.RuleDown);
        if (storageSignals.Count > 0 && hardwareAlert != null)
        {
            var parts = string.Join("、", storageSignals.Select(s => $"{s.Source}#{s.EventId}"));
            hits.Add(new Hit(CorrelationPatternIds.PrtgStorageCorroborated, StoragePrefix,
                $"{StoragePrefix}事件日誌的磁碟 I/O 錯誤（{parts}）與 PRTG 硬體健康 sensor 同日示警（{DetailOf(hardwareAlert)}），" +
                "兩來源同日出現異常；尚未確認為同一裝置或同一時段，請查對硬體與備份"));
        }

        // capacity：srv 2013（磁碟空間即將不足）＋ 磁碟可用空間 sensor 的 warning
        var lowDiskSpace = eventIssues.Any(i =>
            i.EventId == 2013 && i.Source.Contains("srv", StringComparison.OrdinalIgnoreCase));
        var diskWarning = FirstPrtg(prtgIssues, PrtgSensorCategories.Disk, PrtgRuleEvaluator.RuleWarning);
        if (lowDiskSpace && diskWarning != null)
        {
            hits.Add(new Hit(CorrelationPatternIds.PrtgCapacityCorroborated, CapacityPrefix,
                $"{CapacityPrefix}事件日誌回報磁碟空間即將不足，PRTG 磁碟可用空間 sensor 同日示警（{DetailOf(diskWarning)}）；尚未確認為同一磁碟區"));
        }

        // outage：非預期關機 ＋ 連通性 sensor 的 down 或 flapping
        var unexpectedShutdown = CorrelationAnalyzer.UnexpectedShutdown(eventIssues);
        var availabilityAlert = FirstPrtg(prtgIssues, PrtgSensorCategories.Availability,
            PrtgRuleEvaluator.RuleDown, PrtgRuleEvaluator.RuleFlapping);
        if (unexpectedShutdown != null && availabilityAlert != null)
        {
            hits.Add(new Hit(CorrelationPatternIds.PrtgOutageCorroborated, OutagePrefix,
                $"{OutagePrefix}事件日誌記錄非預期關機，PRTG 連通性類 sensor 同日異常（{DetailOf(availabilityAlert)}）；尚未確認時間與探測對象相符"));
        }

        var added = new List<Hit>();
        foreach (var hit in hits)
        {
            // 冪等：未抑制的已進 Refs 就不再加。已抑制清單另外去重——
            // 不能把「曾被抑制」當成「已存在」，否則取消抑制後重跑，這個佐證永遠補不回來
            if (record.CorrelationAlertRefs.Any(r => string.Equals(r.PatternId, hit.PatternId, StringComparison.Ordinal))) continue;

            if (suppressedPatternIds.Contains(hit.PatternId))
            {
                if (!record.SuppressedCorrelationAlerts.Any(t => t.StartsWith(hit.Prefix, StringComparison.Ordinal)))
                    record.SuppressedCorrelationAlerts.Add(hit.Text);
                continue;
            }

            // 抑制已取消：先前留在已抑制清單的同一模式移除，避免同一件事兩邊都有
            record.SuppressedCorrelationAlerts.RemoveAll(t => t.StartsWith(hit.Prefix, StringComparison.Ordinal));

            record.CorrelationAlerts.Add(hit.Text);
            record.CorrelationAlertRefs.Add(new CorrelationAlertRef { Text = hit.Text, PatternId = hit.PatternId });
            added.Add(hit);
        }

        if (added.Count == 0) return (null, null, 0);

        return (null, null, added.Count);
    }

    /// <summary>指定 sensor 分類、且規則代碼為其中之一的第一筆 PRTG 簽章。</summary>
    private static LogIssueSignature? FirstPrtg(
        IReadOnlyList<LogIssueSignature> prtgIssues, string sensorCategory, params string[] ruleCodes) =>
        prtgIssues.FirstOrDefault(i =>
            string.Equals(i.PrtgSensorCategory, sensorCategory, StringComparison.OrdinalIgnoreCase) &&
            PrtgFindingMapper.TryGetRuleCode(i.Source, out var code) &&
            ruleCodes.Contains(code, StringComparer.OrdinalIgnoreCase));

    private static string DetailOf(LogIssueSignature signature) =>
        signature.SampleMessages.Count > 0 ? signature.SampleMessages[0] : signature.Source;
}

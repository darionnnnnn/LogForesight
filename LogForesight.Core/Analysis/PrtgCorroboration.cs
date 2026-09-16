using LogForesight.Core.Models;
using LogForesight.Core.Service;

namespace LogForesight.Core.Analysis;

/// <summary>
/// PRTG 跨來源佐證（純函式）：同一主機日，事件日誌與 PRTG 兩個獨立來源同時示警時，
/// 在紀錄既有的關聯欄位補一條佐證告警，走既有的關聯抑制與風險語意。
///
/// 在 PRTG finding **追加時**判定（兩條追加路徑——資料庫補追加與記憶體就地追加——都呼叫這裡），
/// 不把 PRTG 評估搬到分析之前。佐證判定只有這一份。
///
/// 刻意配對：磁碟 I/O 錯誤配硬體健康 sensor、磁碟空間不足配磁碟可用空間 sensor、非預期關機配連通性 sensor。
/// 「I/O 錯誤＋空間快滿」兩件事不相干，不當雙重確認。
/// </summary>
public static class PrtgCorroboration
{
    private const string StoragePrefix = "【儲存故障雙重確認】";
    private const string CapacityPrefix = "【磁碟容量雙重確認】";
    private const string OutagePrefix = "【失聯獲 PRTG 證實】";

    private sealed record Hit(string PatternId, string Prefix, string Text, bool ElevatesDayRisk);

    /// <summary>
    /// 對紀錄套用佐證判定，就地寫入 <see cref="DailyAnalysisRecord.CorrelationAlerts"/>／
    /// <see cref="DailyAnalysisRecord.CorrelationAlertRefs"/>（未抑制）或
    /// <see cref="DailyAnalysisRecord.SuppressedCorrelationAlerts"/>（模式被抑制）。
    /// 風險不在這裡改：回傳的 <c>RiskLevel</c> 由呼叫端以 <c>RiskLevels.MoreSevere</c> 單向套用。
    /// </summary>
    /// <param name="record">主機日紀錄（TopIssues 已含本次追加的 PRTG 簽章）。</param>
    /// <param name="suppressedPatternIds">該主機生效中的關聯抑制模式 Id（沒有就傳空集合）。</param>
    /// <returns>
    /// 本次新加進 <c>CorrelationAlerts</c> 的佐證：任一帶「重大」→ 高、否則 → 中；
    /// <c>RiskBasis</c> 為決定性模式的 PatternId（重大優先）；<c>Added</c> 為新加的未抑制佐證筆數。
    /// 沒有新的未抑制佐證時回 <c>(null, null, 0)</c>。
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
                "兩個獨立來源一致，應立即確認備份並安排檢修",
                ElevatesDayRisk: true));
        }

        // capacity：srv 2013（磁碟空間即將不足）＋ 磁碟可用空間 sensor 的 warning
        var lowDiskSpace = eventIssues.Any(i =>
            i.EventId == 2013 && i.Source.Contains("srv", StringComparison.OrdinalIgnoreCase));
        var diskWarning = FirstPrtg(prtgIssues, PrtgSensorCategories.Disk, PrtgRuleEvaluator.RuleWarning);
        if (lowDiskSpace && diskWarning != null)
        {
            hits.Add(new Hit(CorrelationPatternIds.PrtgCapacityCorroborated, CapacityPrefix,
                $"{CapacityPrefix}事件日誌回報磁碟空間即將不足，PRTG 磁碟可用空間 sensor 同日示警（{DetailOf(diskWarning)}）",
                ElevatesDayRisk: false));
        }

        // outage：非預期關機 ＋ 連通性 sensor 的 down 或 flapping
        var unexpectedShutdown = CorrelationAnalyzer.UnexpectedShutdown(eventIssues);
        var availabilityAlert = FirstPrtg(prtgIssues, PrtgSensorCategories.Availability,
            PrtgRuleEvaluator.RuleDown, PrtgRuleEvaluator.RuleFlapping);
        if (unexpectedShutdown != null && availabilityAlert != null)
        {
            hits.Add(new Hit(CorrelationPatternIds.PrtgOutageCorroborated, OutagePrefix,
                $"{OutagePrefix}事件日誌記錄非預期關機，PRTG 同日觀測到主機失聯（{DetailOf(availabilityAlert)}），不是日誌誤報",
                ElevatesDayRisk: false));
        }

        var added = new List<Hit>();
        foreach (var hit in hits)
        {
            // 冪等：已加過（未抑制進 Refs、或被抑制進已抑制清單）就不再加
            var alreadyPresent =
                record.CorrelationAlertRefs.Any(r => string.Equals(r.PatternId, hit.PatternId, StringComparison.Ordinal)) ||
                record.SuppressedCorrelationAlerts.Any(t => t.StartsWith(hit.Prefix, StringComparison.Ordinal));
            if (alreadyPresent) continue;

            if (suppressedPatternIds.Contains(hit.PatternId))
            {
                record.SuppressedCorrelationAlerts.Add(hit.Text);
                continue;
            }

            record.CorrelationAlerts.Add(hit.Text);
            record.CorrelationAlertRefs.Add(new CorrelationAlertRef { Text = hit.Text, PatternId = hit.PatternId });
            added.Add(hit);
        }

        if (added.Count == 0) return (null, null, 0);

        var decisive = added.FirstOrDefault(h => h.ElevatesDayRisk);
        return decisive != null
            ? (RiskLevels.High, decisive.PatternId, added.Count)
            : (RiskLevels.Medium, added[0].PatternId, added.Count);
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

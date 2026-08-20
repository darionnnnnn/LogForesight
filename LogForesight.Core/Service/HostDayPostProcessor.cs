using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using System.Collections.Concurrent;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 缺漏日計算：本機與 NetIQ 路徑都要「回望 N 天，挑出還沒有分析紀錄的日期」，
/// 邏輯完全相同，只有回望天數的決定方式不同（本機視是否為首次執行決定天數，
/// NetIQ 固定用管理者設定的回補窗口）。
/// </summary>
internal static class MissingDateFinder
{
    public static List<DateTime> Find(IAnalysisRecordReader store, int lookbackDays, bool requireAi = false, bool useAi = true)
    {
        if (!requireAi)
        {
            return Enumerable.Range(1, lookbackDays)
                .Select(offset => DateTime.Today.AddDays(-offset))
                .Where(date => !store.HasRecord(date))
                .OrderBy(date => date)
                .ToList();
        }

        // GroupBy 再取第一筆而非直接 ToDictionary：同一天若有重複列（合併主機／遷移期間），
        // 撞鍵會在排程的計畫階段炸掉整台 Sentinel——這正是回饋二十輪 I 修掉的那類 500
        var records = store.ReadRecent(DateTime.Today.AddDays(-1), lookbackDays)
            .GroupBy(r => r.Date.Date)
            .ToDictionary(g => g.Key, g => g.First());

        return Enumerable.Range(1, lookbackDays)
            .Select(offset => DateTime.Today.AddDays(-offset))
            .Where(date => !records.TryGetValue(date, out var r) || HostDayPostProcessor.NeedsBackfill(r, useAi))
            .OrderBy(date => date)
            .ToList();
    }
}

/// <summary>
/// 單一主機日分析完成後的共通後續處理：問題案件掛接、風險 log 暫存寫入、AI 呼叫計數——
/// 本機路徑（<see cref="AnalysisOrchestrator"/>）與 NetIQ 機房路徑（<see cref="NetiqPipelineService"/>）
/// 逐日/逐主機分析完成後都要做同一組動作，任一步失敗都不讓這個主機日的分析結果作廢
/// （只記警告，下次執行冪等補跑）。<paramref name="logContext"/> 供 NetIQ 路徑帶入
/// Sentinel／IP 供除錯定位，本機路徑固定傳空字串。
/// </summary>
public static class HostDayPostProcessor
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// NetIQ Windows Security 權限異動事件與中文類型對應（放在單一常數點）。
    /// 各 EventId 意義：
    ///   - 4728: 已將成員新增到安全性全域群組 (A member was added to a security-enabled global group)
    ///   - 4732: 已將成員新增到安全性本機群組 (A member was added to a security-enabled local group)
    ///   - 4756: 已將成員新增到安全性通用群組 (A member was added to a security-enabled universal group)
    ///   - 4729: 已從安全性全域群組移除成員 (A member was removed from a security-enabled global group)
    ///   - 4733: 已從安全性本機群組移除成員 (A member was removed from a security-enabled local group)
    ///   - 4757: 已從安全性通用群組移除成員 (A member was removed from a security-enabled universal group)
    ///   - 4670: 已變更物件的權限 (Permissions on an object were changed)
    ///   - 4717: 已將系統安全性存取權授與帳戶 (System security access was granted to an account)
    ///   - 4718: 已從帳戶移除系統安全性存取權 (System security access was removed from an account)
    ///   - 4719: 已變更系統稽核原則 (System audit policy was changed)
    ///   - 4907: 已變更物件的稽核設定 (Auditing settings on object were changed)
    /// </summary>
    /// <summary>每主機日逐則寫入的上限：一台吵雜的 DC 一天能產生數千則 4670／4907，
    /// 逐則寫會讓待辦頁與 append-only log 失控。超過的部分彙總成一筆（含各類型筆數），
    /// 「有異動」這件事不會被吞掉，只是不逐則列。</summary>
    internal const int MaxPermissionChangeRecordsPerHostDay = 50;

    private static readonly Dictionary<int, string> PermissionChangeEventTypes = new()
    {
        // 成員新增
        [4728] = "成員新增",
        [4732] = "成員新增",
        [4756] = "成員新增",
        // 成員移除
        [4729] = "成員移除",
        [4733] = "成員移除",
        [4757] = "成員移除",
        // ACL／物件權限變更
        [4670] = "權限變更",
        // 稽核政策變更
        [4717] = "稽核政策變更",
        [4718] = "稽核政策變更",
        [4719] = "稽核政策變更",
        [4907] = "稽核政策變更",
    };

    /// <summary>
    /// 「這個主機日是否需要補跑」的唯一定義——「只補跑失敗或未執行」模式的三處判定
    /// （MissingDateFinder requireAi 分支、NetiqPipelineService 孤兒清單、ScheduleController.RunPreview）
    /// 皆呼叫此函式，不各寫一份。一般模式（requireAi=false）只看「有沒有紀錄」，不經過這裡。
    ///
    /// 規則依序：
    /// 1. record 為 null（該日完全沒有紀錄＝缺日）→ 需要
    /// 2. AiPending == true → 需要
    /// 3. useAi == true &amp;&amp; !AiAnalyzed &amp;&amp; RiskLevel != Low → 需要
    /// 4. 其餘 → 不需要
    ///
    /// useAi == false（統計模式，AI 未設定）時，AiAnalyzed 恆為 false 屬正常狀態，
    /// 規則 3 不觸發——只有「缺日」與「AiPending」兩種情況才需要補跑。
    /// 低風險日「刻意不呼叫 AI」是合法終局狀態，不視為失敗。
    /// </summary>
    public static bool NeedsBackfill(DailyAnalysisRecord? record, bool useAi)
    {
        if (record == null) return true;
        if (record.AiPending) return true;
        if (useAi && !record.AiAnalyzed && record.RiskLevel != RiskLevels.Low) return true;
        return false;
    }

    public static void AttachCase(
        IssueCaseCoordinator caseCoordinator, string hostName, DateTime date,
        List<LogIssueSignature> topIssues, string logContext = "")
    {
        try
        {
            var attach = caseCoordinator.AttachNewDay(hostName, date, topIssues, DateTime.Now);
            if (attach.AttachedCount > 0)
                Log.Info("{Context}{Date:yyyy-MM-dd} 案件掛接：掛入 {Count} 個問題", logContext, date, attach.AttachedCount);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 案件掛接失敗（不影響分析結果，下次執行冪等補掛）", logContext, date);
        }
    }

    public static void ReplaceRiskyEvents(
        IRiskyEventStore? riskyEventStore, int retentionDays, DateTime date,
        List<LogIssueSignature> topIssues, List<EventLogEntryData> logs, long hostId, string logContext = "")
    {
        if (riskyEventStore == null || !RiskyEventSelector.WithinRetention(date, retentionDays, DateTime.Today)) return;

        try
        {
            var riskyEvents = RiskyEventSelector.Select(topIssues, logs, hostId, date);
            riskyEventStore.ReplaceDay(hostId, date, riskyEvents);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 風險 log 暫存寫入失敗（不影響分析結果）", logContext, date);
        }
    }

    public static void RecordPermissionChanges(
        PermissionChangeStore? permissionChangeStore,
        ConcurrentDictionary<string, byte> knownKeys,
        string hostName,
        string hostOs,
        List<EventLogEntryData> events,
        DateTime date,
        string logContext = "")
    {
        if (permissionChangeStore == null) return;
        if (!string.Equals(hostOs, WebHost.OsWindows, StringComparison.OrdinalIgnoreCase)) return;
        if (events == null || events.Count == 0) return;

        try
        {
            var matchingEvents = events
                .Where(e => e.EventId != 0 && PermissionChangeEventTypes.ContainsKey(e.EventId))
                .ToList();

            if (matchingEvents.Count == 0) return;

            // 冪等：同一主機日重跑（回補、重新分析）不得產生重複紀錄。去重鍵由呼叫端傳入
            // 的快照承擔——多台主機平行處理，用 ConcurrentDictionary.TryAdd 做原子佔位。
            var recordsToAppend = new List<PermissionChangeRecord>();
            var overflowByType = new Dictionary<string, int>();

            foreach (var evt in matchingEvents)
            {
                var changeType = PermissionChangeEventTypes[evt.EventId];
                var alertText = TextTruncation.Truncate(evt.Message ?? string.Empty, 500);
                var key = PermissionChangeRecord.DedupeKey(hostName, evt.TimeGenerated, evt.EventId, alertText);
                if (!knownKeys.TryAdd(key, 0)) continue;

                if (recordsToAppend.Count >= MaxPermissionChangeRecordsPerHostDay)
                {
                    overflowByType[changeType] = overflowByType.GetValueOrDefault(changeType) + 1;
                    continue;
                }

                var details = PermissionChangeExtractor.Extract(
                    evt.Message, changeType, evt.EventId, evt.Source, evt.InitiatorAccount);

                recordsToAppend.Add(new PermissionChangeRecord
                {
                    ChangeId = Guid.NewGuid().ToString("N"),
                    HostName = hostName,
                    DetectedAt = evt.TimeGenerated,
                    Target = details.Target,
                    ChangeType = changeType,
                    Category = PermissionCategory.Resolve(changeType, evt.EventId),
                    IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget(details.Target, changeType),
                    InitiatorAccount = details.InitiatorAccount,
                    TargetAccount = details.TargetAccount,
                    Before = details.Before,
                    After = details.After,
                    AlertText = alertText,
                    Source = PermissionChangeSources.Netiq,
                    EventId = evt.EventId
                });
            }

            if (overflowByType.Count > 0)
            {
                var overflowTotal = overflowByType.Values.Sum();
                var summaryText = $"本日另有 {overflowTotal} 則權限異動事件未逐則列出（" +
                    string.Join("、", overflowByType.Select(kv => $"{kv.Key} {kv.Value} 則")) + "）";
                var summaryKey = PermissionChangeRecord.DedupeKey(hostName, date.Date, 0, summaryText);
                if (knownKeys.TryAdd(summaryKey, 0))
                {
                    recordsToAppend.Add(new PermissionChangeRecord
                    {
                        ChangeId = Guid.NewGuid().ToString("N"),
                        HostName = hostName,
                        DetectedAt = date.Date,
                        Target = $"{hostName}（彙總）",
                        ChangeType = "權限異動（彙總）",
                        Category = PermissionCategory.Resolve("權限異動（彙總）", 0),
                        IsPrivilegedTarget = false,
                        InitiatorAccount = null,
                        TargetAccount = null,
                        Before = string.Empty,
                        After = string.Empty,
                        AlertText = summaryText,
                        Source = PermissionChangeSources.Netiq,
                        EventId = 0
                    });
                }
            }

            if (recordsToAppend.Count > 0)
            {
                permissionChangeStore.AppendChanges(recordsToAppend);
                Log.Info("{Context}{Date:yyyy-MM-dd} 權限異動待辦：寫入 {Count} 筆紀錄", logContext, date, recordsToAppend.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 權限異動待辦寫入失敗（不影響分析結果）", logContext, date);
        }
    }

    /// <summary>
    /// AiAnalyzed=false 有兩種意義：低風險日「刻意不呼叫」（正常）與呼叫失敗的降級（異常）。
    /// useAi=false（AI 未設定）時本來就不會呼叫，不該被記成失敗，只有後者該計入執行監控頁的
    /// AI 呼叫統計。
    /// </summary>
    public static void RecordAiCallIfApplicable(BatchRunRecorder runRecorder, bool useAi, DailyAnalysisRecord record)
    {
        if (useAi && (record.AiAnalyzed || record.RiskLevel != RiskLevels.Low))
        {
            runRecorder.RecordAiCall(record.AiAnalyzed);
        }
    }
}

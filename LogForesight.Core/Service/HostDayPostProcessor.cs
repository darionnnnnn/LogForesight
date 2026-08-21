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
        string logContext = "",
        PermissionFieldMappings? mappings = null)
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
            //
            // 每主機日不設筆數上限：權限異動全部逐則入庫才查得到、篩得到（一台吵雜的 DC
            // 一天可達數萬則）。量的控制交給保留天數清理（PermissionChangeStore 依 created_at）。
            var recordsToAppend = new List<PermissionChangeRecord>();

            foreach (var evt in matchingEvents)
            {
                var changeType = PermissionChangeEventTypes[evt.EventId];
                var alertText = TextTruncation.Truncate(evt.Message ?? string.Empty, 500);
                var key = PermissionChangeRecord.DedupeKey(hostName, evt.TimeGenerated, evt.EventId, alertText);
                if (!knownKeys.TryAdd(key, 0)) continue;

                var details = PermissionChangeExtractor.Extract(
                    evt.Message, changeType, evt.EventId, evt.InitiatorAccount, mappings);

                recordsToAppend.Add(new PermissionChangeRecord
                {
                    ChangeId = Guid.NewGuid().ToString("N"),
                    HostName = hostName,
                    DetectedAt = evt.TimeGenerated,
                    Target = details.Target,
                    ChangeType = changeType,
                    Category = PermissionCategory.Resolve(changeType, evt.EventId, details.ObjectType),
                    IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget(details.Target, changeType),
                    InitiatorAccount = details.InitiatorAccount,
                    TargetAccount = details.TargetAccount,
                    ObjectType = details.ObjectType,
                    ProcessName = details.ProcessName,
                    Before = details.Before,
                    After = details.After,
                    AlertText = alertText,
                    Source = PermissionChangeSources.Netiq,
                    EventId = evt.EventId
                });
            }

            var routineSummary = ExtractRoutineSyncPairs(recordsToAppend, hostName, date);
            var routineKey = RoutineSyncDedupeKey(hostName, date);
            if (routineSummary != null)
            {
                permissionChangeStore.UpsertByDedupeKey(routineSummary, routineKey);
            }
            else
            {
                // 這次沒達門檻＝這批異動改為逐則列出，先前那筆彙總列必須撤掉，
                // 否則同一批異動會同時以彙總與逐則兩種形式存在（重跑時 NetIQ 只回子集就會發生）
                permissionChangeStore.DeleteByDedupeKey(routineKey);
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

    /// <summary>成員新增＋成員移除成對出現達此對數時，該主機日的成對異動合併為一筆彙總列。
    /// 門檻不做成設定項：這是「明顯是自動化程序」的判斷，不是使用者要調的參數。</summary>
    internal const int RoutineSyncPairThreshold = 50;

    /// <summary>例行同步彙總列的異動類型；去重鍵用的固定字串也是它（不含對數，對數變動時
    /// 更新既有列而不是長出第二筆）。</summary>
    internal const string RoutineSyncChangeType = "例行同步（彙總）";

    /// <summary>例行同步彙總列的去重鍵：**不含對數**——含了的話對數一變就會長出第二筆，
    /// 而不是更新既有那筆。</summary>
    internal static string RoutineSyncDedupeKey(string hostName, DateTime date) =>
        PermissionChangeRecord.DedupeKey(hostName, date.Date, 0, RoutineSyncChangeType);

    /// <summary>
    /// 把「同一主機日內 (成員, 群組) 相同的成員新增＋成員移除」配對；成對數達門檻時，
    /// 從 <paramref name="records"/> 移除這些成對紀錄並回傳一筆彙總列，否則回傳 null
    /// （全部維持逐則）。AD 自動化程序（例如每天先清空再重建的群組同步腳本）一天能產生
    /// 數萬則這種對稱異動，逐則列出會把真正可疑的異動淹沒。
    ///
    /// 刻意只合併這一種形狀：未成對的異動、剖不出成員或群組的異動、以及**特權群組**的異動
    /// （即使成對）一律逐則——安全訊號不能被降噪機制吞掉。
    /// </summary>
    internal static PermissionChangeRecord? ExtractRoutineSyncPairs(
        List<PermissionChangeRecord> records, string hostName, DateTime date)
    {
        bool IsPairable(PermissionChangeRecord r) =>
            (r.ChangeType == "成員新增" || r.ChangeType == "成員移除") &&
            !r.IsPrivilegedTarget &&
            !string.IsNullOrWhiteSpace(r.Target) &&
            !string.IsNullOrWhiteSpace(r.TargetAccount);

        var paired = new HashSet<PermissionChangeRecord>();
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairCount = 0;

        foreach (var group in records.Where(IsPairable)
                     .GroupBy(r => (
                         Account: r.TargetAccount!.Trim().ToUpperInvariant(),
                         Target: r.Target.Trim().ToUpperInvariant())))
        {
            var added = group.Where(r => r.ChangeType == "成員新增").ToList();
            var removed = group.Where(r => r.ChangeType == "成員移除").ToList();
            var pairs = Math.Min(added.Count, removed.Count);   // 一則只配一次，多出來的照常逐則
            if (pairs == 0) continue;

            for (var i = 0; i < pairs; i++)
            {
                paired.Add(added[i]);
                paired.Add(removed[i]);
            }


            pairCount += pairs;
            groups.Add(group.Key.Target);
            accounts.Add(group.Key.Account);
        }

        if (pairCount < RoutineSyncPairThreshold) return null;

        // 涵蓋區間取被合併掉那些事件的首末時間（彙總列自己的 DetectedAt 只有日期）
        var coveredFrom = paired.Min(r => r.DetectedAt);
        var coveredTo = paired.Max(r => r.DetectedAt);

        records.RemoveAll(paired.Contains);

        return new PermissionChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            HostName = hostName,
            DetectedAt = date.Date,
            Target = $"{hostName}（例行同步）",
            ChangeType = RoutineSyncChangeType,
            Category = PermissionCategory.Summary,
            IsPrivilegedTarget = false,
            InitiatorAccount = null,
            TargetAccount = null,
            Before = string.Empty,
            After = string.Empty,
            AlertText =
                $"本日偵測到 {pairCount} 對「成員新增＋成員移除」的對稱異動" +
                $"（涉及 {groups.Count} 個群組、{accounts.Count} 個帳號），未逐則列出。" +
                "此模式可能是 AD 自動化程序（例如每天以先清空再重建的方式同步群組成員的腳本）產生；" +
                "未成對的異動仍逐則列出。",
            Source = PermissionChangeSources.Netiq,
            EventId = null,
            CoveredFrom = coveredFrom,
            CoveredTo = coveredTo,
            PairCount = pairCount
        };
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

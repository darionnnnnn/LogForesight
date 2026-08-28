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
    /// <summary>
    /// 重跑某個主機日之前的「該不該用新結果取代舊結果」判定（第三十一輪）——本機路徑與 NetIQ
    /// 機房路徑共用同一份，不各寫一份（兩邊各寫一份的第一個代價就是刪除時機一個寫在分析前、
    /// 一個寫在分析後）。
    ///
    /// 回 true＝保留原結果、整天跳過（不刪、不分析、不寫入）。原始事件不存 DB，重跑等於重新向
    /// 來源（Windows Event Log／Sentinel）取數，取不到或只取得到殘缺的資料時，用它覆蓋掉當初
    /// 完整分析出來的結果是淨損失。
    /// </summary>
    /// <param name="eventCount">本次自來源取得的事件數</param>
    /// <param name="dataIncomplete">本次取數對這一天是否不完整（來源已滾掉部分事件／查詢被截斷）</param>
    /// <param name="sourceDegraded">來源本身降級（頻道存取被拒等），取得到的事件不足以還原當初的判斷</param>
    public static bool ShouldRetainExistingDay(int eventCount, bool dataIncomplete, bool sourceDegraded) =>
        eventCount == 0 || dataIncomplete || sourceDegraded;

    /// <summary>重跑結果的申報字串——兩條路徑共用，措辭不各寫一份</summary>
    public static string RerunSummary(int analyzed, int retained) =>
        $"重新分析 {analyzed} 天，保留原結果 {retained} 天（來源已無資料或資料不完整）";

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
    /// 2. useAi == true &amp;&amp; !AiPending &amp;&amp; !AiAnalyzed &amp;&amp; RiskLevel != Low → 需要
    /// 3. 其餘 → 不需要
    ///
    /// **AiPending == true 不再視為取數要補的（回饋三十五輪體檢輪）**：AI 補寫已由獨立的
    /// AI 分析排程負責（AiAnalysisHostedService 常駐掃 ai_pending=1），取數側若仍把待補
    /// 當成缺漏日，「只補跑失敗或未執行」會對整個 AI 積壓重打 Sentinel、重跑統計、
    /// 重寫紀錄再標回待補——與 AI 排程互相打架，補跑歷史因此永不收斂。
    /// 規則 2 保的是「AI 已定案失敗」的存量（pending 已被清成 false 的舊資料）；
    /// 排程拆分後 AI 失敗會維持 pending=true 由 AI 排程重試，這條幾乎只對舊資料生效。
    ///
    /// useAi == false（統計模式，AI 未設定）時，AiAnalyzed 恆為 false 屬正常狀態，
    /// 規則 2 不觸發——只有「缺日」需要補跑。
    /// 低風險日「刻意不呼叫 AI」是合法終局狀態，不視為失敗。
    /// </summary>
    public static bool NeedsBackfill(DailyAnalysisRecord? record, bool useAi)
    {
        if (record == null) return true;
        if (useAi && !record.AiPending && !record.AiAnalyzed && record.RiskLevel != RiskLevels.Low) return true;
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

    /// <param name="hostDayClaims">主機日層級的佔位（回饋三十四輪 A2）：多台 Sentinel 平行處理時
    /// 保證同一個主機日只被處理一次。**只放「主機＋日期」的鍵，不放事件內容**——舊做法是把整個
    /// 查詢窗的內容去重鍵全載進來，正式環境每日近十萬筆權限異動會讓它膨脹到數 GB 且整趟不釋放。
    /// 跨執行的內容去重改由資料庫承擔（見下方 <see cref="PermissionChangeStore.GetDedupeKeysForHost"/>）。</param>
    public static void RecordPermissionChanges(
        PermissionChangeStore? permissionChangeStore,
        ConcurrentDictionary<string, byte> hostDayClaims,
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

        // 同一個主機日只處理一次：一台主機只登錄在一台 Sentinel 上，正常情況下不會重入，
        // 這道佔位是平行處理下的安全網（同時也讓佔位集合的大小停在「主機數×天數」量級）
        if (!hostDayClaims.TryAdd($"{HostNameKey.Of(hostName)}|{date:yyyyMMdd}", 0)) return;

        try
        {
            var matchingEvents = events
                .Where(e => e.EventId != 0 && PermissionChangeEventTypes.ContainsKey(e.EventId))
                .ToList();

            if (matchingEvents.Count == 0) return;

            // 每主機日不設筆數上限：權限異動全部逐則入庫才查得到、篩得到（一台吵雜的 DC
            // 一天可達數萬則）。量的控制交給保留天數清理（PermissionChangeStore 依 created_at）。
            //
            // 這裡先把來源本次回傳的當日事件全部轉成紀錄（**還不去重**）：例行同步彙總是對
            // 「當日全部事件」的判斷，先去重的話重跑時只剩子集，成對數會假性跌破門檻——
            // 被合併掉的成對明細從未入庫，去重鍵撈不到它們。
            var dayRecords = new List<PermissionChangeRecord>();

            foreach (var evt in matchingEvents)
            {
                var changeType = PermissionChangeEventTypes[evt.EventId];
                // AlertText 是清單與去重鍵用的 500 字截斷版（長度上限的來龍去脈見 LfDbContext 的 dedupe_key 註解）；
                // 展開明細要看的完整原文另存 RawText（回饋二十八輪 P9）
                var alertText = TextTruncation.Truncate(evt.Message ?? string.Empty, 500);

                var details = PermissionChangeExtractor.Extract(
                    evt.Message, changeType, evt.EventId, evt.InitiatorAccount, mappings);

                dayRecords.Add(new PermissionChangeRecord
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
                    RawText = evt.Message == null ? null : TextTruncation.Truncate(evt.Message, RawTextMaxLength),
                    Source = PermissionChangeSources.Netiq,
                    EventId = evt.EventId
                });
            }

            // 本批內先去重一次（終檢抓到）：去重移到配對之後，同一次回應若把同一則事件回兩次，
            // 配對會把它算成兩對，成對數就被灌水、可能假性跨過門檻把該逐則列出的異動吞掉。
            // 這裡只折疊「本批內完全相同的事件」，與跨執行的 knownKeys 佔位是兩件事。
            var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
            dayRecords.RemoveAll(r => !seenInBatch.Add(PermissionChangeRecord.DedupeKey(
                hostName, r.DetectedAt, r.EventId ?? 0, r.AlertText ?? string.Empty)));

            var routineKey = RoutineSyncDedupeKey(hostName, date);
            var pairing = FindRoutineSyncPairs(dayRecords);

            if (pairing.PairCount >= RoutineSyncPairThreshold)
            {
                permissionChangeStore.UpsertByDedupeKey(
                    BuildRoutineSummary(pairing, hostName, date), routineKey);

                // 這批成對事件先前可能已經以逐則形式入庫（前一次取數只回了子集、成對數不足門檻，
                // 於是全部逐則列出）。現在升級成彙總列，那些逐則列必須撤掉，
                // 否則同一批異動同時以彙總與逐則兩種形式存在、被重複計算——
                // 正是作業 A 要消滅的症狀，只是方向相反（終檢抓到）。
                permissionChangeStore.DeleteByDedupeKeys(
                    pairing.Paired.Select(r => PermissionChangeRecord.DedupeKey(
                        hostName, r.DetectedAt, r.EventId ?? 0, r.AlertText ?? string.Empty)));

                dayRecords.RemoveAll(pairing.Paired.Contains);
            }
            else if (pairing.PairCount > 0 && permissionChangeStore.ExistsByDedupeKey(routineKey))
            {
                // 已有彙總列＝這天先前看過完整的例行同步。這次來源只回子集（成對數不足門檻）時，
                // 這批成對事件是被那筆彙總列涵蓋的同一批，逐則列出會與彙總重複計算——
                // 略過它們，但**不撤掉彙總列**：撤掉等於用一次不完整的取數抹掉完整那次的結論。
                dayRecords.RemoveAll(pairing.Paired.Contains);
            }

            // 冪等：同一主機日重跑（回補、重新分析）不得產生重複紀錄。跨執行的去重鍵改由資料庫
            // 現查（回饋三十四輪 A2）：只撈這台主機、這批事件時間範圍內的既有鍵，走
            // (host_name_key, detected_at) 索引，查回來的集合是區域變數、離開這個方法就回收。
            if (dayRecords.Count == 0) return;   // 成對事件全被彙總／略過後就沒有逐則列要寫了

            var earliest = dayRecords.Min(r => r.DetectedAt);
            var latest = dayRecords.Max(r => r.DetectedAt);
            var existingKeys = permissionChangeStore.GetDedupeKeysForHost(hostName, earliest, latest);

            var recordsToAppend = new List<PermissionChangeRecord>();
            foreach (var record in dayRecords)
            {
                var key = PermissionChangeRecord.DedupeKey(
                    hostName, record.DetectedAt, record.EventId ?? 0, record.AlertText ?? string.Empty);
                if (!existingKeys.Contains(key)) recordsToAppend.Add(record);
            }

            if (recordsToAppend.Count > 0)
            {
                permissionChangeStore.AppendChanges(recordsToAppend);
                Log.Info("{Context}{Date:yyyy-MM-dd} 權限異動檢核：寫入 {Count} 筆紀錄", logContext, date, recordsToAppend.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 權限異動檢核寫入失敗（不影響分析結果）", logContext, date);
        }
    }

    /// <summary>入庫原文（RawText）的長度上限（回饋三十四輪 A4）。展開明細要看完整原文，
    /// 所以留得比清單用的 AlertText（500 字）寬得多；但完全不設限時，一台吵雜的 DC 一天數萬則
    /// 全帶完整原文，記憶體與資料庫體積都沒有上界。8000 字足以涵蓋實務上的權限異動事件全文。</summary>
    internal const int RawTextMaxLength = 8000;

    /// <summary>成員新增＋成員移除成對出現達此對數時，該主機日的成對異動合併為一筆彙總列。
    /// 門檻不做成設定項：這是「明顯是自動化程序」的判斷，不是使用者要調的參數。</summary>
    internal const int RoutineSyncPairThreshold = 50;

    /// <summary>例行同步彙總列的異動類型；去重鍵用的固定字串也是它（不含對數，對數變動時
    /// 更新既有列而不是長出第二筆）。</summary>
    internal const string RoutineSyncChangeType = PermissionCategory.RoutineSyncChangeType;

    /// <summary>例行同步彙總列的去重鍵：**不含對數**——含了的話對數一變就會長出第二筆，
    /// 而不是更新既有那筆。</summary>
    internal static string RoutineSyncDedupeKey(string hostName, DateTime date) =>
        PermissionChangeRecord.DedupeKey(hostName, date.Date, 0, RoutineSyncChangeType);

    /// <summary>例行同步配對結果：哪些紀錄成了對、共幾對、涉及多少群組與帳號。
    /// 門檻判斷與「要不要撤既有彙總列」由呼叫端決定——這裡只認事實。</summary>
    internal sealed record RoutineSyncPairing(
        HashSet<PermissionChangeRecord> Paired,
        int PairCount,
        int GroupCount,
        int AccountCount);

    /// <summary>
    /// 把「同一主機日內 (成員, 群組) 相同的成員新增＋成員移除」配對並回報結果
    /// （**不改動 <paramref name="records"/>**）。AD 自動化程序（例如每天先清空再重建的
    /// 群組同步腳本）一天能產生數萬則這種對稱異動，逐則列出會把真正可疑的異動淹沒。
    ///
    /// 刻意只認這一種形狀：未成對的異動、剖不出成員或群組的異動、以及**特權群組**的異動
    /// （即使成對）一律不算——安全訊號不能被降噪機制吞掉。
    /// </summary>
    internal static RoutineSyncPairing FindRoutineSyncPairs(List<PermissionChangeRecord> records)
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

        return new RoutineSyncPairing(paired, pairCount, groups.Count, accounts.Count);
    }

    /// <summary>依配對結果組出彙總列（呼叫端已確認達門檻）。</summary>
    internal static PermissionChangeRecord BuildRoutineSummary(
        RoutineSyncPairing pairing, string hostName, DateTime date)
    {
        var pairCount = pairing.PairCount;

        // 涵蓋區間取被合併掉那些事件的首末時間（彙總列自己的 DetectedAt 只有日期）
        var coveredFrom = pairing.Paired.Min(r => r.DetectedAt);
        var coveredTo = pairing.Paired.Max(r => r.DetectedAt);

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
            // 彙總列不存 RawText：它代表的是多則異動合併後的結果，沒有單一的原始訊息可對應
            AlertText =
                $"本日偵測到 {pairCount} 對「成員新增＋成員移除」的對稱異動" +
                $"（涉及 {pairing.GroupCount} 個群組、{pairing.AccountCount} 個帳號），未逐則列出。" +
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

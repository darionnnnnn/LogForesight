namespace LogForesight.Core.Service;

public class ScheduleValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 排程時間窗計算（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）：純函數，不碰任何 I/O，方便單測。
/// 全部以「一天內的分鐘數」（0~1439）運算，跨午夜窗口（<c>Start &gt; End</c>）在需要比較區間時
/// 正規化成兩段（<c>[Start,1440)</c>＋<c>[0,End)</c>），其餘情況直接用 <see cref="IsWithinWindow"/>
/// 的環狀比較，不用真的拆兩段物件。
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>視窗數上限——分析單位是「日」，更多窗口沒有對應的工作量，也是後端強制的驗證上限</summary>
    public const int MaxWindows = 4;

    /// <summary>
    /// 儲存驗證（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3「儲存驗證，後端強制非僅 UI」）：
    /// HH:mm 格式、Start != End、至少一個窗口、上限 <see cref="MaxWindows"/> 組、窗口間不重疊
    /// （跨午夜窗口先正規化成分鐘區間再驗）。重疊直接列出衝突的組別，不做聰明合併。
    /// </summary>
    public static ScheduleValidationResult Validate(List<ScheduleWindow> windows)
    {
        var errors = new List<string>();

        if (windows.Count == 0)
        {
            errors.Add("至少要有一個執行窗口。");
            return new ScheduleValidationResult { IsValid = false, Errors = errors };
        }
        if (windows.Count > MaxWindows)
        {
            errors.Add($"執行窗口最多 {MaxWindows} 組。");
        }

        var parsed = new List<(int Start, int End, int Index)>();
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var ok = true;
            if (!TryParseMinutes(w.Start, out var start))
            {
                errors.Add($"第 {i + 1} 組的開始時間「{w.Start}」格式不合法，需為 HH:mm。");
                ok = false;
            }
            if (!TryParseMinutes(w.End, out var end))
            {
                errors.Add($"第 {i + 1} 組的結束時間「{w.End}」格式不合法，需為 HH:mm。");
                ok = false;
            }
            if (!ok) continue;

            if (start == end)
            {
                errors.Add($"第 {i + 1} 組的開始與結束時間不可相同。");
                continue;
            }
            parsed.Add((start, end, i));
        }

        // 重疊檢查：跨午夜窗口（Start > End）先正規化成兩段分鐘區間再兩兩比較
        var segments = new List<(int Start, int End, int Index)>();
        foreach (var (start, end, index) in parsed)
        {
            if (start < end)
            {
                segments.Add((start, end, index));
            }
            else
            {
                segments.Add((start, 1440, index));
                segments.Add((0, end, index));
            }
        }

        var reportedPairs = new HashSet<(int, int)>();
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                if (segments[i].Index == segments[j].Index) continue;
                if (segments[i].Start >= segments[j].End || segments[j].Start >= segments[i].End) continue;

                var pair = segments[i].Index < segments[j].Index
                    ? (segments[i].Index, segments[j].Index)
                    : (segments[j].Index, segments[i].Index);
                if (reportedPairs.Add(pair))
                {
                    errors.Add($"第 {pair.Item1 + 1} 組與第 {pair.Item2 + 1} 組時間重疊。");
                }
            }
        }

        return new ScheduleValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }

    /// <summary>解析 HH:mm 為當天分鐘數（0~1439）；格式不合法回 false</summary>
    public static bool TryParseMinutes(string hhmm, out int minutes)
    {
        minutes = 0;
        if (!TimeSpan.TryParseExact(hhmm ?? "", @"hh\:mm", null, out var ts)) return false;
        if (ts.Ticks < 0 || ts.TotalMinutes >= 1440) return false;
        minutes = (int)ts.TotalMinutes;
        return true;
    }

    /// <summary>「本趟為今日第 n 個執行窗口」：窗口依開始時間排序（跨午夜窗口算在它開始的那一天）；現在不在任何窗口內回 null。</summary>
    public static string? DescribeCurrentWindow(DateTime now, IEnumerable<ScheduleWindow> windows)
    {
        var ordered = windows.OrderBy(w => w.Start, StringComparer.Ordinal).ToList();
        var index = ordered.FindIndex(w => IsWithinWindow(now, w));
        return index < 0 ? null : $"本趟為今日第 {index + 1} 個執行窗口（{ordered[index].Start}～{ordered[index].End}）";
    }

    /// <summary>
    /// 排程趟該不該優雅停止。一般排程趟看「還在不在窗口內」；自動補跑本來就在窗口外開跑，
    /// 用那個判斷會在下一次輪詢被取消、等於補不到東西，所以看自己的停止時刻。
    /// </summary>
    public static bool ShouldStopScheduledRun(DateTime now, IEnumerable<ScheduleWindow> windows, DateTime? catchUpDeadline) =>
        catchUpDeadline is { } deadline ? now >= deadline : !IsWithinAnyWindow(now, windows);

    /// <summary>
    /// now 所在窗口實例的起訖（跨午夜時 End 落在隔天）；不在任何窗口內回 null。
    /// 排程趟的停止點是自己這個實例的 End：只看「還在不在任何窗口內」的話，相鄰窗口（01:00～03:00、03:00～05:00）
    /// 前一趟會一路跑進下一個窗口，第二個窗口的觸發就被吃掉了。
    /// </summary>
    public static (DateTime Start, DateTime End)? CurrentWindowInstance(DateTime now, IEnumerable<ScheduleWindow> windows)
    {
        foreach (var w in windows)
        {
            if (CurrentWindowInstanceStart(now, w) is not { } start) continue;
            if (!TryWindowLength(w, out _, out var length)) continue;
            return (start, start.AddMinutes(length));
        }
        return null;
    }

    /// <summary>窗口的起點（分鐘）與長度（分鐘）：跨午夜時長度跨到隔天。任一端解析不了回 false。</summary>
    private static bool TryWindowLength(ScheduleWindow w, out int startMinutes, out int length)
    {
        length = 0;
        if (!TryParseMinutes(w.Start, out startMinutes) || !TryParseMinutes(w.End, out var endMinutes)) return false;
        length = endMinutes > startMinutes ? endMinutes - startMinutes : endMinutes + 24 * 60 - startMinutes;
        return true;
    }

    /// <summary>
    /// 「應該已經有資料」的最後一個資料日（唯一判定點，報表資料完整度用）。
    /// 某天的資料要等之後的排程窗口跑完才分析：取最近一個**已結束**的窗口實例，它的目標日是開跑日的昨天（跨午夜窗口亦同）。
    /// 窗口還沒跑完之前，昨天的資料本來就不該有——不能算缺漏、也不能說資料落後，否則戰情中心每晚都會亮警示。
    /// 排程未啟用或窗口無法解析時退回「昨天」。
    /// </summary>
    public static DateTime ExpectedLastDataDay(DateTime now, bool scheduleEnabled, IEnumerable<ScheduleWindow> windows)
    {
        var fallback = now.Date.AddDays(-1);
        if (!scheduleEnabled) return fallback;
        DateTime? lastEndedStart = null;
        foreach (var w in windows)
        {
            if (!TryWindowLength(w, out var startMinutes, out var length)) continue;
            // 最長的窗口不到 24 小時，往回看三天一定找得到已結束的實例
            for (var offset = 0; offset <= 2; offset++)
            {
                var start = now.Date.AddDays(-offset).AddMinutes(startMinutes);
                if (start.AddMinutes(length) <= now && (lastEndedStart == null || start > lastEndedStart)) lastEndedStart = start;
            }
        }
        return lastEndedStart is { } s ? s.Date.AddDays(-1) : fallback;
    }

    /// <summary>同一窗口續跑：距窗口結束與距上一趟開跑都要留這麼久（暫定），避免反覆失敗時空轉。</summary>
    public static readonly TimeSpan WindowContinueGap = TimeSpan.FromMinutes(10);

    /// <summary>同一窗口實例最多跑幾趟（含續跑與異常中斷後的補跑）。</summary>
    public const int MaxRunsPerWindowInstance = 2;

    /// <summary>NetIQ 回 0 筆待複查的主機日，距上一次嘗試至少要隔這麼久才複查（暫定）；管線與排程共用這一個常數。</summary>
    public static readonly TimeSpan EmptyRecheckGap = TimeSpan.FromMinutes(60);

    /// <summary>實例內只為空資料複查而多跑一趟時的趟數上限（失敗續跑仍是 <see cref="MaxRunsPerWindowInstance"/>）。</summary>
    public const int MaxRunsWithEmptyRecheck = 3;

    private static List<BatchRun> InstanceRuns(DateTime instanceStart, IEnumerable<BatchRun> recentRuns) =>
        recentRuns
            .Where(r => r.JobType != BatchRun.JobTypeAi && r.Trigger == BatchRunTriggers.Schedule && r.WindowInstanceStart == instanceStart)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.RunId)
            .ToList();

    /// <summary>
    /// 下一次空資料複查的時刻（唯一判定點）：now 在窗口內、實例內已有排程取數趟但未達 <see cref="MaxRunsWithEmptyRecheck"/>、
    /// 最新一趟沒被停止或異常中斷，候選＝最新一趟結束＋<see cref="EmptyRecheckGap"/>（還沒結束以 now 估；已過則取 now）；
    /// 候選距實例結束不足 <see cref="WindowContinueGap"/> 就不排（回 null）。
    /// 是否真的有待複查的主機日由 <see cref="RunToContinue"/> 看上一趟的 <see cref="RunResume"/>。
    /// </summary>
    public static DateTime? NextEmptyRecheckAt(DateTime now, IEnumerable<ScheduleWindow> windows, IEnumerable<BatchRun> recentRuns)
    {
        if (CurrentWindowInstance(now, windows) is not { } instance) return null;
        var inInstance = InstanceRuns(instance.Start, recentRuns);
        if (inInstance.Count == 0 || inInstance.Count >= MaxRunsWithEmptyRecheck) return null;
        var last = inInstance[0];
        if (last.Stopped || last.Interrupted) return null;
        // 從上一趟「結束」起算：那一趟的每個空資料嘗試都發生在結束之前，結束＋間隔時一定全部滿間隔。
        // 從開跑起算的話，跑很久的第一趟（3000 台、數小時）一結束就排複查，管線卻因各主機日未滿間隔全部延後，白跑一趟還吃掉趟數名額
        var candidate = (last.FinishedAt ?? now) + EmptyRecheckGap;
        if (candidate < now) candidate = now;
        return instance.End - candidate < WindowContinueGap ? null : candidate;
    }

    /// <summary>
    /// 同一窗口該不該再跑一趟：上一趟在窗口內提早結束、還留著失敗或待補。回傳那一趟（null＝不續跑）。
    /// 計數讀執行紀錄，站台重啟不歸零。被窗口結束或手動停掉的不續跑（等下一個窗口）；補跑與手動趟沒有窗口實例，不算在內。
    /// 留有 PRTG 待補裝置或失敗數值的趟（停損、重排後仍失敗、數值停損）另外從結束時刻起算間隔：
    /// 執行紀錄分不出「停損」與「一般失敗」，一律等；冷卻只在那一趟的實例內，馬上再跑等於又打一次剛出問題的 PRTG。
    /// </summary>
    public static BatchRun? RunToContinue(DateTime now, IEnumerable<ScheduleWindow> windows, IEnumerable<BatchRun> recentRuns)
    {
        var runs = recentRuns as IReadOnlyCollection<BatchRun> ?? recentRuns.ToList();
        if (CurrentWindowInstance(now, windows) is not { } instance) return null;
        if (instance.End - now < WindowContinueGap) return null;

        var inInstance = InstanceRuns(instance.Start, runs);
        if (inInstance.Count == 0 || inInstance.Count >= MaxRunsWithEmptyRecheck) return null;

        var last = inInstance[0];
        if (last.FinishedAt is not { } finishedAt || last.Stopped || last.Interrupted) return null;
        if (now - last.StartedAt < WindowContinueGap) return null;

        var resume = RunResume.Of(last);
        if (!resume.HasWork) return null;
        if (resume.OnlyEmptyPending)
            // 只剩空資料待複查：間隔與趟數上限由 NextEmptyRecheckAt 這唯一判定點決定
            return NextEmptyRecheckAt(now, windows, runs) is { } recheckAt && now >= recheckAt ? last : null;
        // 還有失敗或待補：沿用失敗續跑規則（實例內最多 MaxRunsPerWindowInstance 趟）
        if (inInstance.Count >= MaxRunsPerWindowInstance) return null;
        if ((resume.PrtgDevices > 0 || resume.PrtgSensors > 0) && now - finishedAt < WindowContinueGap) return null;
        return last;
    }

    /// <summary>單一窗口是否涵蓋 now（環狀比較，天生處理跨午夜：Start &gt; End 時判斷「晚於 Start 或早於 End」）</summary>
    public static bool IsWithinWindow(DateTime now, ScheduleWindow window)
    {
        if (!TryParseMinutes(window.Start, out var start) || !TryParseMinutes(window.End, out var end)) return false;
        var nowMinutes = now.Hour * 60 + now.Minute;
        return start < end
            ? nowMinutes >= start && nowMinutes < end
            : nowMinutes >= start || nowMinutes < end;
    }

    public static bool IsWithinAnyWindow(DateTime now, IEnumerable<ScheduleWindow> windows) =>
        windows.Any(w => IsWithinWindow(now, w));

    /// <summary>
    /// now 所屬窗口「這一次」的起始時刻——跨午夜窗口（例如 22:00→06:00）凌晨仍算「昨晚那次」，
    /// 不是今天一個新的實例。now 不在這個窗口內時回 null。
    /// </summary>
    public static DateTime? CurrentWindowInstanceStart(DateTime now, ScheduleWindow window)
    {
        if (!IsWithinWindow(now, window)) return null;
        if (!TryParseMinutes(window.Start, out var start)) return null;

        var todayStart = now.Date.AddMinutes(start);
        return todayStart <= now ? todayStart : todayStart.AddDays(-1);
    }

    /// <summary>
    /// 下一次觸發時刻：全部窗口的 Start 裡，距離 now 最近的下一個（今天已過就找明天同一個 Start）。
    /// 窗口清單為空時回 null。
    /// </summary>
    public static DateTime? NextTriggerTime(DateTime now, IEnumerable<ScheduleWindow> windows)
    {
        DateTime? earliest = null;
        foreach (var w in windows)
        {
            if (!TryParseMinutes(w.Start, out var start)) continue;

            var candidate = now.Date.AddMinutes(start);
            if (candidate <= now) candidate = candidate.AddDays(1);

            if (earliest == null || candidate < earliest.Value) earliest = candidate;
        }
        return earliest;
    }

    /// <summary>
    /// 現在是否該觸發一次排程執行：now 落在某個窗口內，且**那個窗口目前這次的實例**還沒有觸發過
    /// （<paramref name="recentScheduledTriggerTimes"/> 裡沒有任何一筆落在該實例的起訖區間）。
    /// 同一個函式服務兩個呼叫端：常態輪詢（週期性檢查是否該觸發）與服務啟動時的補觸發
    /// （只在重新啟動時仍落在窗口內才會補觸發；錯過整個窗口不會補）——語意完全相同，不需要兩套邏輯。
    /// </summary>
    public static bool ShouldTriggerNow(DateTime now, IEnumerable<ScheduleWindow> windows, IEnumerable<DateTime> recentScheduledTriggerTimes)
    {
        var triggerTimes = recentScheduledTriggerTimes as IReadOnlyCollection<DateTime> ?? recentScheduledTriggerTimes.ToList();

        foreach (var w in windows)
        {
            var instanceStart = CurrentWindowInstanceStart(now, w);
            if (instanceStart == null) continue;

            if (!WindowAlreadyTriggered(now, instanceStart.Value, triggerTimes)) return true;
        }
        return false;
    }

    /// <summary>
    /// 這個窗口實例是否已經觸發過（唯一判定點，供 <see cref="ShouldTriggerNow"/> 與
    /// 「手動執行是否真的佔用了自動觸發」共用——同一個判定寫兩份會漂移）。
    /// </summary>
    public static bool WindowAlreadyTriggered(
        DateTime now, DateTime windowInstanceStart, IEnumerable<DateTime> scheduledTriggerTimes) =>
        scheduledTriggerTimes.Any(t => t >= windowInstanceStart && t <= now);

    /// <summary>
    /// t 所屬的「窗口群組鍵」：全部窗口中，開始時刻不晚於 t 的最近一個窗口實例開始時刻
    /// （今天的 Start 若晚於 t 就取昨天的同一個 Start）。窗口清單為空或全部無法解析時回 null。
    /// </summary>
    public static DateTime? MostRecentWindowStart(DateTime t, IEnumerable<ScheduleWindow> windows)
    {
        DateTime? latest = null;
        foreach (var w in windows)
        {
            if (!TryParseMinutes(w.Start, out var start)) continue;
            var cand = t.Date.AddMinutes(start);
            var instanceStart = cand <= t ? cand : cand.AddDays(-1);
            if (latest == null || instanceStart > latest.Value)
            {
                latest = instanceStart;
            }
        }
        return latest;
    }

    /// <summary>
    /// 排程觸發時間：一般排程執行一律計入；異常中斷的排程執行，同一個窗口群組鍵內「最早的那一筆」不計入
    /// （讓該窗口可以補跑一次），第二筆起照常計入（避免反覆崩潰時無限重跑）。群組鍵為 null 的中斷執行照常計入。
    /// 回傳值另與 inMemoryTriggerTimes 聯集。
    /// </summary>
    public static IEnumerable<DateTime> EffectiveScheduleTriggerTimes(
        IEnumerable<(DateTime StartedAt, bool Interrupted)> scheduledRuns,
        IEnumerable<DateTime> inMemoryTriggerTimes,
        IEnumerable<ScheduleWindow> windows)
    {
        var windowList = windows as IReadOnlyCollection<ScheduleWindow> ?? windows.ToList();
        var result = new List<DateTime>();
        var interruptedWithKey = new List<(DateTime StartedAt, DateTime? Key)>();

        foreach (var run in scheduledRuns)
        {
            if (!run.Interrupted)
            {
                result.Add(run.StartedAt);
            }
            else
            {
                var key = MostRecentWindowStart(run.StartedAt, windowList);
                if (key == null)
                {
                    result.Add(run.StartedAt);
                }
                else
                {
                    interruptedWithKey.Add((run.StartedAt, key));
                }
            }
        }

        var grouped = interruptedWithKey.GroupBy(x => x.Key!.Value);
        foreach (var g in grouped)
        {
            foreach (var item in g.OrderBy(x => x.StartedAt).Skip(1))
            {
                result.Add(item.StartedAt);
            }
        }

        return result.Union(inMemoryTriggerTimes);
    }

    /// <summary>
    /// 目前窗口實例內「被 <see cref="EffectiveScheduleTriggerTimes"/> 略過不計入」的那筆中斷執行的開始時間——
    /// 也就是讓這個窗口得以再跑一次的原因；窗口外、或窗口內沒有中斷執行時為 null。補跑說明用。
    /// </summary>
    public static DateTime? InterruptedRunAllowingCatchUp(
        DateTime now,
        IEnumerable<(DateTime StartedAt, bool Interrupted)> scheduledRuns,
        IEnumerable<ScheduleWindow> windows)
    {
        var windowList = windows as IReadOnlyCollection<ScheduleWindow> ?? windows.ToList();
        var currentKey = MostRecentWindowStart(now, windowList);
        if (currentKey == null) return null;

        DateTime? earliest = null;
        foreach (var run in scheduledRuns)
        {
            if (!run.Interrupted) continue;
            if (MostRecentWindowStart(run.StartedAt, windowList) != currentKey) continue;
            if (earliest == null || run.StartedAt < earliest) earliest = run.StartedAt;
        }
        return earliest;
    }

    /// <summary>自動補跑的最大延遲（窗口結束後 4 小時內）</summary>
    public static readonly TimeSpan AutoCatchUpMaxDelay = TimeSpan.FromHours(4);

    /// <summary>
    /// 回傳所有窗口中，結束時刻 &lt;= now 的最近一個窗口實例的（起, 訖），沒有則 null。
    /// 跨午夜窗口（例 22:00→06:00）的實例訖＝隔天 06:00。窗口 Start/End 解析沿用 TryParseMinutes。
    /// </summary>
    public static (DateTime Start, DateTime End)? LastEndedWindowInstance(DateTime now, IEnumerable<ScheduleWindow> windows)
    {
        (DateTime Start, DateTime End)? latest = null;

        foreach (var w in windows)
        {
            if (!TryParseMinutes(w.Start, out var start) || !TryParseMinutes(w.End, out var end) || start == end)
                continue;

            var candEnd = now.Date.AddMinutes(end);
            var candStart = start < end ? now.Date.AddMinutes(start) : now.Date.AddDays(-1).AddMinutes(start);

            var instance = candEnd <= now
                ? (Start: candStart, End: candEnd)
                : (Start: candStart.AddDays(-1), End: candEnd.AddDays(-1));

            if (latest == null || instance.End > latest.Value.End || (instance.End == latest.Value.End && instance.Start > latest.Value.Start))
            {
                latest = instance;
            }
        }

        return latest;
    }

    /// <summary>
    /// 回傳需要補跑的窗口實例（起, 訖）或 null。規則全部成立才回傳：
    /// 1. now 不在任何窗口內（在窗口內交給既有 ShouldTriggerNow）。
    /// 2. LastEndedWindowInstance 不為 null。
    /// 3. now - 訖 &lt;= maxDelay。
    /// 4. scheduleTriggerTimes 中沒有任何一筆 &gt;= 該實例的起。
    /// 5. scheduleTriggerTimes 至少有一筆（全新安裝、沒有任何歷史紀錄時不補跑）。
    /// </summary>
    public static (DateTime Start, DateTime End)? FindMissedWindow(
        DateTime now,
        IEnumerable<ScheduleWindow> windows,
        IEnumerable<DateTime> scheduleTriggerTimes,
        TimeSpan maxDelay)
    {
        var windowList = windows as IReadOnlyCollection<ScheduleWindow> ?? windows.ToList();
        if (IsWithinAnyWindow(now, windowList)) return null;

        var lastEnded = LastEndedWindowInstance(now, windowList);
        if (lastEnded == null) return null;

        var (start, end) = lastEnded.Value;
        if (now - end > maxDelay) return null;

        var triggerTimes = scheduleTriggerTimes as IReadOnlyCollection<DateTime> ?? scheduleTriggerTimes.ToList();
        if (triggerTimes.Count == 0) return null;
        if (triggerTimes.Any(t => t >= start)) return null;

        return lastEnded;
    }
}

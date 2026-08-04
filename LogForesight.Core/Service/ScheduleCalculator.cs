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
    /// 同一個函式服務兩個呼叫端：常態輪詢（週期性檢查是否該觸發）與服務啟動時的漏跑補償
    /// （docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）——語意完全相同，不需要兩套邏輯。
    /// </summary>
    public static bool ShouldTriggerNow(DateTime now, IEnumerable<ScheduleWindow> windows, IEnumerable<DateTime> recentScheduledTriggerTimes)
    {
        var triggerTimes = recentScheduledTriggerTimes as IReadOnlyCollection<DateTime> ?? recentScheduledTriggerTimes.ToList();

        foreach (var w in windows)
        {
            var instanceStart = CurrentWindowInstanceStart(now, w);
            if (instanceStart == null) continue;

            var alreadyTriggered = triggerTimes.Any(t => t >= instanceStart.Value && t <= now);
            if (!alreadyTriggered) return true;
        }
        return false;
    }
}

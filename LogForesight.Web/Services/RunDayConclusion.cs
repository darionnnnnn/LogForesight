using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 執行總表每個資料日的一句結論（F21-2）。唯一產生點：畫面不再自己從十多欄數字拼判讀。
/// 列是資料日（不是執行日），日期換算只在 <c>RunMonitorService.GetDaySummaries</c>；這裡只讀已算好的計數。
/// 規則（一個資料日只會有一句，依序累加）：
/// 1. 取數進行中 → 「取數進行中」；
/// 2. 有問題 → 「N 台失敗、N 台異常中斷、N 台未執行」（今天那一列的未執行寫「尚未處理」），
///    失敗主機有名單時接「：A、B、C」（最多 3 台），有可補跑的主機時結尾「，可補跑」；
/// 3. 有空資料待複查 → 另接「N 台回空資料，待複查」（還在進行的那一天附何時再查）；
/// 4. 都沒有 → 「全部成功」（有警告再註明台數），沒有任何主機 → 「這天沒有需要處理的主機」。
/// </summary>
public static class RunDayConclusion
{
    /// <summary>結論最多點名幾台失敗主機（完整名單在展開明細）</summary>
    public const int MaxNamedHosts = 3;

    public static string Build(RunDaySummaryDto s)
    {
        if (s.InProgress) return "取數進行中，尚未處理完";

        var problems = new List<string>();
        if (s.FailedCount > 0) problems.Add($"{s.FailedCount} 台失敗");
        if (s.StuckCount > 0) problems.Add($"{s.StuckCount} 台異常中斷");
        if (s.NotRunCount > 0) problems.Add(s.IsToday ? $"{s.NotRunCount} 台尚未處理" : $"{s.NotRunCount} 台未執行");
        // 今天那一列的最後一趟異常中斷：主機狀態可能還沒轉成異常中斷，仍要說出來
        var interrupted = s.IsToday && s.PendingSummary == "最後一趟異常中斷" && s.StuckCount == 0;
        if (interrupted) problems.Add("最後一趟異常中斷");

        var parts = new List<string>();
        if (problems.Count > 0)
        {
            var text = string.Join("、", problems);
            var named = s.FailedHostNames.Take(MaxNamedHosts).ToList();
            if (named.Count > 0)
            {
                var more = s.FailedHostNames.Count + s.OtherFailedCount - named.Count;
                text += "：" + string.Join("、", named) + (more > 0 ? $" 等 {named.Count + more} 台" : "");
            }
            if (s.CanRerun) text += "，可補跑";
            parts.Add(text);
        }

        if (s.RecheckCount > 0)
        {
            var recheck = $"{s.RecheckCount} 台回空資料，待複查";
            if (!string.IsNullOrWhiteSpace(s.RecheckNote)) recheck += $"（{s.RecheckNote}）";
            parts.Add(recheck);
        }

        if (parts.Count == 0)
        {
            if (s.TotalHosts == 0) return "這天沒有需要處理的主機";
            var ok = s.WarningCount > 0 ? $"全部成功，其中 {s.WarningCount} 台有警告" : "全部成功";
            if (s.StoppedCount > 0) ok += $"；{s.StoppedCount} 台已停止，下次執行自動回補";
            return ok;
        }

        if (s.StoppedCount > 0) parts.Add($"{s.StoppedCount} 台已停止，下次執行自動回補");
        return string.Join("；", parts);
    }
}

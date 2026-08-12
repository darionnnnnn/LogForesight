using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 執行監控（docs/WEB-SPEC.md §9.10）——dev 每天早上的第一個畫面。
///
/// 回答的問題是「昨晚每台主機都跑了嗎、有沒有出問題」，
/// 而不是「分析結果是什麼」（那是儀表板的事）。
/// </summary>
public class RunMonitorService
{
    private readonly BatchRunStore _runs;
    private readonly IHostStore _hosts;
    private readonly IAnalysisRecordQuery _records;
    private readonly IUserStore _users;

    /// <summary>執行超過這個時數仍未回報結束，視為異常中斷（而不是還在跑）</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromHours(6);

    /// <summary>失敗主機清單的顯示上限——超過的部分只算數量，避免單一異常日把整頁撐爆</summary>
    private const int MaxFailedHostNames = 10;

    public RunMonitorService(BatchRunStore runs, IHostStore hosts, IAnalysisRecordQuery records, IUserStore users)
    {
        _runs = runs;
        _hosts = hosts;
        _records = records;
        _users = users;
    }

    /// <summary>主機清單以「有回報過的主機」與「已登記的主機」聯集為準：
    /// 只看已登記會漏掉尚未加入 Web 的主機，只看回報過的會漏掉「從來沒跑過」的主機——
    /// 而後者正是最需要被看到的一種。回傳 HostRef 而非純名稱：NetIQ 主機沒有個別的
    /// <see cref="BatchRun"/> 紀錄可比對（見 <see cref="StatusOf"/>），需要 HostId／Source 才能
    /// 判斷要走哪一套狀態邏輯。</summary>
    private List<HostRef> AllHosts(List<BatchRun> runs)
    {
        var activeHosts = _hosts.GetAll().Where(h => h.Active).ToList();
        var byName = activeHosts
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return activeHosts.Select(h => h.HostName)
            .Union(runs.Select(r => r.HostName), StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(name => byName.TryGetValue(name, out var host)
                ? new HostRef(name, host.HostId, host.Source)
                : new HostRef(name, 0, "local"))
            .ToList();
    }

    public List<RunDaySummaryDto> GetDaySummaries(int days)
    {
        var runs = _runs.GetRecentRuns(days, hostNames: null);
        var from = DateTime.Today.AddDays(-days + 1);
        var hosts = AllHosts(runs);

        // 「執行日 D」對應「D-1 的分析紀錄是否存在」（NetIQ 與本機回補共用同一套對應，見 StatusOf），
        // 窗口因此整體往前多帶一天。涵蓋全部主機（不只 NetIQ）——本機主機的回補 fallback 也要用
        var recordDates = _records.ListHostDates(from.AddDays(-1), DateTime.Today.AddDays(-1));

        var summaries = new List<RunDaySummaryDto>();
        for (var date = from; date <= DateTime.Today; date = date.AddDays(1))
        {
            var dateStr = date.ToString("yyyy-MM-dd");
            var summary = new RunDaySummaryDto { Date = dateStr, TotalHosts = hosts.Count };
            var failedHosts = new List<string>();

            foreach (var host in hosts)
            {
                var dayRuns = RunsForHostOnDate(runs, host.HostName, dateStr);
                var status = StatusOf(host, dayRuns, date, recordDates);

                switch (status)
                {
                    case "success": summary.SuccessCount++; break;
                    case "backfilled": summary.BackfilledCount++; break;
                    case "warning": summary.WarningCount++; break;
                    case "failed": summary.FailedCount++; failedHosts.Add(host.HostName); break;
                    case "stuck": summary.StuckCount++; failedHosts.Add(host.HostName); break;
                    case "running": summary.RunningCount++; break;
                    // 已停止不列失敗主機清單——那是使用者的明確操作，缺漏日下次執行自動回補
                    case "stopped": summary.StoppedCount++; break;
                    default: summary.NotRunCount++; break;
                }
            }

            summary.FailedHostNames = failedHosts.Take(MaxFailedHostNames).ToList();
            summary.OtherFailedCount = Math.Max(0, failedHosts.Count - MaxFailedHostNames);
            summaries.Add(summary);
        }

        return summaries;
    }

    public List<RunDayHostStatusDto> GetDayDetail(DateTime date)
    {
        // GetRecentRuns 的窗口是 [Today-days+1, Today]；用「到這天為止經過的天數」當窗口大小，
        // 剛好含括目標日期，再於 RunsForHostOnDate 篩到那一天。日期在未來時窗口最小取 1。
        var windowDays = Math.Max(1, (DateTime.Today - date.Date).Days + 1);
        var runs = _runs.GetRecentRuns(windowDays, hostNames: null);
        var dateStr = date.ToString("yyyy-MM-dd");
        var hosts = AllHosts(runs);
        var recordDates = _records.ListHostDates(date.AddDays(-1), date.AddDays(-1));

        return hosts.Select(host =>
        {
            var dayRuns = RunsForHostOnDate(runs, host.HostName, dateStr);
            var cell = host.Source == "netiq"
                ? NetiqCell(host, date, recordDates)
                : LocalCell(host, dateStr, dayRuns, date, recordDates);
            return new RunDayHostStatusDto
            {
                HostName = host.HostName,
                Status = cell.Status,
                RunId = cell.RunId,
                StartedAt = cell.StartedAt,
                FinishedAt = cell.FinishedAt,
                DaysAnalyzed = cell.DaysAnalyzed,
                WarnCount = cell.WarnCount,
                ErrorCount = cell.ErrorCount,
                AiFailures = cell.AiFailures,
                RunCount = cell.RunCount
            };
        }).ToList();
    }

    /// <summary>NetIQ 主機沒有個別 <see cref="BatchRun"/>（管線只登記彙總的一筆，見類別註解），
    /// 「該日是否有分析紀錄」是唯一可用的執行代理指標：分析紀錄的日期是「執行日前一天」
    /// （管線在晚上跑，回補的是昨天的缺漏日，見 <c>NetiqPipelineService</c>）。
    /// 只能判斷 success／none 兩態——沒有個別失敗/警告訊號可用：分析失敗時管線刻意不寫入紀錄，
    /// 下次自動重試（與「沒跑」在資料面完全等價，這是誠實的合併，不是遺漏）。</summary>
    private static string StatusOf(HostRef host, List<BatchRun> dayRuns, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates) =>
        host.Source == "netiq" ? NetiqStatus(host, date, recordDates) : LocalStatus(host, dayRuns, date, recordDates);

    private static string NetiqStatus(HostRef host, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates) =>
        recordDates.Contains((host.HostId, date.AddDays(-1).Date)) ? "success" : "none";

    private static RunDayHostStatusDto NetiqCell(HostRef host, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates) =>
        new() { Status = NetiqStatus(host, date, recordDates) };

    /// <summary>
    /// 本機主機的執行日狀態（§3）：有 BatchRun 時照原本的 exit code／錯誤計數判定；
    /// 當天沒有 BatchRun 時，改看「D-1 的分析紀錄是否存在」——立即執行回補會把缺漏日的分析
    /// 紀錄補上（執行日 D 分析的是 D-1，與 NetIQ 同一套日期對應），但 BatchRun 只登記在觸發當天，
    /// 於是被回補的其他日期原本會誤顯示「未執行」。存在則標「backfilled」（已回補），不冒充
    /// success——「當天真的有跑」與「後來補的資料」要分得出來，符合本頁誠實原則。
    /// </summary>
    private static string LocalStatus(HostRef host, List<BatchRun> dayRuns, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates)
    {
        if (dayRuns.Count > 0) return StatusOf(dayRuns);
        return HasBackfilledRecord(host, date, recordDates) ? "backfilled" : "none";
    }

    private static RunDayHostStatusDto LocalCell(
        HostRef host, string dateStr, List<BatchRun> dayRuns, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates)
    {
        // 有 BatchRun：完整 cell（含 runId／計數，供「檢視執行」）。沒有：回補 fallback 只有狀態，
        // 沒有可連到的單次執行紀錄（回補的執行紀錄掛在觸發當天那台，不是這個日期格）
        if (dayRuns.Count > 0) return BuildCell(dateStr, dayRuns);
        return new RunDayHostStatusDto
        {
            Status = HasBackfilledRecord(host, date, recordDates) ? "backfilled" : "none"
        };
    }

    /// <summary>回補 fallback 的比對鍵防護：未登記主機（HostId=0，AllHosts 的 fallback 值）不走
    /// 紀錄比對——舊紀錄的 HostId 也可能是 0（Touch 失敗時以名稱歸戶），(0, date) 會跨主機誤配，
    /// 寧可顯示「未執行」也不冒認別台主機的回補。</summary>
    private static bool HasBackfilledRecord(HostRef host, DateTime date, HashSet<(long HostId, DateTime Date)> recordDates) =>
        host.HostId > 0 && recordDates.Contains((host.HostId, date.AddDays(-1).Date));

    private sealed record HostRef(string HostName, long HostId, string Source);

    private static List<BatchRun> RunsForHostOnDate(List<BatchRun> runs, string hostName, string dateStr) =>
        runs.Where(r => string.Equals(r.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                        r.StartedAt.ToString("yyyy-MM-dd") == dateStr)
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    private static string StatusOf(List<BatchRun> dayRuns) => BuildCell("", dayRuns).Status;

    /// <summary>單筆 BatchRun 的狀態判定（回饋十七輪批次F-3 抽出，原本內嵌在 BuildCell 裡）：
    /// 執行紀錄分頁（GetRunList）需要對每一筆 BatchRun 各自判定，不像 BuildCell 只取「當天最新一筆」
    /// 代表整天。抽出單點化，避免兩處各自維護一份判定邏輯而漂移。</summary>
    private static string ComputeStatus(BatchRun run)
    {
        // Stopped 優先於 exit code／錯誤計數判定（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：
        // 優雅停止是「已停止」不是「失敗」；停止前累積的警告/錯誤仍顯示在各自的計數欄，不會被藏起來
        if (run.FinishedAt == null)
            return DateTime.Now - run.StartedAt > StuckThreshold ? "stuck" : "running";
        if (run.Stopped) return "stopped";
        if (run.ExitCode != 0) return "failed";
        if (run.ErrorCount > 0) return "failed";
        if (run.WarnCount > 0 || run.AiFailures > 0) return "warning";
        return "success";
    }

    private static RunDayHostStatusDto BuildCell(string date, List<BatchRun> dayRuns)
    {
        if (dayRuns.Count == 0)
        {
            // 沒有執行紀錄——排程沒跑、機器關機，或這台根本還沒部署。
            // 這與「跑了但沒問題」是完全不同的狀態，必須分得出來
            return new RunDayHostStatusDto { Status = "none" };
        }

        var latest = dayRuns[0];

        return new RunDayHostStatusDto
        {
            Status = ComputeStatus(latest),
            RunId = latest.RunId,
            StartedAt = latest.StartedAt,
            FinishedAt = latest.FinishedAt,
            DaysAnalyzed = latest.DaysAnalyzed,
            WarnCount = latest.WarnCount,
            ErrorCount = latest.ErrorCount,
            AiFailures = latest.AiFailures,
            RunCount = dayRuns.Count
        };
    }

    public RunDetailDto GetDetail(long runId)
    {
        var run = _runs.GetRun(runId) ?? throw DomainException.NotFound("找不到這次執行紀錄。");
        var logs = _runs.GetLogs(runId);

        return new RunDetailDto
        {
            RunId = run.RunId,
            HostName = run.HostName,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            ExitCode = run.ExitCode,
            Stopped = run.Stopped,
            AppVersion = run.AppVersion,
            Args = run.Args,
            DaysAnalyzed = run.DaysAnalyzed,
            AiCalls = run.AiCalls,
            AiFailures = run.AiFailures,
            WarnCount = run.WarnCount,
            ErrorCount = run.ErrorCount,
            DurationSeconds = run.FinishedAt.HasValue
                ? (int)(run.FinishedAt.Value - run.StartedAt).TotalSeconds
                : null,
            TriggerText = TriggerText(run.Trigger),
            Logs = logs.Select(l => new RunLogDto
            {
                LoggedAt = l.LoggedAt,
                Level = l.Level,
                Logger = l.Logger,
                Message = l.Message,
                ExceptionText = l.ExceptionText
            }).ToList()
        };
    }

    /// <summary>
    /// 「誰跑的」（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：null／"console" 是舊紀錄或批次 exe
    /// 直接執行（升級前唯一的觸發來源，統一顯示「工作排程器」，語意等價且不驚動既有部署）；
    /// "schedule" 是 Web 排程觸發；"manual:{帳號}" 是 Web 手動觸發。
    /// </summary>
    private string TriggerText(string? trigger) => trigger switch
    {
        null or "" or "console" => "工作排程器",
        "schedule" => "排程",
        _ when trigger.StartsWith("manual:", StringComparison.Ordinal) =>
            $"手動（{NameFormat.FormatAccount(_users, trigger["manual:".Length..])}）",
        _ => trigger
    };

    /// <summary>
    /// 執行紀錄（回饋十七輪批次F-3）：每一筆 BatchRun 原始資料的扁平清單，依開始時間新到舊。
    /// 與「執行總表」（按日期彙總每台主機的最新一筆）不同——這裡逐筆列出，含同一天內的多次
    /// 手動重跑，回答的是「每一次執行本身的細節」而非「這一天整體狀況」。
    /// </summary>
    public List<RunListItemDto> GetRunList(int days) =>
        _runs.GetRecentRuns(days, hostNames: null)
            .Select(run => new RunListItemDto
            {
                RunId = run.RunId,
                HostName = run.HostName,
                StartedAt = run.StartedAt,
                FinishedAt = run.FinishedAt,
                DurationSeconds = run.FinishedAt.HasValue
                    ? (int)(run.FinishedAt.Value - run.StartedAt).TotalSeconds
                    : null,
                Status = ComputeStatus(run),
                TriggerText = TriggerText(run.Trigger),
                Args = run.Args,
                DaysAnalyzed = run.DaysAnalyzed,
                AiCalls = run.AiCalls,
                AiFailures = run.AiFailures,
                WarnCount = run.WarnCount,
                ErrorCount = run.ErrorCount
            })
            .ToList();

    /// <summary>
    /// 異常彙總：把近 N 天的 Error/Fatal 依訊息聚合。
    /// 回答的是「這是個案還是通案」——同一個錯誤在 20 台機器上出現，
    /// 跟只在一台上出現，處理方式完全不同。
    /// </summary>
    public List<RunErrorGroupDto> GetErrorSummary(int days)
    {
        var errors = _runs.GetRecentErrors(days);
        var runsById = _runs.GetRecentRuns(days, null).ToDictionary(r => r.RunId);

        return errors
            .GroupBy(e => NormalizeMessage(e.Message))
            .Select(group => new RunErrorGroupDto
            {
                Message = group.First().Message,
                Level = group.Any(e => e.Level == "Fatal") ? "Fatal" : "Error",
                Count = group.Count(),
                AffectedHosts = group
                    .Select(e => runsById.TryGetValue(e.RunId, out var run) ? run.HostName : "（未知）")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList(),
                FirstSeen = group.Min(e => e.LoggedAt),
                LastSeen = group.Max(e => e.LoggedAt),
                LatestRunId = group.OrderByDescending(e => e.LoggedAt).First().RunId
            })
            .OrderByDescending(g => g.Count)
            .ToList();
    }

    /// <summary>
    /// 聚合前正規化：訊息裡的日期、數字、GUID 會讓「同一個錯誤」看起來各不相同。
    /// 不正規化的話異常彙總會退化成一筆一組，完全失去「這是通案嗎」的判斷價值。
    /// </summary>
    private static string NormalizeMessage(string message)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            message, @"\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?)?", "{date}");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized, @"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b", "{guid}");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\d+", "{n}");

        return normalized;
    }
}

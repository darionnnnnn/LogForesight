using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 執行監控（docs/WEB-SPEC.md §9.10）——dev 每天早上的第一個畫面。
///
/// 回答的問題是「昨晚每台主機都跑了嗎、有沒有出問題」，
/// 而不是「分析結果是什麼」（那是儀表板的事）。
/// </summary>
public interface IRunMonitorService
{
    /// <summary>每日彙總（§5.4 D-4）：取代逐主機矩陣，兩千台規模下矩陣會炸出過大的 DOM</summary>
    List<RunDaySummaryDto> GetDaySummaries(int days);

    /// <summary>單一日期的逐主機狀態（點日期下鑽時取得）</summary>
    List<RunDayHostStatusDto> GetDayDetail(DateTime date);

    RunDetailDto GetDetail(long runId);

    List<RunErrorGroupDto> GetErrorSummary(int days);
}

public class RunMonitorService : IRunMonitorService
{
    private readonly IBatchRunStore _runs;
    private readonly IHostStore _hosts;

    /// <summary>執行超過這個時數仍未回報結束，視為異常中斷（而不是還在跑）</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromHours(6);

    /// <summary>失敗主機清單的顯示上限——超過的部分只算數量，避免單一異常日把整頁撐爆</summary>
    private const int MaxFailedHostNames = 10;

    public RunMonitorService(IBatchRunStore runs, IHostStore hosts)
    {
        _runs = runs;
        _hosts = hosts;
    }

    /// <summary>主機清單以「有回報過的主機」與「已登記的主機」聯集為準：
    /// 只看已登記會漏掉尚未加入 Web 的主機，只看回報過的會漏掉「從來沒跑過」的主機——
    /// 而後者正是最需要被看到的一種</summary>
    private List<string> AllHostNames(List<BatchRun> runs) =>
        _hosts.GetAll().Where(h => h.Active).Select(h => h.HostName)
            .Union(runs.Select(r => r.HostName), StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public List<RunDaySummaryDto> GetDaySummaries(int days)
    {
        var runs = _runs.GetRecentRuns(days, hostNames: null);
        var from = DateTime.Today.AddDays(-days + 1);
        var hostNames = AllHostNames(runs);

        var summaries = new List<RunDaySummaryDto>();
        for (var date = from; date <= DateTime.Today; date = date.AddDays(1))
        {
            var dateStr = date.ToString("yyyy-MM-dd");
            var summary = new RunDaySummaryDto { Date = dateStr, TotalHosts = hostNames.Count };
            var failedHosts = new List<string>();

            foreach (var hostName in hostNames)
            {
                var dayRuns = RunsForHostOnDate(runs, hostName, dateStr);
                var status = StatusOf(dayRuns);

                switch (status)
                {
                    case "success": summary.SuccessCount++; break;
                    case "warning": summary.WarningCount++; break;
                    case "failed": summary.FailedCount++; failedHosts.Add(hostName); break;
                    case "stuck": summary.StuckCount++; failedHosts.Add(hostName); break;
                    case "running": summary.RunningCount++; break;
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
        var hostNames = AllHostNames(runs);

        return hostNames.Select(hostName =>
        {
            var dayRuns = RunsForHostOnDate(runs, hostName, dateStr);
            var cell = BuildCell(dateStr, dayRuns);
            return new RunDayHostStatusDto
            {
                HostName = hostName,
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

    private static List<BatchRun> RunsForHostOnDate(List<BatchRun> runs, string hostName, string dateStr) =>
        runs.Where(r => string.Equals(r.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                        r.StartedAt.ToString("yyyy-MM-dd") == dateStr)
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    private static string StatusOf(List<BatchRun> dayRuns) => BuildCell("", dayRuns).Status;

    private static RunDayHostStatusDto BuildCell(string date, List<BatchRun> dayRuns)
    {
        if (dayRuns.Count == 0)
        {
            // 沒有執行紀錄——排程沒跑、機器關機，或這台根本還沒部署。
            // 這與「跑了但沒問題」是完全不同的狀態，必須分得出來
            return new RunDayHostStatusDto { Status = "none" };
        }

        var latest = dayRuns[0];

        var status = latest.FinishedAt == null
            ? (DateTime.Now - latest.StartedAt > StuckThreshold ? "stuck" : "running")
            : latest.ExitCode != 0 ? "failed"
            : latest.ErrorCount > 0 ? "failed"
            : latest.WarnCount > 0 || latest.AiFailures > 0 ? "warning"
            : "success";

        return new RunDayHostStatusDto
        {
            Status = status,
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

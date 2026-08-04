namespace LogForesight;

/// <summary>
/// 一個排程執行窗口（docs/WEB-SCHEDULER-PLAN.md §1.4.3）：<see cref="Start"/> 到點觸發一次完整
/// 執行（<see cref="RunScope.Full"/>），<see cref="End"/> 到點對進行中的執行發 cancel（優雅停止，
/// 停在主機日邊界）。<see cref="Start"/>／<see cref="End"/> 皆為 <c>HH:mm</c> 格式、本地時區；
/// <c>Start &gt; End</c> 表示跨午夜（例如 22:00 → 06:00）。
/// </summary>
public class ScheduleWindow
{
    public string Start { get; set; } = "01:00";
    public string End { get; set; } = "07:00";
}

/// <summary>
/// 排程設定（↔ webdata blob，key=schedule_options，單一物件）。<see cref="Enabled"/>＝false
/// 時不會有任何自動觸發（僅能於「排程作業」頁手動立即執行），開啟後由 Web 的
/// <c>SchedulerHostedService</c> 依 <see cref="Windows"/> 觸發／停止 <see cref="AnalysisOrchestrator"/>。
/// </summary>
public class ScheduleOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>上限 <see cref="ScheduleCalculator.MaxWindows"/> 組——分析單位是「日」，更多窗口沒有對應的工作量</summary>
    public List<ScheduleWindow> Windows { get; set; } = new() { new ScheduleWindow() };

    /// <summary>
    /// AI 診斷傾印開關（承接批次 --debug-dump，docs/WEB-SCHEDULER-PLAN.md §1.4.10）：作用於「下一次
    /// 排程／手動觸發的執行」，與排程設定同一生命週期，不放 SystemSettings。
    /// </summary>
    public bool DebugDump { get; set; } = false;

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
}

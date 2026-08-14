namespace LogForesight.Core.Models;

/// <summary>
/// 一個排程執行窗口（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）：<see cref="Start"/> 到點觸發一次完整
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
    /// AI 診斷傾印開關（承接批次 --debug-dump，docs/archive/WEB-SCHEDULER-PLAN.md §1.4.10）：作用於「下一次
    /// 排程／手動觸發的執行」，與排程設定同一生命週期，不放 SystemSettings。
    /// </summary>
    public bool DebugDump { get; set; } = false;

    /// <summary>
    /// 是否分析本機主機（回饋十八輪批次D）：預設 true（零行為變化）。停用後排程／手動觸發的
    /// 完整執行只跑 NetIQ 機房分析，本機主機不再逐日分析（<see cref="RunRequest.IncludeLocal"/>）。
    /// 停用不等於「這台機器從此不被監控」——若同一台機器日後也以 IP 登錄為 NetIQ 主機，仍會照常
    /// 從 Sentinel 取數，兩者鍵不同（本機用 MachineName、NetIQ 用 IP），互不影響。適用情境：
    /// Web 主機本身不是重點監控目標，或改由 NetIQ 側取數（見 docs/archive/FEEDBACK-18-PLAN.md 批次D）。
    /// 本機的 AI 呼叫與 NetIQ 共用同一個序列化佇列（AIService 的 SemaphoreSlim），停用本機分析
    /// 也讓 NetIQ 的 AI 佇列不用再跟本機搶序——這是本輪選擇「加開關」而非「本機也走 NetIQ 那套
    /// AI 佇列統一」的原因（成本遠大於收益的評估見 FEEDBACK-12-PLAN.md §3.9，本輪重新核對依然成立）。
    /// </summary>
    public bool LocalAnalysisEnabled { get; set; } = true;

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
}

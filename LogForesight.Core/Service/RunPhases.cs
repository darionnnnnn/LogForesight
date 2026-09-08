namespace LogForesight.Core.Service;

/// <summary>
/// 執行進度／訊號的 phase 字面值（<see cref="IRunProgress.Report"/> 的第一個參數）。
///
/// 這些字串是 Core 與 Web 兩邊的約定：Core 送出、Web 的 <c>SchedulerRunState.ReportProgress</c>
/// 據此分派到三條互不覆蓋的進度軌，前端 <c>runs.js</c> 另有一份標籤對照表。
/// 過去散落在三層各自寫裸字串，新增一個 phase 時只要漏改前端，畫面就直接印出裸 phase 給使用者。
/// 集中在這裡之後，前端對照表的完整性由 <c>RunsPageUiTests</c> 以反射逐條核對。
///
/// **不是所有 phase 都是進度**：<see cref="LocalDone"/> 等完工訊號與
/// <see cref="GuardPaused"/>、<see cref="PrtgFindingsReady"/> 只帶語意，done／total 恆為 0，
/// Web 端必須有顯式分支——尤其以 <c>prtg-</c> 開頭的那些，落進前綴分派會把 PRTG 進度軌蓋掉。
/// </summary>
public static class RunPhases
{
    // ── 進度軌 ────────────────────────────────────────────────

    /// <summary>本機分析（單位：主機日）</summary>
    public const string Local = "local";

    /// <summary>NetIQ 機房分析（單位：主機日）</summary>
    public const string Netiq = "netiq";

    /// <summary>PRTG 結構同步的總稱：實際回報走下面三個子階段，這個值只在路徑剛啟動時送一次</summary>
    public const string PrtgSync = "prtg-sync";

    /// <summary>PRTG 階段 1：device 結構同步</summary>
    public const string PrtgSyncDevices = PrtgFetchService.PrtgSyncDevicesPhase;

    /// <summary>PRTG 階段 2：sensor 結構同步</summary>
    public const string PrtgSyncSensors = PrtgFetchService.PrtgSyncSensorsPhase;

    /// <summary>PRTG 階段 3：狀態變更（messages）同步</summary>
    public const string PrtgSyncMessages = PrtgFetchService.PrtgSyncMessagesPhase;

    /// <summary>PRTG 階段 4：每日 hourly 數值</summary>
    public const string PrtgValues = PrtgFetchService.PrtgValuesPhase;

    /// <summary>PRTG 觸發式數值取數</summary>
    public const string PrtgTriggered = PrtgFetchService.PrtgTriggeredPhase;

    // ── 完工訊號（done／total 恆為 0）────────────────────────

    /// <summary>本機路徑收尾</summary>
    public const string LocalDone = "local-done";

    /// <summary>NetIQ 路徑收尾（finally，成功／失敗／取消皆送）</summary>
    public const string NetiqDone = "netiq-done";

    /// <summary>PRTG 路徑收尾（finally，成功／失敗皆送）</summary>
    public const string PrtgDone = "prtg-done";

    // ── 狀態訊號（done／total 恆為 0）────────────────────────

    /// <summary>資源守門進入暫停</summary>
    public const string GuardPaused = "guard-paused";

    /// <summary>資源守門離開暫停</summary>
    public const string GuardResumed = "guard-resumed";

    /// <summary>當日 PRTG finding 已到齊，AI 分析排程可以開始處理當日待補</summary>
    public const string PrtgFindingsReady = AnalysisOrchestrator.PrtgFindingsReadyPhase;

    /// <summary>
    /// 全部 phase 字面值。前端標籤對照表的完整性檢查用——
    /// 新增 phase 卻忘了補前端文案時，畫面會印出裸 phase 字串給使用者。
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Local, Netiq,
        PrtgSync, PrtgSyncDevices, PrtgSyncSensors, PrtgSyncMessages, PrtgValues, PrtgTriggered,
        LocalDone, NetiqDone, PrtgDone,
        GuardPaused, GuardResumed, PrtgFindingsReady
    };

    /// <summary>會畫進度條的 phase（需要前端標籤與單位）。訊號類不在內。</summary>
    public static readonly IReadOnlyList<string> ProgressTracks = new[]
    {
        Local, Netiq,
        PrtgSync, PrtgSyncDevices, PrtgSyncSensors, PrtgSyncMessages, PrtgValues, PrtgTriggered
    };
}

namespace LogForesight.Core.Service;

/// <summary>
/// 執行紀錄的階段計時鍵（docs/WEB-SPEC.md §9.10）。
///
/// 與 <see cref="RunPhases"/> 分開：那份是進度軌契約，<c>SchedulerRunState</c> 依 prtg- 前綴分派、
/// 前端依它畫進度條，混入計時鍵會蓋掉進度軌。這裡的鍵只進 <c>BatchRun.Phases</c>，不得傳給 <c>progress.Report</c>。
/// </summary>
public static class RunTimingPhases
{
    public const string PermissionCheck = "permission-check";
    public const string Retention = "retention";
    public const string Local = RunPhases.Local;
    public const string Netiq = RunPhases.Netiq;
    public const string Prtg = "prtg";
    public const string Dispatch = "dispatch";
    public const string Checkup = "checkup";
    public const string PrtgValueFill = "prtg-value-fill";
    public const string Ai = "ai";

    /// <summary>PRTG 子階段：只隨結束列寫出，不觸發快照與里程碑；畫面依 prtg- 前綴歸到 PRTG 軌</summary>
    public const string PrtgSync = RunPhases.PrtgSync;
    public const string PrtgTriggered = RunPhases.PrtgTriggered;

    private static readonly Dictionary<string, string> TopLevelLabels = new()
    {
        [PermissionCheck] = "權限檢查",
        [Retention] = "保留期清除",
        [Local] = "本機分析",
        [Netiq] = "NetIQ 機房分析",
        [Prtg] = "PRTG",
        [Dispatch] = "夜間派工收尾",
        [Checkup] = "體檢",
        [PrtgValueFill] = "PRTG 數值補齊",
        [Ai] = "AI 分析"
    };

    private static readonly Dictionary<string, string> SubLabels = new()
    {
        [PrtgSync] = "PRTG 結構同步",
        [PrtgTriggered] = "PRTG 觸發式取數"
    };

    /// <summary>全部鍵與顯示名稱；前端對照表（core/run-phases.js）的完整性由測試以此核對</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } =
        TopLevelLabels.Concat(SubLabels).ToDictionary(pair => pair.Key, pair => pair.Value);

    public static bool IsTopLevel(string key) => TopLevelLabels.ContainsKey(key);

    public static string Label(string key) => Labels.TryGetValue(key, out var label) ? label : key;
}

/// <summary>階段結果。PRTG 的 no_output 算 success，個別 sensor 失敗算 partial。</summary>
public static class RunPhaseOutcomes
{
    public const string Success = "success";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
    public const string Skipped = "skipped";

    public static string Label(string outcome) => outcome switch
    {
        Success => "成功",
        Partial => "部分完成",
        Failed => "失敗",
        Stopped => "已停止",
        Skipped => "跳過",
        _ => outcome
    };
}

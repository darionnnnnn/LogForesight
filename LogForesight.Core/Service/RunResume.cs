using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// 一趟取數結束時還剩多少事沒做：直接從該趟已持久化的結果算，不另存一份（重啟後照樣成立）。
/// 只看失敗與待補；被窗口結束停掉的部分等下一個窗口（帳本記「停止」，跨日也補得回來），不算在這裡。
/// </summary>
public sealed record RunResume(bool LocalFailed, int NetiqHostDays, int PrtgDevices, int PrtgSensors, bool PrtgFailed = false, int? NetiqEmptyPending = null)
{
    /// <summary>NetIQ 回 0 筆、仍待複查的主機日數（null＝舊紀錄，視為 0）。</summary>
    public int EmptyPendingDays => Math.Max(NetiqEmptyPending ?? 0, 0);

    public bool HasWork => LocalFailed || NetiqHostDays > 0 || PrtgDevices > 0 || PrtgSensors > 0 || PrtgFailed || EmptyPendingDays > 0;

    /// <summary>除了空資料待複查之外沒有任何其他事（失敗續跑規則不適用，改走複查間隔）。</summary>
    public bool OnlyEmptyPending => EmptyPendingDays > 0 && !LocalFailed && NetiqHostDays <= 0 && PrtgDevices <= 0 && PrtgSensors <= 0 && !PrtgFailed;

    public static RunResume Of(BatchRun run)
    {
        var localFailed = run.Phases?.Any(p => p.Key == RunTimingPhases.Local && p.Outcome == RunPhaseOutcomes.Failed) == true;
        var pending = run.PrtgDays == null ? 0 : PrtgReevaluationPlanner.PendingDeviceUnion(run.PrtgDays).Count();
        var sensors = run.PrtgDays?.Sum(d => d.FailedSensors) ?? 0;
        // 整段同步或登入失敗沒有「待補裝置」，但下一趟會整段重做，一樣算還有事沒做
        var prtgFailed = run.PrtgOutcome == BatchRun.PrtgOutcomeFailed || run.PrtgDays?.Any(d => d.Outcome == BatchRun.PrtgOutcomeFailed) == true;
        return new RunResume(localFailed, run.NetiqDaysFailed ?? 0, pending, sensors, PrtgFailed: prtgFailed, NetiqEmptyPending: run.NetiqEmptyPending);
    }

    /// <summary>給里程碑與狀態卡看的摘要：只列有的項目，單位統一（NetIQ 是主機日）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (LocalFailed) parts.Add("本機分析失敗");
        if (NetiqHostDays > 0) parts.Add($"NetIQ {NetiqHostDays} 主機日");
        if (PrtgFailed) parts.Add("PRTG 同步失敗");
        if (PrtgDevices > 0) parts.Add($"PRTG {PrtgDevices} 裝置");
        if (PrtgSensors > 0) parts.Add($"PRTG 數值 {PrtgSensors} 顆");
        if (EmptyPendingDays > 0) parts.Add($"NetIQ 待複查 {EmptyPendingDays} 主機日");
        return string.Join("／", parts);
    }
}

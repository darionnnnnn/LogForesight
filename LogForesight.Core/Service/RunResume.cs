using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// 一趟取數結束時還剩多少事沒做：直接從該趟已持久化的結果算，不另存一份（重啟後照樣成立）。
/// 只看失敗與待補；被窗口結束停掉的部分由下一趟的缺漏判定補，不算在這裡。
/// </summary>
public sealed record RunResume(bool LocalFailed, int NetiqHostDays, int PrtgDevices, int PrtgSensors, bool PrtgHalted)
{
    public bool HasWork => LocalFailed || NetiqHostDays > 0 || PrtgDevices > 0 || PrtgSensors > 0;

    public static RunResume Of(BatchRun run)
    {
        var localFailed = run.Phases?.Any(p => p.Key == RunTimingPhases.Local && p.Outcome == RunPhaseOutcomes.Failed) == true;
        var pending = run.PrtgDays?
            .SelectMany(d => (d.PendingSensorDevices ?? Array.Empty<long>()).Concat(d.PendingMessageDevices ?? Array.Empty<long>()))
            .Distinct().Count() ?? 0;
        var sensors = run.PrtgDays?.Sum(d => d.FailedSensors) ?? 0;
        return new RunResume(localFailed, run.NetiqDaysFailed ?? 0, pending, sensors, PrtgHalted: pending > 0);
    }

    /// <summary>給里程碑與狀態卡看的摘要：只列有的項目，單位統一（NetIQ 是主機日）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (LocalFailed) parts.Add("本機分析失敗");
        if (NetiqHostDays > 0) parts.Add($"NetIQ {NetiqHostDays} 主機日");
        if (PrtgDevices > 0) parts.Add($"PRTG {PrtgDevices} 裝置");
        if (PrtgSensors > 0) parts.Add($"PRTG 數值 {PrtgSensors} 顆");
        return string.Join("／", parts);
    }
}

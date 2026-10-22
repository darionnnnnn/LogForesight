using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 把手動「同步結構與對應」與歷史回填合成一道閘門給 PRTG 日路徑（docs/PRTG-SPEC.md §5a）。
/// 同步的等待沿用 <see cref="PrtgStructureSyncService.WaitUntilIdleAsync"/>（含它自己的上限）；
/// 回填等到 <see cref="IPrtgManualWorkGate.BackfillMaxWait"/> 為止。立即執行的接續回填（tail）不算。
/// </summary>
public sealed class PrtgManualWorkGate : IPrtgManualWorkGate
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly PrtgStructureSyncService _sync;
    private readonly PrtgBackfillRunState _backfill;
    private readonly TimeSpan _backfillMaxWait;
    private readonly TimeSpan _pollInterval;

    public PrtgManualWorkGate(PrtgStructureSyncService sync, PrtgBackfillRunState backfill)
        : this(sync, backfill, IPrtgManualWorkGate.BackfillMaxWait, DefaultPollInterval)
    {
    }

    internal PrtgManualWorkGate(PrtgStructureSyncService sync, PrtgBackfillRunState backfill,
        TimeSpan backfillMaxWait, TimeSpan pollInterval)
    {
        _sync = sync;
        _backfill = backfill;
        _backfillMaxWait = backfillMaxWait;
        _pollInterval = pollInterval;
    }

    public PrtgManualWork Running =>
        _sync.IsRunning ? PrtgManualWork.StructureSync :
        BackfillRunning ? PrtgManualWork.Backfill :
        PrtgManualWork.None;

    private bool BackfillRunning =>
        _backfill.Snapshot().IsRunning && _backfill.GetRunIdentity().RunKind != "tail";

    public async Task<PrtgManualWaitOutcome> WaitUntilIdleAsync(PrtgManualWork observed, CancellationToken ct)
    {
        // 同步已結束時 PrtgStructureSyncService.WaitUntilIdleAsync 直接回那趟成不成功，不會誤判成回填
        if (observed == PrtgManualWork.StructureSync)
            return await _sync.WaitUntilIdleAsync(ct) ? PrtgManualWaitOutcome.MirrorFresh : PrtgManualWaitOutcome.SyncNotFresh;

        var deadline = DateTime.UtcNow + _backfillMaxWait;
        while (BackfillRunning)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                Log.Warn("等待歷史回填超過 {0} 分鐘仍未結束，PRTG 日路徑不再等待，與回填同時進行。", _backfillMaxWait.TotalMinutes);
                return PrtgManualWaitOutcome.BackfillStillRunning;
            }
            await Task.Delay(_pollInterval, ct);
        }
        return PrtgManualWaitOutcome.BackfillEnded;
    }
}

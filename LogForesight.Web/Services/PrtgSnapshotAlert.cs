using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>快照警示判定結果。兩種原因互斥：停止優先。</summary>
/// <param name="Stopped">快照因待寫檔無法使用而停止（保守策略下立即警示）</param>
/// <param name="NoRecentSuccess">目標非空卻超過門檻沒有成功取樣</param>
/// <param name="StopCode">待寫檔停止原因碼（Stopped 時有值）</param>
/// <param name="SilentSince">NoRecentSuccess 時的起算時間</param>
public sealed record PrtgSnapshotAlert(bool Stopped, bool NoRecentSuccess, string? StopCode, DateTime? SilentSince)
{
    public static PrtgSnapshotAlert None { get; } = new(false, false, null, null);
    public bool Any => Stopped || NoRecentSuccess;

    /// <summary>超過這個長度沒有成功取樣就警示（暫定 6 小時）。</summary>
    public static readonly TimeSpan NoSuccessThreshold = TimeSpan.FromHours(6);

    /// <summary>
    /// 唯一判定點（系統健康與 PRTG 維護頁共用）。
    /// 只在 PRTG 擷取啟用且策略為保守時適用：激進策略夜間會逐顆查歷史值，快照缺口不構成同樣的風險。
    /// 6 小時從「站台啟動時間與最近成功時間取晚者」起算（剛啟動不警示）；目標為空不警示（維持 no-targets）。
    /// </summary>
    public static PrtgSnapshotAlert Evaluate(bool prtgEnabled, string? fetchStrategy, PrtgSnapshotStatus status, DateTime now)
    {
        if (!prtgEnabled) return None;
        if (PrtgFetchStrategy.Normalize(fetchStrategy) != PrtgFetchStrategy.Conservative) return None;
        if (status.JournalStopCode != null) return new(true, false, status.JournalStopCode, null);
        if (status.TargetCount is not > 0) return None;
        var since = status.LastSuccessAt.HasValue && status.LastSuccessAt.Value > status.StartedAt
            ? status.LastSuccessAt.Value : status.StartedAt;
        return now - since > NoSuccessThreshold ? new(false, true, null, since) : None;
    }
}

namespace LogForesight;

/// <summary>
/// 批次端套用 NetIQ 匯入佇列（docs/SCALE-2000-PLAN.md §5.3 D-3）。
///
/// Web 只負責把使用者勾選的主機「排入」佇列（<see cref="NetiqDiscoveryService"/> 已改名為
/// Enqueue，不再直接落盤）；實際的主機新增/更新/孤兒復活統一由這裡處理，兩個呼叫時機：
///   1. 每次批次正常執行的開頭自動處理一次（見 Program.cs）
///   2. 手動 <c>--apply-netiq-imports</c>，供不想等到下次排程、想立即套用時使用
///
/// 稽核紀錄用 <see cref="NetiqImportQueueEntry.RequestedByAccount"/> 歸戶——即使落盤延後到
/// 批次執行，稽核上仍看得出「這是誰要求的」，不會因為批次用系統身分執行而失真。
/// </summary>
public static class NetiqImportQueueCli
{
    /// <summary>處理所有 pending 的佇列項目；回傳處理筆數（供呼叫端印出摘要）</summary>
    public static int ApplyPending(INetiqImportQueueStore queue, IHostStore hosts, IAuditLogStore audit)
    {
        var pending = queue.GetAll().Where(e => e.Status == NetiqImportQueueStatuses.Pending).ToList();
        if (pending.Count == 0) return 0;

        foreach (var entry in pending)
        {
            try
            {
                var outcome = NetiqImportApplier.Apply(entry, hosts);

                entry.Status = NetiqImportQueueStatuses.Applied;
                entry.AppliedAt = DateTime.Now;
                entry.Added = outcome.Added;
                entry.Updated = outcome.Updated;
                entry.Revived = outcome.Revived;
                queue.Save(entry);

                audit.Append(new AuditEntry
                {
                    OccurredAt = DateTime.Now,
                    Account = entry.RequestedByAccount,
                    Action = AuditActions.NetiqImportApplied,
                    TargetKind = "netiq_import_queue",
                    TargetId = entry.QueueId,
                    Summary = $"批次套用 NetIQ 匯入（Sentinel：{entry.ServerName}）：" +
                              $"新增 {outcome.Added}、更新 {outcome.Updated}" +
                              (outcome.Revived > 0 ? $"、重新啟用 {outcome.Revived}" : ""),
                    Result = AuditResult.Ok
                });

                Console.WriteLine($"  ✓ 套用 NetIQ 匯入佇列 {entry.QueueId}（{entry.ServerName}）：" +
                                   $"新增 {outcome.Added}、更新 {outcome.Updated}、復活 {outcome.Revived}");
            }
            catch (Exception ex)
            {
                entry.Status = NetiqImportQueueStatuses.Failed;
                entry.FailureReason = ex.Message;
                queue.Save(entry);

                audit.Append(new AuditEntry
                {
                    OccurredAt = DateTime.Now,
                    Account = entry.RequestedByAccount,
                    Action = AuditActions.NetiqImportApplied,
                    TargetKind = "netiq_import_queue",
                    TargetId = entry.QueueId,
                    Summary = $"套用 NetIQ 匯入佇列失敗（Sentinel：{entry.ServerName}）：{ex.Message}",
                    Result = AuditResult.Failed
                });

                Console.WriteLine($"  ⚠ 套用 NetIQ 匯入佇列 {entry.QueueId} 失敗：{ex.Message}");
            }
        }

        return pending.Count;
    }
}

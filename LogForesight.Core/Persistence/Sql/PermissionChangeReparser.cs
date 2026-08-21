using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 既有 NetIQ 權限異動列的重剖回填：舊版解析器在「攤平成單行」的訊息上會把欄位值切到行尾，
/// 也抓不到群組區段內的群組名，於是 DB 裡留下一批「對象是整段訊息尾巴」或「對象空白」的列。
/// 解析器修好之後，用同一份原始訊息（<c>alert_text</c>）重剖一次把欄位補回去。
///
/// 先天限制：<c>alert_text</c> 只保留原訊息前 500 字，被截掉的部分剖不回來——這種列維持原值，
/// 由顯示層的降級句兜底。這是**盡力回填**，不是保證每列都修好。
/// </summary>
public sealed class PermissionChangeReparser
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>每批處理的列數：一次抓一批改一批，避免大表一次載進記憶體</summary>
    internal const int BatchSize = 500;

    public const string StateBlobKey = "permission_change_reparse";

    private readonly Func<LfDbContext> _contextFactory;
    private readonly PermissionChangeReparseStateStore _stateStore;

    public PermissionChangeReparser(Func<LfDbContext> contextFactory, PermissionChangeReparseStateStore stateStore)
    {
        _contextFactory = contextFactory;
        _stateStore = stateStore;
    }

    public bool IsCompleted => _stateStore.Get().Completed;

    /// <summary>
    /// 重剖回填（背景執行）。跑完即標記完成，之後不再掃描——新寫入的列走的是修好的解析器，
    /// 不需要再回填。
    /// </summary>
    public void Run(CancellationToken cancellationToken = default)
    {
        if (IsCompleted) return;

        var updated = 0;
        long scanned = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var ctx = _contextFactory();

            // 只處理 NetIQ 來源的明細列：本機監控的欄位是程式自己組的、彙總列沒有原始訊息可剖
            var batch = ctx.PermissionChanges
                .Where(r => r.Source == PermissionChangeSources.Netiq
                            && r.Category != PermissionCategory.Summary
                            && r.Id > scanned)
                .OrderBy(r => r.Id)
                .Take(BatchSize)
                .ToList();

            if (batch.Count == 0) break;

            scanned = batch[^1].Id;

            foreach (var row in batch)
            {
                if (string.IsNullOrWhiteSpace(row.AlertText)) continue;

                var details = PermissionChangeExtractor.Extract(
                    row.AlertText, row.ChangeType, row.EventId ?? 0, row.InitiatorAccount);

                var changed = false;

                // 只在重剖得出值時覆蓋：剖不出的欄位維持原狀，不要把既有資料洗成空的
                if (!string.IsNullOrWhiteSpace(details.Target) && details.Target != row.Target)
                {
                    row.Target = details.Target;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.TargetAccount) && details.TargetAccount != row.TargetAccount)
                {
                    row.TargetAccount = details.TargetAccount;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.InitiatorAccount) && details.InitiatorAccount != row.InitiatorAccount)
                {
                    row.InitiatorAccount = details.InitiatorAccount;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.Before) && details.Before != row.BeforeValue)
                {
                    row.BeforeValue = details.Before;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(details.After) && details.After != row.AfterValue)
                {
                    row.AfterValue = details.After;
                    changed = true;
                }

                if (!changed) continue;

                // 對象改對了，特權判定才跟著成立（舊列的對象是訊息尾巴，永遠命中不了關鍵字）
                row.IsPrivilegedTarget = PermissionCategory.IsPrivilegedTarget(row.Target, row.ChangeType);
                updated++;
            }

            ctx.SaveChanges();
        }

        if (cancellationToken.IsCancellationRequested) return;

        _stateStore.Update(s =>
        {
            s.Completed = true;
            s.CompletedAt = DateTime.Now;
            s.UpdatedRows = updated;
        });

        if (updated > 0)
            Log.Info("[SQL] 權限異動重剖回填完成：更新 {Count} 列", updated);
    }
}

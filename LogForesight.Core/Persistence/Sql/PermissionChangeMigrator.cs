using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把權限異動自 JSONL log（key=perm_changes）與確認狀態 blob（key=perm_confirms）搬進真表 lf_permission_changes。
///
/// 四條約束：
/// 1. 中斷後可安全重來：整份包在單一交易內。
/// 2. 完成與否是被寫下的事實（<see cref="PermissionChangeMigrationState"/>），不是從「目標表空不空」反推。
/// 3. 不刪舊 log 與舊 blob：搬完保留當備份。
/// 4. 解析失敗直接拋，不吞。
///
/// 不在啟動路徑上執行：由背景服務驅動（見 Web 端 PermissionChangeMigrationHostedService）。
/// </summary>
public sealed class PermissionChangeMigrator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly Func<string, EfJsonBlobStore> _blobFactory;
    private readonly PermissionChangeMigrationStateStore _stateStore;

    public const string LogKey = "perm_changes";
    public const string BlobKey = "perm_confirms";
    public const string StateBlobKey = "permission_change_migration";

    public PermissionChangeMigrator(
        Func<LfDbContext> contextFactory,
        Func<string, EfJsonBlobStore> blobFactory,
        PermissionChangeMigrationStateStore stateStore)
    {
        _contextFactory = contextFactory;
        _blobFactory = blobFactory;
        _stateStore = stateStore;
    }

    public PermissionChangeMigrationState State => _stateStore.Get();

    /// <summary>
    /// 啟動時的輕量判定（毫秒級，可留在啟動路徑）：決定這個資料庫需不需要遷移。
    /// 判準為「舊資料有內容」而不是「表是空的」。
    /// </summary>
    public void Evaluate()
    {
        var state = _stateStore.Get();
        if (state.State == PermissionChangeMigrationState.Completed) return;

        var needed = HasOldData();

        _stateStore.Update(s =>
        {
            if (!needed)
            {
                // 全新安裝（或本來就沒有舊資料）：直接標完成，寫入不必被擋
                s.State = PermissionChangeMigrationState.Completed;
                s.CompletedAt = DateTime.Now;
                return;
            }

            if (s.State == PermissionChangeMigrationState.Unknown) s.State = PermissionChangeMigrationState.Pending;
            // 上次卡在 running（行程被砍）→ 退回 pending 重來
            if (s.State == PermissionChangeMigrationState.Running) s.State = PermissionChangeMigrationState.Pending;
        });

        if (needed) Log.Info("[SQL] 偵測到舊格式的權限異動資料，將於背景搬移至資料表");
    }

    /// <summary>
    /// 實際搬移（背景執行）。整份包在單一交易內。
    /// 失敗時把訊息寫進遷移狀態並重新拋出。
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        var state = _stateStore.Get();
        if (state.State == PermissionChangeMigrationState.Completed) return;

        _stateStore.Update(s =>
        {
            s.State = PermissionChangeMigrationState.Running;
            s.StartedAt ??= DateTime.Now;
            s.LastError = null;
        });

        int rows;
        try
        {
            if (cancellationToken.IsCancellationRequested) return;

            using var ctx = _contextFactory();
            var strategy = ctx.Database.CreateExecutionStrategy();

            rows = strategy.Execute(() =>
            {
                using var inner = _contextFactory();
                using var tx = inner.Database.BeginTransaction();

                var count = MigratePermissionChanges(inner);
                inner.SaveChanges();
                tx.Commit();

                return count;
            });
        }
        catch (Exception ex)
        {
            _stateStore.Update(s =>
            {
                s.State = PermissionChangeMigrationState.Pending;
                s.LastError = ex.Message;
            });
            throw;
        }

        if (cancellationToken.IsCancellationRequested) return;

        _stateStore.Update(s =>
        {
            s.State = PermissionChangeMigrationState.Completed;
            s.MigratedRows = rows;
            s.CompletedAt = DateTime.Now;
            s.LastError = null;
        });

        Log.Info("[SQL] 權限異動遷移完成：lf_permission_changes {Count} 列（原 log 與 blob 保留未刪，僅作備份）", rows);
    }

    private int MigratePermissionChanges(LfDbContext ctx)
    {
        // 重入保護刻意**不是**「表裡有沒有資料就整批跳過」。遷移閘門只擋得住 HTTP 寫入，
        // 而權限異動的寫入端還有背景排程的分析流程——夜間分析只要在遷移完成前寫進一列，
        // 整批舊資料就會被誤判成「已經搬過」而永久消失，狀態還顯示一切正常。
        // 改成逐筆比對 change_id，只補沒搬過的。
        //
        // 註：HandlingBlobMigrator 目前仍是「表裡有資料就整批跳過」的寫法，而它那三張表
        // 同樣不是只有 HTTP 寫入（AnalysisOrchestrator 把三個真表 store 傳給 IssueCaseCoordinator，
        // 夜間分析會直接寫），所以那邊有同型的資料遺失風險，見 docs/BACKLOG.md。
        var existingChangeIds = ctx.PermissionChanges.AsNoTracking()
            .Select(r => r.ChangeId)
            .ToHashSet(StringComparer.Ordinal);

        var logLines = ctx.LogLines.AsNoTracking()
            .Where(l => l.LogKey == LogKey)
            .OrderBy(l => l.Seq)
            .ToList();

        var parsedRecords = new List<(PermissionChangeRecord Record, DateTime? CreatedAt)>();
        foreach (var logLine in logLines)
        {
            if (string.IsNullOrWhiteSpace(logLine.Line)) continue;

            PermissionChangeRecord record;
            try
            {
                record = JsonSerializer.Deserialize<PermissionChangeRecord>(logLine.Line, LfJsonOptions.Compact)
                    ?? throw new InvalidOperationException($"權限異動遷移失敗：log「{LogKey}」第 {logLine.Seq} 行反序列化為 null。");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"權限異動遷移失敗：log「{LogKey}」第 {logLine.Seq} 行無法解析（{ex.Message}）。" +
                    "資料未被修改，請確認 lf_log_lines 的內容後重新啟動。", ex);
            }

            // 舊列若沒有 change_id，用 log 列的 seq 推出一個**決定性**的 id，不能用
            // Guid.NewGuid()：遷移在 commit 後、寫入完成狀態前被中斷時會重跑，隨機 id 會讓
            // 重入比對認不出上次搬過的那幾列，同一筆資料被搬第二次。
            if (string.IsNullOrWhiteSpace(record.ChangeId))
                record.ChangeId = $"legacy{logLine.Seq}";

            parsedRecords.Add((record, logLine.CreatedAt));
        }

        // 同一個 change_id 在舊資料裡重複出現時，取最後一筆
        var deduped = parsedRecords
            .GroupBy(x => x.Record.ChangeId)
            .Select(g => g.Last())
            .ToList();

        if (deduped.Count != parsedRecords.Count)
        {
            Log.Warn("[SQL] {LogKey} 遷移：{Dup} 筆重複 change_id 已保留最後一筆", LogKey, parsedRecords.Count - deduped.Count);
        }

        // 讀取確認狀態 blob
        var confirmJson = _blobFactory(BlobKey).Read();
        var confirmations = DeserializeConfirmations(confirmJson, BlobKey);

        var validChangeIds = deduped.Select(x => x.Record.ChangeId).ToHashSet(StringComparer.Ordinal);
        var orphanConfirms = confirmations.Where(c => !validChangeIds.Contains(c.ChangeId)).ToList();
        if (orphanConfirms.Count > 0)
        {
            Log.Warn("[SQL] {BlobKey} 遷移：發現 {Count} 筆孤兒確認列（無對應異動），已略過", BlobKey, orphanConfirms.Count);
        }

        var confirmMap = confirmations
            .Where(c => validChangeIds.Contains(c.ChangeId))
            .GroupBy(c => c.ChangeId)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var rows = new List<PermissionChangeRow>(deduped.Count);
        foreach (var (record, logCreatedAt) in deduped)
        {
            // 已經在表裡的（上次遷移搬過、或本來就是新格式寫入的）跳過，避免撞 change_id 唯一索引
            if (existingChangeIds.Contains(record.ChangeId)) continue;

            // 4670 的物件類型決定它是資料夾權限還是物件權限：從原始訊息剖出來再判類別，
            // 少了它遷移進來的 Token／登錄機碼列會先被歸成資料夾權限異動
            var details = PermissionChangeExtractor.Extract(
                record.AlertText, record.ChangeType ?? string.Empty, record.EventId ?? 0);
            var category = PermissionCategory.Resolve(record.ChangeType, record.EventId, details.ObjectType);
            var isPrivilegedTarget = PermissionCategory.IsPrivilegedTarget(record.Target, record.ChangeType);
            var (initiatorAccount, targetAccount) = PermissionChangeExtractor.ExtractAccounts(
                record.AlertText,
                record.ChangeType,
                record.Before,
                record.After,
                record.InitiatorAccount,
                record.TargetAccount);

            var row = new PermissionChangeRow
            {
                ChangeId = record.ChangeId,
                DedupeKey = record.DedupeKey(),
                HostName = record.HostName,
                HostNameKey = HostNameKey.Of(record.HostName),
                DetectedAt = record.DetectedAt,
                CreatedAt = logCreatedAt ?? record.DetectedAt,
                Target = record.Target ?? string.Empty,
                ChangeType = record.ChangeType ?? string.Empty,
                Category = category,
                IsPrivilegedTarget = isPrivilegedTarget,
                InitiatorAccount = initiatorAccount,
                TargetAccount = targetAccount,
                BeforeValue = record.Before ?? string.Empty,
                AfterValue = record.After ?? string.Empty,
                AlertText = record.AlertText ?? string.Empty,
                Source = string.IsNullOrWhiteSpace(record.Source) ? PermissionChangeSources.Local : record.Source,
                EventId = record.EventId,
                ObjectType = details.ObjectType,
                ProcessName = details.ProcessName
            };

            if (confirmMap.TryGetValue(record.ChangeId, out var confirm))
            {
                row.Status = string.IsNullOrWhiteSpace(confirm.Status) ? PermissionConfirmStatuses.Pending : confirm.Status;
                row.ConfirmedBy = confirm.ConfirmedBy;
                row.ConfirmedByAccount = confirm.ConfirmedByAccount;
                row.ConfirmedAt = confirm.ConfirmedAt;
                row.ConfirmNote = confirm.Note;
            }
            else
            {
                row.Status = PermissionConfirmStatuses.Pending;
                row.ConfirmedBy = null;
                row.ConfirmedByAccount = null;
                row.ConfirmedAt = null;
                row.ConfirmNote = null;
            }

            rows.Add(row);
        }

        if (rows.Count > 0)
        {
            ctx.PermissionChanges.AddRange(rows);
        }

        return rows.Count;
    }

    private bool HasOldData()
    {
        using var ctx = _contextFactory();
        var hasLogs = ctx.LogLines.AsNoTracking().Any(l => l.LogKey == LogKey);
        if (hasLogs) return true;

        var blobContent = _blobFactory(BlobKey).Read();
        return !string.IsNullOrWhiteSpace(blobContent);
    }

    private static List<PermissionChangeConfirmation> DeserializeConfirmations(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PermissionChangeConfirmation>();

        try
        {
            return JsonSerializer.Deserialize<List<PermissionChangeConfirmation>>(json, LfJsonOptions.Pretty) ?? new List<PermissionChangeConfirmation>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"權限異動遷移失敗：blob「{key}」無法解析（{ex.Message}）。" +
                "資料未被修改，請確認 lf_blobs 的內容後重新啟動。", ex);
        }
    }
}

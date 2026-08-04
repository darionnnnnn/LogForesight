using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 權限異動的讀寫（↔ lf_permission_changes）。
///
/// **批次與 Web 的寫入職責分離**：批次呼叫 <see cref="AppendChanges"/> 寫入偵測到的異動，
/// Web 呼叫 <see cref="SaveConfirmation"/> 寫入人工確認結果。
///
/// **兩個 key、兩個寫入者**（沿用單一寫入者原則）：
///   - log key=perm_changes：批次寫入偵測到的異動（append-only）
///   - blob key=perm_confirms：Web 寫入人工確認狀態（整份型，需更新）
/// 各寫各的 key，寫入路徑不交錯。
/// </summary>
public class PermissionChangeStore
{
    private readonly EfJsonLogStore _changes;
    private readonly JsonConfirmationFile _confirmations;
    private readonly object _lock = new();

    public PermissionChangeStore(EfJsonLogStore changes, EfJsonBlobStore confirmations)
    {
        _changes = changes;
        _confirmations = new JsonConfirmationFile(confirmations);
    }

    /// <summary>批次寫入本次偵測到的異動（append-only）</summary>
    public void AppendChanges(IEnumerable<PermissionChangeRecord> changes)
    {
        lock (_lock)
        {
            foreach (var change in changes)
            {
                if (string.IsNullOrWhiteSpace(change.ChangeId))
                    change.ChangeId = Guid.NewGuid().ToString("N");

                _changes.AppendLine(JsonSerializer.Serialize(change, LfJsonOptions.Compact));
            }
        }
    }

    /// <summary>依主機與確認狀態查詢；hostNames 為空集合時回空結果（授權範圍為空）</summary>
    public List<PermissionChangeRecord> Query(IReadOnlyCollection<string>? hostNames, string? status, int maxCount)
    {
        var changes = ReadAllChanges();

        // hostNames 為 null = 不限；空集合 = 查不到任何資料（與 RecordQueryFilter 同一語意）
        if (hostNames != null)
        {
            var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            changes = changes.Where(c => names.Contains(c.HostName)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var confirmations = _confirmations.Read()
                .ToDictionary(c => c.ChangeId, StringComparer.OrdinalIgnoreCase);

            changes = changes.Where(c =>
            {
                var current = confirmations.TryGetValue(c.ChangeId, out var confirmation)
                    ? confirmation.Status
                    : PermissionConfirmStatuses.Pending;
                return string.Equals(current, status, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        return changes
            .OrderByDescending(c => c.DetectedAt)
            .Take(maxCount)
            .ToList();
    }

    public PermissionChangeRecord? Get(string changeId) =>
        ReadAllChanges().FirstOrDefault(c =>
            string.Equals(c.ChangeId, changeId, StringComparison.OrdinalIgnoreCase));

    public List<PermissionChangeConfirmation> GetConfirmations(IEnumerable<string> changeIds)
    {
        var ids = changeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _confirmations.Read().Where(c => ids.Contains(c.ChangeId)).ToList();
    }

    public void SaveConfirmation(PermissionChangeConfirmation confirmation) =>
        _confirmations.Upsert(confirmation);

    /// <summary>待確認筆數（儀表板待辦區）</summary>
    public int CountPending(IReadOnlyCollection<string>? hostNames) =>
        Query(hostNames, PermissionConfirmStatuses.Pending, int.MaxValue).Count;

    private List<PermissionChangeRecord> ReadAllChanges() =>
        JsonLogParser.Parse<PermissionChangeRecord>(_changes.ReadLines(), LfJsonOptions.Compact);

    /// <summary>確認狀態的整份型儲存（Web 單一寫入者，原子讀改寫）</summary>
    private class JsonConfirmationFile : JsonBlobCollection<PermissionChangeConfirmation>
    {
        public JsonConfirmationFile(EfJsonBlobStore blob) : base(blob) { }

        public new List<PermissionChangeConfirmation> Read() => base.Read();

        public void Upsert(PermissionChangeConfirmation confirmation)
        {
            Mutate(items =>
            {
                var existing = items.FirstOrDefault(c =>
                    string.Equals(c.ChangeId, confirmation.ChangeId, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    items.Add(confirmation);
                    return;
                }

                existing.Status = confirmation.Status;
                existing.ConfirmedBy = confirmation.ConfirmedBy;
                existing.ConfirmedByAccount = confirmation.ConfirmedByAccount;
                existing.ConfirmedAt = confirmation.ConfirmedAt;
                existing.Note = confirmation.Note;
            });
        }
    }
}

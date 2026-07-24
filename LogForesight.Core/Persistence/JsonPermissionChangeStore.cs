using System.Text;
using System.Text.Json;

namespace LogForesight;

/// <summary>
/// <see cref="IPermissionChangeStore"/> 的實作。
///
/// **兩個 key、兩個寫入者**（沿用單一寫入者原則）：
///   - log key=perm_changes：批次寫入偵測到的異動（append-only）
///   - blob key=perm_confirms：Web 寫入人工確認狀態（整份型，需更新）
/// 各寫各的 key，寫入路徑不交錯。
/// </summary>
public class JsonPermissionChangeStore : IPermissionChangeStore
{
    private readonly IJsonLogStore _changes;
    private readonly JsonConfirmationFile _confirmations;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public JsonPermissionChangeStore(IJsonLogStore changes, IJsonBlobStore confirmations)
    {
        _changes = changes;
        _confirmations = new JsonConfirmationFile(confirmations);
    }

    public void AppendChanges(IEnumerable<PermissionChangeRecord> changes)
    {
        lock (_lock)
        {
            foreach (var change in changes)
            {
                if (string.IsNullOrWhiteSpace(change.ChangeId))
                    change.ChangeId = Guid.NewGuid().ToString("N");

                _changes.AppendLine(JsonSerializer.Serialize(change, JsonOptions));
            }
        }
    }

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

    public PermissionChangeConfirmation? GetConfirmation(string changeId) =>
        _confirmations.Read().FirstOrDefault(c =>
            string.Equals(c.ChangeId, changeId, StringComparison.OrdinalIgnoreCase));

    public List<PermissionChangeConfirmation> GetConfirmations(IEnumerable<string> changeIds)
    {
        var ids = changeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _confirmations.Read().Where(c => ids.Contains(c.ChangeId)).ToList();
    }

    public void SaveConfirmation(PermissionChangeConfirmation confirmation) =>
        _confirmations.Upsert(confirmation);

    public int CountPending(IReadOnlyCollection<string>? hostNames) =>
        Query(hostNames, PermissionConfirmStatuses.Pending, int.MaxValue).Count;

    private List<PermissionChangeRecord> ReadAllChanges()
    {
        var result = new List<PermissionChangeRecord>();
        foreach (var line in _changes.ReadLines())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var change = JsonSerializer.Deserialize<PermissionChangeRecord>(line, JsonOptions);
                if (change != null) result.Add(change);
            }
            catch (JsonException)
            {
                // 逐行獨立：單行損毀只跳過該行
            }
        }
        return result;
    }

    /// <summary>確認狀態的整份型儲存（Web 單一寫入者，原子讀改寫）</summary>
    private class JsonConfirmationFile : JsonBlobCollection<PermissionChangeConfirmation>
    {
        public JsonConfirmationFile(IJsonBlobStore blob) : base(blob) { }

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

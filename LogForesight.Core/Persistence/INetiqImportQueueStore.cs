namespace LogForesight;

/// <summary>NetIQ 匯入佇列的讀寫（整檔型，走 <see cref="JsonCollectionFile{T}"/> 的原子替換＋跨程序鎖）</summary>
public interface INetiqImportQueueStore
{
    List<NetiqImportQueueEntry> GetAll();

    NetiqImportQueueEntry? Get(string queueId);

    /// <summary>新增或更新（依 QueueId）；批次套用完成/失敗時回填狀態走這個方法</summary>
    void Save(NetiqImportQueueEntry entry);
}

public class JsonNetiqImportQueueStore : JsonCollectionFile<NetiqImportQueueEntry>, INetiqImportQueueStore
{
    public JsonNetiqImportQueueStore(string filePath) : base(filePath) { }
    public JsonNetiqImportQueueStore(IJsonBlobStore blob) : base(blob) { }

    public List<NetiqImportQueueEntry> GetAll() =>
        Read().OrderByDescending(e => e.RequestedAt).ToList();

    public NetiqImportQueueEntry? Get(string queueId) =>
        Read().FirstOrDefault(e => string.Equals(e.QueueId, queueId, StringComparison.OrdinalIgnoreCase));

    public void Save(NetiqImportQueueEntry entry)
    {
        Mutate(items =>
        {
            var existing = items.FirstOrDefault(e => string.Equals(e.QueueId, entry.QueueId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                items.Add(entry);
                return;
            }

            existing.Status = entry.Status;
            existing.AppliedAt = entry.AppliedAt;
            existing.Added = entry.Added;
            existing.Updated = entry.Updated;
            existing.Revived = entry.Revived;
            existing.FailureReason = entry.FailureReason;
        });
    }
}

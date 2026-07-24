namespace LogForesight;

/// <summary>Sentinel 連線設定的讀寫（↔ webdata blob，key=sentinels）</summary>
public interface ISentinelStore
{
    List<Sentinel> GetAll();

    Sentinel? Get(long sentinelId);

    Sentinel? FindByName(string name);

    /// <summary>依 Name 自然鍵新增或更新（SentinelId==0 或名稱不存在＝新增）</summary>
    Sentinel Upsert(Sentinel sentinel);

    void Delete(long sentinelId);
}

/// <summary><see cref="ISentinelStore"/> 的實作：整份 JSON 存一筆 blob，與其他 webdata store 同模式</summary>
public class JsonSentinelStore : JsonBlobCollection<Sentinel>, ISentinelStore
{
    public JsonSentinelStore(IJsonBlobStore blob) : base(blob) { }

    public List<Sentinel> GetAll() => Read();

    public Sentinel? Get(long sentinelId) => Read().FirstOrDefault(s => s.SentinelId == sentinelId);

    public Sentinel? FindByName(string name) =>
        Read().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public Sentinel Upsert(Sentinel sentinel)
    {
        return Mutate(items =>
        {
            var existing = sentinel.SentinelId == 0
                ? null
                : items.FirstOrDefault(s => s.SentinelId == sentinel.SentinelId);

            if (existing == null)
            {
                sentinel.SentinelId = NextId(items.Select(s => s.SentinelId));
                if (sentinel.CreatedAt == default) sentinel.CreatedAt = DateTime.Now;
                items.Add(sentinel);
                return sentinel;
            }

            existing.Name = sentinel.Name;
            existing.BaseUrl = sentinel.BaseUrl;
            existing.Username = sentinel.Username;
            existing.PasswordEnc = sentinel.PasswordEnc;
            existing.Active = sentinel.Active;
            existing.UpdatedAt = DateTime.Now;
            return existing;
        });
    }

    public void Delete(long sentinelId) => Mutate(items => items.RemoveAll(s => s.SentinelId == sentinelId));
}

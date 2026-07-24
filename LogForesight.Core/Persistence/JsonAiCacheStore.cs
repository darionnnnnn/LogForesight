namespace LogForesight;

/// <summary>Web AI 加值輸出的快取存取（blob key=ai_cache）</summary>
public interface IAiCacheStore
{
    /// <summary>取快取內容；查無回 null</summary>
    string? Get(string key);

    /// <summary>寫入／更新快取，順便清掉超過保留天數的舊項</summary>
    void Put(string key, string content, int retentionDays = 7);
}

/// <summary>
/// <see cref="IAiCacheStore"/> 的實作（blob key=ai_cache，整份型，原子讀改寫）。
/// Put 時順手修剪過期項——快取量小、寫入低頻，不必另設清理排程。
/// </summary>
public class JsonAiCacheStore : JsonBlobCollection<AiCacheEntry>, IAiCacheStore
{
    public JsonAiCacheStore(IJsonBlobStore blob) : base(blob) { }

    public string? Get(string key) =>
        Read().FirstOrDefault(e => e.Key == key)?.Content;

    public void Put(string key, string content, int retentionDays = 7)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        Mutate(items =>
        {
            items.RemoveAll(e => e.Key == key || e.CreatedAt < cutoff);
            items.Add(new AiCacheEntry { Key = key, Content = content, CreatedAt = DateTime.Now });
        });
    }
}

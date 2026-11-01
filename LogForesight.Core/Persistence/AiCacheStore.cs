
namespace LogForesight.Core.Persistence;

/// <summary>
/// Web AI 加值輸出的快取存取（blob key=ai_cache，整份型，原子讀改寫）。
/// Put 時順手修剪過期項——快取量小、寫入低頻，不必另設清理排程。
/// </summary>
public class AiCacheStore : JsonBlobCollection<AiCacheEntry>
{
    /// <summary>快取筆數上限；超過時依 CreatedAt 由舊到新移除，避免整份 blob 無限長大</summary>
    public const int MaxEntries = 500;

    public AiCacheStore(EfJsonBlobStore blob) : base(blob) { }

    /// <summary>取快取內容；查無回 null</summary>
    public string? Get(string key) =>
        Read().FirstOrDefault(e => e.Key == key)?.Content;

    /// <summary>寫入／更新快取，順便清掉超過保留天數的舊項</summary>
    public void Put(string key, string content, int retentionDays = 7)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        Mutate(items =>
        {
            items.RemoveAll(e => e.Key == key || e.CreatedAt < cutoff);
            items.Add(new AiCacheEntry { Key = key, Content = content, CreatedAt = DateTime.Now });

            var excess = items.Count - MaxEntries;
            if (excess <= 0) return;
            // OrderBy 是穩定排序：時間戳相同時，先寫入（在清單較前面）的先被移除
            var drop = items.OrderBy(e => e.CreatedAt).Take(excess).ToHashSet();
            items.RemoveAll(drop.Contains);
        });
    }
}

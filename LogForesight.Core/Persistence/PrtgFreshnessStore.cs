using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// PRTG 各類資料的擷取新鮮度紀錄（blob key <see cref="BlobKey"/>，回饋第 50 輪批次B-3）。
/// 鏡像頁原本的「最後同步」是從鏡像資料推導（最大資料時間），連續數晚擷取 0 筆時時間不變、
/// 看起來正常；這裡改記「最後一次成功完成擷取的時間與筆數」，並累計連續取得 0 筆的次數。
/// 只在該類別擷取**成功完成**時記錄，失敗不記。
/// </summary>
public class PrtgFreshnessStore : JsonBlobSingleton<Dictionary<string, PrtgFreshnessStore.Entry>>
{
    public const string BlobKey = "prtg_freshness";

    public const string Devices = "devices";
    public const string Sensors = "sensors";
    public const string StateChanges = "state_changes";
    public const string Snapshot = "snapshot";
    public const string Values = "values";

    /// <summary>單一類別的新鮮度</summary>
    public class Entry
    {
        public DateTime LastSuccessAt { get; set; }
        public int LastCount { get; set; }
        /// <summary>連續取得 0 筆的次數（取得非 0 筆即歸零）</summary>
        public int ZeroStreak { get; set; }
        /// <summary>是否曾經取得過非 0 筆——從未有過資料的類別（例如沒開這項功能）不算異常</summary>
        public bool EverNonZero { get; set; }
    }

    public PrtgFreshnessStore(EfJsonBlobStore blob) : base(blob) { }

    /// <summary>該類別成功完成一次擷取時呼叫（原子讀改寫）</summary>
    public void Record(string category, int count) =>
        Update(all =>
        {
            if (!all.TryGetValue(category, out var entry))
            {
                entry = new Entry();
                all[category] = entry;
            }
            entry.LastSuccessAt = DateTime.Now;
            entry.LastCount = count;
            if (count == 0)
            {
                entry.ZeroStreak++;
            }
            else
            {
                entry.ZeroStreak = 0;
                entry.EverNonZero = true;
            }
        });

    public Dictionary<string, Entry> GetAll() => Get();

    /// <summary>曾經有資料、但最近連續 3 次以上取得 0 筆</summary>
    public static bool IsSuspicious(Entry e) => e.EverNonZero && e.ZeroStreak >= 3;
}

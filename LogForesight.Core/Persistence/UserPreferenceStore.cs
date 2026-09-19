namespace LogForesight.Core.Persistence;

/// <summary>單一使用者的個人偏好（回饋第 50 輪 C-4）</summary>
public class UserPreferences
{
    /// <summary>個人常用語；空清單＝沿用全站預設（<c>SystemSettings.DefaultNotePhrases</c>）</summary>
    public List<string> NotePhrases { get; set; } = new();
}

/// <summary>
/// 每使用者偏好的儲存（blob key <c>user_prefs</c>，內容為 userId → 偏好）。
/// 刻意不塞進使用者清單 blob：那份每次登入都會讀寫，偏好的讀改寫會和登入互相覆蓋。
/// </summary>
public class UserPreferenceStore : JsonBlobSingleton<Dictionary<long, UserPreferences>>
{
    public UserPreferenceStore(EfJsonBlobStore blob) : base(blob) { }

    /// <summary>該使用者的偏好；沒有紀錄回空偏好（不是 null）</summary>
    public UserPreferences Get(long userId) =>
        Get().TryGetValue(userId, out var prefs) ? prefs : new UserPreferences();

    /// <summary>原子讀改寫個人常用語；空清單＝刪除個人清單（回到全站預設）</summary>
    public void SetNotePhrases(long userId, List<string> phrases) =>
        Update(all =>
        {
            if (phrases.Count == 0)
            {
                all.Remove(userId);
                return;
            }
            all[userId] = new UserPreferences { NotePhrases = phrases.ToList() };
        });
}

/// <summary>常用語清單的正規化與上限（個人清單與全站預設共用同一套規則）</summary>
public static class NotePhraseRules
{
    public const int MaxCount = 20;
    public const int MaxLength = 200;

    /// <summary>trim、去除空白行、去重（保留第一次出現的順序）</summary>
    public static List<string> Normalize(IEnumerable<string?>? phrases) =>
        (phrases ?? Enumerable.Empty<string?>())
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>超出上限時回中文錯誤訊息；合法回 null</summary>
    public static string? Validate(IReadOnlyList<string> normalized)
    {
        if (normalized.Count > MaxCount)
            return $"常用語最多 {MaxCount} 條（目前 {normalized.Count} 條）。";
        var tooLong = normalized.FirstOrDefault(p => p.Length > MaxLength);
        if (tooLong != null)
            return $"每條常用語最多 {MaxLength} 字（「{tooLong[..20]}…」共 {tooLong.Length} 字）。";
        return null;
    }
}

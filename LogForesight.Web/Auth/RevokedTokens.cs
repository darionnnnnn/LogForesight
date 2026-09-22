using System.Collections.Concurrent;

namespace LogForesight.Web.Auth;

/// <summary>
/// 已登出（撤銷）的 token 清單，以 <c>jti</c> 為鍵、記到 token 原本的到期時間為止。
///
/// 只存在記憶體：行程重啟即清空，重啟前登出的 token 在剩餘效期內又能用。
/// 這是可接受的取捨——登出撤銷防的是「登出後 cookie 被別人拿去重放」，重啟是低頻事件，
/// 為此把撤銷清單落地並逐請求查資料庫不划算；停用帳號仍由 ActiveUserMiddleware 即時擋下。
/// </summary>
public class RevokedTokens
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new(StringComparer.Ordinal);

    public void Revoke(string jti, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrEmpty(jti)) return;
        PurgeExpired(DateTimeOffset.UtcNow);
        _revoked[jti] = expiresAt;
    }

    public bool IsRevoked(string? jti)
    {
        if (string.IsNullOrEmpty(jti)) return false;
        if (!_revoked.TryGetValue(jti, out var expiresAt)) return false;
        if (expiresAt > DateTimeOffset.UtcNow) return true;

        // 已過期的 token 本身就會被 JWT 驗證擋下，不必再佔記憶體
        _revoked.TryRemove(jti, out _);
        return false;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var (jti, expiresAt) in _revoked)
        {
            if (expiresAt <= now) _revoked.TryRemove(jti, out _);
        }
    }
}

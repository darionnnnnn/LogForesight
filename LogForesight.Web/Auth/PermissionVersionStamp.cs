using System.Globalization;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Auth;

/// <summary>
/// 權限版本號：任何會改變「某人能做什麼」的寫入（群組成員、群組角色／啟停／刪除、使用者啟停、
/// 主機負責人、問題負責人）都推進一次。JWT 簽發時帶上當下版本（claim <c>pv</c>），
/// <see cref="Middleware.ActiveUserMiddleware"/> 發現 token 的版本落後就重算能力並無感換發 token。
///
/// 版本號存在 <c>lf_blobs</c>（key <c>permission_version</c>）而不是只放記憶體：
/// 行程重啟後若歸零，重啟前簽出的 token 帶的舊版本號可能恰好等於新行程推進後的值，
/// 等於漏掉一次重算。讀取快取在記憶體——寫入者只有本行程，推進時同步更新快取。
/// </summary>
public class PermissionVersionStamp
{
    public const string BlobKey = "permission_version";

    private readonly EfJsonBlobStore _blob;
    private readonly object _lock = new();
    private long? _cached;

    public PermissionVersionStamp(EfJsonBlobStore blob)
    {
        _blob = blob;
    }

    /// <summary>目前版本號（尚未推進過為 0）</summary>
    public long Current
    {
        get
        {
            lock (_lock)
            {
                _cached ??= Parse(_blob.Read());
                return _cached.Value;
            }
        }
    }

    /// <summary>權限來源有變動：原子遞增並寫回</summary>
    public long Bump()
    {
        lock (_lock)
        {
            var next = _blob.Mutate(content =>
            {
                var value = Parse(content) + 1;
                return (value.ToString(CultureInfo.InvariantCulture), value);
            });
            _cached = next;
            return next;
        }
    }

    private static long Parse(string? content) =>
        long.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

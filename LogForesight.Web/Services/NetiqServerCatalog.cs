namespace LogForesight.Web.Services;

/// <summary>
/// Sentinel 名單的讀取層（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1）。
///
/// **單一事實來源現在是共用的 <see cref="ISentinelStore"/>**（批次與 Web 都讀寫同一份資料庫）。
/// 這是既有決策（NETIQ-HOSTLIST-WEB-PLAN.md 決策 E：曾以批次 appsettings.json 為唯一事實來源）
/// 的修訂——當時的前提是「批次與 Web 靠 DataRoot 共用檔案」，Phase C 之後共用點已是資料庫，
/// 讓 Web 直接管理 Sentinel 反而消除了「畫面選得到、批次卻查不到」的分歧風險。
/// 介面維持原樣，呼叫端（NetiqDiscoveryService／NetiqHostService／HostAdminService）零改動。
/// </summary>
public interface INetiqServerCatalog
{
    /// <summary>已設定的 Sentinel 名稱（依名稱排序，含已停用的——停用不等於不存在，見類別註解）</summary>
    List<string> GetServerNames();

    /// <summary>名稱是否存在於設定中（不分大小寫）；用於登錄與匯入時的驗證</summary>
    bool IsKnownServer(string? name);

    /// <summary>依名稱取單一 Sentinel 設定（含 BaseUrl 與探索帳密，密碼已解密；不分大小寫，查無回 null）——主動探索用，密碼絕不外流至前端</summary>
    SentinelServer? GetServer(string? name);
}

public class NetiqServerCatalog : INetiqServerCatalog
{
    private readonly ISentinelStore _sentinels;

    public NetiqServerCatalog(ISentinelStore sentinels) => _sentinels = sentinels;

    public SentinelServer? GetServer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sentinel = _sentinels.FindByName(name.Trim());
        return sentinel == null ? null : ToProjection(sentinel);
    }

    public List<string> GetServerNames() =>
        _sentinels.GetAll().Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsKnownServer(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _sentinels.FindByName(name.Trim()) != null;

    /// <summary>密碼在這裡解密：探索用戶端需要明碼才能認證,解密結果只留在 Web 行程內、絕不序列化回前端</summary>
    private static SentinelServer ToProjection(Sentinel sentinel) => new()
    {
        Id = sentinel.SentinelId,
        Name = sentinel.Name,
        BaseUrl = sentinel.BaseUrl,
        Username = sentinel.Username,
        Password = CryptoHelper.IsEncrypted(sentinel.PasswordEnc)
            ? CryptoHelper.Decrypt(sentinel.PasswordEnc)
            : sentinel.PasswordEnc
    };
}

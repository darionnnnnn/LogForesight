using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// Sentinel 連線資訊組裝：密碼在這裡解密，僅存在於呼叫端行程記憶體，不落地、不進 log。
/// <see cref="NetiqProbeRunner"/>／<see cref="NetiqPipelineService"/>（Core）與 Web 端的
/// <c>NetiqServerCatalog</c> 共用同一份，避免兩處各寫一份相同的解密邏輯。
/// </summary>
public static class SentinelConnectionFactory
{
    public static SentinelServer ToConnectable(Sentinel s)
    {
        var (password, state) = Resolve(s);
        return new SentinelServer
        {
            Id = s.SentinelId,
            Name = s.Name,
            BaseUrl = s.BaseUrl,
            Username = s.Username,
            // 明文原樣回傳；解不開（金鑰不符）給空字串並標 Undecryptable，由呼叫端決定不送請求
            Password = password,
            UseEsmDirectory = s.UseEsmDirectory,
            Os = s.Os,
            CredentialState = state
        };
    }

    /// <summary>
    /// Sentinel 查詢帳密的可用狀態——全站唯一判定。管線、probe、探索精靈與管理頁清單都讀這裡，
    /// 不要再用 <see cref="Sentinel.CanDiscover"/>（只看密文欄位非空，解不開時仍是 true，會拿空密碼登入）。
    /// 不放在 POCO 的屬性上：屬性會在序列化時被呼叫，等於每次存檔都去解密。
    /// </summary>
    public static CredentialState CredentialStateOf(Sentinel s) => Resolve(s).State;

    private static (string Password, CredentialState State) Resolve(Sentinel s)
    {
        if (string.IsNullOrWhiteSpace(s.PasswordEnc)) return ("", CredentialState.NotConfigured);

        if (!CryptoHelper.TryDecrypt(s.PasswordEnc, out var password))
            return ("", CredentialState.Undecryptable);

        var usable = !string.IsNullOrWhiteSpace(s.Username) && !string.IsNullOrWhiteSpace(password);
        return (password, usable ? CredentialState.Ok : CredentialState.NotConfigured);
    }
}

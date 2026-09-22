namespace LogForesight.Core.Service;

/// <summary>
/// Sentinel 連線資訊組裝：密碼在這裡解密，僅存在於呼叫端行程記憶體，不落地、不進 log。
/// <see cref="NetiqProbeRunner"/>／<see cref="NetiqPipelineService"/>（Core）與 Web 端的
/// <c>NetiqServerCatalog</c> 共用同一份，避免兩處各寫一份相同的解密邏輯。
/// </summary>
public static class SentinelConnectionFactory
{
    public static SentinelServer ToConnectable(Sentinel s) => new()
    {
        Id = s.SentinelId,
        Name = s.Name,
        BaseUrl = s.BaseUrl,
        Username = s.Username,
        // 明文原樣回傳；解不開（金鑰不符）當成未設定，不讓取數整趟失敗
        Password = CryptoHelper.TryDecrypt(s.PasswordEnc, out var password) ? password : "",
        UseEsmDirectory = s.UseEsmDirectory,
        Os = s.Os
    };
}

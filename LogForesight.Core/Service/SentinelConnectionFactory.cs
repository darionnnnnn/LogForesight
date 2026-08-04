namespace LogForesight.Core.Service;

/// <summary>
/// Sentinel 連線資訊組裝，批次端（<see cref="NetiqProbeRunner"/>／<see cref="NetiqPipelineService"/>）
/// 共用同一份——docs/NETIQ-API-REFERENCE.md §2.1 說的「批次與 Web 是不同部署單元、各自一份合理」
/// 講的是跨專案；這兩個都在同一個 console 專案內，沒有理由各寫一份同樣的解密邏輯。
/// public：Web 端的 NetIQ 診斷分頁（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.11）也共用同一份，
/// 不再各寫一份解密邏輯。
/// </summary>
public static class SentinelConnectionFactory
{
    /// <summary>密碼在這裡解密，僅存在於本行程記憶體，不落地、不進 log（同 Web 端
    /// NetiqServerCatalog.ToProjection 的既有慣例）</summary>
    public static SentinelServer ToConnectable(Sentinel s) => new()
    {
        Id = s.SentinelId,
        Name = s.Name,
        BaseUrl = s.BaseUrl,
        Username = s.Username,
        Password = CryptoHelper.IsEncrypted(s.PasswordEnc) ? CryptoHelper.Decrypt(s.PasswordEnc) : s.PasswordEnc
    };
}

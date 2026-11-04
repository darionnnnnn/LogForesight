namespace LogForesight.Core.Models;

/// <summary>
/// 憑證（密碼、token、金鑰）的可用狀態，全站只有這一個判定出口：
/// Sentinel 由 <c>SentinelConnectionFactory</c> 回傳，PRTG 由 <c>PrtgClientFactory.CredentialState</c> 回傳。
/// 「沒填」和「填了但用目前金鑰解不開」要分開——前者是使用者尚未設定，後者是金鑰檔與資料庫不相符，
/// 兩者的處置與訊息完全不同；混成一個「未設定」會讓人以為設定頁是空的。
/// </summary>
public enum CredentialState
{
    /// <summary>已設定且解得開，可以使用</summary>
    Ok = 0,

    /// <summary>欄位為空（或帳號缺漏）：使用者尚未設定</summary>
    NotConfigured = 1,

    /// <summary>密文存在但用目前金鑰解不開：需要重新輸入該憑證</summary>
    Undecryptable = 2
}

public static class CredentialStateExtensions
{
    /// <summary>對外（DTO／JSON）的小寫字串：<c>ok</c>／<c>not_configured</c>／<c>undecryptable</c></summary>
    public static string ToWire(this CredentialState state) => state switch
    {
        CredentialState.Ok => "ok",
        CredentialState.NotConfigured => "not_configured",
        CredentialState.Undecryptable => "undecryptable",
        _ => "not_configured"
    };
}

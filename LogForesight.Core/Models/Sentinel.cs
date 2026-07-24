namespace LogForesight;

/// <summary>
/// NetIQ Sentinel 連線設定（↔ webdata blob，key=sentinels）。
///
/// 取代原本「批次 appsettings.json 的 NetIq.Servers 是唯一事實來源」的決策
/// （docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1）：批次與 Web 現在共用資料庫，Sentinel 改由 Web 維護，
/// 批次與 Web 都讀同一份 store。appsettings.NetIq.Servers 降為僅供空庫時的一次性種子。
/// </summary>
public class Sentinel
{
    public long SentinelId { get; set; }

    /// <summary>識別名稱，也是主機清單登錄「所屬 Sentinel」時填的值。不分大小寫唯一。</summary>
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>探索連線帳號。空白＝此 Sentinel 無法主動掃描</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 探索連線密碼的密文（<see cref="CryptoHelper.Encrypt"/> 產生，帶 <c>enc:v1:</c> 前綴）。
    /// 前端一律 write-only：已設定只顯示「已設定」，留空＝不變；絕不回傳明碼、絕不進稽核。
    /// </summary>
    public string PasswordEnc { get; set; } = string.Empty;

    /// <summary>
    /// false＝停用（暫停輪巡，主機不動、不標記孤兒）——汰換過渡期用的溫和選項，
    /// 與刪除（觸發孤兒流程）刻意分開，見 <see cref="LogForesight.NetiqOrphanSweeper"/>。
    /// </summary>
    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>帳密齊備才可主動掃描（缺任一則精靈的掃描鈕停用並提示設定不完整）</summary>
    public bool CanDiscover => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordEnc);
}

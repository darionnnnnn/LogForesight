namespace LogForesight.Core.Models;

/// <summary>
/// NetIQ Sentinel 連線設定（↔ webdata blob，key=sentinels）。
///
/// 取代原本「批次 appsettings.json 的 NetIq.Servers 是唯一事實來源」的決策
/// （docs/archive/HISTORY.md 定案 1）：批次與 Web 現在共用資料庫，Sentinel 改由 Web 維護，
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

    /// <summary>
    /// 'windows'（預設）| 'linux'。**這台 Sentinel 轄下主機的作業系統**（2026-07-29 環境事實確認：
    /// 此環境 Windows／Linux 的 NetIQ 已完全拆分成不同 Sentinel，同一台 Sentinel 不混平台）。
    /// 掃描匯入精靈以此值預填整批 OS（可改，當混合環境的逃生門，見 docs/LINUX-RULES.md §3）；
    /// 不影響既有主機——匯入不是隱性改設定，只決定「這次新增的主機」的預設值。
    /// 儲存值恆為 <see cref="WebHost.NormalizeOs"/> 正規化後的小寫值。
    /// </summary>
    public string Os { get; set; } = WebHost.OsWindows;

    /// <summary>
    /// 以 ESM 事件來源目錄（<c>/SentinelRESTServices/objects/eventsource</c>）取代事件掃描來探索主機
    /// （docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §五）。**預設 false**。
    ///
    /// ESM 目錄本來是探索的正解——一次唯讀查詢就拿到已註冊主機清單，而且包含
    /// **目前完全沒在回報的主機**（事件掃描原理上看不到那些，見 §3.4 的涵蓋保證）。
    /// 但本環境的探索帳號被 401/403 拒絕（權限問題，不是 API 不存在），
    /// 因此**回應格式在本環境無法驗證**——不能自動信任一個沒驗證過的解析結果，
    /// 錯了會讓主機清單靜默變形。
    ///
    /// 所以做成 per-Sentinel 的開關（不同 Sentinel 的帳號權限本來就可能不同），
    /// 預設關閉、有權限的環境自行開啟；開啟前應先在「診斷」分頁確認步驟 6
    /// 取得得到事件來源清單。開著但每次都退回事件掃描時會持續發出警告——
    /// 刻意吵，逼人把開關關掉或把回應格式回報回來定案。
    /// </summary>
    public bool UseEsmDirectory { get; set; }

    /// <summary>帳密齊備才可主動掃描（缺任一則精靈的掃描鈕停用並提示設定不完整）</summary>
    public bool CanDiscover => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(PasswordEnc);
}

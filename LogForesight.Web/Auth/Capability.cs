namespace LogForesight.Web.Auth;

/// <summary>
/// 功能能力（docs/WEB-SPEC.md §7.1 三層授權的第 2 層）。
///
/// 能力回答的是「你能不能用這個功能」，**不回答「你能看哪些主機的資料」**——
/// 後者是資料範圍，由 Service 層的 IVisibilityService 每次請求即時解析。
/// 兩者刻意分開：能力進 JWT（效期內固定），範圍不進（群組異動即時生效）。
/// </summary>
public enum Capability
{
    /// <summary>檢視全部主機的業務資料（未持有者只能看被授權的主機群組）</summary>
    ViewAll,

    /// <summary>維護風險日的處理狀態、說明、預計完成日（限授權範圍內的主機）</summary>
    Handle,

    /// <summary>指派/改派處理人。刻意獨立於 Handle 之外：只有 admin 能決定「誰來處理」</summary>
    Assign,

    /// <summary>權限異動的逐筆確認（授權操作／標記可疑）</summary>
    ConfirmPermission,

    /// <summary>維護功能：規則、使用者、主機、群組授權、CSV 匯入</summary>
    Maintain,

    /// <summary>執行監控頁（批次執行狀態與診斷 log）</summary>
    DevMonitor,

    /// <summary>操作紀錄查閱</summary>
    ViewAudit
}

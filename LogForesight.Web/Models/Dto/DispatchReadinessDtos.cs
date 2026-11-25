namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 派工就緒檢查（系統健康頁「派工就緒」卡）：把「派工會靜默失效」的八種狀況逐項列出。
/// 每一項都重用既有判定（派工池與可見範圍用派工候選人快照、AI 窗口用排程的 <c>HasAnyWindow</c>、
/// email 資格用 <c>MailNotificationService.EligibleEmail</c>），不另寫第二份。
/// </summary>
public class DispatchReadinessDto
{
    /// <summary>
    /// 要顯示的項目（固定順序）。郵件通知未開啟時「管理員沒有 email」「派工池成員沒有 email」兩項不列入
    /// （不在清單內，也不算不通過），<see cref="MailChecked"/> 為 false。
    /// </summary>
    public List<DispatchReadinessItemDto> Items { get; set; } = new();

    /// <summary>不通過的項目數（略過的項目不算）；0＝派工就緒</summary>
    public int FailedCount { get; set; }

    /// <summary>郵件通知（<c>MailEnabled</c>）是否開啟：開啟時才檢查兩個 email 項</summary>
    public bool MailChecked { get; set; }
}

/// <summary>
/// 就緒檢查的一項。<see cref="Passed"/> 為 false 才算不通過；<see cref="Skipped"/> 為 true 表示前提不成立而無法判定
/// （例如派工池沒有可接單的成員時，無法判定主機群組看不看得到），這種項目 <see cref="Passed"/> 為 true、不進健康原因。
/// </summary>
public class DispatchReadinessItemDto
{
    /// <summary>host-no-group｜host-group-no-pool｜no-pool｜auto-dispatch-off｜ai-window｜schedule-off｜admin-no-email｜pool-no-email</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public bool Skipped { get; set; }

    /// <summary>
    /// 受影響的數量。單位依項目而異：<c>host-no-group</c>＝沒有群組的啟用主機台數；<c>host-group-no-pool</c>＝沒有派工池群組看得到的主機群組數
    /// （只算啟用且有啟用主機的群組，空群組不算）；<c>admin-no-email</c>＝沒有可用 email 的管理員人數；
    /// <c>pool-no-email</c>＝沒有可用 email 的派工池成員人數；其餘開關類項目通過為 0、不通過為 1。
    /// </summary>
    public int Affected { get; set; }

    /// <summary>一句中文說明（通過與不通過各一句；不通過時也是健康整體狀態的原因文字）</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>受影響對象的名稱（主機名、群組名或帳號），最多 5 個</summary>
    public List<string> Examples { get; set; } = new();

    /// <summary>修正入口（站台內路徑，前端以 appUrl 組裝）</summary>
    public string FixUrl { get; set; } = string.Empty;

    public string FixLabel { get; set; } = string.Empty;
}

/// <summary>使用者編輯視窗的群組即時摘要：依「目前勾選」的群組算出，不寫入任何資料。</summary>
public class UserGroupPreviewDto
{
    /// <summary>能力（中文），依固定順序；停用帳號或沒有任何能力時為空</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>可見主機台數：群組授權 ∪ 該使用者負責的主機（與使用者詳情的「看得到的主機」同口徑）；新增中的使用者沒有負責主機</summary>
    public int VisibleHostCount { get; set; }

    /// <summary>是否屬於任一啟用中的派工池群組</summary>
    public bool InPool { get; set; }

    /// <summary>是否具處理能力（<c>Handle</c>）：派工池成員沒有它就不會被派單</summary>
    public bool CanHandle { get; set; }

    /// <summary>勾選的群組裡不存在或已停用而被略過的名稱數（停用群組不給能力、不給可見範圍）</summary>
    public int InactiveGroupCount { get; set; }
}

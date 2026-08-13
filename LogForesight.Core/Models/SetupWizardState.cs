namespace LogForesight.Core.Models;

/// <summary>
/// 首次啟動精靈的狀態（↔ webdata blob，key=setup_wizard_state，回饋十八輪批次H）：
/// 全站單一狀態（不分使用者）——精靈是「這個部署完成初始設定了嗎」的全站性質，
/// 不是個人偏好。
/// </summary>
public class SetupWizardState
{
    /// <summary>使用者明確跳過的步驟 id（見 SetupReadinessService 的步驟清單）。
    /// 跳過可逆——精靈頁隨時可以「取消跳過」。</summary>
    public HashSet<string> SkippedSteps { get; set; } = new();

    /// <summary>教學文件清單裡的精靈入口是否隱藏。只有全部步驟達終態（完成或跳過）時
    /// 前端才會出現隱藏選項，但後端不強制——管理者想提前隱藏也可以。</summary>
    public bool Hidden { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

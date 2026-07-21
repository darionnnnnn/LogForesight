namespace LogForesight;

/// <summary>
/// 抑制設定的儲存後端抽象。與規則庫（IKnownIssueRuleStore）分開儲存：抑制是各主機的營運狀態，
/// 生命週期與規則本身不同（seed／匯入永遠不該碰到抑制資料），見 docs/RULES-PLAN.md。
/// 缺檔＝空清單（不是錯誤，也沒有 seed 概念——第一次使用前本來就不該有任何抑制項目）。
/// </summary>
public interface ISuppressionStore
{
    string Location { get; }

    /// <summary>讀取全部抑制項目（含已到期的，是否生效由呼叫端依 ExpiresAt 判斷）。
    /// 檔案不存在或損毀時回傳空清單並記警告，不拋例外——抑制設定是可選功能，不該擋下主流程。</summary>
    List<RuleSuppression> LoadAll();

    /// <summary>覆寫整份抑制清單（原子寫入）</summary>
    void SaveAll(List<RuleSuppression> suppressions);
}

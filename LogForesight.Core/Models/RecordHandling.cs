namespace LogForesight.Core.Models;

/// <summary>
/// 風險日的處理狀態（↔ lf_record_handling）。
/// 以「主機＋日期」為鍵，與分析紀錄的自然鍵一致。
/// </summary>
public class RecordHandling
{
    public string HostName { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>open | in_progress | resolved | wont_fix | false_positive | known_noise</summary>
    public string Status { get; set; } = HandlingStatuses.Open;

    /// <summary>
    /// 處理人（單人）。**與主機負責人是兩件事**：負責人是「這台機器平常歸誰管」的長期屬性，
    /// 處理人是「這個問題現在誰在處理」的事件層級指派，可以不是負責人。
    /// 只有 admin 能指派/改派。
    /// </summary>
    public long? HandlerId { get; set; }

    /// <summary>預計完成日——儀表板「逾期未處理」的依據</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>目前的處理說明（完整敘事在 <see cref="RecordHandlingLog"/>）</summary>
    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 處理歷程（↔ lf_record_handling_log，append-only）。
///
/// 為什麼快照與歷程要分兩份：處理說明會隨事件演進（指派 → 查修中 → 換了硬碟 → 結案），
/// 單一 Note 欄位每次更新就把前一段蓋掉，「後續查看的人快速了解」會只剩最後一句。
/// </summary>
public class RecordHandlingLog
{
    public long LogId { get; set; }

    public string HostName { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public string Status { get; set; } = string.Empty;

    public long? HandlerId { get; set; }

    public string? Note { get; set; }

    /// <summary>實際執行操作的人（可能不是 HandlerId——同部門互相代理更新狀態是常態）</summary>
    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    /// <summary>這筆歷程記錄的是什麼動作，供 timeline 顯示（指派/狀態變更/說明更新/標記問題）</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// 問題層級操作才有值（docs/archive/HISTORY.md #6/D4）：日層級操作（指派/日層級狀態變更）
    /// 維持 null。問題層級標記逐筆記錄——「攏統的彙總標記沒有意義」，批次勾 N 項就要留下 N 列，
    /// 每一列都查得到「誰、何時、對哪個問題、標成什麼」。
    /// </summary>
    public string? IssueKey { get; set; }

    /// <summary>
    /// 問題的顯示文字（「Source EventId」）**反正規化存下來**：歷程是追責紀錄，
    /// 不能因為日後這筆問題不再出現在報告裡、或規則被改名，就查不回「當時標的是哪個問題」。
    /// </summary>
    public string? IssueLabel { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class HandlingStatuses
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Resolved = "resolved";
    public const string WontFix = "wont_fix";
    public const string FalsePositive = "false_positive";
    public const string KnownNoise = "known_noise";

    /// <summary>無法處理（回饋十八輪批次G）：處理人上報「我處理不了」，等 admin 決定結案或
    /// 重新指派。**非結案**（列入 <see cref="Unresolved"/>——上報中就是還沒處理完，週報未處理數
    /// 與待辦統計都要計入），對外三態歸「處理中」。與問題層級的
    /// <see cref="IssueHandlingStatuses.Escalated"/> 同值同語意，兩層值域必須同步維護。</summary>
    public const string Escalated = "escalated";

    public static readonly string[] All =
    {
        Open, InProgress, Resolved, WontFix, FalsePositive, KnownNoise, Escalated
    };

    /// <summary>尚未結案的狀態——儀表板待辦與逾期清單的依據</summary>
    public static readonly string[] Unresolved = { Open, InProgress, Escalated };

    public static bool IsValid(string status) => All.Contains(status);

    /// <summary>
    /// 對外一律三態（docs/archive/HISTORY.md #12）：問題查詢清單、CSV、儀表板／報表
    /// 統計只呈現 未處理／處理中／已處理，六種結案類細節（不處理/誤報/已知雜訊…）
    /// 一律收斂為「已處理」——那些是「已經有結論」，對外部檢視而言就是不再需要動作了。
    /// **只在對外出口套用，不動 DayHandlingDerivation 的內部推導**：Unresolved/逾期判定
    /// 仍要看真正的 Open/InProgress，不能被這裡的收斂污染。詳細結論只在風險日詳情頁呈現
    /// （問題層級 IssueHandlingStatuses 的六態原樣顯示，不受此函式影響）。
    /// </summary>
    public static string ExternalOf(string status) => status switch
    {
        Open => Open,
        InProgress => InProgress,
        // 觀察中（docs/archive/FEEDBACK-8-PLAN.md #4）：對外三態收斂為「處理中」——有人在管，不是有結論。
        // 日層級推導（DayHandlingDerivation.Derive）已把 observing 映成 in_progress，正常路徑
        // 走不到這個分支；這裡是防禦——沒有它，任何未來漏進來的 observing 會掉進 default
        // 被誤歸「已處理」，觀察中的日子就從待辦統計裡消失了
        IssueHandlingStatuses.Observing => InProgress,
        // 無法處理（回饋十八輪批次G）：上報中＝有人在管但還沒有結論——同 observing 歸「處理中」，
        // 掉進 default 被歸「已處理」的話，上報等 admin 決定的日子會從待辦統計裡消失
        Escalated => InProgress,
        _ => Resolved
    };
}

public static class HandlingActions
{
    public const string Assign = "assign";
    public const string StatusChange = "status";
    public const string NoteUpdate = "note";
    public const string AutoAssign = "auto_assign";

    /// <summary>問題層級標記狀態（含批次套用，逐問題一列）——docs/archive/HISTORY.md #6/D4</summary>
    public const string IssueStatus = "issue_status";

    /// <summary>問題層級清除標記（調回未處理）</summary>
    public const string IssueStatusCleared = "issue_status_cleared";

    /// <summary>建立案件／改派案件處理人（docs/archive/FEEDBACK-4-PLAN.md §0.4-A，記在觸發日的歷程）</summary>
    public const string CaseAssign = "case_assign";

    /// <summary>案件狀態同步展開到某日（逐日一列，actor＝觸發同步的使用者，§0.4-B）</summary>
    public const string CaseSync = "case_sync";

    /// <summary>批次排程把新的一天掛進進行中案件（逐日一列，actor＝系統，§0.4-C）</summary>
    public const string CaseAttach = "case_attach";

    /// <summary>
    /// 改派進行中案件的處理人（docs/archive/FEEDBACK-10-PLAN.md §9）。與 <see cref="CaseAssign"/> 分開是
    /// 因為要回答的問題不同：CaseAssign 是「這個問題開始有人處理」，這個是「換人處理」——
    /// 追責時「原本誰、後來誰、誰換的」必須查得到，混進建案動作就分不出來了。
    /// </summary>
    public const string CaseReassign = "case_reassign";

    /// <summary>
    /// 批次排程依問題檔案的機房結論自動套用到新出現的主機日（回饋十九輪批次F，逐日一列，
    /// actor＝系統，§2 決策一）。與 <see cref="CaseAttach"/> 分開是因為觸發條件不同——
    /// CaseAttach 是「這台主機這個問題已經有進行中案件在追」，這個是「沒有案件，但機房
    /// 對這個問題本身已經有結論」，追責時要分得出這一天的狀態是案件繼承來的還是機房結論
    /// 自動套用的。
    /// </summary>
    public const string FleetApply = "fleet_apply";
}

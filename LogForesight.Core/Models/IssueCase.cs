namespace LogForesight.Core.Models;

/// <summary>
/// 問題案件（↔ 未來 lf_issue_cases，docs/archive/FEEDBACK-4-PLAN.md §0）：以（主機、問題簽章）為鍵的
/// 跨日處理協調紀錄——回答「同一台主機的同一個問題，跨越多天，現在歸誰處理、處理到哪」。
///
/// **案件只是協調紀錄，逐日 <see cref="IssueHandling"/> 列仍是唯一投影面**：案件的每個動作
/// （建案、狀態變更、批次掛接）都由 <see cref="IssueCaseCoordinator"/> 展開寫入受影響日期的
/// <see cref="IssueHandling"/> 列（<see cref="IssueHandling.CaseId"/> 標記出處），而不是讓
/// 清單／儀表板／報表改讀案件——這是控制本次改動爆炸半徑的關鍵：DayHandlingDerivation 等
/// 既有推導規則單點不動，就不會出現「清單說未處理、儀表板說已處理」的漂移。
///
/// 鍵＝主機＋問題簽章（<see cref="IssueSignatureKey"/>）。同一（主機, 問題簽章）同時間
/// 至多一個進行中案件（<see cref="ClosedAt"/> == null）；歷史結案案件保留，查得到
/// 「上次誰處理的、怎麼結的」。
/// </summary>
public class IssueCase
{
    /// <summary>GUID 字串，逐日 IssueHandling 列的 CaseId 回鏈用</summary>
    public string CaseId { get; set; } = string.Empty;

    /// <summary>現行主機名稱，同 RecordHandling 的鍵語意（合併時舊名案件不自動改鍵）</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>問題簽章的穩定鍵（見 <see cref="IssueSignatureKey.For"/>）</summary>
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>「Source EventId」反正規化存下來（同 RecordHandlingLog.IssueLabel 理由：
    /// 案件是追責紀錄，不能因為規則改名或問題不再出現就查不回當時處理的是哪個問題）</summary>
    public string IssueLabel { get; set; } = string.Empty;

    /// <summary>IssueHandlingStatuses 值域（open/in_progress/結案四種）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>案件處理人（單人，docs/archive/FEEDBACK-4-PLAN.md 2.1「同主機同問題只由一個人處理」）。
    /// 與日層級 RecordHandling.HandlerId 是兩件事——後者是「這一天」的案件層概念，
    /// 這裡是「這個問題跨日歸誰」，兩者並存</summary>
    public long? HandlerId { get; set; }

    /// <summary>最近一次說明快照；完整敘事仍在 RecordHandlingLog（案件同步逐日寫一列）</summary>
    public string? Note { get; set; }

    /// <summary>預計完成日——Status=in_progress 時有意義；Status=observing（docs/archive/FEEDBACK-8-PLAN.md #4）
    /// 時沿用同一欄位當「觀察至」日期，同問題層級 <see cref="IssueHandling.DueDate"/> 的規則</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>回溯關聯到的最早風險日</summary>
    public DateTime FirstLinkedDate { get; set; }

    /// <summary>最近一次掛接的風險日（批次逐日推進）</summary>
    public DateTime LastLinkedDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedByAccount { get; set; } = string.Empty;

    /// <summary>結案時間；null＝進行中（批次逐日掛接只找進行中案件）</summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

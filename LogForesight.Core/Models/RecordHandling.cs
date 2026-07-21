namespace LogForesight;

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

    /// <summary>這筆歷程記錄的是什麼動作，供 timeline 顯示（指派/狀態變更/說明更新）</summary>
    public string Action { get; set; } = string.Empty;

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

    public static readonly string[] All =
    {
        Open, InProgress, Resolved, WontFix, FalsePositive, KnownNoise
    };

    /// <summary>尚未結案的狀態——儀表板待辦與逾期清單的依據</summary>
    public static readonly string[] Unresolved = { Open, InProgress };

    public static bool IsValid(string status) => All.Contains(status);
}

public static class HandlingActions
{
    public const string Assign = "assign";
    public const string StatusChange = "status";
    public const string NoteUpdate = "note";
    public const string AutoAssign = "auto_assign";
}

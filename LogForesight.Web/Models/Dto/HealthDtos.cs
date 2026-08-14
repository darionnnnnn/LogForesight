namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 匿名可讀的存活檢查（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
/// **刻意只有三個欄位**：監控系統要的是「活著沒有」，多一個欄位就多一分把內部狀態
/// 洩漏給未登入者的風險。診斷細節在 <see cref="HealthDetailDto"/>（需 Maintain）。
/// </summary>
public class HealthDto
{
    /// <summary>ok｜degraded｜down</summary>
    public string Status { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool StorageOk { get; set; }
}

/// <summary>維運診斷用的完整健康資訊（需 <c>Maintain</c>）</summary>
public class HealthDetailDto : HealthDto
{
    /// <summary>資料庫不可達時的錯誤訊息（正常為 null）</summary>
    public string? StorageError { get; set; }

    // ── 資料層慢操作（E5）────────────────────────────────────────────────

    public int SlowThresholdMs { get; set; }
    public long TotalOperations { get; set; }
    public long SlowOperations { get; set; }
    public long SlowestMs { get; set; }
    public string SlowestOperation { get; set; } = string.Empty;
    public DateTime? LastSlowAt { get; set; }

    // ── 分析執行狀態（E1：夜間分析與 Web 同行程，要看得出現在是不是正在跑）──

    public bool AnalysisRunning { get; set; }
    public string? AnalysisTrigger { get; set; }
    public DateTime? AnalysisStartedAt { get; set; }
    public string? AnalysisPhase { get; set; }
    public int AnalysisDone { get; set; }
    public int AnalysisTotal { get; set; }
    public bool? LastRunSucceeded { get; set; }
    public DateTime? LastRunEndedAt { get; set; }

    // ── 問題聚合欄的背景回填（P4）────────────────────────────────────────
    // 回填未完成時，問題排行的次數與影響範圍會偏低但看起來正常——
    // 這是唯一能發現「數字還不準」的地方

    public bool BackfillInProgress { get; set; }
    public int BackfillDone { get; set; }
    public int BackfillTotal { get; set; }

    // ── 處理狀態的 blob → 真表遷移（升級時才會發生一次）────────────────────
    // 未完成時處理狀態是**唯讀**的（寫入被 MigrationGateMiddleware 擋下），
    // 這是唯一能看出「為什麼標記不了」的地方

    /// <summary>unknown｜pending｜running｜completed</summary>
    public string MigrationState { get; set; } = string.Empty;

    /// <summary>未完成時處理狀態為唯讀</summary>
    public bool MigrationBlocksWrites { get; set; }

    /// <summary>三份中已完成的份數</summary>
    public int MigrationDoneParts { get; set; }

    /// <summary>最後一次搬移失敗的原因（成功為 null）</summary>
    public string? MigrationError { get; set; }

    /// <summary>因連續寄送失敗達門檻而暫停寄送的郵件收件人（回饋十七輪批次B-1）：
    /// 通常代表地址打錯，維運人員不用翻 log 就看得到。</summary>
    public List<string> SuspendedMailRecipients { get; set; } = new();
}

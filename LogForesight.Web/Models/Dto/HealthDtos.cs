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

/// <summary>單一慢操作的累計（最慢前幾支之一）</summary>
public class SlowOperationDto
{
    /// <summary>操作名稱（<c>分類:方法名</c>，例如 <c>prtg:GetValues</c>）</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>達到門檻的次數</summary>
    public long Count { get; set; }

    /// <summary>最大耗時（毫秒）</summary>
    public long MaxMs { get; set; }

    /// <summary>最近一次達到門檻的時間</summary>
    public DateTime LastAt { get; set; }
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

    /// <summary>
    /// 最慢的前幾支操作，依最大耗時由大到小（回饋四十五輪 B6）。
    /// 只有「最慢的那一筆」時，管理者知道「最慢 7 秒」卻不知道是哪幾支慢、各慢幾次；
    /// 這份清單才足以決定要去看哪一頁。沒有任何慢操作時是空清單（不是 null）。
    /// </summary>
    public List<SlowOperationDto> TopSlowOperations { get; set; } = new();

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

    // ── 權限異動的 log/blob → 真表遷移（升級時才會發生一次）───────────────────
    // 未完成時權限異動是唯讀的（寫入被 MigrationGateMiddleware 擋下）

    /// <summary>unknown｜pending｜running｜completed</summary>
    public string PermissionChangeMigrationState { get; set; } = string.Empty;

    /// <summary>未完成時權限異動為唯讀</summary>
    public bool PermissionChangeMigrationBlocksWrites { get; set; }

    /// <summary>已搬移的異動列數</summary>
    public int PermissionChangeMigratedRows { get; set; }

    /// <summary>最後一次搬移失敗的原因（成功為 null）</summary>
    public string? PermissionChangeMigrationError { get; set; }

    // ── 問題機房首見日的背景合併 ────────────────────────────────────────
    // 首次合併成功後會記錄浮水印，重啟時比對浮水印跳過；三次連續失敗時反映為 degraded

    /// <summary>not_started｜running｜completed｜skipped｜failed</summary>
    public string IssueFirstSeenSeedState { get; set; } = string.Empty;

    /// <summary>已失敗/重試次數（0～3）</summary>
    public int IssueFirstSeenSeedFailures { get; set; }

    /// <summary>最後一次合併失敗的原因（成功/未發生為 null）</summary>
    public string? IssueFirstSeenSeedError { get; set; }

    /// <summary>因連續寄送失敗達門檻而暫停寄送的郵件收件人（回饋十七輪批次B-1）：
    /// 通常代表地址打錯，維運人員不用翻 log 就看得到。</summary>
    public List<string> SuspendedMailRecipients { get; set; } = new();

    // ── 密碼欄位加密 ─────────────────────────────────────────────────────

    /// <summary>密文金鑰來源：env｜file｜embedded（見 CryptoHelper.KeySource）</summary>
    public string CryptoKeySource { get; set; } = string.Empty;

    /// <summary>啟動時金鑰指紋與資料庫記錄不符（還原 DB 沒一併還原金鑰檔等）</summary>
    public bool CryptoKeyMismatch { get; set; }

    /// <summary>本行程曾有密文解不開（該欄被當成未設定）</summary>
    public bool CryptoDecryptFailure { get; set; }

    /// <summary>排程資料新鮮度（任務 A-3）</summary>
    public ScheduleFreshnessDto ScheduleFreshness { get; set; } = new();
}

/// <summary>排程資料新鮮度（任務 A-3）</summary>
public class ScheduleFreshnessDto
{
    public bool ScheduleEnabled { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public bool Stale { get; set; }
    public DateTime? AckedUntil { get; set; }
    public bool Acked { get; set; }
    public List<string> AdminContacts { get; set; } = new();
}

/// <summary>確認資料過期提醒請求（任務 A-3）</summary>
public class FreshnessAckRequest
{
    public DateTime Until { get; set; }
}

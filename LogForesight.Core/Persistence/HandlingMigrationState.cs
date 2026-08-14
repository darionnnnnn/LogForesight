namespace LogForesight.Core.Persistence;

/// <summary>
/// 處理狀態自 blob 搬進真表的**遷移狀態**（docs/archive/SCALE-FIX-PLAN-2026-08-06.md §三）。
///
/// **為什麼需要把它寫下來**：原本的冪等判斷是「目標表非空即跳過」——那是**全表**判斷，
/// 不是「這一份搬完了沒」。首次啟動若在遷移中途被強制中止（Windows 服務啟動逾時被 SCM 砍掉），
/// 已 commit 的批次留在表裡，下次啟動看到「表非空」就**永遠跳過剩下的資料**，
/// 而且不會有任何錯誤——那是靜默的資料遺失。
///
/// 有了這份狀態，三件事都有明確答案：
///   1. 「搬完了沒」——不再從表空不空反推；
///   2. 「blob 還能不能寫」——<see cref="Completed"/> 之後舊 blob 即為唯讀備份，
///      這份紀錄就是它的失效標記（體檢 S-2）；
///   3. 「現在能不能寫處理狀態」——未完成時 Web 端擋下寫入（見 MigrationGateMiddleware）。
/// </summary>
public class HandlingMigrationState
{
    /// <summary>尚未評估過（全新安裝或升級後第一次啟動）</summary>
    public const string Unknown = "unknown";

    /// <summary>需要搬移，但還沒開始</summary>
    public const string Pending = "pending";

    /// <summary>搬移進行中——此時處理狀態的**寫入**應被擋下</summary>
    public const string Running = "running";

    /// <summary>三份都已搬完（或本來就沒有舊資料）</summary>
    public const string Completed = "completed";

    public string State { get; set; } = Unknown;

    /// <summary>逐份完成旗標：其中一份搬完就記一份，中斷後重啟只補剩下的</summary>
    public bool IssueHandlingDone { get; set; }
    public bool IssueCasesDone { get; set; }
    public bool RecordHandlingDone { get; set; }

    /// <summary>各份實際搬入的列數（供 log 與畫面顯示，也供事後核對）</summary>
    public int IssueHandlingRows { get; set; }
    public int IssueCasesRows { get; set; }
    public int RecordHandlingRows { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>最後一次失敗的訊息（成功後清空）——失敗要看得見，不能只留在 log 裡</summary>
    public string? LastError { get; set; }

    /// <summary>三份都完成才算完成</summary>
    public bool AllDone => IssueHandlingDone && IssueCasesDone && RecordHandlingDone;

    /// <summary>
    /// 處理狀態的寫入是否應被擋下：只要還沒到 <see cref="Completed"/> 就擋。
    /// <see cref="Unknown"/> 也擋——那代表啟動流程還沒判定完，寧可讓使用者等幾秒，
    /// 也不要讓寫入落進一個可能馬上要被遷移覆蓋的表。
    /// </summary>
    public bool ShouldBlockWrites => State != Completed;
}

/// <summary>遷移狀態的持久化（blob key＝<c>handling_migration</c>）</summary>
public class HandlingMigrationStateStore : JsonBlobSingleton<HandlingMigrationState>
{
    public HandlingMigrationStateStore(EfJsonBlobStore blob) : base(blob) { }
}

namespace LogForesight.Core.Persistence;

/// <summary>
/// 既有 <c>export\</c> 報告檔搬進 <c>lf_reports</c> 的遷移狀態（blob key＝<c>report_file_migration</c>）。
///
/// 與 <see cref="PermissionChangeMigrationState"/> 分開的理由同那一份：既有部署的其他遷移
/// 早已是 <c>Completed</c>，而各遷移器的 <c>Evaluate()</c> 對 <c>Completed</c> 直接短路，
/// 併進去就永遠不會執行。
///
/// **沒有寫入閘門**：報告只由夜間分析寫入，而遷移是逐筆比對自然鍵補缺、不整批跳過，
/// 遷移期間分析照常寫新報告也不會覆蓋或漏搬（見 <c>ReportFileMigrator</c>）。
/// </summary>
public class ReportMigrationState
{
    public const string Unknown = "unknown";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";

    public string State { get; set; } = Unknown;

    /// <summary>實際搬入的份數（供 log 顯示）</summary>
    public int MigratedRows { get; set; }

    /// <summary>來源檔已不存在而略過的份數——誠實申報，不是「全部都搬成功了」</summary>
    public int MissingFiles { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>最後一次失敗的訊息（成功後清空）</summary>
    public string? LastError { get; set; }
}

/// <summary>遷移狀態的持久化（blob key＝<c>report_file_migration</c>）</summary>
public class ReportMigrationStateStore : JsonBlobSingleton<ReportMigrationState>
{
    public ReportMigrationStateStore(EfJsonBlobStore blob) : base(blob) { }
}

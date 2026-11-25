using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>整體狀態的一個原因：<see cref="Key"/> 對應健康頁的卡片，<see cref="Text"/> 是一句中文。</summary>
public sealed record HealthReason(string Key, string Text);

/// <summary>
/// 整體狀態的原因種類。健康頁每張可能出現警示樣式的卡片都在 Settings.cshtml 以 <c>data-health-alert</c> 宣告它對應哪些種類，
/// <c>HealthAttentionGuardTests</c> 比對兩邊：新增一張會亮警示的卡卻沒併進這份清單，守門測試會轉紅。
/// </summary>
public static class HealthAttentionKeys
{
    public const string Storage = "storage";
    public const string SlowQueries = "slowQueries";
    public const string FirstSeen = "firstSeen";
    public const string Freshness = "freshness";
    public const string PrtgSnapshot = "prtgSnapshot";
    public const string Memory = "memory";
    public const string Crypto = "crypto";
    public const string Migration = "migration";
    public const string PrtgFreshness = "prtgFreshness";
    public const string HostDayRecovery = "hostDayRecovery";
    public const string LastRun = "lastRun";
    public const string MailSuspended = "mailSuspended";
    public const string Retention = "retention";
    public const string PrtgSensorsEmpty = "prtgSensorsEmpty";
    public const string PrtgDevicesPurged = "prtgDevicesPurged";
    public const string CertificateTrust = "certificateTrust";
    public const string OutboxDropped = "outboxDropped";
    public const string AiUnreachable = "aiUnreachable";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Storage, SlowQueries, FirstSeen, Freshness, PrtgSnapshot, Memory, Crypto,
        Migration, PrtgFreshness, HostDayRecovery, LastRun, MailSuspended, Retention, PrtgSensorsEmpty, PrtgDevicesPurged,
        CertificateTrust, OutboxDropped, AiUnreachable
    };
}

/// <summary>遷移（處理狀態、權限異動）的狀態摘要；只取判定卡死需要的欄位。</summary>
public sealed record MigrationHealth(string Name, string State, DateTime? StartedAt, string? LastError);

/// <summary>整體狀態判定的輸入：<see cref="HealthService.GetDetail"/> 已經讀到的事實，判定本身不再讀任何資料。</summary>
public sealed class HealthAttentionInput
{
    public bool StorageDown { get; init; }
    public bool SlowOperationsHigh { get; init; }
    public bool FirstSeenFailed { get; init; }
    public bool ScheduleStaleAndNotAcked { get; init; }
    public bool PrtgSnapshotWarning { get; init; }
    public string? PrtgSnapshotMessage { get; init; }
    public bool MemoryWarning { get; init; }
    public bool CryptoKeyMismatch { get; init; }
    public bool CryptoDecryptFailure { get; init; }

    /// <summary>站台模式下沒有可用的站台金鑰：密碼類設定存不進去（加密被拒）</summary>
    public bool CryptoKeyUnavailable { get; init; }
    public IReadOnlyList<MigrationHealth> Migrations { get; init; } = Array.Empty<MigrationHealth>();

    /// <summary>PRTG 擷取新鮮度可疑的類別（中文名與連續 0 筆次數）；PRTG 未啟用時為空</summary>
    public IReadOnlyList<(string Label, int ZeroStreak)> PrtgFreshnessSuspicious { get; init; } = Array.Empty<(string, int)>();

    /// <summary>主機日恢復中需要人看的筆數（內容無效、主機編號無效、超過單列上限）</summary>
    public int HostDayRecoveryNeedsAttention { get; init; }

    /// <summary>最近一趟排程／手動分析的結果；null＝站台啟動後還沒跑過</summary>
    public bool? LastRunSucceeded { get; init; }
    public IReadOnlyList<string> SuspendedMailRecipients { get; init; } = Array.Empty<string>();

    /// <summary>已啟用的郵件摘要中，時刻或星期無法解析的項目名稱（例如「每日摘要時刻」）；這種設定的效果是摘要永不寄出</summary>
    public IReadOnlyList<string> InvalidMailSchedules { get; init; } = Array.Empty<string>();
    public RetentionFailure? RetentionFailure { get; init; }

    /// <summary>開了「忽略憑證錯誤」卻沒填信任指紋的連線（例如「PRTG」「Sentinel」）：任何憑證都會被接受</summary>
    public IReadOnlyList<string> UnpinnedCertificateBypass { get; init; } = Array.Empty<string>();

    /// <summary>交辦 outbox 近期丟棄的筆數與最近一次丟棄時間（本地時間）；從未丟棄為 0／null。
    /// 是否「近期」由 <see cref="HealthAttention.Evaluate"/> 以 <see cref="HealthAttention.OutboxDropRecentWindow"/> 判定。</summary>
    public int OutboxRecentDropped { get; init; }
    public DateTime? OutboxLastDroppedAt { get; init; }

    /// <summary>
    /// AI 服務已設定、上次探測卻連不上或回應格式不相容（<see cref="AiProbeStatus.Failed"/>）。
    /// 沒設定（統計模式是合法運作方式）與還沒探測過都不算——那不是故障。
    /// </summary>
    public bool AiUnreachable { get; init; }

    /// <summary>最近一次 PRTG 同步結果的警告；PRTG 未啟用時為空</summary>
    public IReadOnlyList<PrtgSyncWarning> PrtgSyncWarnings { get; init; } = Array.Empty<PrtgSyncWarning>();
}

/// <summary>
/// 健康整體狀態的唯一判定：回傳原因清單，沒有原因＝正常。健康頁頂端與導覽告示列兩個消費端都讀這份結果，
/// 不另算（<see cref="HealthService.GetDetail"/> 的 <c>Status</c> 與 <c>StatusReasons</c> 都來自這裡）。
/// 每個原因一句中文；外部字串（PRTG 警告文字、收件人信箱）已在來源端截斷，這裡再各自限長。
/// </summary>
public static class HealthAttention
{
    /// <summary>遷移從開始起超過這段時間仍未完成（或有失敗訊息）就視為卡死</summary>
    internal static readonly TimeSpan MigrationStuckAfter = TimeSpan.FromMinutes(30);

    /// <summary>交辦 outbox 丟棄後，這段時間內仍列入整體狀態（與 <c>WorkOrderOutboxDropStore.RecentWindow</c> 同值）</summary>
    internal static readonly TimeSpan OutboxDropRecentWindow = LogForesight.Core.Persistence.WorkOrderOutboxDropStore.RecentWindow;

    /// <summary>忽略憑證錯誤的設定位置：PRTG 在 PRTG 維護的擷取參數，Sentinel 在 NetIQ 維護的連線與節流參數</summary>
    private static string CertificateSettingLocation(string source) => source switch
    {
        "PRTG" => "PRTG 維護的擷取參數",
        "Sentinel" => "NetIQ 維護的連線與節流參數",
        _ => $"{source} 的連線設定"
    };

    public static IReadOnlyList<HealthReason> Evaluate(HealthAttentionInput input, DateTime now)
    {
        var reasons = new List<HealthReason>();
        void Add(string key, string text) => reasons.Add(new HealthReason(key, text));

        if (input.StorageDown)
            Add(HealthAttentionKeys.Storage, "資料庫無法連線，請檢查儲存設定與資料庫服務。");
        if (input.SlowOperationsHigh)
            Add(HealthAttentionKeys.SlowQueries, "資料庫慢操作占比偏高，頁面可能變慢。");
        if (input.FirstSeenFailed)
            Add(HealthAttentionKeys.FirstSeen, "問題首見日的背景合併連續失敗，已達重試上限。");
        if (input.ScheduleStaleAndNotAcked)
            Add(HealthAttentionKeys.Freshness, "排程資料已超過 48 小時沒有成功更新。");
        if (input.PrtgSnapshotWarning)
        {
            var detail = PrtgSyncWarning.Truncate(input.PrtgSnapshotMessage, 120);
            Add(HealthAttentionKeys.PrtgSnapshot, Sentence(detail.Length > 0 ? $"PRTG 即時快照異常：{detail}" : "PRTG 即時快照異常"));
        }
        if (input.MemoryWarning)
            Add(HealthAttentionKeys.Memory, ".NET 記憶體已接近上限，排程可能因記憶體不足中止。");
        if (input.CryptoKeyUnavailable)
            Add(HealthAttentionKeys.Crypto, "站台金鑰檔無法使用，密碼類設定暫時存不進去；請確認站台帳號對資料目錄的 keys 資料夾有讀寫權限。");
        else if (input.CryptoKeyMismatch)
            Add(HealthAttentionKeys.Crypto, "密碼欄位的加密金鑰與資料庫記錄不符，已儲存的密碼可能解不開。");
        else if (input.CryptoDecryptFailure)
            Add(HealthAttentionKeys.Crypto, "有密碼欄位解不開，該欄位會被當成未設定。");

        foreach (var m in input.Migrations)
        {
            if (IsMigrationStuck(m, now))
            {
                var error = PrtgSyncWarning.Truncate(m.LastError, 120);
                Add(HealthAttentionKeys.Migration, error.Length > 0
                    ? $"{m.Name}遷移卡住，相關寫入被擋下：{error}"
                    : $"{m.Name}遷移超過 30 分鐘仍未完成，相關寫入被擋下。");
            }
        }

        if (input.PrtgFreshnessSuspicious.Count > 0)
        {
            var parts = string.Join("、", input.PrtgFreshnessSuspicious.Select(s => $"{s.Label}連續 {s.ZeroStreak} 次取得 0 筆"));
            Add(HealthAttentionKeys.PrtgFreshness, $"PRTG 擷取可疑：{parts}。");
        }
        if (input.HostDayRecoveryNeedsAttention > 0)
            Add(HealthAttentionKeys.HostDayRecovery, $"主機日恢復有 {input.HostDayRecoveryNeedsAttention} 筆需要檢查（內容無效或超過單列上限）。");
        if (input.LastRunSucceeded == false)
            Add(HealthAttentionKeys.LastRun, "最近一趟分析執行失敗。");
        if (input.SuspendedMailRecipients.Count > 0)
        {
            var shown = string.Join("、", input.SuspendedMailRecipients.Take(3).Select(r => PrtgSyncWarning.Truncate(r, 60)));
            var more = input.SuspendedMailRecipients.Count > 3 ? $" 等 {input.SuspendedMailRecipients.Count} 位" : "";
            Add(HealthAttentionKeys.MailSuspended, $"郵件收件人因連續寄送失敗被暫停：{shown}{more}。");
        }
        if (input.UnpinnedCertificateBypass.Count > 0)
            Add(HealthAttentionKeys.CertificateTrust,
                $"{string.Join("、", input.UnpinnedCertificateBypass)}開了「忽略憑證錯誤」且沒有填信任的憑證指紋，任何憑證都會被接受；" +
                $"請到{string.Join("、", input.UnpinnedCertificateBypass.Select(CertificateSettingLocation))}填入伺服器的憑證指紋。");
        if (input.InvalidMailSchedules.Count > 0)
            Add(HealthAttentionKeys.MailSuspended, $"郵件設定的{string.Join("、", input.InvalidMailSchedules)}格式不合法，這些摘要不會寄出；請到郵件設定修正為 HH:mm。");
        if (input.RetentionFailure is { } retention)
            Add(HealthAttentionKeys.Retention, $"保留清除有 {retention.Segments.Count} 段失敗（{string.Join("、", retention.Segments)}），其餘段已照常執行。");

        if (input.OutboxRecentDropped > 0 && input.OutboxLastDroppedAt is { } droppedAt && now - droppedAt <= OutboxDropRecentWindow)
            Add(HealthAttentionKeys.OutboxDropped,
                $"交辦通知佇列已滿，近期丟棄了 {input.OutboxRecentDropped} 筆最舊的待寄通知（最近一次 {droppedAt:MM/dd HH:mm}）；請檢查郵件是否停擺。");

        if (input.AiUnreachable)
            Add(HealthAttentionKeys.AiUnreachable, "AI 服務連線失敗，白話摘要與互動問答暫時無法使用（分析仍以統計模式照常執行）；請到「設定 > AI 服務」確認位址後按「測試連線」。");

        foreach (var warning in input.PrtgSyncWarnings)
        {
            var key = warning.Code switch
            {
                PrtgSyncWarning.SensorsAllEmpty => HealthAttentionKeys.PrtgSensorsEmpty,
                PrtgSyncWarning.MappedDevicesPurged => HealthAttentionKeys.PrtgDevicesPurged,
                _ => null
            };
            if (key != null) Add(key, Sentence(PrtgSyncWarning.Truncate(warning.Message, PrtgSyncWarning.MaxMessageLength)));
        }

        return reasons;
    }

    /// <summary>每個原因是一句話：結尾不是句號類標點時補「。」（外部來源的文字與快照訊息不保證有標點）。</summary>
    private static string Sentence(string text) =>
        text.Length == 0 || "。！？…".Contains(text[^1]) ? text : text + "。";

    /// <summary>卡死＝還沒完成，且（有失敗訊息，或開始後超過 <see cref="MigrationStuckAfter"/>）。剛啟動尚未評估的狀態不算。</summary>
    internal static bool IsMigrationStuck(MigrationHealth m, DateTime now)
    {
        if (string.Equals(m.State, HandlingMigrationState.Completed, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(m.LastError)) return true;
        return m.StartedAt is { } started && now - started > MigrationStuckAfter;
    }
}

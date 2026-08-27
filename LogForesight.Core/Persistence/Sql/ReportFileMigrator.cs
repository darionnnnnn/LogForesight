using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 把既有 <c>export\</c> 目錄下的報告 txt 搬進 <c>lf_reports</c>（一次性，背景執行）。
///
/// **為什麼由紀錄驅動而不是掃目錄**：既有部署的三個寫入端都沒有帶主機識別，
/// 所有檔案都落在 export 根目錄，光看檔案無從得知它屬於哪一台主機——掃目錄只能全部歸給本機，
/// 等於把所有 NetIQ 主機的報告連結丟掉。分析紀錄的 <c>ReportFile</c> 存的是那筆紀錄實際指向的
/// 檔案路徑，照著它搬才能把每份報告歸回正確的主機日。
///
/// **既有的檔名碰撞不會被「修好」**：多台主機同日同風險同類別的報告在檔案系統上互相覆蓋過，
/// 幾筆紀錄指向同一個檔就是同一份內容——遷移忠實保留使用者今天看到的東西，不多不少。
/// 升級後新產生的報告不再碰撞（`lf_reports` 以主機日為鍵）。
///
/// 權限異動報告是唯一的例外：它的路徑從未被存下來，只能掃目錄，而它本來就只有本機監控會產生，
/// 歸給本機是正確的。
///
/// 四條約束同 <see cref="PermissionChangeMigrator"/>：
/// 1. 可安全重來——逐筆比對自然鍵補缺，**不是「表裡有資料就整批跳過」**。
///    夜間分析也會寫這張表，整批跳過會讓舊資料被誤判成已搬而永久消失。
/// 2. 完成與否是被寫下的事實（<see cref="ReportMigrationState"/>），不從目標表反推。
/// 3. 舊檔不刪，保留為備份。
/// 4. 來源檔已不存在（早被保留期清掉）不算失敗，計入 <see cref="ReportMigrationState.MissingFiles"/>。
/// </summary>
public sealed class ReportFileMigrator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const string StateBlobKey = "report_file_migration";

    /// <summary>資料根目錄底下的報告目錄名（升級前的輸出位置）</summary>
    public const string ExportDirectoryName = "export";

    private readonly Func<LfDbContext> _contextFactory;
    private readonly ReportMigrationStateStore _stateStore;
    private readonly string _exportDir;
    private readonly string _localHostName;

    public ReportFileMigrator(Func<LfDbContext> contextFactory, ReportMigrationStateStore stateStore,
        string dataRoot, string? localHostName = null)
    {
        _contextFactory = contextFactory;
        _stateStore = stateStore;
        _exportDir = Path.Combine(dataRoot, ExportDirectoryName);
        _localHostName = string.IsNullOrWhiteSpace(localHostName) ? Environment.MachineName : localHostName;
    }

    public ReportMigrationState State => _stateStore.Get();

    /// <summary>
    /// 啟動時的輕量判定（毫秒級，可留在啟動路徑）：export 目錄不存在就直接標完成——
    /// 全新安裝、或以 SqlServer 部署而資料根目錄下本來就沒有報告檔的情況。
    /// </summary>
    public void Evaluate()
    {
        var state = _stateStore.Get();
        if (state.State == ReportMigrationState.Completed) return;

        var needed = Directory.Exists(_exportDir) &&
                     Directory.EnumerateFiles(_exportDir, "*.txt", SearchOption.AllDirectories).Any();

        _stateStore.Update(s =>
        {
            if (!needed)
            {
                s.State = ReportMigrationState.Completed;
                s.CompletedAt = DateTime.Now;
                return;
            }

            if (s.State is ReportMigrationState.Unknown or ReportMigrationState.Running)
            {
                // 上次卡在 running（行程被砍）→ 退回 pending 重來；重入靠自然鍵比對，重跑安全
                s.State = ReportMigrationState.Pending;
            }
        });

        if (needed) Log.Info("[SQL] 偵測到 export 目錄下的既有報告檔，將於背景搬移至 lf_reports");
    }

    /// <summary>實際搬移（背景執行）。舊檔保留不刪。</summary>
    public void Run(CancellationToken cancellationToken)
    {
        if (_stateStore.Get().State == ReportMigrationState.Completed) return;

        _stateStore.Update(s =>
        {
            s.State = ReportMigrationState.Running;
            s.StartedAt ??= DateTime.Now;
            s.LastError = null;
        });

        int migrated, missing;
        try
        {
            (migrated, missing) = MigrateAll(cancellationToken);
        }
        catch (Exception ex)
        {
            _stateStore.Update(s =>
            {
                s.State = ReportMigrationState.Pending;
                s.LastError = ex.Message;
            });
            throw;
        }

        if (cancellationToken.IsCancellationRequested) return;

        _stateStore.Update(s =>
        {
            s.State = ReportMigrationState.Completed;
            s.MigratedRows = migrated;
            s.MissingFiles = missing;
            s.CompletedAt = DateTime.Now;
            s.LastError = null;
        });

        Log.Info("[SQL] 報告遷移完成：搬入 {Count} 份，來源檔已不存在而略過 {Missing} 份（export\\ 舊檔保留未刪，僅作備份）",
            migrated, missing);
    }

    private (int Migrated, int Missing) MigrateAll(CancellationToken ct)
    {
        var store = new EfReportStore(_contextFactory);
        var migrated = 0;
        var missing = 0;

        foreach (var item in EnumerateCandidates(ct))
        {
            if (ct.IsCancellationRequested) break;

            var content = TryReadFile(item.Path);
            if (content == null)
            {
                missing++;
                continue;
            }

            // 逐筆比對自然鍵：已存在就不動（可能是夜間分析在遷移期間寫進來的新報告，
            // 那份比舊檔新，不該被覆蓋）
            if (Exists(item.Host, item.Date, item.Kind)) continue;

            store.Write(item.Kind, item.Host, item.FileName, content, item.Meta, item.CreatedAt);
            migrated++;
        }

        return (migrated, missing);
    }

    private bool Exists(HostKey host, DateTime date, string kind)
    {
        using var ctx = _contextFactory();
        return ctx.Reports.AsNoTracking().Any(r =>
            r.HostId == host.HostId && r.HostName == host.HostName && r.ReportDate == date && r.Kind == kind);
    }

    /// <summary>
    /// 待搬清單：紀錄驅動的風險／週檢報告 ＋ 目錄驅動的權限異動報告。
    /// </summary>
    private IEnumerable<MigrationItem> EnumerateCandidates(CancellationToken ct)
    {
        foreach (var item in FromRecords(ct)) yield return item;
        foreach (var item in FromPermissionFiles()) yield return item;
    }

    /// <summary>
    /// 分析紀錄裡的報告參照。**只掃有可能帶報告的列**：風險報告只在風險「中」以上的日子產生，
    /// 週檢報告只在有體檢結論的日子產生——把整張 lf_daily_records 的 JSON 全部解一遍，
    /// 在 3000 台環境下是幾十萬列的無謂反序列化。
    /// </summary>
    private IEnumerable<MigrationItem> FromRecords(CancellationToken ct)
    {
        List<DailyRecordRow> rows;
        using (var ctx = _contextFactory())
        {
            rows = ctx.DailyRecords.AsNoTracking()
                .Where(r => r.RiskLevel != RiskLevels.Low || r.WeeklyCheckupDate != null)
                .OrderBy(r => r.RecordId)
                .ToList();
        }

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) yield break;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(row.ContentJson);
            }
            catch (JsonException ex)
            {
                // 紀錄本身壞掉不是報告遷移該處理的問題，略過並記錄——為了搬一份報告
                // 讓整個遷移停擺、進而讓其餘報告全部搬不進去，代價不成比例
                Log.Warn("[SQL] 報告遷移：record_id={Id} 的內容無法解析，略過（{Msg}）", row.RecordId, ex.Message);
                continue;
            }

            using (doc)
            {
                var host = new HostKey { HostId = row.HostId, HostName = row.HostName };

                var riskRef = ReadString(doc.RootElement, "ReportFile");
                if (IsLegacyPath(riskRef))
                {
                    yield return new MigrationItem(riskRef!, host, row.RecordDate.Date, ReportKinds.DailyRisk,
                        Path.GetFileName(riskRef!), new ReportMeta(row.RiskLevel, null), row.CreatedAt);
                }

                if (doc.RootElement.TryGetProperty("WeeklyCheckup", out var checkup) &&
                    checkup.ValueKind == JsonValueKind.Object)
                {
                    var checkupRef = ReadString(checkup, "ReportFile");
                    if (IsLegacyPath(checkupRef))
                    {
                        var checkupDate = ReadDate(checkup, "CheckupDate") ?? row.RecordDate.Date;
                        yield return new MigrationItem(checkupRef!, host, checkupDate, ReportKinds.WeeklyCheckup,
                            Path.GetFileName(checkupRef!), null, row.CreatedAt);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 權限異動報告：路徑從未被存進任何紀錄，只能掃目錄。它只由本機監控產生，歸給本機是正確的。
    /// 檔名固定為 <c>{yyyy-MM-dd}_權限異動.txt</c>，據此辨識與取日期。
    /// </summary>
    private IEnumerable<MigrationItem> FromPermissionFiles()
    {
        if (!Directory.Exists(_exportDir)) yield break;

        var host = new HostKey { HostName = _localHostName };
        foreach (var path in Directory.EnumerateFiles(_exportDir, "*_權限異動.txt", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(path);
            var date = EfReportStore.ParseReportDate(fileName);
            if (date == null) continue;

            yield return new MigrationItem(path, host, date.Value, ReportKinds.Permission, fileName, null,
                SafeLastWriteTime(path) ?? date.Value);
        }
    }

    /// <summary>
    /// 參照是不是「還沒搬過的舊檔案路徑」。搬完後的參照是純數字（report_id），
    /// 空值代表這天沒有報告——兩者都不是待搬對象。
    /// </summary>
    private static bool IsLegacyPath(string? reportRef) =>
        !string.IsNullOrWhiteSpace(reportRef) && !long.TryParse(reportRef, out _);

    /// <summary>
    /// 讀來源檔。不存在（早被保留期清掉）或讀不到都回 null 由呼叫端計入「略過」——
    /// 舊檔的存在與否不是這次升級能控制的事，不該讓整個遷移失敗。
    /// </summary>
    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warn("[SQL] 報告遷移：讀取「{Path}」失敗，略過（{Msg}）", path, ex.Message);
            return null;
        }
    }

    private static DateTime? SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTime(out var date)
            ? date.Date
            : null;

    private record MigrationItem(string Path, HostKey Host, DateTime Date, string Kind, string FileName,
        ReportMeta? Meta, DateTime CreatedAt);
}

using System.Collections.Concurrent;
using System.Text;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;

namespace LogForesight.Web.Services.Import;

/// <summary>
/// CSV 匯入的流程協調（docs/WEB-SPEC.md §9.9）：上傳 → 預覽（不寫入）→ 套用。
/// 各種類的實際邏輯在對應的 <see cref="ICsvImporter"/>，這裡只負責流程與共用驗證。
/// </summary>
public interface IImportService
{
    /// <summary>範本 CSV（UTF-8 BOM，Excel 開啟不亂碼）</summary>
    byte[] GetTemplate(ImportKind kind);

    /// <summary>解析並驗證，回傳預覽計畫。**不寫入任何資料**</summary>
    ImportPlan Preview(ImportKind kind, Stream content, string fileName);

    /// <summary>套用先前預覽的計畫（以 token 綁定，避免預覽 A 檔卻套用 B 檔）</summary>
    ImportResult Apply(ImportKind kind, string token);
}

public class ImportService : IImportService
{
    private readonly IEnumerable<ICsvImporter> _importers;
    private readonly IAuditService _audit;
    private readonly IImportLogStore _logs;
    private readonly Auth.ICurrentUser _currentUser;
    private readonly ImportSettings _settings;

    /// <summary>
    /// 預覽計畫的暫存（token → 計畫＋原始資料）。
    /// 記憶體暫存即可：站台重啟後重新上傳一次的成本很低，
    /// 為它建持久化反而要處理清理與跨機同步的問題。
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingImport> Pending = new();

    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(30);

    public ImportService(
        IEnumerable<ICsvImporter> importers,
        IAuditService audit,
        IImportLogStore logs,
        Auth.ICurrentUser currentUser,
        WebAppSettings settings)
    {
        _importers = importers;
        _audit = audit;
        _logs = logs;
        _currentUser = currentUser;
        _settings = settings.Import;
    }

    public byte[] GetTemplate(ImportKind kind)
    {
        var csv = Resolve(kind).BuildTemplate();

        // BOM 是刻意加的：Excel 開啟不帶 BOM 的 UTF-8 CSV 會用系統 ANSI 解讀，
        // 中文全部變亂碼——使用者拿到範本的第一印象就是「這東西壞了」
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
    }

    public ImportPlan Preview(ImportKind kind, Stream content, string fileName)
    {
        var importer = Resolve(kind);

        CsvTable table;
        try
        {
            table = CsvParser.Parse(content, _settings.MaxRows);
        }
        catch (CsvParseException ex)
        {
            throw DomainException.Validation($"CSV 解析失敗：{ex.Message}");
        }

        ValidateHeaders(importer, table);

        var plan = importer.BuildPlan(table, fileName);

        CleanupExpired();
        Pending[plan.Token] = new PendingImport(plan, table, DateTime.Now);

        return plan;
    }

    public ImportResult Apply(ImportKind kind, string token)
    {
        CleanupExpired();

        if (!Pending.TryGetValue(token, out var pending))
            throw DomainException.Validation("預覽結果已逾期或不存在，請重新上傳檔案。");

        if (pending.Plan.Kind != kind)
            throw DomainException.Validation("預覽與套用的匯入類型不一致，請重新上傳檔案。");

        if (!pending.Plan.CanApply)
            throw DomainException.Validation($"檔案中有 {pending.Plan.ErrorCount} 列錯誤，請修正後重新上傳。");

        var importer = Resolve(kind);
        var result = importer.Apply(pending.Plan, pending.Table);

        Pending.TryRemove(token, out _);

        _logs.Append(new ImportLogEntry
        {
            UserId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            Account = _currentUser.Account,
            Kind = kind.ToString(),
            FileName = pending.Plan.FileName,
            AddedCount = result.Added,
            UpdatedCount = result.Updated,
            RemovedCount = result.Removed,
            CreatedGroups = result.CreatedGroups,
            CreatedAt = DateTime.Now
        });

        _audit.Record(
            action: AuditActions.ImportApply,
            summary: $"匯入 {KindName(kind)}（{pending.Plan.FileName}）：新增 {result.Added}、更新 {result.Updated}" +
                     (result.Removed > 0 ? $"、移除 {result.Removed}" : "") +
                     (result.CreatedGroups.Count > 0 ? $"，新建群組 {string.Join("、", result.CreatedGroups)}" : ""),
            targetKind: "import",
            targetId: kind.ToString(),
            detail: new { result.Added, result.Updated, result.Removed, result.CreatedGroups, pending.Plan.FileName });

        return result;
    }

    /// <summary>
    /// 標題列檢查。缺必填欄位擋下；出現不認得的欄位也提醒——
    /// 那幾乎都是拼錯字（如 accunt），而拼錯的後果是「那一欄被靜默忽略」，
    /// 使用者會以為資料匯進去了。
    /// </summary>
    private static void ValidateHeaders(ICsvImporter importer, CsvTable table)
    {
        var missing = importer.RequiredHeaders
            .Where(h => !table.Headers.Contains(h, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0)
            throw DomainException.Validation($"缺少必要欄位：{string.Join("、", missing)}。請下載範本確認格式。");

        var unknown = table.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Where(h => !importer.KnownHeaders.Contains(h, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknown.Count > 0)
            throw DomainException.Validation(
                $"有無法辨識的欄位：{string.Join("、", unknown)}（可用欄位：{string.Join("、", importer.KnownHeaders)}）。");
    }

    private ICsvImporter Resolve(ImportKind kind) =>
        _importers.FirstOrDefault(i => i.Kind == kind)
        ?? throw DomainException.Validation($"不支援的匯入類型：{kind}。");

    private static string KindName(ImportKind kind) => kind switch
    {
        ImportKind.Users => "使用者",
        ImportKind.Hosts => "主機",
        ImportKind.GroupAccess => "群組授權",
        _ => kind.ToString()
    };

    private static void CleanupExpired()
    {
        var cutoff = DateTime.Now - PlanLifetime;
        foreach (var entry in Pending.Where(p => p.Value.CreatedAt < cutoff).ToList())
            Pending.TryRemove(entry.Key, out _);
    }

    private record PendingImport(ImportPlan Plan, CsvTable Table, DateTime CreatedAt);
}

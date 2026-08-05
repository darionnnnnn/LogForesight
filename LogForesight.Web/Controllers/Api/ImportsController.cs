using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services.Import;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>CSV 匯入 API（docs/WEB-SPEC.md §9.9）</summary>
[ApiController]
[Route("api/imports")]
[Permission(Capability.Maintain)]
public class ImportsController : ControllerBase
{
    private readonly ImportService _imports;
    private readonly IImportLogStore _logs;
    private readonly ISystemSettingsStore _systemSettings;
    private readonly IUserStore _users;

    public ImportsController(ImportService imports, IImportLogStore logs, ISystemSettingsStore systemSettings, IUserStore users)
    {
        _imports = imports;
        _logs = logs;
        _systemSettings = systemSettings;
        _users = users;
    }

    /// <summary>下載範本（含範例列）</summary>
    [HttpGet("{kind}/template")]
    public IActionResult Template(ImportKind kind)
    {
        var content = _imports.GetTemplate(kind);
        var fileName = kind switch
        {
            ImportKind.Users => "users.csv",
            ImportKind.Hosts => "hosts.csv",
            ImportKind.GroupAccess => "group_access.csv",
            ImportKind.Owners => "owners.csv",
            _ => "template.csv"
        };

        return File(content, "text/csv", fileName);
    }

    /// <summary>上傳並預覽（不寫入任何資料）</summary>
    [HttpPost("{kind}/preview")]
    public ApiResponse<ImportPlan> Preview(ImportKind kind, IFormFile? file)
    {
        if (file == null || file.Length == 0)
            throw DomainException.Validation("請選擇要上傳的 CSV 檔案。");

        // §12：上限的事實來源改為 DB（「系統管理 > 設定」頁），每次上傳即時讀取——
        // 管理者調整後不必重啟站台
        var maxFileSizeKb = _systemSettings.Get().ImportMaxFileSizeKb;
        if (file.Length > maxFileSizeKb * 1024L)
            throw DomainException.Validation($"檔案大小超過上限 {maxFileSizeKb} KB。");

        using var stream = file.OpenReadStream();
        return ApiResponse<ImportPlan>.Ok(_imports.Preview(kind, stream, file.FileName));
    }

    /// <summary>套用先前預覽的計畫</summary>
    [HttpPost("{kind}/apply")]
    public ApiResponse<ImportResult> Apply(ImportKind kind, [FromBody] ApplyImportRequest request) =>
        ApiResponse<ImportResult>.Ok(_imports.Apply(kind, request.Token));

    [HttpGet("logs")]
    public ApiResponse<List<ImportLogDto>> Logs()
    {
        // 操作者顯示名稱即時解析（§9：前端以 formatUserName 組「顯示名稱(帳號)」）——
        // Account 是當時的快照，DisplayName 取現值（使用者改名後也對得上）
        var entries = _logs.GetRecent(50).Select(e => new ImportLogDto
        {
            ImportId = e.ImportId,
            Account = e.Account,
            DisplayName = string.IsNullOrEmpty(e.Account) ? null : _users.FindByAccount(e.Account)?.DisplayName,
            Kind = e.Kind,
            FileName = e.FileName,
            AddedCount = e.AddedCount,
            UpdatedCount = e.UpdatedCount,
            RemovedCount = e.RemovedCount,
            RevivedCount = e.RevivedCount,
            CreatedGroups = e.CreatedGroups,
            CreatedAt = e.CreatedAt
        }).ToList();
        return ApiResponse<List<ImportLogDto>>.Ok(entries);
    }
}

/// <summary>匯入紀錄的顯示投影（§9）：ImportLogEntry ＋ 即時解析的操作者顯示名稱</summary>
public class ImportLogDto
{
    public long ImportId { get; set; }
    public string Account { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RemovedCount { get; set; }
    public int RevivedCount { get; set; }
    public List<string> CreatedGroups { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class ApplyImportRequest
{
    public string Token { get; set; } = string.Empty;
}

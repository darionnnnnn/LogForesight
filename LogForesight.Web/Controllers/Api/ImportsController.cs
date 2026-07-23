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
    private readonly IImportService _imports;
    private readonly IImportLogStore _logs;
    private readonly WebAppSettings _settings;

    public ImportsController(IImportService imports, IImportLogStore logs, WebAppSettings settings)
    {
        _imports = imports;
        _logs = logs;
        _settings = settings;
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

        var maxBytes = _settings.Import.MaxFileSizeKb * 1024L;
        if (file.Length > maxBytes)
            throw DomainException.Validation($"檔案大小超過上限 {_settings.Import.MaxFileSizeKb} KB。");

        using var stream = file.OpenReadStream();
        return ApiResponse<ImportPlan>.Ok(_imports.Preview(kind, stream, file.FileName));
    }

    /// <summary>套用先前預覽的計畫</summary>
    [HttpPost("{kind}/apply")]
    public ApiResponse<ImportResult> Apply(ImportKind kind, [FromBody] ApplyImportRequest request) =>
        ApiResponse<ImportResult>.Ok(_imports.Apply(kind, request.Token));

    [HttpGet("logs")]
    public ApiResponse<List<ImportLogEntry>> Logs() =>
        ApiResponse<List<ImportLogEntry>>.Ok(_logs.GetRecent(50));
}

public class ApplyImportRequest
{
    public string Token { get; set; } = string.Empty;
}

using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 沿用此問題上次的說明（回饋第 50 輪 C-4）：同一問題簽章最新一筆非空白說明。
/// 只在目前使用者可見的主機內找——持有 ViewAll 者不限；其他人以 <see cref="IVisibilityService"/>
/// 的可見主機為範圍，看不到的主機上的說明絕不回（說明本身可能帶主機內部資訊）。
/// </summary>
[ApiController]
[Route("api/handling")]
public class IssueNoteController : ControllerBase
{
    private readonly IIssueNoteQuery _notes;
    private readonly IVisibilityService _visibility;

    public IssueNoteController(IIssueNoteQuery notes, IVisibilityService visibility)
    {
        _notes = notes;
        _visibility = visibility;
    }

    [HttpGet("last-note")]
    [Permission(Capability.Handle)]
    public ApiResponse<LastIssueNoteDto?> GetLastNote([FromQuery] string? issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            throw DomainException.Validation("缺少問題簽章。");

        // 一律以可見主機篩：全站檢視者也看不到停用主機（WEB-SPEC §7.1），不能以 null（不篩）代替
        IReadOnlyCollection<string> hostKeys = _visibility.GetVisibleHosts().Select(h => HostNameKey.Of(h.HostName)).ToList();

        var found = _notes.GetLatestNote(issueKey, hostKeys);
        return ApiResponse<LastIssueNoteDto?>.Ok(found is { } f
            ? new LastIssueNoteDto { Note = f.Note, HostName = f.HostName, Date = f.RecordDate.ToString("yyyy-MM-dd") }
            : null);
    }
}

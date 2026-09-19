using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 目前登入者的個人偏好（回饋第 50 輪 C-4）。只要登入即可（全站 FallbackPolicy），不另掛能力。
/// </summary>
[ApiController]
[Route("api/me")]
public class MeController : ControllerBase
{
    private readonly UserPreferenceStore _prefs;
    private readonly ISystemSettingsStore _settings;
    private readonly ICurrentUser _currentUser;

    public MeController(UserPreferenceStore prefs, ISystemSettingsStore settings, ICurrentUser currentUser)
    {
        _prefs = prefs;
        _settings = settings;
        _currentUser = currentUser;
    }

    /// <summary>個人常用語；沒有個人清單時回全站預設（isDefault=true）</summary>
    [HttpGet("note-phrases")]
    public ApiResponse<NotePhrasesDto> GetNotePhrases() => ApiResponse<NotePhrasesDto>.Ok(Resolve());

    /// <summary>儲存個人常用語：去空白與重複後最多 20 條、每條最多 200 字；空陣列＝回到全站預設</summary>
    [HttpPut("note-phrases")]
    public ApiResponse<NotePhrasesDto> SetNotePhrases([FromBody] SetNotePhrasesRequest request)
    {
        if (_currentUser.UserId <= 0)
            throw DomainException.Validation("此帳號沒有個人偏好可儲存。");

        var phrases = NotePhraseRules.Normalize(request.Phrases);
        var error = NotePhraseRules.Validate(phrases);
        if (error != null) throw DomainException.Validation(error);

        _prefs.SetNotePhrases(_currentUser.UserId, phrases);
        return ApiResponse<NotePhrasesDto>.Ok(Resolve());
    }

    private NotePhrasesDto Resolve()
    {
        var own = _currentUser.UserId > 0 ? _prefs.Get(_currentUser.UserId).NotePhrases : new List<string>();
        return own.Count > 0
            ? new NotePhrasesDto { Phrases = own, IsDefault = false }
            : new NotePhrasesDto { Phrases = _settings.Get().DefaultNotePhrases, IsDefault = true };
    }
}

public class NotePhrasesDto
{
    public List<string> Phrases { get; set; } = new();
    public bool IsDefault { get; set; }
}

public class SetNotePhrasesRequest
{
    public List<string?>? Phrases { get; set; }
}

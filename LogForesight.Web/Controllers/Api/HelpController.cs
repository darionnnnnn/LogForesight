using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 操作說明書＋AI 問答。全登入使用者皆可見公開章節，管理員章節依 Maintain 能力過濾。
/// </summary>
[ApiController]
[Route("api/help")]
[Authorize]
public class HelpController : ControllerBase
{
    private readonly HelpContentService _content;
    private readonly HelpQaService _qa;
    private readonly SetupWizardStateStore _setupState;
    private readonly ICurrentUser _currentUser;
    private readonly ManualAiThrottle _manualThrottle;

    public HelpController(HelpContentService content, HelpQaService qa, SetupWizardStateStore setupState, ICurrentUser currentUser,
        ManualAiThrottle manualThrottle)
    {
        _content = content;
        _qa = qa;
        _setupState = setupState;
        _currentUser = currentUser;
        _manualThrottle = manualThrottle;
    }

    /// <summary>manifest＋全部章節內容一次取回（總量 &lt;200KB，不值得做分節載入）。
    /// 依使用者 Capabilities 過濾可見章節；啟動精靈全部步驟完成後使用者選擇隱藏時（回饋十八輪批次H），濾掉精靈導引卡章節。</summary>
    [HttpGet("manual")]
    public ApiResponse<HelpManualDto> Manual() => ApiResponse<HelpManualDto>.Ok(_content.GetManual(_currentUser.Capabilities, _setupState.Get().Hidden));

    /// <summary>AI 是否可用——手冊頁在渲染問答框前先問一次，未設定時整區換成說明文案</summary>
    [HttpGet("ask-available")]
    public ApiResponse<AiStatusDto> AskAvailable() =>
        // 問答需 Maintain（WEB-SPEC §9.9c）；沒有權限回 200＋不可用，讓前端靜默隱藏問答卡而非 403
        ApiResponse<AiStatusDto>.Ok(new AiStatusDto { Available = _currentUser.Has(Capability.Maintain) && _qa.Available });

    /// <summary>
    /// 單輪問答，不保留對話歷史（實驗性功能）。AI 未設定或呼叫失敗一律回 data:null，
    /// 前端顯示「AI 服務暫時無法回應，可先查閱下方章節」。
    /// </summary>
    [SkipDataVersionBump]
    [HttpPost("ask")]
    [Permission(Capability.Maintain)]
    public async Task<ApiResponse<AskHelpResponseDto?>> Ask([FromBody] AskHelpRequest request)
    {
        // 問答不走快取：驗證通過、AI 可用、真的要呼叫時才扣額（驗證錯誤與 AI 未設定不扣）
        var rejectedAfter = 0;
        var answer = await _qa.AskAsync(request.Question, () =>
            _manualThrottle.TryAcquire(_currentUser.UserId, DateTime.UtcNow, out rejectedAfter));
        return rejectedAfter > 0
            ? AiRateLimit.Reject<AskHelpResponseDto?>(Response, rejectedAfter)
            : ApiResponse<AskHelpResponseDto?>.Ok(answer);
    }
}

using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 操作說明書＋AI 問答（docs/archive/FEEDBACK-15-PLAN.md 批次E）。整支 Controller 需要
/// Maintain 能力——手冊頁本身僅 admin 可見（見側欄選單與 PagesController 的頁面級標註）。
/// </summary>
[ApiController]
[Route("api/help")]
[Permission(Capability.Maintain)]
public class HelpController : ControllerBase
{
    private readonly HelpContentService _content;
    private readonly HelpQaService _qa;
    private readonly SetupWizardStateStore _setupState;

    public HelpController(HelpContentService content, HelpQaService qa, SetupWizardStateStore setupState)
    {
        _content = content;
        _qa = qa;
        _setupState = setupState;
    }

    /// <summary>manifest＋全部章節內容一次取回（總量 &lt;200KB，不值得做分節載入）。
    /// 啟動精靈全部步驟完成後使用者選擇隱藏時（回饋十八輪批次H），濾掉精靈導引卡章節。</summary>
    [HttpGet("manual")]
    public ApiResponse<HelpManualDto> Manual() => ApiResponse<HelpManualDto>.Ok(_content.GetManual(_setupState.Get().Hidden));

    /// <summary>AI 是否可用——手冊頁在渲染問答框前先問一次，未設定時整區換成說明文案</summary>
    [HttpGet("ask-available")]
    public ApiResponse<AiStatusDto> AskAvailable() => ApiResponse<AiStatusDto>.Ok(new AiStatusDto { Available = _qa.Available });

    /// <summary>
    /// 單輪問答，不保留對話歷史（實驗性功能）。AI 未設定或呼叫失敗一律回 data:null，
    /// 前端顯示「AI 服務暫時無法回應，可先查閱下方章節」。
    /// </summary>
    [HttpPost("ask")]
    public async Task<ApiResponse<AskHelpResponseDto?>> Ask([FromBody] AskHelpRequest request) =>
        ApiResponse<AskHelpResponseDto?>.Ok(await _qa.AskAsync(request.Question));
}

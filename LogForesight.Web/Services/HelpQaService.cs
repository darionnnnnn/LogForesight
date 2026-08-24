using System.Text;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 操作說明書 AI 問答（docs/archive/FEEDBACK-15-PLAN.md 批次E-3，實驗性功能）：選節
/// （<see cref="HelpChapterScorer"/>）→ 組 prompt → 呼叫既有 <see cref="IWebAiService.ChatOnceAsync"/>
/// （與詳情頁對話共用同一套「AI 不可用/失敗一律回 null，呼叫端靜默降級」慣例）。
///
/// **明確不做**（本輪範圍界定，docs/archive/FEEDBACK-15-PLAN.md 批次E）：向量 RAG／embedding、
/// 多輪對話、非 admin 開放、手冊全文塞進 prompt。
/// </summary>
public class HelpQaService
{
    /// <summary>選入 prompt 的章節內容 token 上限（規劃文件「內容上限約 12K token」）。
    /// 是否連同輸出上限一起超出 context 總預算，交給 AIService.ChatAsync 既有的
    /// PromptBudget.ExceedsBudget 防線把關，這裡不重複做同一件事。</summary>
    private const int ContentTokenBudget = 12000;

    private const int MaxQuestionChars = 500;

    private const string SystemPrompt =
        "你是 LogForesight 系統的操作說明書問答助理。請一律使用台灣繁體中文回答。" +
        "只能依據下方提供的說明書章節內容作答，不可引用章節沒有寫的資訊、不可憑空猜測。" +
        "如果提供的章節內容不足以回答使用者的問題，請明確告知「說明書未涵蓋這個問題」。" +
        "回答結尾另起一行，列出你這次回答實際引用了哪些章節標題。";

    private readonly HelpContentService _content;
    private readonly IWebAiService _ai;

    public HelpQaService(HelpContentService content, IWebAiService ai)
    {
        _content = content;
        _ai = ai;
    }

    /// <summary>AI 是否已設定——手冊頁據此決定要不要顯示問答框，或改換說明文案</summary>
    public bool Available => _ai.Available;

    /// <summary>
    /// 任何失敗（未設定、選不到節仍呼叫失敗、AI 逾時）一律回 null，比照 AiController 既有慣例，
    /// 呼叫端顯示「AI 服務暫時無法回應，可先查閱下方章節」。
    /// </summary>
    public async Task<AskHelpResponseDto?> AskAsync(string question)
    {
        var trimmed = (question ?? "").Trim();
        if (trimmed.Length == 0) throw DomainException.Validation("請輸入問題。");
        if (trimmed.Length > MaxQuestionChars) throw DomainException.Validation($"問題不可超過 {MaxQuestionChars} 字。");

        if (!_ai.Available) return null;

        var selected = HelpChapterScorer.SelectChapters(trimmed, _content.Chapters, ContentTokenBudget);
        var userPrompt = BuildUserPrompt(trimmed, selected);

        var answer = await _ai.ChatOnceAsync(SystemPrompt, userPrompt);
        if (string.IsNullOrWhiteSpace(answer)) return null;

        return new AskHelpResponseDto
        {
            Answer = answer.Trim(),
            // 這是選進 prompt 的候選章節（HelpChapterScorer.SelectChapters），不是模型自述
            // 「實際引用了哪些」——SystemPrompt 另外要求模型在回答結尾自列引用，兩者可能不同
            // （模型未必用到候選裡的每一節）。前端標籤據此改為「參考章節（提供給 AI 的內容）」，
            // 不宣稱這是模型的實際引用（回饋十六輪批次D-2）。
            CitedChapterIds = selected.Select(c => c.Id).ToList()
        };
    }

    private static string BuildUserPrompt(string question, List<HelpChapter> chapters)
    {
        var sb = new StringBuilder();
        if (chapters.Count == 0)
        {
            sb.AppendLine("（沒有找到與這個問題相關的說明書章節，請依系統提示的規則明確告知使用者。）");
        }
        else
        {
            sb.AppendLine("以下是操作說明書的相關章節內容：");
            sb.AppendLine();
            foreach (var chapter in chapters)
            {
                sb.AppendLine($"### {chapter.Title}");
                // 餵給 AI 的是詳細版（回饋二十七輪作業 G）：使用者手冊要好讀、AI 要夠細，
                // 兩者衝突。沒有 AI 版的章節 AiContent 會 fallback 成 Content，行為不變
                sb.AppendLine(chapter.ContentForAi);
                sb.AppendLine();
            }
        }
        sb.AppendLine("使用者問題：");
        sb.AppendLine(question);
        return sb.ToString();
    }
}

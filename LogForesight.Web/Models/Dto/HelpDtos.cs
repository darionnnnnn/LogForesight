namespace LogForesight.Web.Models.Dto;

/// <summary>操作說明書單一章節（回饋十五輪批次E）：Content 是原始 Markdown 文字，
/// 前端以 markdown-lite.js 的安全子集渲染（不引入可解析 HTML/連結的 Markdown 庫）。</summary>
public class HelpChapterDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>相關章節 id（manifest 的 related，人與 AI 共用同一份關聯資訊）</summary>
    public List<string> Related { get; set; } = new();

    /// <summary>章節目錄圖示名稱（回饋十七輪批次G-2），對應 /img/icons.svg 的 symbol id</summary>
    public string Icon { get; set; } = "";

    /// <summary>"markdown"（預設，既有章節）｜"link"（回饋十八輪批次H，導向站內其他頁面的
    /// 導引卡，例如首次啟動精靈）。markdown-lite 刻意不支援連結，type=link 的章節前端另外渲染。</summary>
    public string Type { get; set; } = "markdown";

    /// <summary>type=link 時的目的地路徑；markdown 章節恆為 null。</summary>
    public string? Href { get; set; }
}

public class HelpManualDto
{
    public List<HelpChapterDto> Chapters { get; set; } = new();
}

public class AskHelpRequest
{
    public string Question { get; set; } = "";
}

/// <summary>AI 問答結果。AI 未設定或呼叫失敗時 Controller 回 data:null（比照 AiController 既有慣例），
/// 不用這個型別本身表達失敗。</summary>
public class AskHelpResponseDto
{
    public string Answer { get; set; } = "";
    public List<string> CitedChapterIds { get; set; } = new();
}

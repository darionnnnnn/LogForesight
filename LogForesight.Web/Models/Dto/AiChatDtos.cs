namespace LogForesight.Web.Models.Dto;

/// <summary>風險日詳情頁對話（R7 精簡版）的一則訊息</summary>
public class ChatMessageDto
{
    public string Role { get; set; } = string.Empty; // "user" | "assistant"
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// 對話請求：不持久化，前端每輪送出完整歷史。伺服器端重新從 hostId/date/issueKey
/// 取得 context（授權與資料來源與 interpret-issue 同一條路徑），不信任 client 端帶來的內容以外的任何欄位。
/// </summary>
public class ChatRequest
{
    public long HostId { get; set; }
    public string Date { get; set; } = string.Empty;
    public string IssueKey { get; set; } = string.Empty;
    public List<ChatMessageDto> Messages { get; set; } = new();
}

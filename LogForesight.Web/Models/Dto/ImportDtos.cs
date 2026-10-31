namespace LogForesight.Web.Models.Dto;

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

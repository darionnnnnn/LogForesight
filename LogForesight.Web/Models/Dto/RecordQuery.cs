namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 問題查詢四個清單端點（明細／依主機／依日期／依問題）共用的 query string 參數。
/// 參數名稱（大小寫不敏感）與 URL 形狀不變；<c>hostIds</c>／<c>groupIds</c>／<c>riskLevels</c> 等
/// 逗號分隔的清單維持字串，由控制器以 <c>QueryStringParsing</c> 手動解析
/// （改成 <c>List&lt;long&gt;</c> 綁定會把 URL 形狀變成 <c>hostIds=1&amp;hostIds=2</c>）。
/// 全部宣告成可為 null，缺省值在組請求時才套用，避免空字串被當成「必填」。
/// </summary>
public class RecordQuery
{
    public string? HostIds { get; set; }
    public string? GroupIds { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? RiskLevels { get; set; }
    public string? Categories { get; set; }
    public string? Severity { get; set; }
    public int? EventId { get; set; }
    public string? Source { get; set; }
    public string? Statuses { get; set; }
    public bool? Overdue { get; set; }
    public bool? Unassigned { get; set; }
    public string? Sort { get; set; }

    /// <summary>"asc" 為升冪，其餘（含未給）一律降冪</summary>
    public string? Dir { get; set; }

    public int? Page { get; set; }
    public int? PageSize { get; set; }
}

/// <summary>問題查詢的四個視角</summary>
public enum RecordView
{
    Detail,
    ByIssue,
    ByHost,
    ByDate
}

/// <summary>
/// 各視角支援哪些「處理狀態類」篩選條件的單一出處（WEB-SPEC §10）：
/// 後端組請求時只帶支援的條件，前端 <c>records.js</c> 的 <c>VIEW_FILTER_SUPPORT</c> 與此表逐項對應。
/// 依主機／依日期是彙總視角，支援處理狀態需要全撈處理資料做記憶體過濾，因此不支援。
/// </summary>
public static class RecordQuerySupport
{
    public const string Statuses = "statuses";
    public const string Overdue = "overdue";
    public const string Unassigned = "unassigned";

    private static readonly IReadOnlyDictionary<RecordView, IReadOnlySet<string>> Matrix =
        new Dictionary<RecordView, IReadOnlySet<string>>
        {
            [RecordView.Detail] = new HashSet<string> { Statuses, Overdue, Unassigned },
            [RecordView.ByIssue] = new HashSet<string> { Statuses, Unassigned },
            [RecordView.ByHost] = new HashSet<string>(),
            [RecordView.ByDate] = new HashSet<string>()
        };

    public static bool Supports(RecordView view, string filterKey) => Matrix[view].Contains(filterKey);
}

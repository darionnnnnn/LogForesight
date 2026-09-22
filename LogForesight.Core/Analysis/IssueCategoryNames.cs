namespace LogForesight.Core.Analysis;

/// <summary>
/// 類別中文名的 C# 端**唯一**字典（回饋十九輪批次I 體檢收斂，即 docs/BACKLOG.md S13
/// 「先把 C# 版搬到 Core」那一步）：原本批次報告（RiskReportService.CategoryZh）一份、
/// 批次H 郵件又長出第三份，跨檔案的 switch 拷貝正是 S13 記錄的分歧風險。
/// 主版面將此字典提供給 format.js，前後端共用名稱。
/// </summary>
public static class IssueCategoryNames
{
    /// <summary>提供前端同一份翻譯，順序與既有畫面一致。</summary>
    public static IReadOnlyDictionary<string, string> All { get; } =
        new[] { IssueCategory.Storage, IssueCategory.Hardware, IssueCategory.Security,
                IssueCategory.Service, IssueCategory.Backup, IssueCategory.Config,
                IssueCategory.Resource, IssueCategory.Other }
            .ToDictionary(category => category.ToString(), Zh);

    public static string Zh(IssueCategory category) => category switch
    {
        IssueCategory.Storage => "儲存裝置",
        IssueCategory.Hardware => "硬體",
        IssueCategory.Security => "安全",
        IssueCategory.Service => "服務",
        IssueCategory.Resource => "資源",
        IssueCategory.Backup => "備份",
        IssueCategory.Config => "設定",
        _ => "其他"
    };

    /// <summary>字串版（SQL 聚合結果的 Category 欄是字串）：查無對應時原樣回傳，
    /// 不擋呼叫端的輸出流程——寧可畫面/信件出現英文原文，也不要整封信因此炸掉。</summary>
    public static string Zh(string category) =>
        Enum.TryParse<IssueCategory>(category, ignoreCase: false, out var parsed) ? Zh(parsed) : category;
}

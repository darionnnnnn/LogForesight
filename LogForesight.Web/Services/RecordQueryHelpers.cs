namespace LogForesight.Web.Services;

/// <summary>
/// 問題查詢面共用的純函數：<see cref="RecordListQueryService"/>（依問題視角、共通問題彙總）
/// 與 <see cref="RecordDetailQueryService"/>（主機詳情頁重點問題彙總）都要「依 (Source,
/// EventId) 分組」，分組鍵定義只寫一次，避免兩處各自維護一份遲早漂移不一致。
/// </summary>
internal static class RecordQueryHelpers
{
    public static IEnumerable<IGrouping<(string Source, int EventId), (DailyAnalysisRecord Record, LogIssueSignature Issue)>>
        GroupIssuesBySignature(IEnumerable<DailyAnalysisRecord> records) =>
        records
            .SelectMany(r => r.TopIssues.Select(i => (Record: r, Issue: i)))
            .GroupBy(x => (x.Issue.Source, x.Issue.EventId));
}

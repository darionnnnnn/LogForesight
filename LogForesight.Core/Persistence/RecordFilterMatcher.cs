namespace LogForesight.Core.Persistence;

/// <summary>
/// 分析紀錄對查詢條件的比對（<see cref="RecordQueryFilter"/> 中除了 Hosts 以外的欄位）。
///
/// **單點規則**（docs/DB-SPEC.md 一致性機制 #3）：SQL 端以日期/主機在資料庫預篩後，
/// 其餘欄位一律套用同一個函數比對——篩選語意只有一份定義，不靠 code review 肉眼比對。
/// 與 <see cref="RecordStorageShaper"/>、<see cref="CategoryAggregator"/> 同一套
/// 「規則不長在單一呼叫端裡」的原則。
///
/// Hosts 不在此：那是主機識別（<see cref="HostMatcher"/>）＋空集合＝零結果的授權語意，
/// 與一般欄位篩選是兩件事，各自維護。
/// </summary>
public static class RecordFilterMatcher
{
    public static bool Matches(DailyAnalysisRecord record, RecordQueryFilter filter)
    {
        if (filter.From.HasValue && record.Date.Date < filter.From.Value.Date) return false;
        if (filter.To.HasValue && record.Date.Date > filter.To.Value.Date) return false;

        if (filter.RiskLevels is { Count: > 0 } && !filter.RiskLevels.Contains(record.RiskLevel)) return false;

        if (filter.Categories is { Count: > 0 } &&
            !record.TopIssues.Any(i => filter.Categories.Contains(i.Category)))
        {
            return false;
        }

        if (filter.MinSeverity.HasValue &&
            !record.TopIssues.Any(i => i.Severity >= filter.MinSeverity.Value))
        {
            return false;
        }

        if (filter.EventId.HasValue &&
            !record.TopIssues.Any(i => i.EventId == filter.EventId.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Source) &&
            !record.TopIssues.Any(i => string.Equals(i.Source, filter.Source, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}

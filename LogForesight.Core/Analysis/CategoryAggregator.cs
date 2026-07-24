namespace LogForesight;

/// <summary>
/// 單一類別在某一天的彙總（↔ lf_record_categories）。
/// 各嚴重度的分解（<see cref="CriticalCount"/> 等）是 2026-07-21 為 Web 報表的
/// 「類別×嚴重度」堆疊圖新增的——只有 MaxSeverity 答不出「這個類別各嚴重度各幾項」。
/// </summary>
public class CategorySummary
{
    public IssueCategory Category { get; set; }

    /// <summary>該類別當日的問題簽章數</summary>
    public int IssueCount { get; set; }

    /// <summary>該類別當日的事件總筆數（各簽章 Count 的加總）</summary>
    public int TotalEvents { get; set; }

    public IssueSeverity MaxSeverity { get; set; }

    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
}

/// <summary>
/// 類別彙總的計算（docs/WEB-SPEC.md §10.3、DB-PLAN 一致性機制 #4）。
///
/// **純函數、單點定義**：批次寫入時呼叫它把結果存進 lf_record_categories，
/// 任何需要重算彙總的呼叫端也用同一個函數。所有呼叫端因此保證產出逐位一致的數字——
/// 如果各自實作一份，遲早會出現「儀表板說 3 件、明細列出 4 件」這種沒人能解釋的差異。
/// （Jsonl 後端退役前，這裡曾是「檔案端查詢期即時聚合」與「SQL 端寫入時入庫」共用的單點。）
///
/// 與 <see cref="RecordStorageShaper"/> 同一個理由：能被兩個後端共用的規則，
/// 就不該長在單一實作裡面。
/// </summary>
public static class CategoryAggregator
{
    /// <summary>依類別彙總問題簽章。回傳依「最高嚴重度、問題數」排序（最嚴重的類別在前）</summary>
    public static List<CategorySummary> Aggregate(IEnumerable<LogIssueSignature> issues)
    {
        return issues
            .GroupBy(i => i.Category)
            .Select(group => new CategorySummary
            {
                Category = group.Key,
                IssueCount = group.Count(),
                TotalEvents = group.Sum(i => i.Count),
                MaxSeverity = group.Max(i => i.Severity),
                CriticalCount = group.Count(i => i.Severity == IssueSeverity.Critical),
                HighCount = group.Count(i => i.Severity == IssueSeverity.High),
                MediumCount = group.Count(i => i.Severity == IssueSeverity.Medium),
                LowCount = group.Count(i => i.Severity == IssueSeverity.Low)
            })
            .OrderByDescending(s => s.MaxSeverity)
            .ThenByDescending(s => s.IssueCount)
            .ToList();
    }

    /// <summary>
    /// 跨多天/多主機的合併彙總（報表的期間統計用）。
    /// 逐日彙總後再相加，語意是「這段期間各類別總共出現幾個問題簽章」。
    /// </summary>
    public static List<CategorySummary> Merge(IEnumerable<CategorySummary> summaries)
    {
        return summaries
            .GroupBy(s => s.Category)
            .Select(group => new CategorySummary
            {
                Category = group.Key,
                IssueCount = group.Sum(s => s.IssueCount),
                TotalEvents = group.Sum(s => s.TotalEvents),
                MaxSeverity = group.Max(s => s.MaxSeverity),
                CriticalCount = group.Sum(s => s.CriticalCount),
                HighCount = group.Sum(s => s.HighCount),
                MediumCount = group.Sum(s => s.MediumCount),
                LowCount = group.Sum(s => s.LowCount)
            })
            .OrderByDescending(s => s.MaxSeverity)
            .ThenByDescending(s => s.IssueCount)
            .ToList();
    }
}


namespace LogForesight.Tests;

// ── 測試替身：報告輸出／週期體檢相關 ─────────────────────────────────────────
// FakeReportSink 原本在 RiskReportServiceTests 與 WeeklyCheckupServiceTests 裡
// 各自定義一份幾乎相同的替身（一份只記 LastContent、一份只記 Called）——
// 兩者是同一件事的子集，合併成同時記錄兩者的超集版本，不影響任一邊原本的斷言。
// FakeReader 目前只有 WeeklyCheckupServiceTests 在用，因同屬體檢排程領域一併集中在這裡。

internal sealed class FakeReportSink : IReportSink
{
    public bool Called { get; private set; }
    public string? LastContent { get; private set; }

    public Task<string> WriteAsync(ReportKind kind, string host, string fileName, string content)
    {
        Called = true;
        LastContent = content;
        return Task.FromResult($"fake/{fileName}");
    }
}

internal sealed class FakeReader : IAnalysisRecordReader
{
    private readonly List<DailyAnalysisRecord> _records;
    private readonly DateTime? _lastCheckup;

    public FakeReader(List<DailyAnalysisRecord> records, DateTime? lastCheckup = null)
    {
        _records = records;
        _lastCheckup = lastCheckup;
    }

    // 替身照實作錨定窗語意過濾，否則測試會在「未來紀錄混入窗口」這件事上失去防護
    public List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days) =>
        _records
            .Where(r => r.Date.Date >= anchorDate.Date.AddDays(-(days - 1)) && r.Date.Date <= anchorDate.Date)
            .OrderBy(r => r.Date)
            .ToList();

    public bool HasAnyRecord() => _records.Count > 0;
    public bool HasRecord(DateTime date) => _records.Any(r => r.Date.Date == date.Date);
    public DateTime? LastWeeklyCheckupDate() => _lastCheckup;
}

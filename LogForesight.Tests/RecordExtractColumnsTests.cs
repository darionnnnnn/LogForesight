using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 讀取面 SQL 化的抽出欄（回饋十九輪批次B）：寫入時同步填好 lf_daily_records／lf_top_issues
/// 的新欄位，以及機房首見日（lf_issue_first_seen）的 insert-if-absent／取較早日期邏輯。
/// </summary>
public class RecordExtractColumnsTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _store;

    public RecordExtractColumnsTests()
    {
        _store = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static DailyAnalysisRecord Record(DateTime date, string risk, params LogIssueSignature[] issues) => new()
    {
        Date = date, HostId = 1, Host = "HOST-A", RiskLevel = risk,
        Headline = "今天狀況", ErrorCount = 3, WarningCount = 5, AiAnalyzed = true, AiPending = false,
        DataIncomplete = true, SecurityLogAvailable = false,
        TopIssues = issues.ToList()
    };

    private static LogIssueSignature Issue(string source, int eventId, string? knownIssue = null, string eventKey = "") => new()
    {
        LogName = "System", Source = source, EventId = eventId, Count = 1,
        Category = IssueCategory.Storage, Severity = IssueSeverity.High,
        KnownIssue = knownIssue, EventKey = eventKey
    };

    [Fact]
    public void Append_填好lf_daily_records的抽出欄()
    {
        _store.Append(Record(new DateTime(2026, 8, 1), "高", Issue("disk", 153)));

        using var ctx = _fx.NewContext();
        var row = ctx.DailyRecords.Single();

        Assert.Equal("今天狀況", row.Headline);
        Assert.Equal(3, row.ErrorCount);
        Assert.Equal(5, row.WarningCount);
        Assert.True(row.AiAnalyzed);
        Assert.False(row.AiPending);
        Assert.True(row.DataIncomplete);
        Assert.False(row.SecurityLogAvailable);
        Assert.Equal(DailyRecordBackfiller.CurrentVersion, row.ExtractVersion);   // 寫入即最新版本，不需要回填
    }

    [Fact]
    public void Append_填好lf_top_issues的KnownIssue與EventKey()
    {
        _store.Append(Record(new DateTime(2026, 8, 1), "高",
            Issue("sshd", 0, knownIssue: "疑似暴力破解", eventKey: "ssh-bruteforce")));

        using var ctx = _fx.NewContext();
        var row = ctx.TopIssues.Single();

        Assert.Equal("疑似暴力破解", row.KnownIssue);
        Assert.Equal("ssh-bruteforce", row.EventKey);
    }

    [Fact]
    public void Append_低風險日精簡後仍保留KnownIssue與EventKey()
    {
        // 低風險日會走 RecordStorageShaper 的精簡路徑——這條路徑曾漏抄 EventKey（本輪修復），
        // 這裡從 Append 端到端驗證修復確實生效，不只是 shaper 單元測試
        _store.Append(Record(new DateTime(2026, 8, 1), "低",
            Issue("sshd", 0, knownIssue: "疑似暴力破解", eventKey: "ssh-bruteforce")));

        using var ctx = _fx.NewContext();
        var row = ctx.TopIssues.Single();

        Assert.Equal("ssh-bruteforce", row.EventKey);
    }

    [Fact]
    public void Append_新問題建立機房首見日()
    {
        _store.Append(Record(new DateTime(2026, 8, 10), "高", Issue("disk", 153)));

        using var ctx = _fx.NewContext();
        var row = ctx.IssueFirstSeen.Single();

        Assert.Equal("DISK", row.SourceKey);
        Assert.Equal("disk", row.SourceName);
        Assert.Equal(153, row.EventId);
        Assert.Equal(new DateTime(2026, 8, 10), row.FirstSeen);
    }

    /// <summary>之後幾天同一個問題再出現——首見日不會被較晚的日期蓋掉</summary>
    [Fact]
    public void Append_同問題較晚日期不覆蓋首見日()
    {
        _store.Append(Record(new DateTime(2026, 8, 10), "高", Issue("disk", 153)));
        _store.Append(Record(new DateTime(2026, 8, 11), "高", Issue("disk", 153)));

        using var ctx = _fx.NewContext();
        var row = ctx.IssueFirstSeen.Single();

        Assert.Equal(new DateTime(2026, 8, 10), row.FirstSeen);
    }

    /// <summary>
    /// 回補（NetIQ BackfillDays／首次執行回補近 120 天）走過去的日期，可能比目前記錄的
    /// 首見日更早——這種情況必須更新，否則「機房首見」會比實際發生的還晚，跟審查要解的
    /// 「FirstSeen 被查詢期間截斷」是同一類「顯示得比實際發生晚」的問題。
    /// </summary>
    [Fact]
    public void Append_較早日期回補時更新首見日()
    {
        _store.Append(Record(new DateTime(2026, 8, 10), "高", Issue("disk", 153)));
        _store.Append(Record(new DateTime(2026, 7, 1), "高", Issue("disk", 153)));   // 回補的更早一筆

        using var ctx = _fx.NewContext();
        var row = ctx.IssueFirstSeen.Single();

        Assert.Equal(new DateTime(2026, 7, 1), row.FirstSeen);
    }

    [Fact]
    public void Append_不同問題各自一筆首見日()
    {
        _store.Append(Record(new DateTime(2026, 8, 10), "高", Issue("disk", 153), Issue("DCOM", 10016)));

        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.IssueFirstSeen.Count());
    }

    // ── DailyRecordBackfiller（B6）────────────────────────────────────────────

    [Fact]
    public void DailyRecordBackfiller_本輪寫入的列不需要回填()
    {
        _store.Append(Record(new DateTime(2026, 8, 1), "高", Issue("disk", 153)));

        var backfiller = new DailyRecordBackfiller(_fx.NewContext);

        Assert.Equal(0, backfiller.CountPending());
    }

    /// <summary>模擬升級前既存的舊列（extract_version=0，只有 ContentJson），回填後抽出欄補齊</summary>
    [Fact]
    public void DailyRecordBackfiller_補齊舊列的抽出欄()
    {
        var legacy = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 8, 1), HostId = 1, Host = "HOST-A", RiskLevel = "高",
            Headline = "舊資料標題", ErrorCount = 9, WarningCount = 2, AiAnalyzed = true,
            CorrelationAlerts = new List<string> { "corr" }
        };
        using (var ctx = _fx.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = legacy.Date, RiskLevel = "高",
                ContentJson = JsonSerializer.Serialize(legacy), CreatedAt = DateTime.Now,
                ExtractVersion = 0   // 舊列：升級前寫入，尚未回填
            });
            ctx.SaveChanges();
        }

        var backfiller = new DailyRecordBackfiller(_fx.NewContext);
        Assert.Equal(1, backfiller.CountPending());

        backfiller.Run(CancellationToken.None);

        using var verify = _fx.NewContext();
        var row = verify.DailyRecords.Single();
        Assert.Equal("舊資料標題", row.Headline);
        Assert.Equal(9, row.ErrorCount);
        Assert.True(row.HasCorrelation);   // 同一趟順便補 P1-2 既有欄
        Assert.Equal(DailyRecordBackfiller.CurrentVersion, row.ExtractVersion);
        Assert.True(backfiller.Progress.Completed);
    }

    // ── TopIssueBackfiller 擴充（B2）─────────────────────────────────────────

    /// <summary>TopIssueBackfiller 既有回填流程一併補齊本輪新增的 KnownIssue／EventKey</summary>
    [Fact]
    public void TopIssueBackfiller_一併補齊KnownIssue與EventKey()
    {
        var legacy = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 8, 1), HostId = 1, Host = "HOST-A", RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                Issue("sshd", 0, knownIssue: "疑似暴力破解", eventKey: "ssh-bruteforce")
            }
        };
        using (var ctx = _fx.NewContext())
        {
            var recordRow = new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = legacy.Date, RiskLevel = "高",
                ContentJson = JsonSerializer.Serialize(legacy), CreatedAt = DateTime.Now, ExtractVersion = 1
            };
            ctx.DailyRecords.Add(recordRow);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = recordRow.RecordId, SourceName = "sshd", EventId = 0,
                Category = "Storage", SeverityRank = (int)IssueSeverity.High,
                RecordDate = DateTime.MinValue   // 舊列的哨兵：尚未回填
            });
            ctx.SaveChanges();
        }

        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        backfiller.Run(CancellationToken.None);

        using var verify = _fx.NewContext();
        var row = verify.TopIssues.Single();
        Assert.Equal("疑似暴力破解", row.KnownIssue);
        Assert.Equal("ssh-bruteforce", row.EventKey);
    }
}

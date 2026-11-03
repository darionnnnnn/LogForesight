using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63 輪 F-4：<see cref="IAnalysisRecordQuery.QueryByHeadline"/>（依日期範圍與標題精確相等查完整紀錄）。
/// 「什麼算一個 AI 格式異常案例」只有這一個判定來源，所以標題比對必須是精確相等、
/// 不是包含或前綴，並且下推到 SQL 的 headline 欄位。
/// </summary>
public sealed class AnalysisRecordHeadlineQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _store;

    private static readonly string Anomaly = LogAnalysisService.AiFormatAnomalyHeadline;
    private static readonly DateTime D = new(2026, 10, 10);

    public AnalysisRecordHeadlineQueryTests()
    {
        _store = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static DailyAnalysisRecord Rec(long hostId, DateTime date, string headline, string summary = "摘要") => new()
    {
        HostId = hostId,
        Host = $"HOST-{hostId}",
        Date = date,
        RiskLevel = "中",
        Headline = headline,
        Summary = summary,
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, Count = 3, SampleMessages = new List<string> { "範例訊息" } }
        }
    };

    [Fact]
    public void 標題精確相等_包含與前綴後綴變體都不算()
    {
        _store.Append(Rec(1, D, Anomaly));
        _store.Append(Rec(2, D, Anomaly + "（補）"));            // 前綴相同
        _store.Append(Rec(3, D, "【註】" + Anomaly));             // 包含
        _store.Append(Rec(4, D, Anomaly[..^2]));                  // 被包含
        _store.Append(Rec(5, D, "今日分析摘要暫缺（AI 服務未回應，AI 待補）"));
        _store.Append(Rec(6, D, ""));

        var result = ((IAnalysisRecordQuery)_store).QueryByHeadline(D, D, Anomaly, 100);

        Assert.Equal(1L, Assert.Single(result).HostId);
    }

    [Fact]
    public void 日期範圍含上下界_範圍外不回()
    {
        _store.Append(Rec(1, D.AddDays(-1), Anomaly));
        _store.Append(Rec(2, D, Anomaly));
        _store.Append(Rec(3, D.AddDays(2), Anomaly));
        _store.Append(Rec(4, D.AddDays(3), Anomaly));
        _store.Append(Rec(5, D.AddDays(-2), Anomaly));

        var result = ((IAnalysisRecordQuery)_store).QueryByHeadline(D.AddHours(13), D.AddDays(2).AddHours(5), Anomaly, 100);

        Assert.Equal(new long[] { 3, 2 }, result.Select(r => r.HostId).ToArray());
    }

    [Fact]
    public void 新到舊_同日依主機id_take截斷最舊的()
    {
        _store.Append(Rec(2, D, Anomaly));
        _store.Append(Rec(1, D, Anomaly));
        _store.Append(Rec(3, D.AddDays(1), Anomaly));
        _store.Append(Rec(1, D.AddDays(-1), Anomaly));

        var all = ((IAnalysisRecordQuery)_store).QueryByHeadline(D.AddDays(-5), D.AddDays(5), Anomaly, 100);
        var top2 = ((IAnalysisRecordQuery)_store).QueryByHeadline(D.AddDays(-5), D.AddDays(5), Anomaly, 2);

        Assert.Equal(new[] { (3L, D.AddDays(1)), (1L, D), (2L, D), (1L, D.AddDays(-1)) },
            all.Select(r => (r.HostId, r.Date.Date)).ToArray());
        Assert.Equal(new[] { 3L, 1L }, top2.Select(r => r.HostId).ToArray());
    }

    [Fact]
    public void 回傳完整紀錄_不是輕量投影()
    {
        _store.Append(Rec(1, D, Anomaly, summary: "（AI 回覆經 2 次嘗試仍未通過 JSON 檢查，保留原文供參考）{\"risk_level\":"));

        var record = Assert.Single(((IAnalysisRecordQuery)_store).QueryByHeadline(D, D, Anomaly, 10));

        Assert.StartsWith("（AI 回覆經 2 次嘗試", record.Summary);
        Assert.NotEqual(0, record.RecordId);
        var issue = Assert.Single(record.TopIssues);
        Assert.Equal(new[] { "範例訊息" }, issue.SampleMessages);   // 輕量投影不帶 SampleMessages
        Assert.Equal(3, issue.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void take不為正_回空(int take)
    {
        _store.Append(Rec(1, D, Anomaly));
        Assert.Empty(((IAnalysisRecordQuery)_store).QueryByHeadline(D, D, Anomaly, take));
    }

    [Fact]
    public void 標題為空或區間顛倒_回空()
    {
        _store.Append(Rec(1, D, ""));
        _store.Append(Rec(2, D, Anomaly));
        Assert.Empty(((IAnalysisRecordQuery)_store).QueryByHeadline(D, D, "", 10));
        Assert.Empty(((IAnalysisRecordQuery)_store).QueryByHeadline(D.AddDays(1), D, Anomaly, 10));
    }

    [Fact]
    public void 跨主機都回_不套主機授權()
    {
        _store.Append(Rec(1, D, Anomaly));
        _store.Append(Rec(2, D, Anomaly));
        Assert.Equal(2, ((IAnalysisRecordQuery)_store).QueryByHeadline(D, D, Anomaly, 10).Count);
    }

    // ── 替身與正式實作語意一致 ────────────────────────────────

    [Fact]
    public void 兩個替身與EfStore對同一組資料回相同結果()
    {
        var records = new[]
        {
            Rec(2, D, Anomaly), Rec(1, D, Anomaly), Rec(3, D.AddDays(1), Anomaly),
            Rec(4, D, Anomaly + "x"), Rec(5, D.AddDays(9), Anomaly), Rec(6, D, "其他")
        };
        var fakeRepo = new FakeRecordRepository(new FakeHostStore());
        var fakeQuery = new FakeAnalysisRecordQuery();
        foreach (var r in records)
        {
            _store.Append(Clone(r));
            fakeRepo.Add(Clone(r));
            fakeQuery.Add(Clone(r));
        }

        (long, DateTime)[] Keys(IAnalysisRecordQuery q) =>
            q.QueryByHeadline(D, D.AddDays(3), Anomaly, 3).Select(r => (r.HostId, r.Date.Date)).ToArray();

        var expected = Keys(_store);
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected, Keys(fakeRepo));
        Assert.Equal(expected, Keys(fakeQuery));
    }

    private static DailyAnalysisRecord Clone(DailyAnalysisRecord r) =>
        System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(System.Text.Json.JsonSerializer.Serialize(r))!;

    // ── SQL 下推：provider 翻譯 ────────────────────────────────

    [Fact]
    public void SQLServerProvider_標題相等與日期範圍下推到SQL()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var sql = EfAnalysisRecordStore.BuildHeadlineQuery(ctx, D, D.AddDays(3), Anomaly, 100).ToQueryString();

        AssertPushedDown(sql);
        Assert.Contains("TOP(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SQLiteProvider_標題相等與日期範圍下推到SQL()
    {
        using var ctx = _fx.NewContext();

        var sql = EfAnalysisRecordStore.BuildHeadlineQuery(ctx, D, D.AddDays(3), Anomaly, 100).ToQueryString();

        AssertPushedDown(sql);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPushedDown(string sql)
    {
        Assert.Contains("headline", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"headline\W+=\s", sql);                                  // 相等比較，不是 LIKE／INSTR
        Assert.DoesNotContain("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSTR", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CHARINDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("record_date", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content_json", sql, StringComparison.OrdinalIgnoreCase);   // 完整紀錄，不是輕量投影
    }
}

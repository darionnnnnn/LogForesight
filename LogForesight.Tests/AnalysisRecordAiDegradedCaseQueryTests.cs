using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪 B-b-5：<see cref="IAnalysisRecordQuery.QueryAiDegradedCases"/>（AI 格式異常降級案例）。
/// 「什麼算一個案例」只有這一個判定來源：降級次數 &gt; 0 且仍未成功（待補或已放棄），或歷史舊標題精確相等；
/// 已恢復成功的不算。全部條件必須下推到 SQL 的抽出欄。
/// </summary>
public sealed class AnalysisRecordAiDegradedCaseQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _store;

    private static readonly string Anomaly = LogAnalysisService.AiFormatAnomalyHeadline;
    private static readonly DateTime D = new(2026, 10, 10);

    public AnalysisRecordAiDegradedCaseQueryTests()
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
        AiAnalyzed = true,
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, Count = 3, SampleMessages = new List<string> { "範例訊息" } }
        }
    };

    /// <summary>寫入一筆並直接把抽出欄設成指定的降級狀態（<c>Append</c> 一律把次數歸零，所以只能事後改欄位）</summary>
    private void Seed(long hostId, DateTime date, string headline, int attempts, bool pending, string summary = "摘要")
    {
        _store.Append(Rec(hostId, date, headline, summary));
        using var ctx = _fx.NewContext();
        ctx.DailyRecords.Where(r => r.HostId == hostId && r.RecordDate == date.Date)
            .ExecuteUpdate(s => s.SetProperty(r => r.AiDegradedAttempts, attempts).SetProperty(r => r.AiPending, pending));
    }

    private List<DailyAnalysisRecord> Query(DateTime from, DateTime to, int take = 100) =>
        ((IAnalysisRecordQuery)_store).QueryAiDegradedCases(from, to, take);

    [Fact]
    public void 四種狀態_降級中與已放棄是案例_已恢復與從未降級不是()
    {
        Seed(1, D, AiDegradation.ComposeHeadline("磁碟錯誤偏多", 1), attempts: 1, pending: true);                  // 降級中
        Seed(2, D, AiDegradation.ComposeHeadline("磁碟錯誤偏多", 3), attempts: 3, pending: false);                 // 已放棄
        Seed(3, D, "磁碟錯誤偏多，建議檢查陣列", attempts: 1, pending: false);                                       // 已恢復成功
        Seed(4, D, "磁碟錯誤偏多，建議檢查陣列", attempts: 2, pending: false);                                       // 已恢復成功（第二次後）
        Seed(5, D, "磁碟錯誤偏多，建議檢查陣列", attempts: 0, pending: false);                                       // 從未降級
        Seed(6, D, "（統計已完成，AI 分析排隊中）", attempts: 0, pending: true);                                     // 排隊中（沒降級過）

        var result = Query(D, D);

        Assert.Equal(new long[] { 1, 2 }, result.Select(r => r.HostId).ToArray());
        Assert.Equal(1, result[0].AiDegradedAttempts);
        Assert.True(result[0].AiPending);
        Assert.Equal(3, result[1].AiDegradedAttempts);
        Assert.False(result[1].AiPending);
    }

    [Fact]
    public void 歷史舊標題仍算案例_次數為零也算()
    {
        Seed(1, D, Anomaly, attempts: 0, pending: false);
        Seed(2, D, Anomaly, attempts: 0, pending: true);

        Assert.Equal(new long[] { 1, 2 }, Query(D, D).Select(r => r.HostId).ToArray());
    }

    [Fact]
    public void 舊標題精確相等_包含與前綴後綴變體都不算()
    {
        Seed(1, D, Anomaly, 0, false);
        Seed(2, D, Anomaly + "（補）", 0, false);
        Seed(3, D, "【註】" + Anomaly, 0, false);
        Seed(4, D, Anomaly[..^2], 0, false);
        Seed(5, D, "今日分析摘要暫缺（AI 服務未回應，AI 待補）", 0, true);
        Seed(6, D, "", 0, false);

        Assert.Equal(1L, Assert.Single(Query(D, D)).HostId);
    }

    [Fact]
    public void 已恢復但標題剛好是舊標題_仍算案例()
    {
        // 舊標題是獨立的歷史來源：不論次數與是否待補都算
        Seed(1, D, Anomaly, attempts: 1, pending: false);
        Assert.Single(Query(D, D));
    }

    [Fact]
    public void 日期範圍含上下界_範圍外不回()
    {
        foreach (var (host, day) in new[] { (1L, -1), (2L, 0), (3L, 2), (4L, 3), (5L, -2) })
            Seed(host, D.AddDays(day), "x", 1, true);

        var result = Query(D.AddHours(13), D.AddDays(2).AddHours(5));

        Assert.Equal(new long[] { 3, 2 }, result.Select(r => r.HostId).ToArray());
    }

    [Fact]
    public void 新到舊_同日依主機id_take截斷最舊的()
    {
        Seed(2, D, "x", 1, true);
        Seed(1, D, "x", 1, true);
        Seed(3, D.AddDays(1), "x", 1, true);
        Seed(1, D.AddDays(-1), "x", 3, false);

        var all = Query(D.AddDays(-5), D.AddDays(5));
        var top2 = Query(D.AddDays(-5), D.AddDays(5), 2);

        Assert.Equal(new[] { (3L, D.AddDays(1)), (1L, D), (2L, D), (1L, D.AddDays(-1)) },
            all.Select(r => (r.HostId, r.Date.Date)).ToArray());
        Assert.Equal(new[] { 3L, 1L }, top2.Select(r => r.HostId).ToArray());
    }

    [Fact]
    public void 回傳完整紀錄_不是輕量投影()
    {
        Seed(1, D, AiDegradation.ComposeHeadline("磁碟錯誤偏多", 2), 2, true,
            summary: "（AI 回覆經 3 次嘗試仍未通過 JSON 檢查，保留原文供參考）[\n]");

        var record = Assert.Single(Query(D, D));

        Assert.StartsWith("（AI 回覆經 3 次嘗試", record.Summary);
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
        Seed(1, D, "x", 1, true);
        Assert.Empty(Query(D, D, take));
    }

    [Fact]
    public void 區間顛倒_回空()
    {
        Seed(1, D, "x", 1, true);
        Assert.Empty(Query(D.AddDays(1), D));
    }

    [Fact]
    public void 跨主機都回_不套主機授權()
    {
        Seed(1, D, "x", 1, true);
        Seed(2, D, "x", 3, false);
        Assert.Equal(2, Query(D, D).Count);
    }

    // ── 替身與正式實作語意一致 ────────────────────────────────

    [Fact]
    public void 兩個替身與EfStore對同一組資料回相同結果()
    {
        var fakeRepo = new FakeRecordRepository(new FakeHostStore());
        var fakeQuery = new FakeAnalysisRecordQuery();
        // (主機, 日期偏移, 標題, 次數, 待補)
        var specs = new (long Host, int Day, string Headline, int Attempts, bool Pending)[]
        {
            (2, 0, "x", 1, true), (1, 0, "x", 3, false), (3, 1, "x", 2, true),
            (4, 0, "x", 1, false), (5, 9, "x", 1, true), (6, 0, "x", 0, false), (7, 0, Anomaly, 0, false)
        };
        foreach (var s in specs)
        {
            Seed(s.Host, D.AddDays(s.Day), s.Headline, s.Attempts, s.Pending);
            foreach (var target in new Action<DailyAnalysisRecord>[] { fakeRepo.Add, fakeQuery.Add })
            {
                var r = Rec(s.Host, D.AddDays(s.Day), s.Headline);
                r.AiDegradedAttempts = s.Attempts;
                r.AiPending = s.Pending;
                target(r);
            }
        }

        (long, DateTime)[] Keys(IAnalysisRecordQuery q) =>
            q.QueryAiDegradedCases(D, D.AddDays(3), 4).Select(r => (r.HostId, r.Date.Date)).ToArray();

        var expected = Keys(_store);
        Assert.Equal(4, expected.Length);
        Assert.Equal(expected, Keys(fakeRepo));
        Assert.Equal(expected, Keys(fakeQuery));
    }

    // ── SQL 下推：provider 翻譯 ────────────────────────────────

    [Fact]
    public void SQLServerProvider_條件與日期範圍下推到SQL()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var sql = EfAnalysisRecordStore.BuildAiDegradedCaseQuery(ctx, D, D.AddDays(3), 100).ToQueryString();

        AssertPushedDown(sql);
        Assert.Contains("TOP(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SQLiteProvider_條件與日期範圍下推到SQL()
    {
        using var ctx = _fx.NewContext();

        var sql = EfAnalysisRecordStore.BuildAiDegradedCaseQuery(ctx, D, D.AddDays(3), 100).ToQueryString();

        AssertPushedDown(sql);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPushedDown(string sql)
    {
        Assert.Contains("ai_degraded_attempts", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"ai_degraded_attempts\W+>\s", sql);                        // 次數 > 0 與 >= 上限都在 SQL
        Assert.Contains("ai_pending", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("headline", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"headline\W+=\s", sql);                                  // 舊標題相等比較，不是 LIKE／INSTR
        Assert.DoesNotContain("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSTR", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CHARINDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("record_date", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content_json", sql, StringComparison.OrdinalIgnoreCase);   // 完整紀錄，不是輕量投影
    }
}

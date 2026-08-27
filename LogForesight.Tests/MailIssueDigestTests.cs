using System.Diagnostics;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 郵件問題優先摘要（回饋十九輪批次H1）：分區判定（新出現／擴散中／逾期／其他高風險）。
/// 串真的 EfIssueAggregateQuery＋EfAnalysisRecordStore（同 IssueRankingBuilderTests 的既有慣例）
/// ——這裡的核心是「本期 vs 前期」兩次不同期間的 Aggregate 查詢結果比較，用真表寫入不同日期的
/// 紀錄，比用假物件塞兩份手算結果更能證明查詢本身接對了。
/// </summary>
public class MailIssueDigestTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public MailIssueDigestTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private MailIssueDigest Digest()
    {
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlings, _cases, _settings);
        return new MailIssueDigest(aggregates, statusResolver, _settings);
    }

    private static LogIssueSignature Issue(
        string source, int eventId, IssueSeverity severity = IssueSeverity.Low,
        IssueCategory category = IssueCategory.Storage) => new()
        {
            LogName = "System", Source = source, EventId = eventId,
            EntryType = EventLogEntryType.Warning, Category = category, Severity = severity, Count = 1
        };

    private void Add(long hostId, string host, DateTime date, params LogIssueSignature[] issues) =>
        Add(hostId, host, date, RiskLevels.High, issues);

    /// <summary>逾期判定的母體是 ActionableOccurrences（日層級 RiskLevel 為高或中，見
    /// IIssueAggregateQuery.ActionableOccurrences 的既有定義），不是問題自身嚴重度——
    /// 這裡需要能覆寫日風險等級的版本，供逾期分區的測試使用。</summary>
    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList()
        });

    [Fact]
    public void 可見範圍為空集合時回空清單()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, Issue("disk", 153));

        var rows = Digest().Build(d0, d0, Array.Empty<long>());

        Assert.Empty(rows);
    }

    [Fact]
    public void 期間內完全沒有問題時回空清單()
    {
        var rows = Digest().Build(new DateTime(2026, 8, 1), new DateTime(2026, 8, 1), null);

        Assert.Empty(rows);
    }

    /// <summary>新出現＝等長前期完全沒有這個簽章（與 IssueRankingBuilder.IsNew 同一套判定式）</summary>
    [Fact]
    public void 前期無此簽章時分區為新出現()
    {
        var from = new DateTime(2026, 8, 8);
        var to = new DateTime(2026, 8, 8);   // 單日查詢，等長前期窗口＝8/7（同一天），完全沒寫入紀錄
        Add(1, "A", from, Issue("disk", 153, severity: IssueSeverity.Low));

        var row = Assert.Single(Digest().Build(from, to, null));

        Assert.Equal(MailIssueBucket.New, row.Bucket);
        Assert.Equal(0, row.PreviousHostCount);
    }

    /// <summary>擴散中＝本期主機數 &gt; 前期主機數（不要求嚴重度達高，擴散本身就是訊號）。
    /// 等長前期算法同 IssueRankingBuilder.Build：單日查詢（from=to）時，前期窗口是緊鄰的前一天
    /// （同樣單日）。</summary>
    [Fact]
    public void 本期主機數大於前期時分區為擴散中()
    {
        var currentDay = new DateTime(2026, 8, 8);
        var singleDayBefore = currentDay.AddDays(-1);
        Add(1, "A", singleDayBefore, Issue("disk", 153, severity: IssueSeverity.Low));   // 前期：1 台
        Add(1, "A", currentDay, Issue("disk", 153, severity: IssueSeverity.Low));
        Add(2, "B", currentDay, Issue("disk", 153, severity: IssueSeverity.Low));         // 本期：2 台

        var row = Assert.Single(Digest().Build(currentDay, currentDay, null));

        Assert.Equal(MailIssueBucket.Spreading, row.Bucket);
        Assert.Equal(2, row.HostCount);
        Assert.Equal(1, row.PreviousHostCount);
    }

    /// <summary>其他高風險＝不屬於新出現／擴散中／逾期，但嚴重度達高——這是唯一依嚴重度過濾的
    /// 分區（其餘三區的訊號本身已經是「該優先看」的理由，不疊加嚴重度門檻）。</summary>
    [Fact]
    public void 持平且非逾期時_達高嚴重度才進其他高風險()
    {
        var day1 = new DateTime(2026, 8, 1);
        var day2 = new DateTime(2026, 8, 2);
        // 持平（每天都 1 台，不新不擴散）：高嚴重度該進榜，低嚴重度不該
        Add(1, "A", day1, Issue("disk", 153, severity: IssueSeverity.High));
        Add(1, "A", day2, Issue("disk", 153, severity: IssueSeverity.High));
        Add(2, "B", day1, Issue("DCOM", 10016, severity: IssueSeverity.Low));
        Add(2, "B", day2, Issue("DCOM", 10016, severity: IssueSeverity.Low));

        var rows = Digest().Build(day2, day2, null);

        var row = Assert.Single(rows);
        Assert.Equal("disk", row.Source);
        Assert.Equal(MailIssueBucket.OtherHighRisk, row.Bucket);
    }

    /// <summary>逾期＝處理中但預計完成日已過（與 IssueTodoQuery.IsOverdueInProgress 同一口徑）——
    /// 母體要求日風險為高/中（ActionableOccurrences 既有定義），優先序高於新出現/擴散中/其他高風險。</summary>
    [Fact]
    public void 處理中且預計完成日已過時分區為逾期()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        var today = DateTime.Today;
        Add(host.HostId, "A", today, RiskLevels.High, Issue("disk", 153, severity: IssueSeverity.High));
        _issueHandlings.Save(new IssueHandling
        {
            HostName = "A", Date = today,
            IssueKey = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Warning),
            Status = IssueHandlingStatuses.InProgress, DueDate = today.AddDays(-1)   // 預計完成日已過
        });

        var row = Assert.Single(Digest().Build(today, today, null));

        Assert.Equal(MailIssueBucket.Overdue, row.Bucket);
    }

    /// <summary>FormatLine 產出規劃原文明定的格式：{Source}/{EventId}（{Category}）｜影響 N 台
    /// （前期 M）｜{區塊標記}</summary>
    [Fact]
    public void FormatLine輸出規劃原文格式()
    {
        var row = new MailIssueRow("disk", 153, "Storage", 3, 1, MailIssueBucket.Spreading);

        Assert.Equal("disk/153（儲存裝置）｜影響 3 台（前期 1）｜擴散中", row.FormatLine());
    }

    /// <summary>站台設定為 SiteHidden 且只顯示高嚴重度時，郵件問題摘要排除低嚴重度問題</summary>
    [Fact]
    public void 站台設為SiteHidden時_郵件問題摘要排除隱藏嚴重度()
    {
        _settings.Update(s =>
        {
            s.SeverityDisplayMode = "SiteHidden";
            s.UnhandledSeverities = new List<string> { "High" };
        });

        var today = new DateTime(2026, 8, 8);
        Add(1, "A", today,
            Issue("disk", 153, severity: IssueSeverity.High),
            Issue("DCOM", 10016, severity: IssueSeverity.Low));

        var rows = Digest().Build(today, today, null);

        var row = Assert.Single(rows);
        Assert.Equal("disk", row.Source);
        Assert.Equal(153, row.EventId);
    }

    /// <summary>
    /// 日風險等級顯示範圍同樣套用到郵件（<c>VisibleDayRiskLevels</c> 出廠預設是「高,中」）：
    /// 低風險日上的問題在站台任何頁面都看不到，不該只在信裡冒出來。
    /// 這一半行為若無測試釘住，日後被改掉不會有人發現。
    /// </summary>
    [Fact]
    public void 低風險日的問題不進郵件問題摘要()
    {
        var today = new DateTime(2026, 8, 8);
        Add(1, "A", today, RiskLevels.Low, Issue("lowday", 900, severity: IssueSeverity.High));
        Add(2, "B", today, RiskLevels.High, Issue("highday", 901, severity: IssueSeverity.High));

        var rows = Digest().Build(today, today, null);

        var row = Assert.Single(rows);
        Assert.Equal("highday", row.Source);
    }
}

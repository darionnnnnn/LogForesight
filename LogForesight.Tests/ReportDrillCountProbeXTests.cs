using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 80 輪批次 X4 探針：報表「問題類型」熱度格的數字（問題日＝主機×問題×日）與格子下鑽目的地
/// （問題查詢預設「明細」視角，一列是一個主機日）的總筆數，單位不同時兩者會不相等。
/// 下鑽參數與前端 <c>cellDrillUrl</c> 一致：categories＝該類別、riskLevels＝高,中,低、from／to＝該桶起訖。
/// </summary>
public class ReportDrillCountProbeXTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsService _settings = new();
    private static readonly DateTime Day = DateTime.Today.AddDays(-5);

    public ReportDrillCountProbeXTests() => _store = new EfAnalysisRecordStore(_fixture.NewContext, "test");

    public void Dispose() => _fixture.Dispose();

    private static LogIssueSignature Issue(string source, int id) => new()
    {
        LogName = "System", Source = source, EventId = id, EntryType = LogEntryType.Error,
        Severity = IssueSeverity.High, Category = IssueCategory.Storage, Count = 1
    };

    [Fact(Skip = "第 80 輪批次 X 探針成立，待決：類型熱度格是問題日（主機×問題×日），下鑽到問題查詢預設的明細視角總筆數是主機日，同一主機同日多個同類問題時兩者不等（實測 3 對 2），且清單頁沒有說明；修法需改畫面文案或下鑽目的視角，見回報")]
    public void ProbeX4_類型熱度格數字_對上下鑽清單總筆數的單位差異()
    {
        var h1 = TestData.AddHost(_hosts, "H1");
        var h2 = TestData.AddHost(_hosts, "H2");
        // H1 同一天有兩個不同的儲存問題、H2 一個：問題日 3、主機日 2
        _store.Append(new DailyAnalysisRecord
        {
            HostId = h1.HostId, Host = h1.HostName, Date = Day, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { Issue("disk", 7), Issue("ntfs", 55) }
        });
        _store.Append(new DailyAnalysisRecord
        {
            HostId = h2.HostId, Host = h2.HostName, Date = Day, RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { Issue("disk", 7) }
        });

        var repo = new RecordRepository(_store, _hosts, new AlwaysVisibleService(_hosts), _settings);
        var service = new ReportCategoryTrendService(repo, _hosts, new AlwaysVisibleService(_hosts), _settings,
            new SummaryCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));
        var dto = service.Get(Day, Day);
        var row = Assert.Single(dto.Rows);
        var bucket = dto.Buckets.Single(b => row.Cells[dto.Buckets.IndexOf(b)] > 0);
        var cell = row.Cells[dto.Buckets.IndexOf(bucket)];

        // 下鑽目的地：明細視角（RecordsController.Search）依 BuildFilter 組出的條件做 QueryPage
        var listTotal = repo.QueryPage(new RecordQueryFilter
        {
            Categories = new[] { IssueCategory.Storage },
            RiskLevels = new[] { "高", "中", "低" },
            From = DateTime.Parse(bucket.From), To = DateTime.Parse(bucket.To)
        }, 1, 50, null, false).Total;

        // 期望狀態：格內數字與下鑽清單總筆數一致（或畫面明講單位不同）。
        // 取消 Skip 時的實測：cell＝3（問題日）、listTotal＝2（主機日）。
        Assert.Equal(cell, listTotal);
    }
}

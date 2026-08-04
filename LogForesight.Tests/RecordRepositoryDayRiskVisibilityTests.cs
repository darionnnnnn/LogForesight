using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-3-PLAN.md #8：日風險等級顯示設定收斂到 RecordRepository 單一咽喉點——
/// 與問題嚴重度可見性（<see cref="RecordRepositorySeverityVisibilityTests"/>）是不同的兩套
/// 過濾機制，這裡是獨立測試檔（不共用建構子種子資料，避免兩組斷言互相干擾）。
///
/// 特別驗證「交集為空」的短路行為：EfAnalysisRecordStore 對 RiskLevels 的語意是
/// 「非空集合才套用 WHERE 子句」，空集合等同不限制——這與 Hosts 的「空集合＝零結果」
/// 慣例恰好相反，若不在 RecordRepository 明確短路，交集為空時會錯誤地回傳全部風險等級。
/// </summary>
public class RecordRepositoryDayRiskVisibilityTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly WebHost _host;

    public RecordRepositoryDayRiskVisibilityTests()
    {
        _store = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _host = _hosts.Upsert(new WebHost { HostName = "SRV-A" });

        AddRecord(DateTime.Today, "高");
        AddRecord(DateTime.Today.AddDays(-1), "中");
        AddRecord(DateTime.Today.AddDays(-2), "低");
    }

    public void Dispose() => _fixture.Dispose();

    private void AddRecord(DateTime date, string risk) =>
        _store.Append(new DailyAnalysisRecord { HostId = _host.HostId, Host = _host.HostName, Date = date, RiskLevel = risk });

    private RecordRepository CreateRepository(IReadOnlySet<string>? visibleDayRiskLevels) =>
        new(_store, _hosts, new AlwaysVisibleService(_hosts), new FakeSystemSettingsService { VisibleDayRiskLevels = visibleDayRiskLevels });

    [Fact]
    public void 未設定或全勾時等同全顯示不過濾()
    {
        var repository = CreateRepository(visibleDayRiskLevels: null);

        var result = repository.Query(new RecordQueryFilter());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void 只顯示高時_中與低的紀錄整筆消失()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        var result = repository.Query(new RecordQueryFilter());

        var record = Assert.Single(result);
        Assert.Equal("高", record.RiskLevel);
    }

    [Fact]
    public void 顯式篩選被隱藏的等級時_回傳空清單而非全部()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        // 使用者自己在問題查詢頁篩選「中」，但「中」已被全站顯示設定藏起來
        var result = repository.Query(new RecordQueryFilter { RiskLevels = new[] { "中" } });

        Assert.Empty(result);
    }

    [Fact]
    public void 顯式篩選高中且顯示設定只允許高時_取交集只回高()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        var result = repository.Query(new RecordQueryFilter { RiskLevels = new[] { "高", "中" } });

        var record = Assert.Single(result);
        Assert.Equal("高", record.RiskLevel);
    }

    [Fact]
    public void QueryPage總筆數與過濾後一致_不是分頁前的原始筆數()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        var page = repository.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10);

        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
    }

    [Fact]
    public void QueryPage交集為空時總筆數為零_不會誤回全部()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        var page = repository.QueryPage(new RecordQueryFilter { RiskLevels = new[] { "中" } }, page: 1, pageSize: 10);

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    /// <summary>豁免（docs/archive/FEEDBACK-3-PLAN.md #8）：主機詳情頁時間軸專用，applyDayRiskVisibility=false
    /// 時完整看到證據，不受顯示設定影響——避免時間軸把「有分析但被藏」誤顯示成「無分析紀錄」</summary>
    [Fact]
    public void applyDayRiskVisibility為false時完全不過濾()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        var result = repository.Query(new RecordQueryFilter(), applyDayRiskVisibility: false);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void GetOne不受日風險等級顯示設定影響_風險日詳情直連仍可看()
    {
        var repository = CreateRepository(new HashSet<string> { "高" });

        // 中風險日已被顯示設定藏起來，但直連該日詳情仍應看得到完整證據
        var record = repository.GetOne(_host.HostId, DateTime.Today.AddDays(-1));

        Assert.NotNull(record);
        Assert.Equal("中", record!.RiskLevel);
    }
}

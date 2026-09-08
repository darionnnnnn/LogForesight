using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG finding 登錄簿（docs/PRTG-SPEC.md §9）與寫入路徑就地追加的行為。
/// </summary>
public class PrtgFindingsRegistryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static LogIssueSignature Finding(long sensorObjid, string code = "down") =>
        PrtgFindingMapper.ToSignature(new PrtgFinding(1001, sensorObjid, code, "Sensor down", 60), DateTime.Today);

    private static IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>> ByHost(
        long hostId, params LogIssueSignature[] findings) =>
        new Dictionary<long, IReadOnlyList<LogIssueSignature>> { [hostId] = findings };

    [Fact]
    public void 未發佈時未就緒且查詢回空清單()
    {
        var registry = new PrtgFindingsRegistry();

        Assert.False(registry.IsReady);
        Assert.Empty(registry.For(101));
        Assert.Empty(registry.PublishedHostIds());
    }

    [Fact]
    public void 發佈空集合也算就緒()
    {
        // 「算不出東西」與「還沒算完」必須分得出來：PRTG 停用、規則評估失敗、
        // 規則庫尚無 PRTG 規則都會發佈空集合，AI 分析排程據此立刻放行。
        var registry = new PrtgFindingsRegistry();
        registry.Publish(new Dictionary<long, IReadOnlyList<LogIssueSignature>>());

        Assert.True(registry.IsReady);
        Assert.Empty(registry.For(101));
    }

    [Fact]
    public void 發佈後可依主機取得finding()
    {
        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001), Finding(2002, "flapping")));

        Assert.True(registry.IsReady);
        Assert.Equal(2, registry.For(101).Count);
        Assert.Empty(registry.For(999));
        Assert.Equal(new[] { 101L }, registry.PublishedHostIds());
    }

    [Fact]
    public void Empty是已就緒且無finding的共用實例()
    {
        Assert.True(PrtgFindingsRegistry.Empty.IsReady);
        Assert.Empty(PrtgFindingsRegistry.Empty.For(101));
    }

    [Fact]
    public void AttachPrtgFindings_同時併入記憶體紀錄與資料庫()
    {
        // 記憶體那份是關鍵：問題案件掛接吃的是 record.TopIssues，
        // 只寫資料庫的話 PRTG finding 永遠進不了案件與處理狀態鏈。
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        var added = HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101);

        Assert.Equal(1, added);
        Assert.Equal("prtg:down:2001", Assert.Single(record.TopIssues).EventKey);

        var persisted = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal("prtg:down:2001", Assert.Single(persisted.TopIssues).EventKey);
    }

    [Fact]
    public void AttachPrtgFindings_未就緒時整段短路()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = DateTime.Today,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);

        var added = HostDayPostProcessor.AttachPrtgFindings(new PrtgFindingsRegistry(), store, record, 101);

        Assert.Equal(0, added);
        Assert.Empty(record.TopIssues);
    }

    [Fact]
    public void AttachPrtgFindings_重複呼叫依EventKey去重()
    {
        // 補追加階段與寫入路徑都可能對同一個主機日呼叫（重跑模式覆寫後也會再走一次），
        // 不去重就會在同一天的 TopIssues 裡長出重複列。
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        Assert.Equal(1, HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101));
        Assert.Equal(0, HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101));

        Assert.Single(record.TopIssues);
        Assert.Single(Assert.Single(store.ReadRecent(date, 1)).TopIssues);
    }

    [Fact]
    public void AttachPrtgFindings_該主機無finding時不碰紀錄()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = new DailyAnalysisRecord
        {
            HostId = 999,
            Host = "SRV-OTHER",
            Date = DateTime.Today,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        Assert.Equal(0, HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 999));
        Assert.Empty(record.TopIssues);
    }
}

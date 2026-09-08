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
    // ── 批次C：風險單向上調與 AI 待補重標 ──────────────────────────────

    private DailyAnalysisRecord Seed(EfAnalysisRecordStore store, string risk, bool aiAnalyzed = false, bool pruned = false)
    {
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = DateTime.Today,
            RiskLevel = risk,
            AiAnalyzed = aiAnalyzed,
            DetailPruned = pruned,
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);
        return record;
    }

    [Fact]
    public void 風險上調_down規則把低風險日拉成高並記錄依據()
    {
        // down 在 PrtgRuleCatalog 是 High + ElevatesDayRisk。這個旗標過去是死值——
        // 追加發生在風險計算之後，從來沒有生效過。
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Equal("prtg:down", record.RiskBasis);
        Assert.True(record.AiPending);

        var persisted = Assert.Single(store.ReadRecent(DateTime.Today, 1));
        Assert.Equal(RiskLevels.High, persisted.RiskLevel);
        Assert.True(persisted.AiPending);
    }

    /// <summary>
    /// Medium 嚴重度的 PRTG 規則（flapping／warning／silent）不拉風險——與事件層同語意：
    /// `ComputeRuleBasedRisk` 也只有 High 嚴重度才把日風險拉到中。
    /// 只有 down（High ＋ ElevatesDayRisk）會拉高。
    /// </summary>
    [Fact]
    public void 風險上調_中嚴重度規則不改變風險等級()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2002, "flapping")));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.False(record.AiPending);
        // finding 本身仍然併入（問題排行、處理狀態鏈都看得到），只是不改變風險等級
        Assert.Single(record.TopIssues);
    }

    [Fact]
    public void 風險上調_絕不壓低既有等級()
    {
        // PRTG 只是輔助訊號，看不到事件層的證據——既有高風險不得被 PRTG 的中風險壓下來。
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.High);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2002, "flapping")));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(RiskLevels.High, record.RiskLevel);
        var persisted = Assert.Single(store.ReadRecent(DateTime.Today, 1));
        Assert.Equal(RiskLevels.High, persisted.RiskLevel);
    }

    [Fact]
    public void 風險上調_AI未設定時不標待補()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: false);

        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.False(record.AiPending);
    }

    [Fact]
    public void 風險上調_已完成AI的紀錄只升風險不重標待補()
    {
        // 重標會讓已定案的內容被無謂重跑。
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low, aiAnalyzed: true);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.False(record.AiPending);
    }

    [Fact]
    public void 風險上調_重複追加不重複改動()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(ByHost(101, Finding(2001)));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);
        record.AiPending = false;   // 模擬 AI 已補寫完成
        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        // 第二次因 EventKey 去重整段短路，不會再把已完成的紀錄標回待補
        Assert.False(record.AiPending);
        Assert.Single(record.TopIssues);
    }
}

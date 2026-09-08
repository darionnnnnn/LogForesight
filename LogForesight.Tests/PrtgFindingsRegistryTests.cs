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

    /// <summary>發佈今天這批 finding（多數測試的目標日就是今天）。</summary>
    private static void PublishToday(PrtgFindingsRegistry registry, IReadOnlyDictionary<long, IReadOnlyList<LogIssueSignature>> byHost) =>
        registry.Publish(DateTime.Today, byHost);

    [Fact]
    public void 未發佈時未就緒且查詢回空清單()
    {
        var registry = new PrtgFindingsRegistry();

        Assert.False(registry.IsReady);
        Assert.Empty(registry.For(101, DateTime.Today));
    }

    [Fact]
    public void 發佈空集合也算就緒()
    {
        // 「算不出東西」與「還沒算完」必須分得出來：PRTG 停用、規則評估失敗、
        // 規則庫尚無 PRTG 規則都會發佈空集合，AI 分析排程據此立刻放行。
        var registry = new PrtgFindingsRegistry();
        registry.Publish(DateTime.Today, new Dictionary<long, IReadOnlyList<LogIssueSignature>>());

        Assert.True(registry.IsReady);
        Assert.Empty(registry.For(101, DateTime.Today));
    }

    [Fact]
    public void 發佈後可依主機取得finding()
    {
        var registry = new PrtgFindingsRegistry();
        PublishToday(registry, ByHost(101, Finding(2001), Finding(2002, "flapping")));

        Assert.True(registry.IsReady);
        Assert.Equal(2, registry.For(101, DateTime.Today).Count);
        Assert.Empty(registry.For(999, DateTime.Today));
    }

    [Fact]
    public void Empty是已就緒且無finding的共用實例()
    {
        Assert.True(PrtgFindingsRegistry.Empty.IsReady);
        Assert.Empty(PrtgFindingsRegistry.Empty.For(101, DateTime.Today));
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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2002, "flapping")));

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
        PublishToday(registry, ByHost(101, Finding(2002, "flapping")));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

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
        PublishToday(registry, ByHost(101, Finding(2001)));

        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);
        record.AiPending = false;   // 模擬 AI 已補寫完成
        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        // 第二次因 EventKey 去重整段短路，不會再把已完成的紀錄標回待補
        Assert.False(record.AiPending);
        Assert.Single(record.TopIssues);
    }
    // ── 終檢補強：日期維度與資料庫端早退 ────────────────────────────

    /// <summary>
    /// 登錄簿只裝「PRTG 評估的那一天」的 finding，但兩條寫入路徑都在**逐日迴圈**裡呼叫追加。
    /// 少了日期比對，站台停機三天後開跑，昨天的 down 會被掛到三天份的紀錄上，
    /// 而 EventKey（prtg:{code}:{objid}）不含日期、去重完全生效，重跑也不會自癒。
    /// </summary>
    [Fact]
    public void 登錄簿只對發佈的那一天回傳finding()
    {
        var registry = new PrtgFindingsRegistry();
        registry.Publish(DateTime.Today, ByHost(101, Finding(2001)));

        Assert.Single(registry.For(101, DateTime.Today));
        Assert.Empty(registry.For(101, DateTime.Today.AddDays(-1)));
        Assert.Empty(registry.For(101, DateTime.Today.AddDays(-3)));
        Assert.Empty(registry.For(101, DateTime.Today.AddDays(1)));
    }

    [Fact]
    public void AttachPrtgFindings_回補其他日期時不追加()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var olderDay = DateTime.Today.AddDays(-3);
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = olderDay,
            RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(DateTime.Today, ByHost(101, Finding(2001)));   // 只評估了今天

        var added = HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(0, added);
        Assert.Empty(record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.False(record.AiPending);
    }

    /// <summary>
    /// 詳情已被保留期精簡的紀錄，資料庫端整段早退不寫任何東西——
    /// 記憶體端也不能改，否則呼叫端拿去組執行摘要的 RiskLevel 會是「高」、資料庫裡卻還是「低」。
    /// </summary>
    [Fact]
    public void AttachPrtgFindings_已精簡的紀錄記憶體與資料庫都不動()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");

        // 走真實的保留期精簡（Append 不寫 detail_pruned，那個旗標只由精簡程序設定）
        var oldDay = DateTime.Today.AddDays(-10);
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = oldDay,
            RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(record);
        Assert.True(store.PruneDetails(detailRetentionDays: 5) > 0);

        var registry = new PrtgFindingsRegistry();
        registry.Publish(oldDay, ByHost(101, Finding(2001)));

        var added = HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(0, added);
        Assert.Empty(record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.False(record.AiPending);
    }

    /// <summary>
    /// 當天沒有紀錄的主機：資料庫端回 false，記憶體端同樣不得改
    /// （硬造一筆只有 PRTG finding 的紀錄會讓「未回報主機」等既有統計失真）。
    /// </summary>
    [Fact]
    public void AttachPrtgFindings_當天無紀錄時記憶體也不動()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        // 刻意不 Append：資料庫裡沒有這個主機日
        var record = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = DateTime.Today,
            RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>()
        };

        var registry = new PrtgFindingsRegistry();
        PublishToday(registry, ByHost(101, Finding(2001)));

        Assert.Equal(0, HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true));
        Assert.Empty(record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    /// <summary>發佈後內容不再變動是登錄簿的前提（NetIQ 是多執行緒平行迴圈，For 會被並行呼叫）。</summary>
    [Fact]
    public void 二次發佈以最後一次為準且日期跟著換()
    {
        var registry = new PrtgFindingsRegistry();
        registry.Publish(DateTime.Today.AddDays(-1), ByHost(101, Finding(2001)));
        Assert.Single(registry.For(101, DateTime.Today.AddDays(-1)));

        registry.Publish(DateTime.Today, ByHost(102, Finding(2002, "flapping")));

        Assert.Empty(registry.For(101, DateTime.Today.AddDays(-1)));
        Assert.Empty(registry.For(101, DateTime.Today));
        Assert.Single(registry.For(102, DateTime.Today));
    }
    // ── 體檢輪：並行競態與 AI 回寫 ─────────────────────────────────

    /// <summary>
    /// AI 判讀期間 PRTG finding 才追加並上調了列上的等級；AI 回寫若無條件覆蓋，
    /// 上調會被蓋回去。AI 只能往上拉（DETECTION-SPEC），列上較高的等級與它的依據要保留。
    /// </summary>
    [Fact]
    public void AI回寫不得蓋掉PRTG上調的風險()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        PublishToday(registry, ByHost(101, Finding(2001)));
        HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);
        Assert.Equal(RiskLevels.High, Assert.Single(store.ReadRecent(DateTime.Today, 1)).RiskLevel);

        // AI 撿到這筆時算出來的是「低」（它撿的是上調前的快照）
        store.AttachAiResult(DateTime.Today, new AiOutcome(
            Headline: "一切正常", Summary: "無異常", TrendAssessment: "平穩", Action: "無",
            RiskLevel: RiskLevels.Low, RiskBasis: null, AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>()));

        var persisted = Assert.Single(store.ReadRecent(DateTime.Today, 1));
        Assert.Equal(RiskLevels.High, persisted.RiskLevel);
        Assert.Equal("prtg:down", persisted.RiskBasis);
        Assert.True(persisted.AiAnalyzed);
        Assert.False(persisted.AiPending);
    }

    /// <summary>AI 判定比列上高時照常寫入（只升不降的另一半）。</summary>
    [Fact]
    public void AI回寫比列上高時照常上調()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        Seed(store, RiskLevels.Low);

        store.AttachAiResult(DateTime.Today, new AiOutcome(
            Headline: "x", Summary: "x", TrendAssessment: "x", Action: "x",
            RiskLevel: RiskLevels.High, RiskBasis: "ai_raise", AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>()));

        var persisted = Assert.Single(store.ReadRecent(DateTime.Today, 1));
        Assert.Equal(RiskLevels.High, persisted.RiskLevel);
        Assert.Equal("ai_raise", persisted.RiskBasis);
    }

    /// <summary>
    /// 補追加與寫入路徑對同一主機日賽跑：補追加先寫進資料庫時，寫入路徑的 store 呼叫會因
    /// 去重回 false——但記憶體那份仍要併入（登錄簿記得「已追加」），執行摘要才與資料庫一致。
    /// </summary>
    [Fact]
    public void AttachPrtgFindings_補追加先到時寫入路徑仍併入記憶體()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var record = Seed(store, RiskLevels.Low);

        var registry = new PrtgFindingsRegistry();
        var findings = ByHost(101, Finding(2001));
        PublishToday(registry, findings);

        // 模擬補追加路徑先走（同 PrtgDailyPipeline 的呼叫形狀）
        Assert.True(registry.AttachExclusive(101, DateTime.Today,
            () => store.AttachPrtgFindings(101, DateTime.Today, findings[101], aiConfigured: true)));
        Assert.True(registry.WasAttached(101, DateTime.Today));

        // 寫入路徑隨後對「未含 finding 的記憶體紀錄」呼叫
        var added = HostDayPostProcessor.AttachPrtgFindings(registry, store, record, 101, aiConfigured: true);

        Assert.Equal(1, added);
        Assert.Single(record.TopIssues);
        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Single(Assert.Single(store.ReadRecent(DateTime.Today, 1)).TopIssues);   // 資料庫仍只有一列
    }

    [Fact]
    public void AttachExclusive_只在真的寫入時標記已追加()
    {
        var registry = new PrtgFindingsRegistry();
        Assert.False(registry.AttachExclusive(101, DateTime.Today, () => false));
        Assert.False(registry.WasAttached(101, DateTime.Today));

        Assert.True(registry.AttachExclusive(101, DateTime.Today, () => true));
        Assert.True(registry.WasAttached(101, DateTime.Today));
        Assert.False(registry.WasAttached(101, DateTime.Today.AddDays(-1)));
        Assert.False(registry.WasAttached(102, DateTime.Today));
    }
}

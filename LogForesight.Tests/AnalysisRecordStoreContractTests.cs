using Xunit;
using System.Text.Json;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordStore"/> 的測試（docs/DB-SPEC.md 一致性機制 #3）。
///
/// 尤其是 <see cref="IAnalysisRecordReader.ReadRecent"/> 的錨定窗語意：它決定趨勢基準的
/// 計算範圍，換一種算法就會讓同一天的分析得出不同的風險判定。
/// </summary>
public class AnalysisRecordStoreContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private IAnalysisRecordStore CreateStore() =>
        new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly KnownIssueRule SeedDownRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-down-availability");
    private static readonly KnownIssueRule SeedWarnRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-warning");
    private static readonly KnownIssueRule SeedHardwareWarnRule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-warning-hardware");
    private static readonly IReadOnlySet<string> NoPatternIds = new HashSet<string>();

    private static DailyAnalysisRecord Record(DateTime date, string risk = "低") => new()
    {
        Date = date,
        RiskLevel = risk,
        Headline = $"{date:MM-dd}"
    };

    private static string ExpectedAttachedAiReportInput(DailyAnalysisRecord parent, AiOutcome outcome,
        bool includeAddendum = true)
    {
        var final = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(
            System.Text.Json.JsonSerializer.Serialize(parent))!;
        final.Headline = outcome.Headline;
        final.Summary = outcome.Summary;
        final.TrendAssessment = outcome.TrendAssessment;
        final.Action = outcome.Action;
        final.RiskLevel = outcome.RiskLevel;
        final.RiskBasis = outcome.RiskBasis;
        final.AiAnalyzed = outcome.AiAnalyzed;
        final.AiPending = false;
        final.ScreenedTailCount = outcome.ScreenedTailCount;
        final.ScreeningNotes = outcome.ScreeningNotes;
        if (includeAddendum && outcome.UncoveredChecksAddendum is { Count: > 0 })
            foreach (var note in outcome.UncoveredChecksAddendum)
                if (!final.UncoveredChecks.Contains(note, StringComparer.Ordinal)) final.UncoveredChecks.Add(note);
        return HostDayWorkflowFingerprint.ForReportInput(final);
    }

    [Fact]
    public void ReportInputFingerprintTracksRenderedNarrativeWithoutChangingParentDecisionFingerprint()
    {
        var parent = Record(DateTime.Today, RiskLevels.High);
        parent.Summary = "original summary";
        var decisionFingerprint = HostDayWorkflowFingerprint.ForRecord(parent);
        var reportFingerprint = HostDayWorkflowFingerprint.ForReportInput(parent);

        parent.RiskReportPending = true;
        Assert.Equal(decisionFingerprint, HostDayWorkflowFingerprint.ForRecord(parent));
        Assert.Equal(reportFingerprint, HostDayWorkflowFingerprint.ForReportInput(parent));
        parent.RiskReportPending = false;

        parent.Summary = "changed summary";

        Assert.Equal(decisionFingerprint, HostDayWorkflowFingerprint.ForRecord(parent));
        Assert.NotEqual(reportFingerprint, HostDayWorkflowFingerprint.ForReportInput(parent));
    }

    // ── ReadRecent：錨定日期窗 ────────────────────────────────────────────────

    [Fact]
    public void ReadRecent_取錨定日往回N天()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-2)));

        Assert.Equal(2, store.ReadRecent(anchor, 3).Count);
    }

    [Fact]
    public void WorkflowRecoveryPage_usesBoundedSqlPrefixAndAdvancesPastOversizedRow()
    {
        var query = (EfAnalysisRecordStore)CreateStore();
        var day = new DateTime(2026, 10, 5);
        var oversized = new DailyAnalysisRecord
        {
            HostId = 42, Host = "large-row", Date = day, RiskLevel = RiskLevels.Low,
            Headline = new string('x', WorkflowRecoveryPage.MaximumPayloadBytes + 256)
        };
        query.Append(oversized);
        var normal = new DailyAnalysisRecord
        { HostId = 43, Host = "small-row", Date = day, RiskLevel = RiskLevels.Low, Headline = "bounded" };
        query.Append(normal);

        var page = query.QueryWorkflowRecoveryPage(day, day, 0, WorkflowRecoveryPage.MaximumRows);

        Assert.Equal(2, page.RawCount);
        Assert.Equal(normal.RecordId, page.NextRecordId);
        Assert.Single(page.Records);
        Assert.Equal(normal.RecordId, page.Records[0].RecordId);
        Assert.True(page.CapturedWriteRevisions!.ContainsKey(normal.RecordId));
        var waiting = Assert.Single(page.WaitingHostDays);
        Assert.Equal(oversized.RecordId, waiting.RecordId);
        Assert.Equal("recovery-payload-over-limit", waiting.ReasonCode);
        using (var context = _fx.NewContext())
            Assert.Equal(context.DailyRecords.Single(row => row.RecordId == oversized.RecordId).WriteRevision,
                waiting.CapturedWriteRevision);
        Assert.Equal(DateTimeKind.Utc, waiting.CapturedAtUtc.Kind);
        Assert.InRange(page.PayloadBytesRead, 0, (long)WorkflowRecoveryPage.MaximumRows *
            WorkflowRecoveryPage.MaximumPayloadPrefixCharacters * 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => query.QueryWorkflowRecoveryPage(day, day, 0,
            WorkflowRecoveryPage.MaximumRows + 1));
    }

    /// <summary>
    /// 窗外的較舊紀錄不得補位。若實作寫成「最近 N 筆」，缺漏日會讓更舊的紀錄墊進來，
    /// 14 日平均的分母就不誠實了——原本該判「首次出現」的簽章會被誤判成「重複發生」。
    /// </summary>
    [Fact]
    public void ReadRecent_窗外較舊紀錄不回傳()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-30)));

        var result = store.ReadRecent(anchor, 14);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-1), result[0].Date.Date);
    }

    /// <summary>
    /// **中間缺漏日 bug 的迴歸測試**：回補流程會分析「已經有後續紀錄」的日子
    /// （某天執行中斷、之後幾天照常執行）。錨定日之後的紀錄若混進基準，
    /// 等於拿未來的資料判斷過去那一天——趨勢分析最不該發生的事。
    /// </summary>
    [Fact]
    public void ReadRecent_錨定日之後的紀錄不回傳()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(1)));
        store.Append(Record(anchor.AddDays(2)));

        var result = store.ReadRecent(anchor, 14);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-1), result[0].Date.Date);
    }

    /// <summary>體檢在當日分析寫入之後執行，窗口必須含當天剛寫入的那筆</summary>
    [Fact]
    public void ReadRecent_含錨定當日()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor));

        Assert.Single(store.ReadRecent(anchor, 7));
    }

    [Fact]
    public void ReadRecent_依日期升冪()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-1)));
        store.Append(Record(anchor.AddDays(-3)));
        store.Append(Record(anchor.AddDays(-2)));

        var dates = store.ReadRecent(anchor, 7).Select(r => r.Date.Date).ToList();

        Assert.Equal(dates.OrderBy(d => d), dates);
    }

    /// <summary>窗長 N 含錨定日本身，所以 anchor-N 那天正好在窗外</summary>
    [Fact]
    public void ReadRecent_窗長邊界()
    {
        var store = CreateStore();
        var anchor = new DateTime(2026, 7, 21);
        store.Append(Record(anchor.AddDays(-6)));
        store.Append(Record(anchor.AddDays(-7)));

        var result = store.ReadRecent(anchor, 7);

        Assert.Single(result);
        Assert.Equal(anchor.AddDays(-6), result[0].Date.Date);
    }

    // ── 存在性判定 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 「有沒有任何歷史」是獨立的問題，不能用 ReadRecent 的窗口間接表達——
    /// 錨定窗下 ReadRecent(today, 1) 問的是「今天有沒有紀錄」，語意完全不同。
    /// </summary>
    [Fact]
    public void HasAnyRecord_空為false_有紀錄為true()
    {
        var store = CreateStore();
        Assert.False(store.HasAnyRecord());

        store.Append(Record(DateTime.Today.AddDays(-30)));
        Assert.True(store.HasAnyRecord());
    }

    [Fact]
    public void HasRecord_同日不同時刻視為同一天()
    {
        var store = CreateStore();
        store.Append(Record(new DateTime(2026, 7, 20, 3, 15, 0)));

        Assert.True(store.HasRecord(new DateTime(2026, 7, 20, 23, 59, 0)));
        Assert.False(store.HasRecord(new DateTime(2026, 7, 21)));
    }

    // ── Prune ────────────────────────────────────────────────────────────────

    /// <summary>邊界日保留：cutoff 當天還在保留期內，不該被誤刪</summary>
    [Fact]
    public void Prune_保留天數邊界_cutoff當天保留()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-90)));
        store.Append(Record(DateTime.Today.AddDays(-91)));

        var removed = store.Prune(90);

        Assert.Equal(1, removed);
        Assert.Equal(DateTime.Today.AddDays(-90), store.ReadRecent(DateTime.Today, 365).Single().Date.Date);
    }

    [Fact]
    public void Prune_無可刪除時回0且不動檔案()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today));

        Assert.Equal(0, store.Prune(90));
        Assert.Single(store.ReadRecent(DateTime.Today, 7));
    }

    // ── PruneDetails ─────────────────────────────────────────────────────────

    [Fact]
    public void PruneDetails_超過保留期_清除詳情但保留統計欄與子列()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        var date = DateTime.Today.AddDays(-130);
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            RiskLevel = "高",
            Headline = "測試標題",
            ErrorCount = 5,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, Count = 7, Severity = IssueSeverity.Critical }
            }
        });

        // Act
        var pruned = store.PruneDetails(120);

        // Assert
        Assert.Equal(1, pruned);
        using var ctx = _fx.NewContext();
        var row = ctx.DailyRecords.Single();
        Assert.True(row.DetailPruned);
        Assert.Equal(string.Empty, row.ContentJson);
        Assert.Equal("高", row.RiskLevel);
        Assert.Equal("測試標題", row.Headline);
        Assert.Equal(5, row.ErrorCount);

        var topIssue = Assert.Single(ctx.TopIssues.ToList());
        Assert.Equal("disk", topIssue.SourceName);
        Assert.Equal(153, topIssue.EventId);
    }

    [Fact]
    public void PruneDetails_保留期內的列不受影響()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-10)));

        var pruned = store.PruneDetails(120);

        Assert.Equal(0, pruned);
        using var ctx = _fx.NewContext();
        var row = ctx.DailyRecords.Single();
        Assert.False(row.DetailPruned);
        Assert.NotEqual(string.Empty, row.ContentJson);
    }

    [Fact]
    public void PruneDetails_冪等_連續執行第二次回傳0()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-130)));

        var pruned1 = store.PruneDetails(120);
        var pruned2 = store.PruneDetails(120);

        Assert.Equal(1, pruned1);
        Assert.Equal(0, pruned2);
    }

    [Fact]
    public void PruneDetails_讀取已被清除的列不拋例外_回傳DetailPruned為true且標頭資訊正確()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        var date = DateTime.Today.AddDays(-130);
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            HostId = 99,
            Host = "test-host",
            RiskLevel = "Medium",
            Headline = "已被清除的紀錄"
        });

        store.PruneDetails(120);

        var record = store.ReadRecent(DateTime.Today, 150).FirstOrDefault(r => r.Date == date);
        Assert.NotNull(record);
        Assert.True(record.DetailPruned);
        Assert.Equal(99, record.HostId);
        Assert.Equal("test-host", record.Host);
        Assert.Equal(date, record.Date);
        Assert.Equal("Medium", record.RiskLevel);

        var one = store.GetOne(new[] { new HostKey { HostId = 99, HostName = "test-host" } }, date);
        Assert.NotNull(one);
        Assert.True(one.DetailPruned);
        Assert.Equal(99, one.HostId);
        Assert.Equal("test-host", one.Host);
        Assert.Equal(date, one.Date);
    }

    [Fact]
    public void PruneDetails_未被清除的列讀出來DetailPruned為false且內容與清理前相同()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        var date = DateTime.Today.AddDays(-10);
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            HostId = 99,
            Host = "test-host",
            RiskLevel = "High",
            Headline = "未過期"
        });

        store.PruneDetails(120);

        var record = store.ReadRecent(DateTime.Today, 15).Single();
        Assert.False(record.DetailPruned);
        Assert.Equal("High", record.RiskLevel);
        Assert.Equal("未過期", record.Headline);
    }

    [Fact]
    public void PruneDetails_QueryLightweight回傳的DetailPruned正確()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        var oldDate = DateTime.Today.AddDays(-130);
        var newDate = DateTime.Today.AddDays(-10);
        store.Append(Record(oldDate));
        store.Append(Record(newDate));

        store.PruneDetails(120);

        var results = store.QueryLightweight(new RecordQueryFilter());
        var oldRecord = results.Single(r => r.Date == oldDate);
        var newRecord = results.Single(r => r.Date == newDate);

        Assert.True(oldRecord.DetailPruned);
        Assert.False(newRecord.DetailPruned);
    }

    [Fact]
    public void CountPrunableRecords_回傳正確數值且不改變任何資料()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-130)));
        store.Append(Record(DateTime.Today.AddDays(-131)));
        store.Append(Record(DateTime.Today.AddDays(-10)));

        using var ctxPre = _fx.NewContext();
        var rowsPre = ctxPre.DailyRecords.Count();

        var count = store.CountPrunableRecords(120);

        Assert.Equal(2, count);
        using var ctxPost = _fx.NewContext();
        var rowsPost = ctxPost.DailyRecords.Count();
        Assert.Equal(rowsPre, rowsPost);
    }

    [Fact]
    public void CountPrunableDetails_回傳筆數正確且不改變資料()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-130)));
        store.Append(Record(DateTime.Today.AddDays(-131)));
        store.Append(Record(DateTime.Today.AddDays(-10)));

        using var ctxPre = _fx.NewContext();
        var originalContent = ctxPre.DailyRecords.Where(r => r.RecordDate < DateTime.Today.AddDays(-120)).Select(r => r.ContentJson).ToList();

        var count = store.CountPrunableDetails(120);

        Assert.Equal(2, count);

        using var ctxPost = _fx.NewContext();
        var currentContent = ctxPost.DailyRecords.Where(r => r.RecordDate < DateTime.Today.AddDays(-120)).Select(r => r.ContentJson).ToList();
        Assert.Equal(originalContent, currentContent);
    }

    [Fact]
    public void PruneDetails_分批執行_不超過上限()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        store.Append(Record(DateTime.Today.AddDays(-130)));
        store.Append(Record(DateTime.Today.AddDays(-131)));
        store.Append(Record(DateTime.Today.AddDays(-132)));

        // maxRows = 2, batchSize = 1
        var pruned1 = store.PruneDetails(120, 2, 1);
        Assert.Equal(2, pruned1);

        var countLeft = store.CountPrunableDetails(120);
        Assert.Equal(1, countLeft);

        var pruned2 = store.PruneDetails(120, 2, 1);
        Assert.Equal(1, pruned2);
    }

    [Fact]
    public void PruneDetails_分多批次執行_完整清除所有符合條件的列()
    {
        var store = (EfAnalysisRecordStore)CreateStore();
        for (int i = 0; i < 5; i++)
        {
            store.Append(Record(DateTime.Today.AddDays(-130 - i)));
        }

        // maxRows = 10, batchSize = 2 (應該會跑 3 個 batch: 2, 2, 1)
        var pruned = store.PruneDetails(120, 10, 2);
        Assert.Equal(5, pruned);

        var remaining = store.CountPrunableDetails(120);
        Assert.Equal(0, remaining);
    }

    // ── 週體檢附掛 ───────────────────────────────────────────────────────────

    [Fact]
    public void 週體檢附掛_更新既有當日紀錄且LastWeeklyCheckupDate可讀回()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        store.Append(Record(date));

        Assert.Null(store.LastWeeklyCheckupDate());

        store.AttachWeeklyCheckup(date, new WeeklyCheckupResult
        {
            CheckupDate = date,
            HasFindings = true,
            Conclusion = "本週磁碟錯誤緩慢上升"
        });

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.NotNull(read.WeeklyCheckup);
        Assert.True(read.WeeklyCheckup!.HasFindings);
        Assert.Equal("本週磁碟錯誤緩慢上升", read.WeeklyCheckup.Conclusion);
        Assert.Equal(date.Date, store.LastWeeklyCheckupDate());
    }

    /// <summary>
    /// 找不到對應日期時「安靜略過」是**契約**不是實作巧合：呼叫端在分析寫入之後才附掛，
    /// 理論上必定找得到；真的找不到時中斷整個批次比留下一筆 WARN 更糟。
    /// </summary>
    [Fact]
    public void AttachWeeklyCheckup_日期不存在_不擲例外且不新增紀錄()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today));

        store.AttachWeeklyCheckup(DateTime.Today.AddDays(-5), new WeeklyCheckupResult
        {
            CheckupDate = DateTime.Today.AddDays(-5),
            Conclusion = "不該被寫入"
        });

        Assert.Single(store.ReadRecent(DateTime.Today, 30));
        Assert.Null(store.LastWeeklyCheckupDate());
    }

    // ── AttachAiResult：AI 段結果附掛（docs/archive/FEEDBACK-12-PLAN.md §3.5）─────────────

    [Fact]
    public void AttachAiResult_覆寫暫代欄位並清除AiPending()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            RiskLevel = "低",
            Headline = "（統計已完成，AI 分析排隊中）",
            Summary = "（統計已完成，AI 分析排隊中）",
            AiPending = true,
            AiAnalyzed = false
        });

        store.AttachAiResult(date, new AiOutcome(
            Headline: "磁碟即將故障",
            Summary: "偵測到大量磁碟I/O錯誤。",
            TrendAssessment: "持續惡化",
            Action: "今天就要處理",
            RiskLevel: "高",
            RiskBasis: "ai_raise",
            AiAnalyzed: true,
            ScreenedTailCount: 0,
            ScreeningNotes: new List<string>(),
            ReportFile: "export/x.txt",
            DeepDives: new List<CategoryDeepDive>()));

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal("磁碟即將故障", read.Headline);
        Assert.Equal("偵測到大量磁碟I/O錯誤。", read.Summary);
        Assert.Equal("持續惡化", read.TrendAssessment);
        Assert.Equal("今天就要處理", read.Action);
        Assert.Equal("高", read.RiskLevel);
        Assert.Equal("ai_raise", read.RiskBasis);
        Assert.True(read.AiAnalyzed);
        Assert.False(read.AiPending);
        Assert.Equal("export/x.txt", read.ReportFile);
    }

    /// <summary>
    /// 欄位漂移防護：AttachAiResult 把風險往上拉（ai_raise）時，抽出欄
    /// （lf_daily_records.risk_level）必須跟著同步——清單/排行/儀表板的篩選查詢讀的是抽出欄，
    /// 只改 ContentJson 不改抽出欄的話，這裡會查不到剛被拉高風險的那筆紀錄，
    /// 但 ReadRecent（反序列化 ContentJson）卻會顯示正確的「高」風險，兩者對不上。
    /// </summary>
    [Fact]
    public void AttachAiResult_風險被拉高時抽出欄risk_level同步更新_篩選查詢查得到()
    {
        var store = CreateStore();
        var query = (IAnalysisRecordQuery)store;
        var date = DateTime.Today;
        store.Append(Record(date, risk: "低"));

        store.AttachAiResult(date, new AiOutcome(
            Headline: "h", Summary: "s", TrendAssessment: "", Action: "",
            RiskLevel: "高", RiskBasis: "ai_raise", AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>()));

        var highRiskResults = query.Query(new RecordQueryFilter { RiskLevels = new[] { "高" } });
        var lowRiskResults = query.Query(new RecordQueryFilter { RiskLevels = new[] { "低" } });

        Assert.Contains(highRiskResults, r => r.Date.Date == date.Date);
        Assert.DoesNotContain(lowRiskResults, r => r.Date.Date == date.Date);
    }

    /// <summary>
    /// 回饋十三輪 C（孤兒補跑產報告）：UncoveredChecksAddendum 是追加，不是覆寫——統計段寫入的
    /// 既有申報項目（如「趨勢基準建立中」）補跑後仍要看得到，AI 段只把自己才知道的新缺口併進去。
    /// </summary>
    [Fact]
    public void AttachAiResult_UncoveredChecksAddendum追加而非覆寫既有申報項目()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            RiskLevel = "低",
            UncoveredChecks = new List<string> { "趨勢基準建立中（歷史 0/13 天）" }
        });

        store.AttachAiResult(date, new AiOutcome(
            Headline: "h", Summary: "s", TrendAssessment: "", Action: "",
            RiskLevel: "中", RiskBasis: null, AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>(),
            UncoveredChecksAddendum: new List<string> { "風險事件暫存已逾期，補跑報告從缺" }));

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(new[] { "趨勢基準建立中（歷史 0/13 天）", "風險事件暫存已逾期，補跑報告從缺" }, read.UncoveredChecks);
    }

    /// <summary>UncoveredChecksAddendum 為 null（絕大多數呼叫端，主分析路徑）時完全不影響既有內容</summary>
    [Fact]
    public void AttachAiResult_UncoveredChecksAddendum為null時不影響既有內容()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        store.Append(new DailyAnalysisRecord { Date = date, RiskLevel = "低", UncoveredChecks = new List<string> { "既有項目" } });

        store.AttachAiResult(date, new AiOutcome(
            Headline: "h", Summary: "s", TrendAssessment: "", Action: "",
            RiskLevel: "低", RiskBasis: null, AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>()));

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(new[] { "既有項目" }, read.UncoveredChecks);
    }

    /// <summary>契約同 AttachWeeklyCheckup：找不到對應日期安靜略過，不擲例外、不新增紀錄。</summary>
    [Fact]
    public void AttachAiResult_日期不存在_不擲例外且不新增紀錄()
    {
        var store = CreateStore();
        store.Append(Record(DateTime.Today));

        store.AttachAiResult(DateTime.Today.AddDays(-5), new AiOutcome(
            Headline: "不該被寫入", Summary: "", TrendAssessment: "", Action: "",
            RiskLevel: "高", RiskBasis: null, AiAnalyzed: true,
            ScreenedTailCount: 0, ScreeningNotes: new List<string>(), ReportFile: null,
            DeepDives: new List<CategoryDeepDive>()));

        Assert.Single(store.ReadRecent(DateTime.Today, 30));
    }

    // ── 儲存整形（RecordStorageShaper 是共用規則）───────────────────────────

    [Fact]
    public void 無風險日精簡_保留計數與趨勢數字但砍除範例訊息與KeyDetails()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            RiskLevel = "低",
            ErrorCount = 3,
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, Count = 7,
                    Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                    FirstSeen = "01:00", LastSeen = "05:00",
                    DistinctMessageCount = 4, HistoryDailyAverage = 3.5, DaysSeenInHistory = 5,
                    SampleMessages = new List<string> { "sample A", "sample B", "sample C" },
                    KeyDetails = "相關帳號(2個): admin, guest"
                }
            }
        });

        var issue = Assert.Single(Assert.Single(store.ReadRecent(DateTime.Today, 1)).TopIssues);

        // 數字全留（趨勢基準所需）
        Assert.Equal(7, issue.Count);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(4, issue.DistinctMessageCount);
        Assert.Equal(3.5, issue.HistoryDailyAverage);
        Assert.Equal(5, issue.DaysSeenInHistory);
        Assert.Equal("01:00", issue.FirstSeen);

        // 文字砍掉（體積大戶，無風險日基準用不到）
        Assert.Empty(issue.SampleMessages);
        Assert.Null(issue.KeyDetails);
    }

    [Fact]
    public void 風險中以上_完整保留範例訊息與KeyDetails()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, Count = 7,
                    Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                    SampleMessages = new List<string> { "sample A", "sample B" },
                    KeyDetails = "相關帳號(2個): admin, guest"
                }
            }
        });

        var issue = Assert.Single(store.ReadRecent(DateTime.Today, 1).Single().TopIssues);

        Assert.Equal(2, issue.SampleMessages.Count);
        Assert.Equal("相關帳號(2個): admin, guest", issue.KeyDetails);
    }

    [Fact]
    public void HostId與Host與DeepDives可完整序列化與讀回()
    {
        var store = CreateStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            HostId = 42,
            Host = "SRV-DB01",
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, Count = 1 }
            },
            DeepDives = new List<CategoryDeepDive>
            {
                new()
                {
                    Category = IssueCategory.Storage,
                    Findings = new List<DeepDiveFinding>
                    {
                        new()
                        {
                            Problem = "磁碟壞軌", Impact = "資料遺失風險",
                            LikelyCauses = new() { "硬碟老化" }, NextSteps = new() { "更換硬碟" }
                        }
                    }
                }
            }
        });

        var read = Assert.Single(store.ReadRecent(DateTime.Today, 1));

        Assert.Equal(42, read.HostId);
        Assert.Equal("SRV-DB01", read.Host);
        var deepDive = Assert.Single(read.DeepDives);
        Assert.Equal(IssueCategory.Storage, deepDive.Category);
        var finding = Assert.Single(deepDive.Findings);
        Assert.Equal("磁碟壞軌", finding.Problem);
        Assert.Equal("更換硬碟", finding.NextSteps.Single());
    }

    // ── AttachPrtgFindings：PRTG finding 附掛 ─────────────────────────────────

    [Fact]
    public void AttachPrtgFindings_對已有紀錄的主機成功追加且子列寫入真表()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var hostId = 101L;

        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            HostId = hostId,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        });

        var finding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule);
        var sig = PrtgFindingMapper.ToSignature(finding, date);

        var result = store.AttachPrtgFindings(hostId, date, new[] { sig }, NoPatternIds, out _);
        Assert.True(result);

        // 斷言 ContentJson 內部的 TopIssues 有追加
        var read = Assert.Single(store.ReadRecent(date, 1));
        var appendedIssue = Assert.Single(read.TopIssues);
        Assert.Equal("prtg:down:2001", appendedIssue.EventKey);
        Assert.Equal("PRTG", appendedIssue.LogName);
        Assert.Equal(IssueCategory.Service, appendedIssue.Category);
        Assert.Equal(IssueSeverity.High, appendedIssue.Severity);
        Assert.True(appendedIssue.ElevatesDayRisk);

        // 斷言 lf_top_issues 子列真的有寫入真表
        using var ctx = _fx.NewContext();
        var topIssueRow = Assert.Single(ctx.TopIssues.Where(t => t.HostId == hostId));
        Assert.Equal("PRTG:down", topIssueRow.SourceName);
        Assert.Equal("PRTG", topIssueRow.LogName);
        Assert.Equal(0, topIssueRow.EventId);
        Assert.Equal("Service", topIssueRow.Category);
        Assert.Equal((int)IssueSeverity.High, topIssueRow.SeverityRank);
        Assert.True(topIssueRow.ElevatesDayRisk);
        Assert.Equal("prtg:down:2001", topIssueRow.EventKey);
        Assert.Equal(SeedDownRule.Description, topIssueRow.KnownIssue);
        Assert.Equal(date.Date, topIssueRow.RecordDate);
    }

    [Fact]
    public void AttachPrtgFindings_對當天無紀錄的主機回false且不建立紀錄()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var finding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule);
        var sig = PrtgFindingMapper.ToSignature(finding, date);

        var result = store.AttachPrtgFindings(999, date, new[] { sig }, NoPatternIds, out _);
        Assert.False(result);

        using var ctx = _fx.NewContext();
        Assert.Empty(ctx.DailyRecords);
        Assert.Empty(ctx.TopIssues);
    }

    [Fact]
    public void AttachDailyRiskReport_報告內容與父列參照同交易且過期草稿不覆寫現行內容()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-1);
        var parent = new DailyAnalysisRecord
        {
            HostId = 101,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "高",
            TopIssues = []
        };
        store.Append(parent);
        var expected = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);

        var draftA = new PreparedRiskReport(date,
            new HostKey { HostId = 101, HostName = "SRV-TEST" },
            $"{date:yyyy-MM-dd}_高風險_儲存裝置.txt", "report-content-A",
            new ReportMeta(RiskLevels.High, "儲存裝置"), expected,
            HostDayWorkflowFingerprint.ForReportInput(parent));
        var decision = HostDayWorkflowFingerprint.ForRecord(parent);
        var reportRefA = store.AttachDailyRiskReport(date, draftA, parent.RecordId, decision, expected);
        Assert.NotNull(reportRefA);
        var saved = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(reportRefA, saved.ReportFile);
        Assert.Equal(expected, saved.PrtgReportEvidenceFingerprint);
        var reports = new EfReportStore(_fx.NewContext);
        Assert.Equal("report-content-A", reports.Read(draftA.Host, date, ReportKinds.DailyRisk)?.Content);

        var staleDraftB = draftA with
        {
            Content = "stale-report-content-B",
            PrtgEvidenceFingerprint = new string('0', 64)
        };
        Assert.Null(store.AttachDailyRiskReport(date, staleDraftB, parent.RecordId, decision,
            staleDraftB.PrtgEvidenceFingerprint));
        var unchanged = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(reportRefA, unchanged.ReportFile);
        Assert.Equal(expected, unchanged.PrtgReportEvidenceFingerprint);
        Assert.Equal("report-content-A", reports.Read(draftA.Host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void AttachDailyRiskReport_舊日報告以新產生時間保留而非立即清除()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-181);
        var parent = new DailyAnalysisRecord
        {
            HostId = 102, Host = "OLD-REPORT-HOST", Date = date, RiskLevel = RiskLevels.High,
            TopIssues = []
        };
        store.Append(parent);
        var prtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        var draft = new PreparedRiskReport(date, new HostKey { HostId = parent.HostId, HostName = parent.Host },
            $"{date:yyyy-MM-dd}_高風險_服務.txt", "freshly generated historical report",
            new ReportMeta(RiskLevels.High, "服務"), prtg,
            HostDayWorkflowFingerprint.ForReportInput(parent));

        Assert.NotNull(store.AttachDailyRiskReport(date, draft, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtg));
        var reports = new EfReportStore(_fx.NewContext);
        Assert.Equal(0, reports.Prune(180));
        Assert.Equal("freshly generated historical report",
            reports.Read(draft.Host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void AttachDailyRiskReport_相同PRTG但替換的父列不能承接舊報告草稿()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-3);
        var host = new HostKey { HostId = 103, HostName = "REPLACED-REPORT-HOST" };
        var original = new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = RiskLevels.High,
            ErrorCount = 3, TopIssues = []
        };
        var reportInputFingerprint = HostDayWorkflowFingerprint.ForReportInput(original);
        store.Append(original);
        var prtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(original);
        var oldDecision = HostDayWorkflowFingerprint.ForRecord(original);
        var draft = new PreparedRiskReport(date, host, $"{date:yyyy-MM-dd}_高風險_服務.txt",
            "old parent report", new ReportMeta(RiskLevels.High, "服務"), prtg,
            reportInputFingerprint);
        var originalRef = store.AttachDailyRiskReport(date, draft, original.RecordId, oldDecision, prtg);
        Assert.NotNull(originalRef);

        using (var context = _fx.NewContext())
        {
            var row = context.DailyRecords.Single(item => item.RecordId == original.RecordId);
            var changed = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
            changed.RiskLevel = RiskLevels.Medium;
            changed.ErrorCount = 9;
            row.ContentJson = System.Text.Json.JsonSerializer.Serialize(changed);
            row.RiskLevel = RiskLevels.Medium;
            row.ErrorCount = 9;
            context.SaveChanges();
        }
        Assert.Equal(prtg, HostDayWorkflowFingerprint.PrtgInputFingerprint(
            Assert.Single(store.ReadRecent(date, 1))));
        Assert.Null(store.AttachDailyRiskReport(date, draft, original.RecordId, oldDecision, prtg));
        Assert.Equal(originalRef, Assert.Single(store.ReadRecent(date, 1)).ReportFile);

        store.DeleteDays([date]);
        var replacement = new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = RiskLevels.Medium,
            ErrorCount = 9, TopIssues = []
        };
        store.Append(replacement);
        Assert.Equal(prtg, HostDayWorkflowFingerprint.PrtgInputFingerprint(replacement));
        Assert.NotEqual(oldDecision, HostDayWorkflowFingerprint.ForRecord(replacement));

        Assert.Null(store.AttachDailyRiskReport(date, draft, original.RecordId, oldDecision, prtg));
        var current = Assert.Single(store.ReadRecent(date, 1));
        Assert.NotEqual(original.RecordId, current.RecordId);
        Assert.Null(current.ReportFile);
        Assert.Equal("old parent report", new EfReportStore(_fx.NewContext)
            .Read(host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void AttachDailyRiskReport_父列更新失敗會回滾報告Upsert()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-4);
        var host = new HostKey { HostId = 104, HostName = "REPORT-ROLLBACK-HOST" };
        var parent = new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = RiskLevels.High,
            TopIssues = []
        };
        store.Append(parent);
        var prtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        var inputFingerprint = HostDayWorkflowFingerprint.ForReportInput(parent);
        var decision = HostDayWorkflowFingerprint.ForRecord(parent);
        var first = new PreparedRiskReport(date, host, $"{date:yyyy-MM-dd}_高風險_服務.txt",
            "original report", new ReportMeta(RiskLevels.High, "服務"), prtg, inputFingerprint);
        var originalRef = store.AttachDailyRiskReport(date, first, parent.RecordId, decision, prtg);
        Assert.NotNull(originalRef);
        // A prior report may describe older evidence. Change its marker so the guarded attach
        // must actually UPDATE the parent; an identical pointer/marker is a valid EF no-op.
        var priorMarker = new string('f', 64);
        using (var context = _fx.NewContext())
        {
            var row = context.DailyRecords.Single(item => item.RecordId == parent.RecordId);
            var prior = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
            prior.PrtgReportEvidenceFingerprint = priorMarker;
            row.ContentJson = JsonSerializer.Serialize(prior);
            context.SaveChanges();
        }
        var replacement = first with { Content = "must roll back" };
        using (var context = _fx.NewContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "CREATE TRIGGER fail_daily_report_parent_update BEFORE UPDATE ON lf_daily_records BEGIN SELECT RAISE(ABORT, 'fixture parent update failure'); END;");

        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            store.AttachDailyRiskReport(date, replacement, parent.RecordId, decision, prtg));

        var current = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(originalRef, current.ReportFile);
        Assert.Equal(priorMarker, current.PrtgReportEvidenceFingerprint);
        Assert.Equal("original report", new EfReportStore(_fx.NewContext)
            .Read(host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void TryAttachAiResult_報告內容與AI父列同交易且不匹配草稿不覆寫現行報告()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-2);
        var host = new HostKey { HostId = 101, HostName = "SRV-TEST" };
        store.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = RiskLevels.High,
            AiPending = true,
            TopIssues = []
        });

        var parent = Assert.Single(store.ReadRecent(date, 1));
        var fingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        const string cacheNote = "本報告原始 log 來自有界風險事件暫存。";
        var currentOutcome = new AiOutcome("標題 A", "摘要 A", "趨勢", "處置", RiskLevels.High, null,
            true, 0, [], null, [], InputPrtgFingerprint: fingerprint,
            UncoveredChecksAddendum: [cacheNote], ReportPrtgEvidenceFingerprint: fingerprint);
        var draftA = new PreparedRiskReport(date, host, $"{date:yyyy-MM-dd}_高風險_儲存裝置.txt",
            "ai-report-content-A", new ReportMeta(RiskLevels.High, "儲存裝置"), fingerprint,
            ExpectedAttachedAiReportInput(parent, currentOutcome));
        currentOutcome = currentOutcome with { ReportDraft = draftA };

        Assert.True(store.TryAttachAiResult(date, currentOutcome, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), fingerprint));

        var attached = Assert.Single(store.ReadRecent(date, 1));
        Assert.NotNull(attached.ReportFile);
        Assert.Equal(fingerprint, attached.PrtgReportEvidenceFingerprint);
        Assert.Contains(cacheNote, attached.UncoveredChecks);
        var reports = new EfReportStore(_fx.NewContext);
        Assert.Equal("ai-report-content-A", reports.Read(host, date, ReportKinds.DailyRisk)?.Content);

        var changedNarrative = currentOutcome with { Summary = "摘要 B" };
        changedNarrative = changedNarrative with
        {
            ReportDraft = draftA with { Content = "stale narrative report" }
        };
        Assert.False(store.TryAttachAiResult(date, changedNarrative, attached.RecordId,
            HostDayWorkflowFingerprint.ForRecord(attached), fingerprint));
        Assert.Equal("ai-report-content-A", reports.Read(host, date, ReportKinds.DailyRisk)?.Content);

        const string lateProvenanceNote = "本次補跑原始 log 來自有限容量快取。";
        var incompatibleOutcome = currentOutcome with { UncoveredChecksAddendum = [lateProvenanceNote] };
        var incompatibleDraft = draftA with
        {
            Content = "report omits final provenance",
            DecisionInputFingerprint = ExpectedAttachedAiReportInput(attached, incompatibleOutcome,
                includeAddendum: false)
        };
        incompatibleOutcome = incompatibleOutcome with { ReportDraft = incompatibleDraft };
        Assert.False(store.TryAttachAiResult(date, incompatibleOutcome, attached.RecordId,
            HostDayWorkflowFingerprint.ForRecord(attached), fingerprint));
        Assert.Equal("ai-report-content-A", reports.Read(host, date, ReportKinds.DailyRisk)?.Content);

        var staleDraftB = draftA with
        {
            Content = "ai-report-content-B-stale",
            PrtgEvidenceFingerprint = new string('0', 64)
        };
        var staleOutcome = currentOutcome with
        {
            Summary = "stale AI summary B",
            InputPrtgFingerprint = fingerprint,
            ReportPrtgEvidenceFingerprint = staleDraftB.PrtgEvidenceFingerprint,
            ReportDraft = staleDraftB
        };
        Assert.False(store.TryAttachAiResult(date, staleOutcome, attached.RecordId,
            HostDayWorkflowFingerprint.ForRecord(attached), fingerprint));

        var unchanged = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(attached.ReportFile, unchanged.ReportFile);
        Assert.Equal(fingerprint, unchanged.PrtgReportEvidenceFingerprint);
        Assert.Equal("ai-report-content-A", reports.Read(host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void TryAttachAiResult_父列更新失敗會回滾AI報告Upsert()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-5);
        var host = new HostKey { HostId = 105, HostName = "AI-REPORT-ROLLBACK-HOST" };
        var parentRow = new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = RiskLevels.High,
            AiPending = true, AiAnalyzed = false, TopIssues = []
        };
        store.Append(parentRow);
        var parent = Assert.Single(store.ReadRecent(date, 1));
        var prtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        var decisionInput = HostDayWorkflowFingerprint.ForReportInput(parent);
        var existingDraft = new PreparedRiskReport(date, host, $"{date:yyyy-MM-dd}_高風險_服務.txt",
            "existing AI report", new ReportMeta(RiskLevels.High, "服務"), prtg, decisionInput);
        var existingRef = store.AttachDailyRiskReport(date, existingDraft, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtg);
        Assert.NotNull(existingRef);
        var current = Assert.Single(store.ReadRecent(date, 1));
        var outcome = new AiOutcome("new headline", "new summary", "trend", "action", RiskLevels.High,
            null, true, 0, [], null, [], InputPrtgFingerprint: prtg,
            ReportPrtgEvidenceFingerprint: prtg);
        var replacementDraft = existingDraft with
        {
            Content = "replacement AI report",
            DecisionInputFingerprint = ExpectedAttachedAiReportInput(current, outcome)
        };
        outcome = outcome with { ReportDraft = replacementDraft };
        using (var context = _fx.NewContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "CREATE TRIGGER fail_ai_report_parent_update BEFORE UPDATE ON lf_daily_records BEGIN SELECT RAISE(ABORT, 'fixture AI parent update failure'); END;");

        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            store.TryAttachAiResult(date, outcome, current.RecordId,
                HostDayWorkflowFingerprint.ForRecord(current), prtg));

        var unchanged = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(existingRef, unchanged.ReportFile);
        Assert.True(unchanged.AiPending);
        Assert.False(unchanged.AiAnalyzed);
        Assert.Equal("existing AI report", new EfReportStore(_fx.NewContext)
            .Read(host, date, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void NotificationWorkflowUsesBoundedScalarRowsEvenWithOversizedInvalidContent()
    {
        var store = CreateStore();
        var query = (IAnalysisRecordQuery)store;
        var day = DateTime.Today.AddDays(-1);
        store.Append(new DailyAnalysisRecord { HostId = 101, Host = "scalar-low", Date = day, RiskLevel = RiskLevels.Low });
        store.Append(new DailyAnalysisRecord { HostId = 102, Host = "scalar-high", Date = day, RiskLevel = RiskLevels.High });
        using (var db = _fx.NewContext())
        {
            foreach (var row in db.DailyRecords) row.ContentJson = new string('x', 1_000_000);
            db.SaveChanges();
        }
        var first = query.QueryNotificationWorkflowPage(day, day, 0, 1);
        Assert.Single(first);
        var second = query.QueryNotificationWorkflowPage(day, day, first[0].RecordId, 1);
        Assert.Single(second);
        Assert.NotEqual(first[0].RecordId, second[0].RecordId);
        Assert.Equal(new[] { RiskLevels.Low, RiskLevels.High }, first.Concat(second).Select(row => row.RiskLevel));
        Assert.Empty(query.QueryNotificationWorkflowPage(day, day, second[0].RecordId, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.QueryNotificationWorkflowPage(day, day, 0, 501));
    }

    [Fact]
    public void ExactNotificationParentLookupPreservesSerializedHostAndDateKindAndReportsBoundedOversize()
    {
        var query = (EfAnalysisRecordStore)CreateStore();
        var localDate = DateTime.SpecifyKind(new DateTime(2026, 10, 5), DateTimeKind.Local);
        var parent = new DailyAnalysisRecord
        {
            HostId = 4401, Host = "serialized-parent-host", Date = localDate, RiskLevel = RiskLevels.High,
            Headline = "bounded exact parent"
        };
        query.Append(parent);

        var lookup = query.LookupByRecordId(parent.RecordId);

        Assert.True(lookup.Exists);
        Assert.False(lookup.IdentityMismatch);
        Assert.Equal(parent.RecordId, lookup.Record!.RecordId);
        Assert.Equal(4401, lookup.Record.HostId);
        Assert.Equal("serialized-parent-host", lookup.Record.Host);
        Assert.Equal(DateTimeKind.Local, lookup.Record.Date.Kind);
        Assert.Equal(localDate.Date, lookup.Record.Date.Date);

        var oversized = new DailyAnalysisRecord
        {
            HostId = 4402, Host = "oversized-parent", Date = localDate, RiskLevel = RiskLevels.High,
            Headline = new string('x', WorkflowRecoveryPage.MaximumPayloadBytes + 512)
        };
        query.Append(oversized);
        var overLimit = query.LookupByRecordId(oversized.RecordId);
        Assert.True(overLimit.Exists);
        Assert.True(overLimit.PayloadTooLarge);
        Assert.Null(overLimit.Record);
    }

    [Fact]
    public void ExactNotificationParentLookupRejectsSqlIdentityMismatchWithoutRewritingJsonIdentity()
    {
        var query = (EfAnalysisRecordStore)CreateStore();
        var date = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local);
        var parent = new DailyAnalysisRecord { HostId = 4403, Host = "serialized-host", Date = date, RiskLevel = RiskLevels.High };
        query.Append(parent);
        using (var db = _fx.NewContext())
        {
            var row = db.DailyRecords.Single(candidate => candidate.RecordId == parent.RecordId);
            var stored = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
            stored.HostId++;
            stored.Date = stored.Date.AddDays(1);
            row.ContentJson = System.Text.Json.JsonSerializer.Serialize(stored);
            db.SaveChanges();
        }

        var lookup = query.LookupByRecordId(parent.RecordId);

        Assert.True(lookup.Exists);
        Assert.True(lookup.IdentityMismatch);
        Assert.Null(lookup.Record);
    }

    [Fact]
    public void AttachPrtgFindings_零FindingManifest原子持久_支援舊JSON及父版本拒絕()
    {
        var store = CreateStore();
        var hostId = 991L;
        var date = new DateTime(2026, 10, 3);
        var appended = new DailyAnalysisRecord
        {
            HostId = hostId, Host = "manifest-host", Date = date,
            LogSource = AnalysisLogSource.Netiq, RiskLevel = "低", ErrorCount = 0,
            TopIssues = new List<LogIssueSignature>()
        };
        store.Append(appended);
        Assert.True(appended.RecordId > 0);
        var parent = Assert.Single(store.ReadRecent(date, 1));
        Assert.Null(parent.PrtgManifest); // old ContentJson omits the additive field

        var manifest = new PrtgDecisionManifest
        {
            ParentRecordId = parent.RecordId,
            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
            ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
            SourceGeneration = "source-v1",
            ResourceFingerprint = new string('a', 64),
            SemanticFingerprint = new string('b', 64),
            StrategyFingerprint = new string('c', 64),
            HostMappingFingerprint = new string('f', 64),
            RuleFingerprint = new string('d', 64),
            EvidenceFingerprint = new string('e', 64),
            FindingFingerprint = PrtgFindingMapper.Fingerprint(Array.Empty<LogIssueSignature>()),
            CompletedAtUtc = DateTime.UtcNow,
            Outcome = "complete"
        };

        Assert.False(store.AttachPrtgFindings(hostId, date, Array.Empty<LogIssueSignature>(), NoPatternIds,
            out _, aiConfigured: true, manifest: manifest)); // no finding delta; manifest is still committed
        var persisted = Assert.Single(store.ReadRecent(date, 1));
        Assert.Empty(persisted.TopIssues);
        Assert.NotNull(persisted.PrtgManifest);
        Assert.True(persisted.PrtgManifest!.ParentRecordId > 0);
        Assert.Equal(manifest.EvidenceFingerprint, persisted.PrtgManifest.EvidenceFingerprint);
        Assert.Equal(PrtgFindingMapper.Fingerprint(Array.Empty<LogIssueSignature>()), persisted.PrtgManifest.FindingFingerprint);
        Assert.Equal(manifest.ParentFingerprint, HostDayWorkflowFingerprint.ForParentRecord(persisted));
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(persisted));

        var changedParent = new DailyAnalysisRecord
        {
            HostId = persisted.HostId, Date = persisted.Date, LogSource = persisted.LogSource,
            LatestNetiqAttemptStatus = "success", LatestNetiqAttemptAtUtc = DateTime.UtcNow,
            ErrorCount = 1, TopIssues = persisted.TopIssues
        };
        Assert.NotEqual(persisted.PrtgManifest.ParentFingerprint,
            HostDayWorkflowFingerprint.ForParentRecord(changedParent));
        changedParent.RecordId = persisted.RecordId;
        changedParent.PrtgManifest = persisted.PrtgManifest;
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgManifest(changedParent));
    }

    [Fact]
    public void AttachPrtgFindings_同一天重複追加依EventKey去重不重複寫入()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var hostId = 101L;

        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            HostId = hostId,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        });

        var finding1 = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule);
        var finding2 = new PrtgFinding(1001, 2002, "warning", "Sensor warning", 250, SeedWarnRule);
        var sig1 = PrtgFindingMapper.ToSignature(finding1, date);
        var sig2 = PrtgFindingMapper.ToSignature(finding2, date);

        var firstResult = store.AttachPrtgFindings(hostId, date, new[] { sig1, sig2 }, NoPatternIds, out _);
        Assert.True(firstResult);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.TopIssues.Count(t => t.HostId == hostId));
        }

        // 第二次追加相同的 findings：回傳 false 且子列數維持 2
        var secondResult = store.AttachPrtgFindings(hostId, date, new[] { sig1, sig2 }, NoPatternIds, out _);
        Assert.False(secondResult);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.TopIssues.Count(t => t.HostId == hostId));
        }

        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Equal(2, read.TopIssues.Count);
    }

    [Fact]
    public void AttachPrtgFindings_DetailPruned為true時回false且ContentJson未被改動()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        var hostId = 101L;

        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            HostId = hostId,
            Host = "SRV-TEST",
            Date = date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature>()
        });

        // 手動將 DetailPruned 設為 true 並記錄當前的 ContentJson
        string originalContentJson;
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single(r => r.HostId == hostId && r.RecordDate == date.Date);
            row.DetailPruned = true;
            originalContentJson = row.ContentJson;
            ctx.SaveChanges();
        }

        var finding = new PrtgFinding(1001, 2001, "down", "Sensor down", 60, SeedDownRule);
        var sig = PrtgFindingMapper.ToSignature(finding, date);

        var result = store.AttachPrtgFindings(hostId, date, new[] { sig }, NoPatternIds, out _);
        Assert.False(result);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single(r => r.HostId == hostId && r.RecordDate == date.Date);
            Assert.True(row.DetailPruned);
            Assert.Equal(originalContentJson, row.ContentJson);
        }
    }

    private LogIssueSignature SeedDiskErrorDayWithHardwareWarning(IAnalysisRecordStore store, long hostId, DateTime date)
    {
        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq,
            HostId = hostId, Host = "SRV-TEST", Date = date, RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { new() { LogName = "System", Source = "disk", EventId = 153, Count = 4 } }
        });
        var finding = new PrtgFinding(1001, 2001, "warning", "[SRV-TEST] RAID Warning", 180, SeedHardwareWarnRule)
        {
            SensorCategory = PrtgSensorCategories.Hardware
        };
        return PrtgFindingMapper.ToSignature(finding, date);
    }

    [Fact]
    public void AttachPrtgFindings_跨來源佐證寫入關聯欄位_資料列HasCorrelation與高風險()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        var sig = SeedDiskErrorDayWithHardwareWarning(store, 101, date);

        var result = store.AttachPrtgFindings(101, date, new[] { sig }, NoPatternIds, out var corroborated);

        Assert.True(result);
        Assert.Equal(1, corroborated);
        var read = Assert.Single(store.ReadRecent(date, 1));
        var text = Assert.Single(read.CorrelationAlerts);
        Assert.StartsWith("【儲存異常同日訊號】", text);
        Assert.Contains("disk#153", text);
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, Assert.Single(read.CorrelationAlertRefs).PatternId);
        Assert.Equal("中", read.RiskLevel); // 硬體 warning 本身為 High；同日配對不再額外拉高
        Assert.NotEqual(CorrelationPatternIds.PrtgStorageCorroborated, read.RiskBasis);
        Assert.Equal(PrtgSensorCategories.Hardware, read.TopIssues.Single(i => i.EventKey == "prtg:warning:2001").PrtgSensorCategory);

        using var ctx = _fx.NewContext();
        var row = ctx.DailyRecords.Single(r => r.HostId == 101 && r.RecordDate == date.Date);
        Assert.True(row.HasCorrelation);
        Assert.Equal("中", row.RiskLevel);
    }

    [Fact]
    public void AttachPrtgFindings_佐證模式被抑制時只進已抑制清單且HasCorrelation為false()
    {
        var store = CreateStore();
        var date = DateTime.Today;
        var sig = SeedDiskErrorDayWithHardwareWarning(store, 101, date);
        var suppressed = new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated };

        var result = store.AttachPrtgFindings(101, date, new[] { sig }, suppressed, out var corroborated);

        Assert.True(result);
        Assert.Equal(0, corroborated);
        var read = Assert.Single(store.ReadRecent(date, 1));
        Assert.Empty(read.CorrelationAlerts);
        Assert.StartsWith("【儲存異常同日訊號】", Assert.Single(read.SuppressedCorrelationAlerts));
        Assert.NotEqual("高", read.RiskLevel);

        using var ctx = _fx.NewContext();
        Assert.False(ctx.DailyRecords.Single(r => r.HostId == 101 && r.RecordDate == date.Date).HasCorrelation);
    }
    [Fact]
    public void AttachPrtgFindings_一般寫入失敗不能偽裝成日紀錄不存在()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today;
        store.Append(new DailyAnalysisRecord { LogSource = AnalysisLogSource.Netiq, HostId = 101, Host = "SRV", Date = date, RiskLevel = "低" });
        using (var ctx = _fx.NewContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(ctx.Database,
                "CREATE TRIGGER fail_prtg_insert BEFORE INSERT ON lf_top_issues BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;");
        var sig = PrtgFindingMapper.ToSignature(new PrtgFinding(1001, 2001, "down", "Down", 60, SeedDownRule), date);
        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            store.AttachPrtgFindings(101, date, new[] { sig }, NoPatternIds, out _));
        var record = Assert.Single(store.ReadRecent(date, 1));
        Assert.Empty(record.TopIssues);
        Assert.Equal("低", record.RiskLevel);
    }

}

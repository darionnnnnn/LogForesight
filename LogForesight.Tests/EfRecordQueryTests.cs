using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// EF 後端的查詢面（IAnalysisRecordQuery.Query/GetOne）——Web 的熱路徑。
/// 驗證 DB 端預篩（日期/主機/風險）＋共用 RecordFilterMatcher（類別/事件）的組合結果，
/// 以及 HostMatcher 的 PK 優先與「空集合＝零結果」授權語意。
/// </summary>
public class EfRecordQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private EfAnalysisRecordStore Store() => new(_fx.NewContext, "test");

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date, string risk = "低",
        params LogIssueSignature[] issues) => new()
    {
        HostId = hostId, Host = host, Date = date, RiskLevel = risk, TopIssues = issues.ToList()
    };

    private static LogIssueSignature Issue(string source, int eventId, IssueCategory cat = IssueCategory.Other) =>
        new() { LogName = "System", Source = source, EventId = eventId, Category = cat, Count = 1 };

    [Fact]
    public void Query_日期區間篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", new DateTime(2026, 7, 10)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));

        var result = store.Query(new RecordQueryFilter
        {
            From = new DateTime(2026, 7, 15), To = new DateTime(2026, 7, 25)
        });

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 7, 20), result[0].Date.Date);
    }

    [Fact]
    public void Query_主機以PK優先比對()
    {
        var store = Store();
        store.Append(Rec(2, "舊名稱", DateTime.Today));   // 有 HostId → 只認 id
        store.Append(Rec(0, "SRV-B", DateTime.Today));    // HostId=0 → 認名稱

        // 找 HostId=2（名稱給錯的也該以 id 命中）＋名稱 SRV-B
        var result = store.Query(new RecordQueryFilter
        {
            Hosts = new[] { new HostKey { HostId = 2, HostName = "隨便" }, new HostKey { HostId = 0, HostName = "SRV-B" } }
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Query_空主機集合_零結果()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));

        // 空集合＝授權範圍為空 → 什麼都不該回（授權正確性關鍵）
        var result = store.Query(new RecordQueryFilter { Hosts = System.Array.Empty<HostKey>() });

        Assert.Empty(result);
    }

    [Fact]
    public void Query_類別與事件篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today, "高", Issue("disk", 153, IssueCategory.Storage)));
        store.Append(Rec(2, "B", DateTime.Today, "高", Issue("svc", 7031, IssueCategory.Service)));

        var byCat = store.Query(new RecordQueryFilter { Categories = new[] { IssueCategory.Storage } });
        Assert.Single(byCat);
        Assert.Equal("A", byCat[0].Host);

        var byEvent = store.Query(new RecordQueryFilter { EventId = 7031 });
        Assert.Single(byEvent);
        Assert.Equal("B", byEvent[0].Host);
    }

    [Fact]
    public void Query_風險層級篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today, "高"));
        store.Append(Rec(2, "B", DateTime.Today, "低"));

        var result = store.Query(new RecordQueryFilter { RiskLevels = new[] { "高" } });
        Assert.Single(result);
        Assert.Equal("A", result[0].Host);
    }

    [Fact]
    public void GetOne_依主機順序擇一()
    {
        var store = Store();
        var date = DateTime.Today;
        store.Append(Rec(2, "存活", date, "高"));
        store.Append(Rec(3, "墓碑", date, "中"));

        // 傳入順序：存活主機（id=2）排前面 → 取到它
        var result = store.GetOne(new[] { new HostKey { HostId = 2, HostName = "存活" }, new HostKey { HostId = 3, HostName = "墓碑" } }, date);

        Assert.NotNull(result);
        Assert.Equal("存活", result!.Host);
    }

    [Fact]
    public void GetOne_空集合回null()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));
        Assert.Null(store.GetOne(System.Array.Empty<HostKey>(), DateTime.Today));
    }

    // ── QueryPage 表頭排序（docs/WEB-SPEC.md §9.2）───────────────────────────

    [Fact]
    public void QueryPage_依日期升冪排序()
    {
        var store = Store();
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 10)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 15)));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "date", ascending: true);

        Assert.Equal(new[] { 10, 15, 20 }, page.Items.Select(r => r.Date.Day));
    }

    [Fact]
    public void QueryPage_依主機名降冪排序()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));
        store.Append(Rec(2, "C", DateTime.Today));
        store.Append(Rec(3, "B", DateTime.Today));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "host", ascending: false);

        Assert.Equal(new[] { "C", "B", "A" }, page.Items.Select(r => r.Host));
    }

    [Fact]
    public void QueryPage_不合法SortKey_退回預設緊急程度排序()
    {
        var store = Store();
        store.Append(Rec(1, "低", DateTime.Today, "低"));
        store.Append(Rec(2, "高", DateTime.Today, "高"));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "not-a-real-key");

        Assert.Equal("高", page.Items[0].Host);
    }

    /// <summary>含 HostId=0 舊列時 QueryPage 退回記憶體排序路徑（見類別內 hasLegacyHostRows 分支），
    /// 表頭排序在這條路徑也必須維持正確——這是唯二兩條實作路徑的另一條，不能只顧 SQL 全下推那條。</summary>
    [Fact]
    public void QueryPage_含舊列退回記憶體路徑_排序仍正確()
    {
        var store = Store();
        store.Append(Rec(0, "舊列-無HostId", DateTime.Today));   // 觸發 hasLegacyHostRows
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));
        store.Append(Rec(2, "B", new DateTime(2026, 7, 10)));

        var page = store.QueryPage(new RecordQueryFilter { Hosts = new[]
        {
            new HostKey { HostId = 1, HostName = "A" }, new HostKey { HostId = 2, HostName = "B" }
        } }, page: 1, pageSize: 10, sortKey: "date", ascending: true);

        Assert.Equal(new[] { "B", "A" }, page.Items.Select(r => r.Host));
    }

    // ── 批次C：全域待補查詢與計數 ─────────────────────────────────────────────

    [Fact]
    public void QueryPendingAi_多主機多日期_只回傳pending且新到舊排序且取批數量正確()
    {
        var store = Store();

        var d1 = new DateTime(2026, 8, 20);
        var d2 = new DateTime(2026, 8, 22);
        var d3 = new DateTime(2026, 8, 24);
        var d4 = new DateTime(2026, 8, 26);

        // 建立多主機多日期的資料（含 pending 與非 pending）
        var r1 = Rec(1, "HOST-A", d1, "高"); r1.AiPending = true; store.Append(r1);
        var r2 = Rec(2, "HOST-B", d1, "低"); r2.AiPending = true; store.Append(r2);
        var r3 = Rec(1, "HOST-A", d2, "高"); r3.AiPending = false; store.Append(r3);
        var r4 = Rec(3, "HOST-C", d3, "中"); r4.AiPending = true; store.Append(r4);
        var r5 = Rec(2, "HOST-B", d4, "高"); r5.AiPending = true; store.Append(r5);
        var r6 = Rec(3, "HOST-C", d4, "低"); r6.AiPending = false; store.Append(r6);

        // 取前 10 筆：應該只拿到 4 筆 pending 的紀錄，且排序為日期新→舊
        var batchAll = store.QueryPendingAi(10);
        Assert.Equal(4, batchAll.Count);
        Assert.All(batchAll, r => Assert.True(r.AiPending));

        // 排序驗證：d4 (2026-08-26) -> d3 (2026-08-24) -> d1 (2026-08-20, HOST-A, HOST-B)
        Assert.Equal(d4, batchAll[0].Date.Date);
        Assert.Equal("HOST-B", batchAll[0].Host);

        Assert.Equal(d3, batchAll[1].Date.Date);
        Assert.Equal("HOST-C", batchAll[1].Host);

        Assert.Equal(d1, batchAll[2].Date.Date);
        Assert.Equal(d1, batchAll[3].Date.Date);
        Assert.Equal(new[] { "HOST-A", "HOST-B" }, batchAll.Skip(2).Take(2).Select(r => r.Host).OrderBy(h => h));

        // 取批數量正確：取前 2 筆
        var batch2 = store.QueryPendingAi(2);
        Assert.Equal(2, batch2.Count);
        Assert.Equal(d4, batch2[0].Date.Date);
        Assert.Equal(d3, batch2[1].Date.Date);

        // 批次大小 <= 0 時回傳空清單
        Assert.Empty(store.QueryPendingAi(0));
        Assert.Empty(store.QueryPendingAi(-5));
    }

    [Fact]
    public void CountPendingAi_計數正確且無pending時回傳0()
    {
        var store = Store();

        // 初始無任何資料：回傳 0，不擲例外，非 null
        Assert.Equal(0, store.CountPendingAi());

        // 寫入非 pending 紀錄：計數仍為 0
        var r1 = Rec(1, "HOST-A", new DateTime(2026, 8, 20));
        r1.AiPending = false;
        store.Append(r1);
        Assert.Equal(0, store.CountPendingAi());

        // 寫入 pending 紀錄
        var r2 = Rec(1, "HOST-A", new DateTime(2026, 8, 21));
        r2.AiPending = true;
        store.Append(r2);
        Assert.Equal(1, store.CountPendingAi());

        var r3 = Rec(2, "HOST-B", new DateTime(2026, 8, 21));
        r3.AiPending = true;
        store.Append(r3);
        Assert.Equal(2, store.CountPendingAi());
    }

    /// <summary>
    /// 批次C 驗收 3：不載入 ContentJson 客觀驗證。
    /// 寫入一筆 ContentJson 明顯異常（毀損非 JSON 格式字串）的資料，
    /// 斷言 QueryPendingAi 仍可正常回傳且不含該內容、不拋 JsonException；
    /// 反之，一般會反序列化 ContentJson 的 GetOne 則必然丟出 JsonException，客觀對比驗證。
    /// </summary>
    [Fact]
    public void QueryPendingAi_不載入ContentJson客觀驗證()
    {
        var store = Store();
        var date = new DateTime(2026, 8, 25);

        // 直接寫入資料列，給予毀損的非 JSON 字串
        using (var ctx = _fx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 999,
                HostName = "CORRUPT-JSON-HOST",
                RecordDate = date,
                RiskLevel = "高",
                Headline = "純抽出欄標題",
                ErrorCount = 42,
                WarningCount = 7,
                AiPending = true,
                AiAnalyzed = false,
                ContentJson = "<<MALFORMED_NON_JSON_CORRUPTED_PAYLOAD_NOT_READABLE>>",
                CreatedAt = DateTime.Now
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = row.RecordId,
                HostId = 999,
                RecordDate = date,
                SourceName = "disk",
                EventId = 153,
                Category = "Storage",
                SeverityRank = (int)IssueSeverity.High,
                EventCount = 3,
                LogName = "System",
                EventKey = ""
            });
            ctx.SaveChanges();
        }

        // 1. QueryPendingAi 正常回傳，未觸發 ContentJson 反序列化
        var pending = store.QueryPendingAi(10);
        var item = Assert.Single(pending);
        Assert.Equal(999, item.HostId);
        Assert.Equal("CORRUPT-JSON-HOST", item.Host);
        Assert.Equal(date, item.Date);
        Assert.Equal("高", item.RiskLevel);
        Assert.Equal("純抽出欄標題", item.Headline);
        Assert.Equal(42, item.ErrorCount);
        Assert.Equal(7, item.WarningCount);
        Assert.True(item.AiPending);
        // ContentJson 特有欄位（Summary、TrendAssessment、Action 等）未被反序列化，維持預設空字串
        Assert.Equal(string.Empty, item.Summary);
        Assert.Equal(string.Empty, item.TrendAssessment);
        Assert.Equal(string.Empty, item.Action);
        // TopIssues 正常自 TopIssues 表載入
        Assert.Single(item.TopIssues);
        Assert.Equal(153, item.TopIssues[0].EventId);

        // 2. 對比驗證：真正反序列化 ContentJson 的 GetOne 會在此列拋出 JsonException
        Assert.Throws<System.Text.Json.JsonException>(() =>
            store.GetOne(new[] { new HostKey { HostId = 999, HostName = "CORRUPT-JSON-HOST" } }, date));
    }

    /// <summary>
    /// 批次C 驗收：待補判定的事實來源單點化（ai_pending 欄位為準，不讀 ContentJson 內的 AiPending）。
    /// 模擬批次D 的整批 UPDATE 欄位情境：ContentJson 內為 false，但 DB 欄位為 true。
    /// </summary>
    [Fact]
    public void 待補判定_事實來源以ai_pending欄位為準_不因ContentJson分岔而誤判()
    {
        var store = Store();
        var date = new DateTime(2026, 8, 25);

        // 寫入一筆：ContentJson 內部包含 "AiPending": false，但資料列 ai_pending 欄位設為 true
        using (var ctx = _fx.NewContext())
        {
            var row = new DailyRecordRow
            {
                HostId = 101,
                HostName = "HOST-SPLIT-STATE",
                RecordDate = date,
                RiskLevel = "高",
                AiPending = true, // 欄位為 true
                AiAnalyzed = false,
                ContentJson = """{"Date":"2026-08-25T00:00:00","HostId":101,"Host":"HOST-SPLIT-STATE","RiskLevel":"高","AiPending":false,"AiAnalyzed":false}""",
                CreatedAt = DateTime.Now
            };
            ctx.DailyRecords.Add(row);
            ctx.SaveChanges();
        }

        // QueryPendingAi 以欄位為準
        var pendingList = store.QueryPendingAi(10);
        Assert.Single(pendingList);
        Assert.True(pendingList[0].AiPending);

        // CountPendingAi 以欄位為準
        Assert.Equal(1, store.CountPendingAi());

        // ReadRecent 反序列化後，AiPending 亦以欄位為準（覆蓋 ContentJson 內的舊值）
        var recent = store.ReadRecent(date, 1);
        Assert.Single(recent);
        Assert.True(recent[0].AiPending);

        // NeedsBackfill（取數側判定，體檢輪語意變更）：AiPending=true 的紀錄**不再**由取數補跑
        // ——待補由獨立的 AI 分析排程消化，取數重抓只會與它打架。這裡斷言取數側判定為不補。
        Assert.False(LogForesight.Core.Service.HostDayPostProcessor.NeedsBackfill(recent[0], useAi: false));
    }

    [Fact]
    public void 測試替身_QueryPendingAi與CountPendingAi實作正確()
    {
        var fakeQuery = new FakeAnalysisRecordQuery();
        var fakeRepo = new FakeRecordRepository(new FakeHostStore());

        var r1 = Rec(1, "HOST-A", new DateTime(2026, 8, 20)); r1.AiPending = true;
        var r2 = Rec(2, "HOST-B", new DateTime(2026, 8, 22)); r2.AiPending = false;
        var r3 = Rec(1, "HOST-A", new DateTime(2026, 8, 24)); r3.AiPending = true;

        fakeQuery.Add(r1); fakeQuery.Add(r2); fakeQuery.Add(r3);
        fakeRepo.Add(r1); fakeRepo.Add(r2); fakeRepo.Add(r3);

        IAnalysisRecordQuery q1 = fakeQuery;
        IAnalysisRecordQuery q2 = fakeRepo;

        Assert.Equal(2, q1.CountPendingAi());
        Assert.Equal(2, q2.CountPendingAi());

        var list1 = q1.QueryPendingAi(10);
        var list2 = q2.QueryPendingAi(10);

        Assert.Equal(2, list1.Count);
        Assert.Equal(new DateTime(2026, 8, 24), list1[0].Date.Date);
        Assert.Equal(new DateTime(2026, 8, 20), list1[1].Date.Date);

        Assert.Equal(2, list2.Count);
        Assert.Equal(new DateTime(2026, 8, 24), list2[0].Date.Date);
        Assert.Equal(new DateTime(2026, 8, 20), list2[1].Date.Date);

        // 支援取批
        Assert.Single(q1.QueryPendingAi(1));
        Assert.Single(q2.QueryPendingAi(1));
        Assert.Empty(q1.QueryPendingAi(0));
        Assert.Empty(q2.QueryPendingAi(0));
    }
}

using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理狀態三表的合約測試（docs/WEB-SPEC.md §12「新增 store 時，SQLite 合約子類為必要項」）。
///
/// 這三個 store 自 P3 起由整份 JSON blob 換成真表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 B）。
/// **換的是儲存方式，不是語意**——這組測試釘住的就是那些必須逐位不變的語意：
///   - 「缺列即未處理」（清除＝刪列，不留狀態為空的殭屍列）
///   - 同一 (主機, 日期, 問題) 至多一列
///   - 主機名比對不分大小寫（跨 provider collation 一致，靠 host_name_key 正規化欄位）
///   - 案件的進行中／結案語意（結案後 GetOpen 查不到，但歷史仍在）
/// 語意一旦破掉，症狀多半是「處理狀態看起來消失了」或「同一問題出現兩筆狀態」，
/// 兩者都不會報錯，只會讓人以為資料壞了。
/// </summary>
public class HandlingStoreContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueHandlingStore Issues() => new(_fx.NewContext);
    private EfIssueCaseStore Cases() => new(_fx.NewContext);
    private EfRecordHandlingStore Days() => new(_fx.NewContext, _fx.LogStore("handling_log"));

    private static IssueHandling Handling(string host, DateTime date, string key, string status) => new()
    {
        HostName = host, Date = date, IssueKey = key, Status = status,
        ActorAccount = "tester", UpdatedAt = DateTime.Now
    };

    // ── 問題層級處理狀態 ────────────────────────────────────────────────────

    [Fact]
    public void 問題狀態_同一鍵重複寫入只留一列()
    {
        var store = Issues();
        var date = DateTime.Today;

        store.Save(Handling("SRV-01", date, "k1", "in_progress"));
        store.Save(Handling("SRV-01", date, "k1", "resolved"));

        var forDay = store.GetForDay("SRV-01", date);
        Assert.Single(forDay);
        Assert.Equal("resolved", forDay[0].Status);
    }

    /// <summary>空狀態＝清除標記，回到「缺列即未處理」——不留一列狀態為空的殭屍資料</summary>
    [Fact]
    public void 問題狀態_空狀態刪列而非留空狀態()
    {
        var store = Issues();
        var date = DateTime.Today;
        store.Save(Handling("SRV-01", date, "k1", "resolved"));

        store.Save(Handling("SRV-01", date, "k1", ""));

        Assert.Empty(store.GetForDay("SRV-01", date));
    }

    [Fact]
    public void 問題狀態_Clear刪除指定問題不影響同日其他問題()
    {
        var store = Issues();
        var date = DateTime.Today;
        store.SaveMany(new[] { Handling("SRV-01", date, "k1", "resolved"), Handling("SRV-01", date, "k2", "wont_fix") });

        store.Clear("SRV-01", date, "k1");

        var remaining = store.GetForDay("SRV-01", date);
        Assert.Single(remaining);
        Assert.Equal("k2", remaining[0].IssueKey);
    }

    /// <summary>
    /// 主機名比對不分大小寫。這條是**跨後端**的關鍵：C# 全站用 OrdinalIgnoreCase，
    /// 但 SQLite 預設 BINARY（區分大小寫）、SqlServer 常見 CI（不分）——
    /// 不做正規化的話同一份資料在兩個後端行為不同。
    /// </summary>
    [Fact]
    public void 問題狀態_主機名比對不分大小寫()
    {
        var store = Issues();
        var date = DateTime.Today;
        store.Save(Handling("SRV-01", date, "k1", "resolved"));

        Assert.Single(store.GetForDay("srv-01", date));
        Assert.Single(store.GetMany(new[] { "SrV-01" }, date.AddDays(-1), date.AddDays(1)));

        // 大小寫不同也視為同一鍵——不該產生第二列
        store.Save(Handling("srv-01", date, "k1", "wont_fix"));
        Assert.Single(store.GetForDay("SRV-01", date));
    }

    [Fact]
    public void 問題狀態_GetMany依日期區間與主機集合過濾()
    {
        var store = Issues();
        var d0 = DateTime.Today.AddDays(-5);
        store.SaveMany(new[]
        {
            Handling("SRV-01", d0, "k1", "resolved"),
            Handling("SRV-01", d0.AddDays(10), "k1", "resolved"),
            Handling("SRV-02", d0, "k1", "resolved")
        });

        var result = store.GetMany(new[] { "SRV-01" }, d0.AddDays(-1), d0.AddDays(1));

        Assert.Single(result);
        Assert.Equal("SRV-01", result[0].HostName);
    }

    [Fact]
    public void 問題狀態_SaveMany混合新增更新與清除()
    {
        var store = Issues();
        var d1 = DateTime.Today;
        var d2 = d1.AddDays(1);
        var d3 = d1.AddDays(2);
        store.Save(Handling("SRV-01", d1, "k1", "open"));

        store.SaveMany(new[]
        {
            new IssueHandling { HostName = "SRV-01", Date = d1, IssueKey = "k1", Status = "in_progress", CaseId = "c1", UpdatedAt = DateTime.Now },
            new IssueHandling { HostName = "SRV-01", Date = d2, IssueKey = "k1", Status = "in_progress", CaseId = "c1", UpdatedAt = DateTime.Now },
            new IssueHandling { HostName = "SRV-01", Date = d3, IssueKey = "k1", Status = "", UpdatedAt = DateTime.Now }
        });

        Assert.Equal("in_progress", store.GetForDay("SRV-01", d1).Single().Status);
        Assert.Empty(store.GetForDay("SRV-01", d3));
        Assert.Equal(2, store.GetByCase("c1").Count);
    }

    [Fact]
    public void 問題狀態_更新既有列時DueDate與CaseId也要落盤()
    {
        var store = Issues();
        var date = DateTime.Today;
        store.Save(Handling("SRV-01", date, "k1", "in_progress"));
        Assert.Null(store.GetForDay("SRV-01", date).Single().DueDate);

        var due = date.AddDays(7);
        store.Save(new IssueHandling
        {
            HostName = "SRV-01", Date = date, IssueKey = "k1", Status = "in_progress",
            DueDate = due, CaseId = "c1", ActorAccount = "tester", UpdatedAt = DateTime.Now
        });

        var updated = store.GetForDay("SRV-01", date).Single();
        Assert.Equal(due, updated.DueDate);
        Assert.Equal("c1", updated.CaseId);
    }

    // ── 案件 ────────────────────────────────────────────────────────────────

    private static IssueCase NewCase(string caseId, string host, string key, long handler) => new()
    {
        CaseId = caseId, HostName = host, IssueKey = key, IssueLabel = "Disk 153",
        Status = IssueHandlingStatuses.InProgress, HandlerId = handler,
        FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
        CreatedAt = DateTime.Now, CreatedByAccount = "admin", UpdatedAt = DateTime.Now
    };

    [Fact]
    public void 案件_進行中與結案語意()
    {
        var store = Cases();
        var c = NewCase("c1", "SRV-01", "k1", 42);
        store.Save(c);

        Assert.NotNull(store.GetOpen("SRV-01", "k1"));
        Assert.Single(store.GetOpenForHost("SRV-01"));
        Assert.Single(store.GetOpenByHandler(42));

        c.ClosedAt = DateTime.Now;
        store.Save(c);

        // 結案後不再算「進行中」，但歷史仍查得到——「上次誰處理的、怎麼結的」不因結案消失
        Assert.Null(store.GetOpen("SRV-01", "k1"));
        Assert.Empty(store.GetOpenByHandler(42));
        Assert.Single(store.GetByHandler(42));
        Assert.Single(store.GetMany(new[] { "SRV-01" }));
    }

    [Fact]
    public void 案件_主機名比對不分大小寫()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "k1", 42));

        Assert.NotNull(store.GetOpen("srv-01", "k1"));
        Assert.Single(store.GetOpenForHost("SrV-01"));
        Assert.Single(store.GetMany(new[] { "srv-01" }));
    }

    /// <summary>建案當下的事實（主機／簽章／建立者／建立時間）不因後續更新被覆寫</summary>
    [Fact]
    public void 案件_更新不動建案當下的事實欄位()
    {
        var store = Cases();
        var created = DateTime.Now.AddDays(-3);
        var c = NewCase("c1", "SRV-01", "k1", 42);
        c.CreatedAt = created;
        store.Save(c);

        store.Save(new IssueCase
        {
            CaseId = "c1", HostName = "改過的名字", IssueKey = "改過的鍵", IssueLabel = "改過",
            Status = IssueHandlingStatuses.Resolved, HandlerId = 7,
            FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
            CreatedAt = DateTime.Now, CreatedByAccount = "someone-else", UpdatedAt = DateTime.Now
        });

        var stored = store.Get("c1")!;
        Assert.Equal("SRV-01", stored.HostName);
        Assert.Equal("k1", stored.IssueKey);
        Assert.Equal("admin", stored.CreatedByAccount);
        Assert.Equal(created, stored.CreatedAt);
        // 可變欄位確實有更新
        Assert.Equal(7, stored.HandlerId);
        Assert.Equal(IssueHandlingStatuses.Resolved, stored.Status);
    }

    [Fact]
    public void 案件_SaveMany批次新增與更新()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "k1", 1));

        var updated = NewCase("c1", "SRV-01", "k1", 1);
        updated.LastLinkedDate = DateTime.Today.AddDays(3);
        store.SaveMany(new[] { updated, NewCase("c2", "SRV-02", "k2", 2) });

        Assert.Equal(DateTime.Today.AddDays(3), store.Get("c1")!.LastLinkedDate);
        Assert.NotNull(store.Get("c2"));
    }

    [Fact]
    public void 案件_SourceName與EventId由IssueKey自動算出_四段與五段鍵()
    {
        var store = Cases();
        var four = NewCase("c4", "SRV-01", "System|Disk|153|2", 1);
        four.SourceName = "呼叫端亂填";
        four.EventId = 999;
        store.Save(four);
        store.SaveMany(new[] { NewCase("c5", "SRV-02", "auth|sshd|0|4|ssh-bruteforce", 1), NewCase("bad", "SRV-03", "abc", 1) });

        var got4 = store.Get("c4")!;
        Assert.Equal("Disk", got4.SourceName);
        Assert.Equal(153, got4.EventId);
        var got5 = store.Get("c5")!;
        Assert.Equal("sshd", got5.SourceName);
        Assert.Equal(0, got5.EventId);
        var bad = store.Get("bad")!;
        Assert.Null(bad.SourceName);
        Assert.Null(bad.EventId);
    }

    [Fact]
    public void 案件_交辦單欄位往返()
    {
        var store = Cases();
        var c = NewCase("c1", "SRV-01", "System|Disk|153|2", 1);
        c.WorkOrderId = 12;
        c.DaySyncPending = true;
        store.Save(c);

        c.Cancelled = true;
        c.DaySyncPending = false;
        store.Save(c);

        var got = store.Get("c1")!;
        Assert.Equal(12, got.WorkOrderId);
        Assert.False(got.DaySyncPending);
        Assert.True(got.Cancelled);
    }

    /// <summary>不變式：連結後只會換成另一張單，永遠不會變回 null（背景整併寫連結時刻意不動 updated_at，沒有併發衝突可擋）</summary>
    [Fact]
    public void 案件_舊模型存檔不抹掉整併寫入的交辦單連結()
    {
        var store = Cases();
        var stale = NewCase("c1", "SRV-01", "System|Disk|153|2", 1);
        store.Save(stale);

        using (var ctx = _fx.NewContext())
        {
            Microsoft.EntityFrameworkCore.RelationalQueryableExtensions.ExecuteUpdate(
                ctx.IssueCases.Where(c => c.CaseId == "c1"),
                s => s.SetProperty(c => c.WorkOrderId, (long?)77));
        }

        stale.Status = IssueHandlingStatuses.Resolved;
        Assert.Null(stale.WorkOrderId);
        store.Save(stale);

        var got = store.Get("c1")!;
        Assert.Equal(77, got.WorkOrderId);
        Assert.Equal(IssueHandlingStatuses.Resolved, got.Status);
    }

    [Fact]
    public void 案件_GetOpenByIssue大小寫不敏感且只回進行中()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "System|Disk|153|2", 1));
        store.Save(NewCase("c2", "SRV-02", "Application|disk|153|1", 2));
        var closed = NewCase("c3", "SRV-03", "System|Disk|153|2", 1);
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);
        store.Save(NewCase("c4", "SRV-04", "System|Disk|154|2", 1));

        var open = store.GetOpenByIssue("DISK", 153);

        Assert.Equal(new[] { "c1", "c2" }, open.Select(x => x.CaseId).OrderBy(x => x));
    }

    [Fact]
    public void 案件_GetOpenMany只回指定主機與問題的進行中案件()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "System|Disk|153|2", 1));
        store.Save(NewCase("c2", "SRV-02", "System|Disk|153|2", 1));   // 主機不在清單
        store.Save(NewCase("c3", "SRV-01", "System|DCOM|10016|1", 1)); // 問題不同
        var closed = NewCase("c4", "SRV-03", "System|Disk|153|2", 1);
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);
        store.Save(NewCase("c5", "SRV-04", "System|Disk|153|2", 1));

        var result = store.GetOpenMany(new[] { "srv-01", "SRV-03", "Srv-04" }, "disk", 153);

        Assert.Equal(new[] { "c1", "c5" }, result.Select(x => x.CaseId).OrderBy(x => x));
    }

    [Fact]
    public void 案件_GetByWorkOrder分頁與CountByWorkOrder()
    {
        var store = Cases();
        for (var i = 0; i < 5; i++)
        {
            var c = NewCase("c" + i, "SRV-0" + i, "System|Disk|153|2", 1);
            c.WorkOrderId = 7;
            if (i == 4) c.ClosedAt = DateTime.Now;   // 已結案成員也算
            store.Save(c);
        }
        var other = NewCase("x", "SRV-09", "System|Disk|153|2", 1);
        other.WorkOrderId = 8;
        store.Save(other);

        var page1 = store.GetByWorkOrder(7, 0, 3);
        var page2 = store.GetByWorkOrder(7, 3, 3);

        Assert.Equal(5, store.CountByWorkOrder(7));
        Assert.Equal(3, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Equal(5, page1.Concat(page2).Select(x => x.CaseId).Distinct().Count());
        Assert.Equal(0, store.CountByWorkOrder(99));
    }

    // ── 日層級快照 ──────────────────────────────────────────────────────────

    private static CaseDayIntent DayIntent(string note) => new()
    {
        Mode = CaseDayModes.Sync, Status = IssueHandlingStatuses.Observing, Note = note,
        DueDate = new DateTime(2026, 9, 30), Clearing = false, ActorId = 7, ActorAccount = "acc",
        OccurredAt = new DateTime(2026, 9, 17, 10, 30, 0), TriggerDate = new DateTime(2026, 9, 16)
    };

    [Fact]
    public void 案件_逐日同步意圖JSON往返()
    {
        var store = Cases();
        var c = NewCase("c1", "SRV-01", "System|Disk|153|2", 1);
        c.DaySyncPending = true;
        c.DaySyncIntent = DayIntent("中文備註");
        store.Save(c);

        var got = store.Get("c1")!;
        Assert.True(got.DaySyncPending);
        var intent = got.DaySyncIntent!;
        Assert.Equal(CaseDayModes.Sync, intent.Mode);
        Assert.Equal(IssueHandlingStatuses.Observing, intent.Status);
        Assert.Equal("中文備註", intent.Note);
        Assert.Equal(new DateTime(2026, 9, 30), intent.DueDate);
        Assert.False(intent.Clearing);
        Assert.Equal(7, intent.ActorId);
        Assert.Equal("acc", intent.ActorAccount);
        Assert.Equal(new DateTime(2026, 9, 17, 10, 30, 0), intent.OccurredAt);
        Assert.Equal(new DateTime(2026, 9, 16), intent.TriggerDate);

        // SaveMany 同樣寫入；清成 null 也要寫回
        got.DaySyncPending = false;
        got.DaySyncIntent = null;
        store.SaveMany(new[] { got });
        Assert.Null(store.Get("c1")!.DaySyncIntent);
    }

    [Fact]
    public void 案件_GetDaySyncPending依更新時間與案件編號排序_take與Count()
    {
        var store = Cases();
        var t = new DateTime(2026, 9, 17, 8, 0, 0);
        IssueCase Pending(string id, DateTime updatedAt, bool pending)
        {
            var c = NewCase(id, "SRV-" + id, "System|Disk|153|2", 1);
            c.UpdatedAt = updatedAt;
            c.DaySyncPending = pending;
            c.DaySyncIntent = pending ? DayIntent(id) : null;
            return c;
        }
        store.SaveMany(new[]
        {
            Pending("c3", t.AddMinutes(1), true),
            Pending("c2", t, true),
            Pending("c1", t, true),
            Pending("c0", t.AddMinutes(-5), false)
        });

        Assert.Equal(new[] { "c1", "c2" }, store.GetDaySyncPending(2).Select(c => c.CaseId));
        Assert.Equal(new[] { "c1", "c2", "c3" }, store.GetDaySyncPending(10).Select(c => c.CaseId));
        Assert.Equal(3, store.CountDaySyncPending());
    }

    [Fact]
    public void 案件_ClearDaySyncPendingIfUnchanged_相同意圖才清()
    {
        var store = Cases();
        var c = NewCase("c1", "SRV-01", "System|Disk|153|2", 1);
        c.DaySyncPending = true;
        c.DaySyncIntent = DayIntent("B");
        store.Save(c);

        Assert.False(store.ClearDaySyncPendingIfUnchanged("c1", DayIntent("A")));
        var still = store.Get("c1")!;
        Assert.True(still.DaySyncPending);
        Assert.Equal("B", still.DaySyncIntent!.Note);

        Assert.True(store.ClearDaySyncPendingIfUnchanged("c1", DayIntent("B")));
        var cleared = store.Get("c1")!;
        Assert.False(cleared.DaySyncPending);
        Assert.Null(cleared.DaySyncIntent);
        Assert.Equal(c.UpdatedAt, cleared.UpdatedAt);   // 背景清旗標不動樂觀鎖欄位

        Assert.False(store.ClearDaySyncPendingIfUnchanged("c1", DayIntent("B")));
    }

    [Fact]
    public void 問題狀態_GetByCases依案件id精確查_跨批()
    {
        var store = Issues();
        var rows = new List<IssueHandling>();
        for (var i = 0; i < 520; i++)
        {
            var h = Handling("SRV-" + i, DateTime.Today.AddDays(-i % 30), "k1", "in_progress");
            h.CaseId = "c" + i;
            rows.Add(h);
        }
        var orphan = Handling("SRV-X", DateTime.Today, "k1", "in_progress");
        rows.Add(orphan);
        store.SaveMany(rows);

        var ids = Enumerable.Range(0, 510).Select(i => "c" + i).ToList();
        var got = store.GetByCases(ids);

        Assert.Equal(510, got.Count);
        Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), got.Select(g => g.CaseId!).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(store.GetByCases(Array.Empty<string>()));
    }

    [Fact]
    public void 日狀態_快照與歷程往返()
    {
        var store = Days();
        var date = DateTime.Today;

        store.Save(new RecordHandling { HostName = "SRV-01", Date = date, Status = "in_progress", UpdatedAt = DateTime.Now });
        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Status = "in_progress", Action = "status" });

        Assert.Equal("in_progress", store.Get("SRV-01", date)!.Status);
        Assert.Single(store.GetLogs("SRV-01", date));
    }

    [Fact]
    public void 日狀態_同一主機日重複寫入只留一列()
    {
        var store = Days();
        var date = DateTime.Today;

        store.Save(new RecordHandling { HostName = "SRV-01", Date = date, Status = "in_progress", HandlerId = 1, UpdatedAt = DateTime.Now });
        store.Save(new RecordHandling { HostName = "srv-01", Date = date, Status = "resolved", HandlerId = 2, UpdatedAt = DateTime.Now });

        Assert.Single(store.GetMany(new[] { "SRV-01" }, date, date));
        Assert.Equal("resolved", store.Get("SRV-01", date)!.Status);
        Assert.Equal(2, store.Get("SRV-01", date)!.HandlerId);
    }

    [Fact]
    public void 日狀態_GetUnresolved只回未結案()
    {
        var store = Days();
        var date = DateTime.Today;
        store.Save(new RecordHandling { HostName = "SRV-01", Date = date, Status = HandlingStatuses.InProgress, UpdatedAt = DateTime.Now });
        store.Save(new RecordHandling { HostName = "SRV-02", Date = date, Status = HandlingStatuses.Resolved, UpdatedAt = DateTime.Now });

        var unresolved = store.GetUnresolved();

        Assert.Single(unresolved);
        Assert.Equal("SRV-01", unresolved[0].HostName);
    }

    /// <summary>
    /// 歷程序號續號：改版後不再於建構式整份讀取（N4），改為第一次寫入時只讀最後一行。
    /// 這條測試釘住「新的 store 實例接續既有序號，不會從 1 重新開始」——
    /// 重號會讓同一天的歷程排序錯亂。
    /// </summary>
    [Fact]
    public void 日狀態_歷程序號跨store實例續號()
    {
        var date = DateTime.Today;
        Days().AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "a" });
        Days().AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "b" });

        var logs = Days().GetLogs("SRV-01", date);

        Assert.Equal(2, logs.Count);
        Assert.Equal(new long[] { 1, 2 }, logs.Select(l => l.LogId));
    }
    [Fact]
    public void 日狀態_多個獨立實例交互寫入不重號()
    {
        var date = DateTime.Today;
        var storeA = Days();
        var storeB = Days();

        // A 先寫入，建立快取（如果在舊版有快取的情況下）
        storeA.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "a" });
        // B 寫入
        storeB.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "b" });
        // A 再寫入，如果 A 的快取沒有被移除，它會從它上次的紀錄續號，導致重號
        storeA.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "c" });

        var logs = Days().GetLogs("SRV-01", date);

        Assert.Equal(3, logs.Count);
        // LogId 必須依序遞增，不重號
        Assert.True(logs[1].LogId > logs[0].LogId);
        Assert.True(logs[2].LogId > logs[1].LogId);
    }

    [Fact]
    public void 歷程批次附加_續號接續既有且依序遞增()
    {
        var store = Days();
        var date = DateTime.Today;

        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "a1" });
        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "a2" });
        store.AppendLogs(new[]
        {
            new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "b1" },
            new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "b2" },
            new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "b3" }
        });
        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "c1" });

        var logs = store.GetLogs("SRV-01", date);

        Assert.Equal(6, logs.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, logs.Select(l => l.LogId));
        Assert.Equal(new[] { "a1", "a2", "b1", "b2", "b3", "c1" }, logs.Select(l => l.Action));
    }

    [Fact]
    public void 歷程批次附加_空清單不寫入()
    {
        var store = Days();
        var date = DateTime.Today;

        store.AppendLogs(Array.Empty<RecordHandlingLog>());

        var logs = store.GetLogs("SRV-01", date);
        Assert.Empty(logs);
    }

    [Fact]
    public void 歷程批次附加_超過一批大小仍全數寫入且續號連續()
    {
        var store = Days();
        var date = DateTime.Today;
        const int count = 2500;
        var batch = Enumerable.Range(1, count)
            .Select(i => new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "act_" + i })
            .ToList();

        store.AppendLogs(batch);

        var logs = store.GetLogs("SRV-01", date);
        Assert.Equal(count, logs.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i + 1, logs[i].LogId);
            Assert.Equal("act_" + (i + 1), logs[i].Action);
        }
    }

    /// <summary>
    /// 尾端 LogId 不照順序（SQL Server 批次插入時 seq 不保證照清單順序配發）：續號要接在回看窗內的最大值之後，
    /// 不能只看最後一行——最後一行是 3、實際最大是 5 時，取最後一行會發出 4、5 兩個重號。
    /// </summary>
    [Fact]
    public void 歷程續號_尾端順序錯亂仍接最大值不重號()
    {
        var date = DateTime.Today;
        var raw = _fx.LogStore("handling_log");
        raw.AppendLines(new[] { 1L, 2L, 5L, 4L, 3L }
            .Select(id => System.Text.Json.JsonSerializer.Serialize(
                new RecordHandlingLog { LogId = id, HostName = "SRV-01", Date = date, Action = "seed" + id, CreatedAt = DateTime.Now },
                LfJsonOptions.Compact))
            .ToList());

        var store = Days();
        store.AppendLog(new RecordHandlingLog { HostName = "SRV-01", Date = date, Action = "next" });

        var next = store.GetLogs("SRV-01", date).Single(l => l.Action == "next");
        Assert.Equal(6, next.LogId);
    }

    // ── 派工脈絡用：GetResolvedSince／NoiseMarkStore.GetAll／旗標舊資料相容 ──────────

    private static void SeedResolvedSinceCases(IIssueCaseStore store, DateTime since)
    {
        var inRange = NewCase("in", "SRV-01", "k1", 1);
        inRange.Status = IssueHandlingStatuses.Resolved;
        inRange.ClosedAt = since;
        store.Save(inRange);

        var tooOld = NewCase("old", "SRV-01", "k2", 1);
        tooOld.Status = IssueHandlingStatuses.Resolved;
        tooOld.ClosedAt = since.AddSeconds(-1);
        store.Save(tooOld);

        var otherStatus = NewCase("other", "SRV-01", "k3", 1);
        otherStatus.Status = IssueHandlingStatuses.Escalated;
        otherStatus.ClosedAt = since.AddDays(1);
        store.Save(otherStatus);

        store.Save(NewCase("open", "SRV-01", "k4", 1));
    }

    [Fact]
    public void 案件_GetResolvedSince只收resolved且結案時間在期間內()
    {
        var since = DateTime.Today.AddDays(-30);
        var store = Cases();
        SeedResolvedSinceCases(store, since);

        Assert.Equal(new[] { "in" }, store.GetResolvedSince(since).Select(c => c.CaseId));
    }

    [Fact]
    public void 案件_GetResolvedSince替身語意同EF版()
    {
        var since = DateTime.Today.AddDays(-30);
        var store = new FakeIssueCaseStore();
        SeedResolvedSinceCases(store, since);

        Assert.Equal(new[] { "in" }, store.GetResolvedSince(since).Select(c => c.CaseId));
    }

    [Fact]
    public void 已知雜訊_GetAll回全部記憶()
    {
        var store = new NoiseMarkStore(_fx.Blob("noise_marks"));
        store.Save(new NoiseMark { HostName = "SRV-01", IssueKey = "k1" });
        store.Save(new NoiseMark { HostName = "SRV-02", IssueKey = "k2" });

        var all = store.GetAll().Select(m => (m.HostName, m.IssueKey)).OrderBy(x => x.HostName).ToList();

        Assert.Equal(new[] { ("SRV-01", "k1"), ("SRV-02", "k2") }, all);
    }

    [Fact]
    public void 舊使用者blob缺DispatchPaused反序列化為false()
    {
        _fx.Blob("users").Mutate(_ => ("[{\"UserId\":1,\"Account\":\"a\",\"Active\":true}]", 0));

        var user = Assert.Single(new UserStore(_fx.Blob("users"), _fx.Blob("users_last_login")).GetAll());
        Assert.False(user.DispatchPaused);
    }

    [Fact]
    public void 舊群組blob缺DispatchPool反序列化為false()
    {
        _fx.Blob("user_groups").Mutate(_ => ("[{\"GroupId\":1,\"GroupName\":\"dept\",\"Role\":\"User\",\"Active\":true}]", 0));

        var group = Assert.Single(new UserGroupStore(_fx.Blob("user_groups")).GetAll());
        Assert.False(group.DispatchPool);
    }

    // ── 案件 QueryMembers（回饋第 47 輪 C-2a）──────────────────────────────────────

    private sealed class ReaderCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Readers { get; set; }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Readers++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private EfIssueCaseStore CountingCases(ReaderCounter counter)
    {
        using var probe = _fx.NewContext();
        var connection = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(probe.Database);
        return new EfIssueCaseStore(() => new LfDbContext(
            Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions
                .UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LfDbContext>(), connection)
                .AddInterceptors(counter).Options));
    }

    /// <summary>單 7：SRV-00～SRV-(n-1)，每 5 台有一台已結案、每 7 台有一台 escalated、SRV-03 逾期；另有單 8 的一件</summary>
    private static void SeedMembers(IIssueCaseStore store, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var c = NewCase($"c{i:D4}", $"srv-{i:D2}", "System|Disk|153|2", 1);
            c.WorkOrderId = 7;
            if (i % 5 == 4) { c.Status = IssueHandlingStatuses.Resolved; c.ClosedAt = DateTime.Now; }
            else if (i % 7 == 6) c.Status = IssueHandlingStatuses.Escalated;
            if (i == 3) c.DueDate = DateTime.Today.AddDays(-1);
            store.Save(c);
        }
        var other = NewCase("x", "SRV-01", "System|Disk|153|2", 1);
        other.WorkOrderId = 8;
        store.Save(other);
    }

    [Theory]
    [InlineData("all", null, 1, 5)]
    [InlineData("all", null, 3, 5)]
    [InlineData("active", null, 1, 200)]
    [InlineData("closed", null, 1, 200)]
    [InlineData("escalated", null, 1, 200)]
    [InlineData("overdue", null, 1, 200)]
    [InlineData("all", "SRV-01,srv-02,Srv-10,SRV-99", 1, 2)]
    [InlineData("all", "SRV-01,srv-02,Srv-10,SRV-99", 2, 2)]
    [InlineData("active", "srv-04,srv-03,srv-06", 1, 200)]
    public void 案件_QueryMembers_EF與替身同資料結果一致(string status, string? hosts, int page, int pageSize)
    {
        var ef = Cases();
        SeedMembers(ef, 20);
        var fake = new FakeIssueCaseStore();
        SeedMembers(fake, 20);

        var q = new WorkOrderMemberQuery
        {
            WorkOrderId = 7, Status = status, HostNameKeys = hosts?.Split(','), Page = page, PageSize = pageSize
        };
        var efResult = ef.QueryMembers(q);
        var fakeResult = fake.QueryMembers(q);

        Assert.Equal(fakeResult.Total, efResult.Total);
        Assert.Equal(fakeResult.Items.Select(c => c.CaseId), efResult.Items.Select(c => c.CaseId));
        Assert.NotEqual(0, efResult.Total);
    }

    [Fact]
    public void 案件_QueryMembers_主機名過濾與分頁_排序依主機鍵與案件編號()
    {
        var store = Cases();
        SeedMembers(store, 20);

        var q = new WorkOrderMemberQuery { WorkOrderId = 7, Status = "all", HostNameKeys = new[] { "srv-10", "SRV-01", "srv-02", "nope" }, PageSize = 2 };
        var p1 = store.QueryMembers(q);
        var p2 = store.QueryMembers(new WorkOrderMemberQuery { WorkOrderId = 7, Status = "all", HostNameKeys = q.HostNameKeys, Page = 2, PageSize = 2 });

        Assert.Equal(3, p1.Total);
        Assert.Equal(new[] { "c0001", "c0002" }, p1.Items.Select(c => c.CaseId));
        Assert.Equal(new[] { "c0010" }, p2.Items.Select(c => c.CaseId));
        Assert.Equal(new[] { "c0003" }, store.QueryMembers(new WorkOrderMemberQuery { WorkOrderId = 7, Status = "overdue" }).Items.Select(c => c.CaseId));
        Assert.Throws<ArgumentException>(() => store.QueryMembers(new WorkOrderMemberQuery { WorkOrderId = 7, PageSize = 201 }));
    }

    [Fact]
    public void 案件_QueryMembers_超過一批主機名仍正確分頁()
    {
        var store = Cases();
        SeedMembers(store, 30);
        var keys = Enumerable.Range(0, 1200).Select(i => $"SRV-{i:D2}").Reverse().ToList();

        var result = store.QueryMembers(new WorkOrderMemberQuery { WorkOrderId = 7, Status = "all", HostNameKeys = keys, Page = 2, PageSize = 10 });

        Assert.Equal(30, result.Total);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => $"c{i:D4}"), result.Items.Select(c => c.CaseId));
    }

    [Fact]
    public void 案件_QueryMembers_命令數不隨成員數增長()
    {
        var counter = new ReaderCounter();
        var store = CountingCases(counter);
        SeedMembers(store, 10);

        int Run(IReadOnlyCollection<string>? keys)
        {
            counter.Readers = 0;
            store.QueryMembers(new WorkOrderMemberQuery { WorkOrderId = 7, Status = "active", HostNameKeys = keys, PageSize = 200 });
            return counter.Readers;
        }

        var hostKeys = Enumerable.Range(0, 400).Select(i => $"SRV-{i:D2}").ToList();
        var smallAll = Run(null);
        var smallHosts = Run(hostKeys);

        for (var i = 10; i < 300; i++)
        {
            var c = NewCase($"c{i:D4}", $"srv-{i:D2}", "System|Disk|153|2", 1);
            c.WorkOrderId = 7;
            store.Save(c);
        }

        Assert.Equal(2, smallAll);
        Assert.Equal(2, smallHosts);
        Assert.Equal(smallAll, Run(null));
        Assert.Equal(smallHosts, Run(hostKeys));
    }

    private static void SeedOpenKeys(IIssueCaseStore store)
    {
        store.Save(NewCase("o1", "srv-01", "System|Disk|153|1", 1));
        store.Save(NewCase("o2", "SRV-02", "System|Ntfs|55|1", 2));
        var closed = NewCase("o3", "SRV-03", "System|Disk|153|1", 1);
        closed.Status = IssueHandlingStatuses.Resolved;
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);
    }

    [Fact]
    public void 案件_GetOpenKeys_EF只回進行中且單一查詢()
    {
        var counter = new ReaderCounter();
        var store = CountingCases(counter);
        SeedOpenKeys(store);

        counter.Readers = 0;
        var keys = store.GetOpenKeys().OrderBy(k => k.HostNameKey).ToList();

        Assert.Equal(1, counter.Readers);
        Assert.Equal(new[] { ("SRV-01", "System|Disk|153|1"), ("SRV-02", "System|Ntfs|55|1") }, keys);
    }

    [Fact]
    public void 案件_GetOpenKeys_替身只回進行中且單一呼叫()
    {
        var store = new FakeIssueCaseStore();
        SeedOpenKeys(store);

        var keys = store.GetOpenKeys().OrderBy(k => k.HostNameKey).ToList();

        Assert.Equal(1, store.GetOpenKeysCalls);
        Assert.Equal(new[] { ("SRV-01", "System|Disk|153|1"), ("SRV-02", "System|Ntfs|55|1") }, keys);
    }

    [Fact]
    public void 案件_HasCaseOnHost_含已結案且大小寫不敏感且排除他人()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "System|Disk|153|1", 42));

        var closed = NewCase("c2", "SRV-02", "System|Ntfs|55|1", 42);
        closed.Status = IssueHandlingStatuses.Resolved;
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);

        store.Save(NewCase("c3", "SRV-03", "System|Disk|153|1", 99));

        // 進行中、大小寫不同
        Assert.True(store.HasCaseOnHost(42, "srv-01"));
        Assert.True(store.HasCaseOnHost(42, "SRV-01"));

        // 已結案
        Assert.True(store.HasCaseOnHost(42, "srv-02"));
        Assert.True(store.HasCaseOnHost(42, "SRV-02"));

        // 他人的案件不算
        Assert.False(store.HasCaseOnHost(42, "srv-03"));

        // 不存在的主機
        Assert.False(store.HasCaseOnHost(42, "NON-EXISTENT"));
        // 查無案件的人
        Assert.False(store.HasCaseOnHost(12345, "SRV-01"));
    }

    [Fact]
    public void 案件_IssueKeysOnHost_含已結案且大小寫不敏感且排除他人()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "System|Disk|153|1", 42));

        var closed = NewCase("c2", "srv-01", "System|Ntfs|55|1", 42);
        closed.Status = IssueHandlingStatuses.Resolved;
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);

        // 他人在同一台主機上的案件
        store.Save(NewCase("c3", "SRV-01", "System|Cpu|100|1", 99));

        // 大小寫不同主機名查詢，含進行中與已結案，排除他人
        var keys = store.IssueKeysOnHost(42, "SrV-01");
        Assert.Equal(2, keys.Count);
        Assert.Contains("System|Disk|153|1", keys);
        Assert.Contains("System|Ntfs|55|1", keys);
        Assert.DoesNotContain("System|Cpu|100|1", keys);

        // 他人查詢
        var otherKeys = store.IssueKeysOnHost(99, "srv-01");
        Assert.Equal(new[] { "System|Cpu|100|1" }, otherKeys);

        // 查無案件主機
        Assert.Empty(store.IssueKeysOnHost(42, "OTHER-HOST"));
    }

    [Fact]
    public void 案件_HostNamesWithCases_含已結案且排除他人()
    {
        var store = Cases();
        store.Save(NewCase("c1", "SRV-01", "System|Disk|153|1", 42));

        var closed = NewCase("c2", "SRV-02", "System|Ntfs|55|1", 42);
        closed.Status = IssueHandlingStatuses.Resolved;
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);

        // 同主機第二筆，驗證 Distinct
        store.Save(NewCase("c3", "SRV-01", "System|Ntfs|55|1", 42));

        // 他人案件
        store.Save(NewCase("c4", "SRV-03", "System|Disk|153|1", 99));

        var hosts42 = store.HostNamesWithCases(42).OrderBy(h => h).ToList();
        Assert.Equal(new[] { "SRV-01", "SRV-02" }, hosts42);

        var hosts99 = store.HostNamesWithCases(99);
        Assert.Equal(new[] { "SRV-03" }, hosts99);

        Assert.Empty(store.HostNamesWithCases(12345));
    }
}

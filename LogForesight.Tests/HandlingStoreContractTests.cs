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

        var user = Assert.Single(new UserStore(_fx.Blob("users")).GetAll());
        Assert.False(user.DispatchPaused);
    }

    [Fact]
    public void 舊群組blob缺DispatchPool反序列化為false()
    {
        _fx.Blob("user_groups").Mutate(_ => ("[{\"GroupId\":1,\"GroupName\":\"dept\",\"Role\":\"User\",\"Active\":true}]", 0));

        var group = Assert.Single(new UserGroupStore(_fx.Blob("user_groups")).GetAll());
        Assert.False(group.DispatchPool);
    }
}

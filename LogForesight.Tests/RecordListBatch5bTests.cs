using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;
using Microsoft.EntityFrameworkCore;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 5b（5-7）：明細清單處理狀態推導的處理列查找、上限檢查前移、執行預覽窄查詢。
/// 原則是「不改運作邏輯」：清單每個欄位、錯誤訊息與改動前相同。
/// </summary>
public sealed class RecordListBatch5bTests
{
    private static readonly DateTime Day1 = new(2026, 8, 20);

    private readonly ITestOutputHelper _output;
    private readonly FakeHostStore _hosts = new();

    public RecordListBatch5bTests(ITestOutputHelper output) => _output = output;

    /// <summary>可以回傳「同一主機同一天兩筆」的處理列替身（真實 store 不會有，但清單查找規則是「清單順序中的第一筆」）。</summary>
    private sealed class ListHandlingStore(List<RecordHandling> items) : IRecordHandlingStore
    {
        public RecordHandling? Get(string hostName, DateTime date) => throw new NotSupportedException();
        public List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
        {
            var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return items.Where(h => names.Contains(h.HostName)).ToList();
        }
        public List<RecordHandling> GetUnresolved() => throw new NotSupportedException();
        public List<RecordHandling> GetByHandler(long userId) => throw new NotSupportedException();
        public void Save(RecordHandling handling) => throw new NotSupportedException();
        public void AppendLog(RecordHandlingLog log) => throw new NotSupportedException();
        public void AppendLogs(IReadOnlyList<RecordHandlingLog> logs) => throw new NotSupportedException();
        public List<RecordHandlingLog> GetLogs(string hostName, DateTime date) => throw new NotSupportedException();
        public List<RecordHandlingLog> GetReassignments(long previousHandlerId, DateTime from) => throw new NotSupportedException();
    }

    /// <summary>快速路徑與慢速路徑都回同一份紀錄的 repository 替身（分頁就是整份）。</summary>
    private sealed class FixedRepository(List<DailyAnalysisRecord> records) : IRecordRepository
    {
        public List<DailyAnalysisRecord> Query(RecordQueryFilter filter, bool applyDayRiskVisibility = true) => records.ToList();
        public List<DailyAnalysisRecord> QueryLightweight(RecordQueryFilter filter) => records.ToList();
        public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false) =>
            new() { Items = records.Skip((page - 1) * pageSize).Take(pageSize).ToList(), Page = page, PageSize = pageSize, Total = records.Count };
        public DailyAnalysisRecord? GetOne(long hostId, DateTime date) => null;
        public List<HostKey> ResolveHostKeys(long hostId) => new();
        public WebHost? ResolveHost(string hostName) => null;
    }

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date) => new()
    {
        HostId = hostId, Host = host, Date = date, RiskLevel = RiskLevels.High,
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Warning,
                    Category = IssueCategory.Hardware, Severity = IssueSeverity.High, Count = 1 }
        }
    };

    private RecordListQueryService Service(IRecordRepository repository, IRecordHandlingStore handlings, int? limit = null)
    {
        var settings = new FakeSystemSettingsStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        return new RecordListQueryService(
            repository, _hosts, new FakeUserStore(), handlings, issueHandlings, cases, settings,
            new FakeSystemSettingsService(), new AlwaysVisibleService(_hosts), new FakeIssueAggregateQuery(),
            new OccurrenceStatusResolver(_hosts, issueHandlings, cases, settings),
            new UserDisplayNameService(settings),
            new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None),
            handlingFilterCandidateLimit: limit);
    }

    private static RecordHandling Handling(string host, DateTime date, string status, long? handler, DateTime? due = null) => new()
    {
        HostName = host, Date = date, Status = status, HandlerId = handler, DueDate = due, UpdatedAt = date
    };

    /// <summary>
    /// A3：同主機同日兩筆取清單順序中的第一筆、主機名大小寫不同、合併前寫在舊識別下的紀錄（處理狀態掛在存活主機名稱下）、
    /// 以名稱歸戶的舊列（HostId=0）、沒有處理列的紀錄。快速路徑與慢速路徑都要得到同一組結果。
    /// </summary>
    [Fact]
    public void A3_處理列查找_重複鍵取第一筆_大小寫不拘_合併與舊列歸戶()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "SRV-A" });
        var survivor = _hosts.Upsert(new WebHost { HostName = "SRV-B" });
        var tombstone = _hosts.Upsert(new WebHost { HostName = "SRV-B-OLD" });
        _hosts.Merge(tombstone.HostId, survivor.HostId);
        _hosts.Upsert(new WebHost { HostName = "Legacy-X" });

        var records = new List<DailyAnalysisRecord>
        {
            Rec(a.HostId, "SRV-A", Day1),                       // 重複鍵：取第一筆（處理中、處理人 11、已逾期）
            Rec(a.HostId, "SRV-A", Day1.AddDays(1)),            // 同主機另一天
            Rec(tombstone.HostId, "SRV-B-OLD", Day1),           // 合併前識別，處理列在存活主機名稱下
            Rec(0, "legacy-x", Day1),                           // 以名稱歸戶的舊列
            Rec(a.HostId, "SRV-A", Day1.AddDays(2)),            // 沒有處理列
        };
        var handlings = new ListHandlingStore(new List<RecordHandling>
        {
            Handling("SRV-A", Day1, HandlingStatuses.InProgress, 11, due: Day1),
            Handling("srv-a", Day1, HandlingStatuses.Resolved, 22),               // 同鍵第二筆：不得被取到
            Handling("srv-a", Day1.AddDays(1), HandlingStatuses.Resolved, 33),    // 主機名大小寫與主機清單不同
            Handling("SRV-B", Day1, HandlingStatuses.Resolved, 44),
            Handling("LEGACY-X", Day1, HandlingStatuses.InProgress, 55),
        });

        foreach (var request in new[]
        {
            new RecordSearchRequest { PageSize = 50 },                                                   // 快速路徑
            new RecordSearchRequest { PageSize = 50, Statuses = new List<string> { "open", "in_progress", "resolved" } }, // 慢速路徑
        })
        {
            var items = Service(new FixedRepository(records), handlings).Search(request).Items;
            Assert.Equal(5, items.Count);

            var first = items.Single(i => i.HostName == "SRV-A" && i.Date == "2026-08-20");
            Assert.Equal("in_progress", first.HandlingStatus);
            Assert.Equal(11, first.HandlerId);
            Assert.True(first.IsOverdue);

            var caseDiff = items.Single(i => i.HostName == "SRV-A" && i.Date == "2026-08-21");
            Assert.Equal("resolved", caseDiff.HandlingStatus);
            Assert.Equal(33, caseDiff.HandlerId);

            var merged = items.Single(i => i.HostName == "SRV-B");
            Assert.Equal(survivor.HostId, merged.HostId);
            Assert.Equal("resolved", merged.HandlingStatus);
            Assert.Equal(44, merged.HandlerId);

            var legacy = items.Single(i => i.HostName == "Legacy-X");
            Assert.Equal("in_progress", legacy.HandlingStatus);
            Assert.Equal(55, legacy.HandlerId);

            var none = items.Single(i => i.HostName == "SRV-A" && i.Date == "2026-08-22");
            Assert.Equal("open", none.HandlingStatus);
            Assert.Null(none.HandlerId);
            Assert.False(none.IsOverdue);
        }
        _output.WriteLine("ok");
    }

    /// <summary>慢速路徑的狀態篩選、逾期篩選、未指派篩選與重複鍵：過濾看到的狀態也是第一筆。</summary>
    [Fact]
    public void A3_處理列查找_狀態逾期與未指派篩選的結果()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "SRV-A" });
        var b = _hosts.Upsert(new WebHost { HostName = "SRV-B" });
        var records = new List<DailyAnalysisRecord>
        {
            Rec(a.HostId, "SRV-A", Day1), Rec(b.HostId, "SRV-B", Day1)
        };
        var handlings = new ListHandlingStore(new List<RecordHandling>
        {
            Handling("SRV-A", Day1, HandlingStatuses.InProgress, 11, due: Day1),
            Handling("srv-a", Day1, HandlingStatuses.Resolved, null),
        });
        var service = Service(new FixedRepository(records), handlings);

        Assert.Equal(new[] { "SRV-A" }, service.Search(new RecordSearchRequest { Statuses = new List<string> { "in_progress" } }).Items.Select(i => i.HostName));
        Assert.Equal(new[] { "SRV-B" }, service.Search(new RecordSearchRequest { Statuses = new List<string> { "open" } }).Items.Select(i => i.HostName));
        Assert.Empty(service.Search(new RecordSearchRequest { Statuses = new List<string> { "resolved" } }).Items);
        Assert.Equal(new[] { "SRV-A" }, service.Search(new RecordSearchRequest { Overdue = true }).Items.Select(i => i.HostName));
        // 未指派：SRV-A 的第一筆有處理人 → 不算未指派；SRV-B 沒有處理列 → 未指派
        Assert.Equal(new[] { "SRV-B" }, service.Search(new RecordSearchRequest { Unassigned = true }).Items.Select(i => i.HostName));
    }

    /// <summary>A3：比對次數隨「紀錄數 + 處理列數」線性成長，不是「紀錄數 × 處理列數」。</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(400)]
    public void A3_處理列查找_比對次數不隨紀錄數乘處理列數成長(int count)
    {
        var records = new List<DailyAnalysisRecord>();
        var handlingRows = new List<RecordHandling>();
        for (var i = 0; i < count; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"SRV-{i:D4}" });
            records.Add(Rec(host.HostId, host.HostName, Day1));
            handlingRows.Add(Handling(host.HostName.ToLowerInvariant(), Day1, HandlingStatuses.InProgress, 7));
        }
        foreach (var request in new[]
        {
            new RecordSearchRequest { PageSize = 1000 },
            new RecordSearchRequest { PageSize = 1000, Statuses = new List<string> { "in_progress" } },
        })
        {
            var service = Service(new FixedRepository(records), new ListHandlingStore(handlingRows));
            var result = service.Search(request);
            var items = result.Items;

            Assert.Equal(count, result.Total);
            Assert.Equal(Math.Min(count, 200), items.Count);
            Assert.All(items, i => Assert.Equal(7, i.HandlerId));
            // 每條路徑：建查找表一次（處理列數）＋每筆紀錄的查找（推導、逾期、輸出各一次，至多 4 次）
            Assert.True(service.HandlingProbeCount <= count + 4L * count,
                $"{count} 筆紀錄 × {count} 筆處理列，比對 {service.HandlingProbeCount} 次");
            _output.WriteLine($"{count}：{service.HandlingProbeCount}");
        }
    }

    // ── A4：候選上限前移 ─────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public readonly EfSqliteFixture Fx = new();
        public readonly SqlCommandRecorder Recorder = new();
        public readonly EfAnalysisRecordStore Seed;
        public readonly EfAnalysisRecordStore Store;

        public Rig()
        {
            Seed = new EfAnalysisRecordStore(Fx.NewContext, "test");
            var connection = Fx.NewContext().Database.GetDbConnection();
            Store = new EfAnalysisRecordStore(
                () => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(Recorder).Options),
                "test");
        }

        public void Dispose() => Fx.Dispose();
    }

    private (RecordListQueryService Service, Rig Rig, List<long> HostIds) Build(int hostCount, int limit, Func<IVisibilityService>? visibility = null,
        FakeSystemSettingsService? severity = null)
    {
        var rig = new Rig();
        var ids = new List<long>();
        for (var i = 0; i < hostCount; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"SRV-{i:D3}" });
            ids.Add(host.HostId);
            rig.Seed.Append(new DailyAnalysisRecord
            {
                HostId = host.HostId, Host = host.HostName, Date = Day1, RiskLevel = RiskLevels.High,
                TopIssues = Rec(host.HostId, host.HostName, Day1).TopIssues
            });
        }
        var vis = visibility?.Invoke() ?? new AlwaysVisibleService(_hosts);
        var repository = new RecordRepository(rig.Store, _hosts, vis, severity ?? new FakeSystemSettingsService());
        var settings = new FakeSystemSettingsStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        var service = new RecordListQueryService(
            repository, _hosts, new FakeUserStore(), new FakeHandlingStore(), issueHandlings, cases, settings,
            new FakeSystemSettingsService(), vis, new FakeIssueAggregateQuery(),
            new OccurrenceStatusResolver(_hosts, issueHandlings, cases, settings),
            new UserDisplayNameService(settings),
            new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None),
            handlingFilterCandidateLimit: limit);
        return (service, rig, ids);
    }

    private static readonly RecordSearchRequest OpenOnly = new() { Statuses = new List<string> { "open" }, From = Day1, To = Day1, PageSize = 200 };

    [Fact]
    public void A4_上限_剛好等於上限正常回應_多一筆拒絕且訊息相同且拒絕前沒載入紀錄內容()
    {
        // 剛好等於上限
        var (atLimit, rigA, _) = Build(hostCount: 5, limit: 5);
        using (rigA)
        {
            Assert.Equal(5, atLimit.Search(OpenOnly).Total);
            // 正常回應時照原樣載入紀錄內容
            Assert.Contains(rigA.Recorder.Selects(), c => c.Sql.Contains("lf_top_issues", StringComparison.OrdinalIgnoreCase));
        }

        // 多一筆
        var (over, rigB, _) = Build(hostCount: 6, limit: 5);
        using (rigB)
        {
            rigB.Recorder.Clear();
            var ex = Assert.Throws<DomainException>(() => over.Search(OpenOnly));
            Assert.Equal("符合條件的主機日超過 5 筆，請縮小日期範圍或主機範圍。", ex.Message);

            // 拒絕前只做了計數：沒有讀問題子表、沒有讀日紀錄的內容欄位
            var selects = rigB.Recorder.Selects();
            Assert.DoesNotContain(selects, c => c.Sql.Contains("lf_top_issues", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(selects, c => c.Sql.Contains("headline", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(selects, c => c.Sql.Contains("content_json", StringComparison.OrdinalIgnoreCase));
            Assert.All(selects, c => Assert.Contains("COUNT(", c.Sql, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void A4_上限_預設上限的訊息與改動前相同()
    {
        var (service, rig, _) = Build(hostCount: 1, limit: RecordListQueryService.HandlingFilterCandidateLimit);
        using (rig)
        {
            Assert.Equal(1, service.Search(OpenOnly).Total);
            Assert.Equal("符合條件的主機日超過 50,000 筆，請縮小日期範圍或主機範圍。",
                DomainException.Validation($"符合條件的主機日超過 {RecordListQueryService.HandlingFilterCandidateLimit:N0} 筆，請縮小日期範圍或主機範圍。").Message);
        }
    }

    /// <summary>可見範圍受限的使用者：計數只算自己看得到的主機日（別人的不算進上限）。</summary>
    [Theory]
    [InlineData(5, false)]   // 全站 10 筆、可見 5 筆：剛好等於上限 → 正常回應
    [InlineData(6, true)]    // 可見 6 筆 > 上限 5 → 拒絕
    public void A4_上限_可見範圍受限時計數只算自己看得到的(int visibleCount, bool rejected)
    {
        var (service, rig, _) = Build(hostCount: 10, limit: 5, visibility: () =>
            new ScopedVisibility(_hosts.GetAll().Take(visibleCount).Select(h => h.HostId).ToArray()));
        using (rig)
        {
            if (rejected) Assert.Throws<DomainException>(() => service.Search(OpenOnly));
            else Assert.Equal(visibleCount, service.Search(OpenOnly).Total);
        }
    }

    /// <summary>日風險等級顯示設定只留中風險時，高風險日不計入候選：條件與 QueryLightweight 套用的完全相同。</summary>
    [Fact]
    public void A4_上限_日風險等級顯示設定同樣套用在計數()
    {
        var severity = new FakeSystemSettingsService { VisibleDayRiskLevels = new HashSet<string> { RiskLevels.Medium } };
        var (service, rig, _) = Build(hostCount: 8, limit: 5, severity: severity);
        using (rig)
        {
            // 8 筆都是高風險、顯示設定只允許中 → 候選 0 筆，不拒絕
            var result = service.Search(new RecordSearchRequest
            {
                Statuses = new List<string> { "open" }, From = Day1, To = Day1, PageSize = 200,
                RiskLevels = new List<string> { RiskLevels.High, RiskLevels.Medium }
            });
            Assert.Equal(0, result.Total);
        }
    }

    /// <summary>帶問題層級條件時無法保證計數與載入後的覆核相同 → 不前移，行為與改動前一致（載入後才檢查）。</summary>
    [Fact]
    public void A4_上限_帶問題層級條件時不前移_仍載入後檢查()
    {
        var (service, rig, _) = Build(hostCount: 6, limit: 5);
        using (rig)
        {
            rig.Recorder.Clear();
            var ex = Assert.Throws<DomainException>(() => service.Search(new RecordSearchRequest
            {
                Statuses = new List<string> { "open" }, From = Day1, To = Day1, EventId = 153, PageSize = 200
            }));
            Assert.Contains("5", ex.Message);
            Assert.Contains(rig.Recorder.Selects(), c => c.Sql.Contains("lf_top_issues", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>「下一筆未處理」捷徑不受上限（整個保留期、結果跨請求快取）：計數也不能擋它。</summary>
    [Fact]
    public void A4_上限_下一筆未處理捷徑不受上限限制()
    {
        var (service, rig, ids) = Build(hostCount: 6, limit: 5);
        using (rig)
        {
            var exception = Record.Exception(() => service.FindNextUnhandled(ids[0], Day1));
            Assert.Null(exception);
        }
    }
}

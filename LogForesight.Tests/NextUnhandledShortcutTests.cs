using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋四十五輪 B2：「下一筆未處理」專用端點（C1）、捷徑清單的跨請求快取（C2）、
/// 記錄查詢的缺省日期範圍（C4）、處理狀態推導不重複計算（C5）。
///
/// 一律串接真正的 <see cref="EfAnalysisRecordStore"/>＋<see cref="RecordRepository"/>，
/// 走的就是正式碼那條慢路徑（帶處理狀態篩選時整批撈回逐筆推導），不是另寫一份簡化邏輯。
/// </summary>
public class NextUnhandledShortcutTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSystemSettingsService _severityVisibility = new();
    private readonly DataVersionStamp _stamp = new();
    private readonly NextUnhandledSequenceCache _cache;
    private readonly RecordListQueryService _service;

    /// <summary>分析永遠只產出到昨天——測試紀錄以此為基準日而非 DateTime.Today</summary>
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    public NextUnhandledShortcutTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _cache = new NextUnhandledSequenceCache(_stamp);
        _service = NewService(new AlwaysVisibleService(_hosts));
    }

    private RecordListQueryService NewService(IVisibilityService visibility) =>
        new(
            new RecordRepository(_recordStore, _hosts, visibility, _severityVisibility),
            _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore,
            _severityVisibility, visibility,
            new EfIssueAggregateQuery(_fixture.NewContext, _hosts),
            new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore),
            new UserDisplayNameService(_settingsStore),
            _cache, new FixedIssueExclusionSource(IssueExclusion.None));

    public void Dispose() => _fixture.Dispose();

    private WebHost AddHost(string name) => TestData.AddHost(_hosts, name);

    private void AddRecord(WebHost host, DateTime date, string risk) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = date,
            RiskLevel = risk,
            Headline = $"{host.HostName} {date:MM-dd}",
            TopIssues = new List<LogIssueSignature>()
        });

    private static string D(DateTime date) => date.ToString("yyyy-MM-dd");

    // ── C1：窗口＝保留期，不得縮短 ─────────────────────────────────────────────

    /// <summary>
    /// 驗收 1（反例守門）：一件開了很久（遠早於 90 天）還沒結的高風險日，
    /// 必須仍然出現在捷徑裡——把窗口縮成近 N 天就是把使用者的待辦弄不見。
    /// </summary>
    [Fact]
    public void 下一筆未處理_遠早於90天的未處理高風險日仍找得到()
    {
        var recent = AddHost("HOST-RECENT");
        var old = AddHost("HOST-OLD");
        var oldDate = Yesterday.AddDays(-200);
        AddRecord(recent, Yesterday, "高");
        AddRecord(old, oldDate, "高");

        var next = _service.FindNextUnhandled(recent.HostId, Yesterday);

        Assert.NotNull(next);
        Assert.Equal(old.HostId, next!.HostId);
        Assert.Equal(D(oldDate), next.Date);
    }

    // ── C1：「下一筆」語意三種情況 ────────────────────────────────────────────

    [Fact]
    public void 下一筆未處理_目前這筆在清單中間_回下一筆()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        var c = AddHost("HOST-C");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday.AddDays(-1), "高");
        AddRecord(c, Yesterday.AddDays(-2), "高");

        // 預設排序＝風險 → 關聯訊號 → 日期新到舊，因此順序是 A、B、C
        var next = _service.FindNextUnhandled(b.HostId, Yesterday.AddDays(-1));

        Assert.NotNull(next);
        Assert.Equal(c.HostId, next!.HostId);
        Assert.Equal(D(Yesterday.AddDays(-2)), next.Date);
    }

    [Fact]
    public void 下一筆未處理_目前這筆不在清單中_回第一筆()
    {
        var a = AddHost("HOST-A");
        var closed = AddHost("HOST-CLOSED");
        AddRecord(a, Yesterday, "高");
        AddRecord(closed, Yesterday, "高");
        // 剛結案：不再屬於 open／in_progress，因此不在捷徑清單裡
        _handlingStore.Save(new RecordHandling
        {
            HostName = closed.HostName, Date = Yesterday, Status = HandlingStatuses.Resolved
        });

        var next = _service.FindNextUnhandled(closed.HostId, Yesterday);

        Assert.NotNull(next);
        Assert.Equal(a.HostId, next!.HostId);
    }

    [Fact]
    public void 下一筆未處理_目前這筆是最後一筆_沒有下一筆()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday.AddDays(-1), "高");

        Assert.Null(_service.FindNextUnhandled(b.HostId, Yesterday.AddDays(-1)));
    }

    /// <summary>低風險日不在捷徑條件（高＋中）內，整份清單為空時沒有下一筆</summary>
    [Fact]
    public void 下一筆未處理_只剩低風險日_沒有下一筆()
    {
        var a = AddHost("HOST-A");
        AddRecord(a, Yesterday, "低");

        Assert.Null(_service.FindNextUnhandled(a.HostId, Yesterday));
    }

    /// <summary>自帶授權：可見範圍為空的使用者拿不到任何捷徑（不因「內部呼叫既有服務」而放行）</summary>
    [Fact]
    public void 下一筆未處理_可見範圍為空_回null()
    {
        var a = AddHost("HOST-A");
        AddRecord(a, Yesterday, "高");

        var blind = NewService(new ScopedVisibility());

        Assert.Null(blind.FindNextUnhandled(a.HostId, Yesterday));
    }

    // ── C2：跨請求快取 ───────────────────────────────────────────────────────

    [Fact]
    public void 捷徑快取_同一使用者第二次呼叫不重新推導()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday.AddDays(-1), "高");

        _service.FindNextUnhandled(a.HostId, Yesterday);
        var afterFirst = _service.ProgressDerivationCount;
        Assert.True(afterFirst > 0, "第一次呼叫本來就該推導");

        _service.FindNextUnhandled(a.HostId, Yesterday);

        Assert.Equal(afterFirst, _service.ProgressDerivationCount);
    }

    [Fact]
    public void 捷徑快取_資料版本戳推進後重新推導()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday.AddDays(-1), "高");

        _service.FindNextUnhandled(a.HostId, Yesterday);
        var afterFirst = _service.ProgressDerivationCount;

        _stamp.Bump();
        _service.FindNextUnhandled(a.HostId, Yesterday);

        Assert.True(_service.ProgressDerivationCount > afterFirst,
            "版本戳推進後必須重算，否則資料變了捷徑還停在舊清單");
    }

    /// <summary>
    /// 洩漏守門：兩個可見範圍不同的使用者共用同一份快取時，不得互相命中——
    /// 鍵漏掉可見主機集合就是把 A 的待辦端給 B。
    /// </summary>
    [Fact]
    public void 捷徑快取_不同可見範圍的兩個使用者不互相命中()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday, "高");

        var onlyA = NewService(new ScopedVisibility(a.HostId));
        var onlyB = NewService(new ScopedVisibility(b.HostId));

        // 目前這筆兩邊都不在清單裡（不存在的主機）→ 各自取自己清單的第一筆
        var forA = onlyA.FindNextUnhandled(999999, Yesterday);
        var forB = onlyB.FindNextUnhandled(999999, Yesterday);

        Assert.NotNull(forA);
        Assert.NotNull(forB);
        Assert.Equal(a.HostId, forA!.HostId);
        Assert.Equal(b.HostId, forB!.HostId);
    }

    /// <summary>可見度設定（日風險等級）也是鍵的一部分：設定不同不得互相命中</summary>
    [Fact]
    public void 捷徑快取_可見日風險等級不同時不互相命中()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday, "中");

        var all = _service.FindNextUnhandled(999999, Yesterday);
        Assert.NotNull(all);
        Assert.Equal(a.HostId, all!.HostId);

        // 只顯示「中」的設定下，第一筆必須換成中風險那筆——沿用上一份快取就會答錯
        _severityVisibility.VisibleDayRiskLevels = new HashSet<string> { RiskLevels.Medium };
        var mediumOnly = _service.FindNextUnhandled(999999, Yesterday);

        Assert.NotNull(mediumOnly);
        Assert.Equal(b.HostId, mediumOnly!.HostId);
    }

    // ── C4：缺省日期範圍 ─────────────────────────────────────────────────────

    /// <summary>不帶起始日期且無狀態篩選：下界＝昨天往前 90 天（邊界日仍在範圍內）</summary>
    [Fact]
    public void 記錄查詢_無狀態篩選且不帶起始日期_下界為昨天往前90天()
    {
        var host = AddHost("HOST-A");
        var onBoundary = Yesterday.AddDays(-RecordListQueryService.DefaultLookbackDays);
        var beyond = onBoundary.AddDays(-1);
        AddRecord(host, onBoundary, "高");
        AddRecord(host, beyond, "高");

        var result = _service.Search(new RecordSearchRequest());

        Assert.Single(result.Items);
        Assert.Equal(D(onBoundary), result.Items[0].Date);
    }

    /// <summary>
    /// 反例（必須有）：不帶起始日期但**帶狀態篩選**時不受 90 天限制——
    /// 依狀態找案子不能被日期切掉，否則開了幾個月的待辦會消失。
    /// </summary>
    [Fact]
    public void 記錄查詢_帶狀態篩選且不帶起始日期_不受90天限制()
    {
        var host = AddHost("HOST-A");
        var beyond = Yesterday.AddDays(-RecordListQueryService.DefaultLookbackDays - 1);
        AddRecord(host, beyond, "高");

        var result = _service.Search(new RecordSearchRequest
        {
            Statuses = new List<string> { HandlingStatuses.Open }
        });

        Assert.Single(result.Items);
        Assert.Equal(D(beyond), result.Items[0].Date);
    }

    /// <summary>逾期／未指派篩選同屬處理狀態類路徑，一樣維持整個保留期</summary>
    [Fact]
    public void 記錄查詢_帶未指派篩選且不帶起始日期_不受90天限制()
    {
        var host = AddHost("HOST-A");
        var beyond = Yesterday.AddDays(-RecordListQueryService.DefaultLookbackDays - 1);
        AddRecord(host, beyond, "高");

        var result = _service.Search(new RecordSearchRequest { Unassigned = true });

        Assert.Single(result.Items);
        Assert.Equal(D(beyond), result.Items[0].Date);
    }

    /// <summary>呼叫端自己帶了更早的起始日期時不被缺省值覆蓋</summary>
    [Fact]
    public void 記錄查詢_明確帶起始日期時不套用缺省下界()
    {
        var host = AddHost("HOST-A");
        var beyond = Yesterday.AddDays(-RecordListQueryService.DefaultLookbackDays - 30);
        AddRecord(host, beyond, "高");

        var result = _service.Search(new RecordSearchRequest { From = beyond });

        Assert.Single(result.Items);
    }

    // ── C5：處理狀態推導不重複計算 ────────────────────────────────────────────

    /// <summary>
    /// 慢路徑對同一筆紀錄只推導一次（改版前「篩選」與「投影」各一次、逾期判定再一次）。
    /// 結果本身同時斷言，確保記憶化沒有改變行為。
    /// </summary>
    [Fact]
    public void 慢路徑_同一筆紀錄的處理狀態只推導一次()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        var c = AddHost("HOST-C");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday, "高");
        AddRecord(c, Yesterday, "高");
        _handlingStore.Save(new RecordHandling
        {
            HostName = c.HostName, Date = Yesterday, Status = HandlingStatuses.Resolved
        });

        var result = _service.Search(new RecordSearchRequest
        {
            Statuses = new List<string> { HandlingStatuses.Open }
        });

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(HandlingStatuses.Open, i.HandlingStatus));
        // 三筆候選、每筆只算一次
        Assert.Equal(3, _service.ProgressDerivationCount);
    }

    /// <summary>快速路徑（無狀態篩選）同樣每筆只推導一次</summary>
    [Fact]
    public void 快速路徑_同一筆紀錄的處理狀態只推導一次()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        AddRecord(a, Yesterday, "高");
        AddRecord(b, Yesterday, "中");
        // 逆期判定只有在 DueDate 有值時才會往下算日狀態（&& 短路）：
        // 不先建立這個前提，「只推導一次」就是一條恆真的斷言
        foreach (var host in new[] { a, b })
        {
            _handlingStore.Save(new RecordHandling
            {
                HostName = host.HostName, Date = Yesterday,
                Status = HandlingStatuses.InProgress, DueDate = Yesterday.AddDays(-1)
            });
        }

        var result = _service.Search(new RecordSearchRequest());

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, _service.ProgressDerivationCount);
    }
}

/// <summary>可見範圍固定為指定主機集合的替身（空集合＝什麼都看不到）</summary>
internal class ScopedVisibility : IVisibilityService
{
    private readonly HashSet<long> _visible;

    public ScopedVisibility(params long[] hostIds) => _visible = hostIds.ToHashSet();

    public IReadOnlySet<long> GetVisibleHostIds() => _visible;
    public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
    public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
    public IReadOnlyList<string> GetCaseGrantHostNames() => Array.Empty<string>();
    public bool IsCaseGrantOnly(long hostId) => false;
    public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    public List<WebHost> GetVisibleHosts() => new();
    public void EnsureVisible(long hostId)
    {
        if (!_visible.Contains(hostId)) throw DomainException.NotFound("找不到這台主機。");
    }
}

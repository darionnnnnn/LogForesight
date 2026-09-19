using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題靜音（回饋第 47 輪 B-1）：判定邊界、合成與包裝層、提示詞排除、派工脈絡、設定／解除 API、Maintain 服務層檢查。
/// 分析側（LogAnalysisService／PRTG）的端到端測試分別在 LogAnalysisServiceSplitTests／PrtgDailyPipelineTests。
/// </summary>
public sealed class IssueMuteTests : IDisposable
{
    private const string Source = "disk";
    private const int EventId = 153;

    private static DateTime Today => DateTime.Today;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-issue-mute-" + Guid.NewGuid().ToString("N"));
    private StorageBackend? _backend;

    // 設定／解除 API 的組裝
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly FakeUserStore _users = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly WorkOrderCoordinator _coordinator;

    public IssueMuteTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        var caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlings, new FakeRecordRepository(_hosts), _hosts, _owners);
        _coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, caseCoordinator, _handlings, _hosts);
    }

    public void Dispose()
    {
        if (_backend != null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        }
        GC.SuppressFinalize(this);
    }

    private IssueOwnerAdminService Admin(ICurrentUser user, IIssueOwnerStore? store = null) =>
        new(store ?? _owners, new FakeIssueAggregateQuery(), _users, _audit, user,
            new UserDisplayNameService(new FakeSystemSettingsStore()), _orders, _coordinator, TestPermissionStamps.Shared);

    private static ICurrentUser Maintainer() => FakeCurrentUser.ForUser(5, Capability.Maintain);

    private IssueOwnerStore RealStore()
    {
        Directory.CreateDirectory(_dir);
        _backend ??= new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "m.db")}" }, _dir);
        return new IssueOwnerStore(_backend.Blob("issue_owners"));
    }

    private static IssueProfile Profile(params (DateTime From, DateTime To)[] intervals) => new()
    {
        SourceName = Source, EventId = EventId,
        Mutes = intervals.Select(i => new MuteInterval { From = i.From, To = i.To, Reason = "r", ByAccount = "a" }).ToList()
    };

    private static LogIssueSignature Issue(string source = Source, int eventId = EventId) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = EventLogEntryType.Error,
        Severity = IssueSeverity.High, Count = 3
    };

    // ── 判定邊界（IssueProfile helper）────────────────────────────────────

    [Fact]
    public void 判定_今天設1天_今天靜音中明天不是()
    {
        var admin = Admin(Maintainer());
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 1, Reason = "測試", ExistingOrders = "pause" });

        var p = _owners.Get(Source, EventId)!;
        Assert.True(IssueProfile.IsMutedOn(p, Today));
        Assert.True(IssueProfile.IsMutedOn(p, Today.AddHours(23)));
        Assert.False(IssueProfile.IsMutedOn(p, Today.AddDays(1)));
        Assert.False(IssueProfile.IsMutedOn(p, Today.AddDays(-1)));
    }

    [Fact]
    public void 判定_迄日為昨天_今天不是且CurrentMute為null()
    {
        var p = Profile((Today.AddDays(-3), Today.AddDays(-1)));

        Assert.True(IssueProfile.IsMutedOn(p, Today.AddDays(-1)));
        Assert.False(IssueProfile.IsMutedOn(p, Today));
        Assert.Null(IssueProfile.CurrentMute(p, Today));
    }

    [Fact]
    public void 判定_不重疊區間_跨區間日只命中一個()
    {
        var p = Profile((Today.AddDays(-10), Today.AddDays(-6)), (Today.AddDays(-5), Today.AddDays(-1)));

        foreach (var d in Enumerable.Range(-10, 10).Select(i => Today.AddDays(i)))
            Assert.Single(p.Mutes, m => MuteInterval.Covers(m.From, m.To, d));
        Assert.Equal(Today.AddDays(-6), p.Mutes.Single(m => MuteInterval.Covers(m.From, m.To, Today.AddDays(-6))).To);
        Assert.Equal(Today.AddDays(-5), p.Mutes.Single(m => MuteInterval.Covers(m.From, m.To, Today.AddDays(-5))).From);
    }

    [Fact]
    public void 判定_延長同一區間_不新增()
    {
        var admin = Admin(Maintainer());
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 1, Reason = "第一次", ExistingOrders = "pause" });
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 7, Reason = "延長", ExistingOrders = "pause" });

        var p = _owners.Get(Source, EventId)!;
        var interval = Assert.Single(p.Mutes);
        Assert.Equal(Today, interval.From);
        Assert.Equal(Today.AddDays(6), interval.To);
        Assert.Equal("延長", interval.Reason);
        Assert.Same(interval, IssueProfile.CurrentMute(p, Today));
    }

    // ── 合成與包裝 ─────────────────────────────────────────────────────────

    [Fact]
    public void 包裝層_LoadAll含合成靜音項目()
    {
        var inner = new FakeSuppressionStore();
        inner.SaveAll(new List<RuleSuppression> { new() { RuleId = "rule-x", Scope = SuppressionScopes.Site, Reason = "既有" } });
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(Profile((Today.AddDays(-1), Today.AddDays(2))));

        var all = new MuteAwareSuppressionStore(inner, owners).LoadAll();

        Assert.Equal(2, all.Count);
        var mute = Assert.Single(all, s => s.TargetType == SuppressionTargetTypes.IssueMute);
        Assert.Equal((Source, (int?)EventId, (DateTime?)Today.AddDays(-1), (DateTime?)Today.AddDays(2), SuppressionScopes.Site),
            (mute.SourceName, mute.EventId, mute.MuteFrom, mute.MuteTo, mute.Scope));
        Assert.Equal("r", mute.Reason);
        Assert.Equal("a", mute.SuppressedBy);
        Assert.DoesNotContain(inner.LoadAll(), s => s.TargetType == SuppressionTargetTypes.IssueMute);
    }

    [Fact]
    public void 包裝層_SaveAll濾掉IssueMute後交給內層()
    {
        var inner = new FakeSuppressionStore();
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(Profile((Today, Today)));
        var store = new MuteAwareSuppressionStore(inner, owners);

        var list = store.LoadAll();
        list.Add(new RuleSuppression { RuleId = "rule-new", Scope = SuppressionScopes.Site });
        Assert.Contains(list, s => s.TargetType == SuppressionTargetTypes.IssueMute);
        store.SaveAll(list);

        var saved = Assert.Single(inner.LoadAll());
        Assert.Equal("rule-new", saved.RuleId);
    }

    [Fact]
    public void 篩選_ActiveForHost與ExpiredForHost與StillSuppressedElsewhere不含IssueMute()
    {
        var all = new List<RuleSuppression>
        {
            new() { RuleId = "rule-a", Scope = SuppressionScopes.Site },
            new() { RuleId = "rule-b", Scope = SuppressionScopes.Site, ExpiresAt = DateTime.Now.AddDays(-1) }
        };
        // 一筆涵蓋今天、一筆已過去（若誤當一般抑制，ExpiresAt 為 null 會被當成永久生效）
        all.AddRange(MuteSuppressions.From(new[] { Profile((Today, Today.AddDays(1)), (Today.AddDays(-9), Today.AddDays(-8))) }));

        var active = SuppressionFilter.ActiveForHost(all, "SRV-01", Array.Empty<long>(), DateTime.Now);
        var expired = SuppressionFilter.ExpiredForHost(all, "SRV-01", Array.Empty<long>(), DateTime.Now);
        var elsewhere = SuppressionFilter.StillSuppressedElsewhere(all, "SRV-01", Array.Empty<long>(), DateTime.Now);

        Assert.Equal("rule-a", Assert.Single(active).RuleId);
        Assert.Equal("rule-b", Assert.Single(expired).RuleId);
        Assert.Empty(elsewhere);
        Assert.Equal(2, SuppressionFilter.MutesOf(all).Count);
    }

    /// <summary>單點：合成項目經 MarkSuppressed 的比對與 IssueProfile.IsMutedOn 對每一天結論一致（含首尾、大小寫）</summary>
    [Fact]
    public void 一致性_MarkSuppressed靜音比對與IsMutedOn同規則()
    {
        var profile = Profile((Today.AddDays(-6), Today.AddDays(-4)), (Today.AddDays(-1), Today.AddDays(1)));
        var mutes = SuppressionFilter.MutesOf(MuteSuppressions.From(new[] { profile }));

        foreach (var d in Enumerable.Range(-8, 11).Select(i => Today.AddDays(i).AddHours(13)))
        {
            var issue = Issue(source: "DISK");
            SuppressionFilter.MarkSuppressed(new[] { issue }, new List<RuleSuppression>(), mutes, d);
            Assert.Equal(IssueProfile.IsMutedOn(profile, d), issue.Suppressed);
        }
    }

    [Fact]
    public void MarkSuppressed_靜音只比對同Source與EventId()
    {
        var mutes = MuteSuppressions.From(new[] { Profile((Today, Today)) });
        var same = Issue();
        var otherEvent = Issue(eventId: 154);
        var otherSource = Issue(source: "Ntfs");

        var marked = SuppressionFilter.MarkSuppressed(new[] { same, otherEvent, otherSource }, new List<RuleSuppression>(), mutes, Today);

        Assert.Equal(1, marked);
        Assert.True(same.Suppressed);
        Assert.False(otherEvent.Suppressed);
        Assert.False(otherSource.Suppressed);
    }

    // ── AI 提示詞 ─────────────────────────────────────────────────────────

    [Fact]
    public void 提示詞_被抑制的事件問題不出現()
    {
        var muted = Issue(source: "MutedAppZq");
        muted.Suppressed = true;
        var mutedFlagged = Issue(source: "MutedRuleAppZq");
        mutedFlagged.KnownIssue = "已知問題描述";
        mutedFlagged.Suppressed = true;
        var visible = Issue(source: "VisibleAppZq");

        var prompt = AnalysisPromptBuilder.BuildPrompt(
            Today, new List<LogIssueSignature> { muted, mutedFlagged, visible }, errorCount: 3, warningCount: 0, auditCount: 0,
            history: new List<DailyAnalysisRecord>(), trendAlerts: new List<string>(),
            correlations: new List<CorrelationFinding>(), screening: null,
            dataIncomplete: false, uncoveredChecks: new List<string>(), serverDescription: "測試主機");

        Assert.Contains("VisibleAppZq", prompt);
        Assert.DoesNotContain("MutedAppZq", prompt);
        Assert.DoesNotContain("MutedRuleAppZq", prompt);
    }

    [Fact]
    public void 前置掃描尾巴_不含被抑制的問題()
    {
        var issues = Enumerable.Range(0, 200).Select(i => Issue(source: $"App{i}")).ToList();
        foreach (var i in issues.Take(150)) i.Suppressed = true;

        var tail = AnalysisPromptBuilder.GetTailIssues(issues);

        Assert.DoesNotContain(tail, i => i.Suppressed);
        Assert.Equal(AnalysisPromptBuilder.GetTailIssues(issues.Where(i => !i.Suppressed).ToList()).Count, tail.Count);
    }

    // ── 派工脈絡 ───────────────────────────────────────────────────────────

    [Fact]
    public void 派工脈絡_Build由問題檔案填入區間_IsMuted對區間內日期為真()
    {
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(Profile((Today.AddDays(-3), Today.AddDays(-2)), (Today, Today.AddDays(1))));
        var pool = new DispatchCandidatePool
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };

        // 現在不在任何區間內，只驗紀錄日的判定；目前靜音中的規則見下一條測試
        var ctx = DispatchContext.Build(pool, owners, _orders, _cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now.AddDays(10));

        Assert.True(ctx.IsMuted("DISK", EventId, Today.AddDays(-3)));
        Assert.True(ctx.IsMuted(Source, EventId, Today.AddDays(-2).AddHours(20)));
        Assert.False(ctx.IsMuted(Source, EventId, Today.AddDays(-1)));
        Assert.True(ctx.IsMuted(Source, EventId, Today.AddDays(1)));
        Assert.False(ctx.IsMuted(Source, EventId, Today.AddDays(2)));
        Assert.False(ctx.IsMuted(Source, 154, Today));
    }

    [Fact]
    public void 派工脈絡_今天在區間內時區間外的紀錄日也算靜音()
    {
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(Profile((Today, Today.AddDays(1))));
        var pool = new DispatchCandidatePool
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };

        var ctx = DispatchContext.Build(pool, owners, _orders, _cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now);

        Assert.True(ctx.IsMuted(Source, EventId, Today.AddDays(-5)));
        Assert.False(ctx.IsMuted(Source, 154, Today.AddDays(-5)));
    }

    // ── 設定／解除 API ─────────────────────────────────────────────────────

    public static IEnumerable<object?[]> InvalidRequests()
    {
        yield return new object?[] { 0, null };
        yield return new object?[] { 366, null };
        yield return new object?[] { 3, DateTime.Today.AddDays(3) };
        yield return new object?[] { null, null };
        yield return new object?[] { null, DateTime.Today.AddDays(-1) };
        // MemberData 可能在午夜前探索、午夜後才執行；用明確超出上限的 400 天，
        // 避免原本 366 天在跨日時縮成合法的 365 天而偶發失敗。
        yield return new object?[] { null, DateTime.Today.AddDays(400) };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void SetMute_天數與截止日驗證失敗_Validation且零寫入(int? days, DateTime? until)
    {
        var ex = Assert.Throws<DomainException>(() => Admin(Maintainer()).SetMute(Source, EventId,
            new SetIssueMuteRequest { Days = days, Until = until, Reason = "原因", ExistingOrders = "pause" }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Null(_owners.Get(Source, EventId));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void SetMute_邊界值365天與截止日今天加365可通過()
    {
        Admin(Maintainer()).SetMute(Source, EventId, new SetIssueMuteRequest { Days = 365, Reason = "r", ExistingOrders = "pause" });
        Assert.Equal(Today.AddDays(364), _owners.Get(Source, EventId)!.Mutes.Single().To);

        Admin(Maintainer()).SetMute("Ntfs", 55, new SetIssueMuteRequest { Until = Today.AddDays(365), Reason = "r", ExistingOrders = "pause" });
        Assert.Equal(Today.AddDays(365), _owners.Get("Ntfs", 55)!.Mutes.Single().To);
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    public void SetMute_原因或交辦單處置不合法_Validation(string mode)
    {
        Assert.Throws<DomainException>(() => Admin(Maintainer()).SetMute(Source, EventId,
            new SetIssueMuteRequest { Days = 1, Reason = "  ", ExistingOrders = "pause" }));
        Assert.Throws<DomainException>(() => Admin(Maintainer()).SetMute(Source, EventId,
            new SetIssueMuteRequest { Days = 1, Reason = new string('x', 501), ExistingOrders = "pause" }));
        Assert.Throws<DomainException>(() => Admin(Maintainer()).SetMute(Source, EventId,
            new SetIssueMuteRequest { Days = 1, Reason = "r", ExistingOrders = mode }));
        Assert.Null(_owners.Get(Source, EventId));
    }

    [Fact]
    public void SetMute_問題檔案不存在時建立_無負責人無結論()
    {
        var dto = Admin(Maintainer()).SetMute(Source, EventId,
            new SetIssueMuteRequest { Until = Today.AddDays(2), Reason = "搬機房", ExistingOrders = "pause" });

        var p = _owners.Get(Source, EventId)!;
        Assert.Empty(p.OwnerUserIds);
        Assert.Null(p.ConclusionStatus);
        var m = Assert.Single(p.Mutes);
        Assert.Equal((Today, Today.AddDays(2), "搬機房", (long?)5), (m.From, m.To, m.Reason, m.ById));
        Assert.NotNull(dto.CurrentMute);
        Assert.Equal(Today.AddDays(2), dto.CurrentMute!.To);
        Assert.Single(dto.MuteHistory);
    }

    [Fact]
    public void SetMute_close無指派加處理權限_Forbidden且零寫入()
    {
        var order = CreateActiveOrder();

        var ex = Assert.Throws<DomainException>(() => Admin(FakeCurrentUser.ForUser(5, Capability.Maintain, Capability.Handle))
            .SetMute(Source, EventId, new SetIssueMuteRequest { Days = 3, Reason = "r", ExistingOrders = "close" }));

        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
        Assert.Null(_owners.Get(Source, EventId));
        Assert.Null(_orders.Get(order)!.ClosedAt);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void SetMute_close有能力_進行中單以wont_fix代為結案()
    {
        var order = CreateActiveOrder();

        Admin(FakeCurrentUser.ForUser(5, Capability.Maintain, Capability.Assign, Capability.Handle))
            .SetMute(Source, EventId, new SetIssueMuteRequest { Days = 3, Reason = "換硬體", ExistingOrders = "close" });

        var closed = _orders.Get(order)!;
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(WorkOrderCloseReasons.AdminClosed, closed.ClosedReason);
        Assert.Empty(_orders.GetActiveByIssue(Source, EventId));
        Assert.All(_cases.GetMany(new[] { "SRV-01" }), c => Assert.Equal(IssueHandlingStatuses.WontFix, c.Status));
        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("代為結案進行中交辦單 1 張", entry.Summary);
    }

    [Fact]
    public void SetMute_pause_不動交辦單()
    {
        var order = CreateActiveOrder();

        Admin(FakeCurrentUser.ForUser(5, Capability.Maintain)).SetMute(Source, EventId,
            new SetIssueMuteRequest { Days = 3, Reason = "r", ExistingOrders = "pause" });

        Assert.Null(_orders.Get(order)!.ClosedAt);
    }

    [Fact]
    public void ClearMute_今天才開始_刪除該區間()
    {
        var admin = Admin(Maintainer());
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 5, Reason = "r", ExistingOrders = "pause" });

        var dto = admin.ClearMute(Source, EventId);

        Assert.Empty(_owners.Get(Source, EventId)!.Mutes);
        Assert.Null(dto.CurrentMute);
    }

    [Fact]
    public void ClearMute_較早開始_迄日改為昨天()
    {
        _owners.Upsert(Profile((Today.AddDays(-3), Today.AddDays(4))));

        Admin(Maintainer()).ClearMute(Source, EventId);

        var m = Assert.Single(_owners.Get(Source, EventId)!.Mutes);
        Assert.Equal((Today.AddDays(-3), Today.AddDays(-1)), (m.From, m.To));
    }

    [Fact]
    public void ClearMute_沒有進行中的靜音_Validation()
    {
        Assert.Throws<DomainException>(() => Admin(Maintainer()).ClearMute(Source, EventId));
        _owners.Upsert(Profile((Today.AddDays(-5), Today.AddDays(-1))));
        var ex = Assert.Throws<DomainException>(() => Admin(Maintainer()).ClearMute(Source, EventId));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void 無Maintain_四個方法皆Forbidden且零寫入()
    {
        _owners.Upsert(Profile((Today.AddDays(-1), Today.AddDays(1))));
        var before = _owners.Get(Source, EventId)!.Mutes.Single();
        var admin = Admin(FakeCurrentUser.ForUser(5, Capability.Assign, Capability.Handle));

        var calls = new Action[]
        {
            () => admin.SetConclusion(Source, EventId, new SetIssueConclusionRequest { Status = IssueHandlingStatuses.KnownNoise, Note = "n" }),
            () => admin.ClearConclusion(Source, EventId),
            () => admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 3, Reason = "r", ExistingOrders = "pause" }),
            () => admin.ClearMute(Source, EventId)
        };
        foreach (var call in calls)
            Assert.Equal(ApiErrorCodes.Forbidden, Assert.Throws<DomainException>(call).Code);

        var p = _owners.Get(Source, EventId)!;
        Assert.Null(p.ConclusionStatus);
        Assert.Same(before, p.Mutes.Single());
        Assert.Equal(Today.AddDays(1), p.Mutes.Single().To);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 稽核_兩個動作有中文名稱且各寫一筆()
    {
        var admin = Admin(Maintainer());
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 2, Reason = "維護窗", ExistingOrders = "pause" });
        admin.ClearMute(Source, EventId);

        Assert.Equal(new[] { AuditActions.IssueMute, AuditActions.IssueUnmute }, _audit.Entries.Select(e => e.Action));
        Assert.Contains("維護窗", _audit.Entries[0].Summary);
        Assert.Contains($"{Today:yyyy-MM-dd}～{Today.AddDays(1):yyyy-MM-dd}", _audit.Entries[0].Summary);

        var names = new AuditQueryService(null!, _users, new UserDisplayNameService(new FakeSystemSettingsStore())).GetActionNames();
        Assert.Equal("靜音問題", names[AuditActions.IssueMute]);
        Assert.Equal("解除靜音", names[AuditActions.IssueUnmute]);
    }

    [Fact]
    public void Dto_MuteHistory最近10筆新到舊()
    {
        var profile = Profile(Enumerable.Range(1, 12).Select(i => (Today.AddDays(-i * 3), Today.AddDays(-i * 3 + 1))).ToArray());
        _owners.Upsert(profile);

        var dto = Admin(Maintainer()).List().Single();

        Assert.Null(dto.CurrentMute);
        Assert.Equal(10, dto.MuteHistory.Count);
        Assert.Equal(Today.AddDays(-3), dto.MuteHistory[0].From);
        Assert.Equal(dto.MuteHistory.Select(m => m.From).OrderByDescending(d => d), dto.MuteHistory.Select(m => m.From));
    }

    // ── 真實 IssueOwnerStore：重讀驗證寫入（逐欄複製不漏 Mutes）───────────────

    [Fact]
    public void 真實store_編輯負責人存回後靜音仍在()
    {
        var store = RealStore();
        var admin = Admin(Maintainer(), store);
        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 3, Reason = "r", ExistingOrders = "pause" });
        var user = _users.Upsert(new WebUser { Account = "owner1", DisplayName = "負責人", Active = true });

        admin.Upsert(new SaveIssueOwnerRequest { SourceName = Source, EventId = EventId, OwnerUserIds = new List<long> { user.UserId } });

        var reread = RealStore().Get(Source, EventId)!;
        Assert.Equal(new[] { user.UserId }, reread.OwnerUserIds);
        var m = Assert.Single(reread.Mutes);
        Assert.Equal((Today, Today.AddDays(2)), (m.From, m.To));
    }

    [Fact]
    public void 真實store_問題檔案已存在時_延長確實寫入()
    {
        var store = RealStore();
        store.Upsert(new IssueProfile { SourceName = Source, EventId = EventId, Note = "既有" });
        var admin = Admin(Maintainer(), store);

        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 1, Reason = "第一次", ExistingOrders = "pause" });
        Assert.Equal(Today, Assert.Single(RealStore().Get(Source, EventId)!.Mutes).To);

        admin.SetMute(Source, EventId, new SetIssueMuteRequest { Days = 10, Reason = "延長", ExistingOrders = "pause" });

        var reread = RealStore().Get(Source, EventId)!;
        var m = Assert.Single(reread.Mutes);
        Assert.Equal((Today, Today.AddDays(9), "延長"), (m.From, m.To, m.Reason));
        Assert.Equal("既有", reread.Note);
    }

    [Fact]
    public void 真實store_問題檔案已存在時_解除確實寫入()
    {
        var store = RealStore();
        store.Upsert(new IssueProfile { SourceName = Source, EventId = EventId });
        var existing = store.Get(Source, EventId)!;
        store.Upsert(new IssueProfile
        {
            SourceName = existing.SourceName, EventId = existing.EventId,
            Mutes = new List<MuteInterval> { new() { From = Today.AddDays(-2), To = Today.AddDays(3), Reason = "r", ByAccount = "a" } }
        });
        Assert.Single(RealStore().Get(Source, EventId)!.Mutes);

        Admin(Maintainer(), store).ClearMute(Source, EventId);

        var m = Assert.Single(RealStore().Get(Source, EventId)!.Mutes);
        Assert.Equal((Today.AddDays(-2), Today.AddDays(-1)), (m.From, m.To));
    }

    private long CreateActiveOrder()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-01", Active = true });
        var issue = Issue();
        return _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = Source, EventId = EventId, IssueLabel = "disk 153", HandlerId = 9,
            Members = new List<WorkOrderMember>
            {
                new() { HostName = "SRV-01", IssueKey = IssueSignatureKey.For(issue), IssueLabel = "disk 153", TriggerDate = Today.AddDays(-1) }
            },
            Actor = new WorkOrderActor { ActorAccount = "boss", OccurredAt = DateTime.Now.AddHours(-1) }
        }).WorkOrderId;
    }
}

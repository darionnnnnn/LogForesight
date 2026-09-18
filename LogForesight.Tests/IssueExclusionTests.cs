using System.Diagnostics;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 讀取側靜音排除（回饋第 47 輪批次 B-2a）：單一規則的邊界、快取鍵、提供者快取、
/// 介面反射守門與呼叫端接線。SQL 條件與推導同口徑分別在 IssueAggregateQueryTests／
/// DayHandlingSqlDerivationTests。
/// </summary>
public class IssueExclusionTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 8, 31);

    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();

    public IssueExclusionTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static IssueProfile Profile(string source, int eventId, params (DateTime From, DateTime To)[] spans) => new()
    {
        SourceName = source,
        EventId = eventId,
        Mutes = spans.Select(s => new MuteInterval { From = s.From, To = s.To, Reason = "測試" }).ToList()
    };

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = EventLogEntryType.Warning,
        Category = IssueCategory.Storage, Severity = severity, Count = 1
    };

    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord { HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel, TopIssues = issues.ToList() });

    // ── 規則邊界 ──────────────────────────────────────────────

    [Fact]
    public void 目前靜音中_區間前的日子也算靜音()
    {
        var exclusion = IssueExclusion.From(new[] { Profile("cron", 7, (Today.AddDays(-3), Today.AddDays(5))) }, Today);

        Assert.True(exclusion.IsCurrentlyMuted("cron", 7));
        Assert.True(exclusion.IsMuted("cron", 7, Today.AddDays(-30)));   // 區間前
        Assert.True(exclusion.IsMuted("cron", 7, Today.AddDays(-3)));    // 區間首日
        Assert.False(exclusion.IsMuted("cron", 8, Today.AddDays(-30)));  // 別的問題不受影響
    }

    [Fact]
    public void 區間已到期_區間內的日子算靜音_區間前後不算()
    {
        var from = Today.AddDays(-20);
        var to = Today.AddDays(-10);
        var exclusion = IssueExclusion.From(new[] { Profile("disk", 153, (from, to)) }, Today);

        Assert.False(exclusion.IsCurrentlyMuted("disk", 153));
        Assert.True(exclusion.IsMuted("disk", 153, from));
        Assert.True(exclusion.IsMuted("disk", 153, to));
        Assert.True(exclusion.IsMuted("disk", 153, from.AddDays(3).AddHours(15)));   // 時間部分不影響
        Assert.False(exclusion.IsMuted("disk", 153, from.AddDays(-1)));
        Assert.False(exclusion.IsMuted("disk", 153, to.AddDays(1)));
    }

    [Fact]
    public void 提前解除_To為昨天_今天不算_昨天算()
    {
        var exclusion = IssueExclusion.From(new[] { Profile("disk", 153, (Today.AddDays(-5), Today.AddDays(-1))) }, Today);

        Assert.False(exclusion.IsCurrentlyMuted("disk", 153));
        Assert.False(exclusion.IsMuted("disk", 153, Today));
        Assert.True(exclusion.IsMuted("disk", 153, Today.AddDays(-1)));
    }

    [Fact]
    public void 鍵大小寫不敏感()
    {
        var exclusion = IssueExclusion.From(new[] { Profile("Microsoft-Windows-Disk", 153, (Today.AddDays(-1), Today.AddDays(1))) }, Today);

        Assert.True(exclusion.IsMuted("microsoft-windows-disk", 153, Today.AddDays(-40)));
        Assert.True(exclusion.IsCurrentlyMuted("MICROSOFT-WINDOWS-DISK", 153));
        Assert.Contains(("MICROSOFT-WINDOWS-DISK", 153), exclusion.CurrentlyMuted);
    }

    [Fact]
    public void None_IsMuted恆假()
    {
        Assert.True(IssueExclusion.None.IsEmpty);
        Assert.False(IssueExclusion.None.IsMuted("disk", 153, Today));
        Assert.False(IssueExclusion.None.IsCurrentlyMuted("disk", 153));
        Assert.Equal("mute:none", IssueExclusion.None.CacheToken);
    }

    [Fact]
    public void CacheToken_設定不同則不同_同設定不同今天也不同_同設定同日相同()
    {
        var a = new[] { Profile("disk", 153, (Today.AddDays(-5), Today.AddDays(5))) };
        var b = new[] { Profile("disk", 153, (Today.AddDays(-5), Today.AddDays(6))) };
        var aReordered = new[] { Profile("net", 1, (Today, Today)), Profile("disk", 153, (Today.AddDays(-5), Today.AddDays(5))) };
        var aWithNet = new[] { Profile("disk", 153, (Today.AddDays(-5), Today.AddDays(5))), Profile("net", 1, (Today, Today)) };

        Assert.NotEqual(IssueExclusion.From(a, Today).CacheToken, IssueExclusion.From(b, Today).CacheToken);
        Assert.NotEqual(IssueExclusion.From(a, Today).CacheToken, IssueExclusion.From(a, Today.AddDays(1)).CacheToken);
        Assert.Equal(IssueExclusion.From(a, Today).CacheToken, IssueExclusion.From(a, Today.AddHours(20)).CacheToken);
        Assert.Equal(IssueExclusion.From(aReordered, Today).CacheToken, IssueExclusion.From(aWithNet, Today).CacheToken);
        Assert.NotEqual(IssueExclusion.None.CacheToken, IssueExclusion.From(a, Today).CacheToken);
    }

    [Fact]
    public void DayStatusRule_全部靜音為Resolved_有未處理時維持日層級()
    {
        Assert.Equal(HandlingStatuses.Resolved, DayStatusRule.Resolve(0, 0, false, 2, null));
        Assert.Equal(HandlingStatuses.Open, DayStatusRule.Resolve(1, 0, false, 2, null));
        Assert.Equal(HandlingStatuses.Open, DayStatusRule.Resolve(0, 0, false, 0, null));
        Assert.Equal(HandlingStatuses.InProgress, DayStatusRule.Resolve(2, 1, false, 0, null));
        Assert.Equal(HandlingStatuses.Resolved, DayStatusRule.Resolve(2, 2, false, 0, HandlingStatuses.Open));
    }

    [Fact]
    public void ForRange_只留期間內重疊區間且保留目前靜音中的鍵()
    {
        var queryFrom = new DateTime(2026, 8, 10);
        var queryTo = new DateTime(2026, 8, 20);

        // 三個鍵：目前靜音中但有一段舊區間在期間外、已到期且區間在期間內、已到期且區間在期間外
        var key1 = Profile("cron", 7, (Today.AddDays(-2), Today.AddDays(5)), (new DateTime(2026, 7, 1), new DateTime(2026, 7, 10)));
        var key2 = Profile("disk", 153, (new DateTime(2026, 8, 12), new DateTime(2026, 8, 15)));
        var key3 = Profile("net", 1, (new DateTime(2026, 7, 1), new DateTime(2026, 7, 10)));

        var original = IssueExclusion.From(new[] { key1, key2, key3 }, Today);
        var narrowed = original.ForRange(queryFrom, queryTo);

        // 斷言 Spans 內容：保留目前靜音中鍵的所有區間、以及與期間重疊的區間；期間外的已到期區間被排除
        Assert.Contains(narrowed.Spans, s => s.SourceKey == "CRON" && s.EventId == 7 && s.From == new DateTime(2026, 7, 1));
        Assert.Contains(narrowed.Spans, s => s.SourceKey == "CRON" && s.EventId == 7 && s.From == Today.AddDays(-2));
        Assert.Contains(narrowed.Spans, s => s.SourceKey == "DISK" && s.EventId == 153 && s.From == new DateTime(2026, 8, 12));
        Assert.DoesNotContain(narrowed.Spans, s => s.SourceKey == "NET");

        // 斷言 CurrentlyMuted、CacheToken 不變
        Assert.Equal(original.CurrentlyMuted, narrowed.CurrentlyMuted);
        Assert.Equal(original.CacheToken, narrowed.CacheToken);
        Assert.Equal(original.Today, narrowed.Today);
        Assert.Same(IssueExclusion.None, IssueExclusion.None.ForRange(queryFrom, queryTo));
    }

    [Fact]
    public void ForRange_期間內逐日IsMuted與原物件相同()
    {
        var queryFrom = new DateTime(2026, 8, 10);
        var queryTo = new DateTime(2026, 8, 20);

        var key1 = Profile("cron", 7, (Today.AddDays(-2), Today.AddDays(5)), (new DateTime(2026, 7, 1), new DateTime(2026, 7, 10)));
        var key2 = Profile("disk", 153, (new DateTime(2026, 8, 12), new DateTime(2026, 8, 15)));
        var key3 = Profile("net", 1, (new DateTime(2026, 7, 1), new DateTime(2026, 7, 10)));

        var original = IssueExclusion.From(new[] { key1, key2, key3 }, Today);
        var narrowed = original.ForRange(queryFrom, queryTo);

        var keys = new[] { ("cron", 7), ("disk", 153), ("net", 1) };
        for (var day = queryFrom.Date; day <= queryTo.Date; day = day.AddDays(1))
        {
            foreach (var (src, eventId) in keys)
            {
                Assert.Equal(original.IsMuted(src, eventId, day), narrowed.IsMuted(src, eventId, day));
            }
        }
    }

    // ── 提供者 ──────────────────────────────────────────────

    private sealed class CountingOwnerStore : IIssueOwnerStore
    {
        public readonly FakeIssueOwnerStore Inner = new();
        public int GetAllCount { get; private set; }
        public List<IssueProfile> GetAll() { GetAllCount++; return Inner.GetAll(); }
        public IssueProfile? Get(string source, int eventId) => Inner.Get(source, eventId);
        public IssueProfile Upsert(IssueProfile rule) => Inner.Upsert(rule);
        public void Delete(string source, int eventId) => Inner.Delete(source, eventId);
    }

    [Fact]
    public void 提供者_同版本同日只讀一次問題檔案()
    {
        var store = new CountingOwnerStore();
        store.Inner.Upsert(Profile("cron", 7, (Today.AddDays(-1), Today.AddDays(1))));
        var provider = new IssueExclusionProvider(store, new DataVersionStamp(), () => Today);

        var first = provider.Current();
        var second = provider.Current();

        Assert.Same(first, second);
        Assert.Equal(1, store.GetAllCount);
        Assert.True(first.IsCurrentlyMuted("cron", 7));
    }

    [Fact]
    public void 提供者_版本戳推進後重讀()
    {
        var store = new CountingOwnerStore();
        var stamp = new DataVersionStamp();
        var provider = new IssueExclusionProvider(store, stamp, () => Today);

        Assert.True(provider.Current().IsEmpty);

        store.Inner.Upsert(Profile("cron", 7, (Today.AddDays(-1), Today.AddDays(1))));
        Assert.True(provider.Current().IsEmpty);   // 沒推進就沿用快取
        stamp.Bump();

        Assert.True(provider.Current().IsCurrentlyMuted("cron", 7));
        Assert.Equal(2, store.GetAllCount);
    }

    [Fact]
    public void 提供者_換日後CurrentlyMuted改變()
    {
        var store = new CountingOwnerStore();
        store.Inner.Upsert(Profile("cron", 7, (Today.AddDays(-3), Today)));
        var day = Today;
        var provider = new IssueExclusionProvider(store, new DataVersionStamp(), () => day);

        Assert.True(provider.Current().IsCurrentlyMuted("cron", 7));

        day = Today.AddDays(1);
        var next = provider.Current();

        Assert.False(next.IsCurrentlyMuted("cron", 7));
        Assert.Empty(next.CurrentlyMuted);
        Assert.Equal(2, store.GetAllCount);
    }

    // ── 反射守門 ──────────────────────────────────────────────

    [Fact]
    public void 介面每個方法都有無預設值的IssueExclusion參數()
    {
        var methods = typeof(IIssueAggregateQuery).GetMethods();

        Assert.Equal(20, methods.Length);   // 19 個抽象方法＋預設介面方法 AggregateReportKpiPair
        foreach (var method in methods)
        {
            var exclusionParams = method.GetParameters().Where(p => p.ParameterType == typeof(IssueExclusion)).ToList();
            Assert.True(exclusionParams.Count == 1, $"{method.Name} 缺少 IssueExclusion 參數");
            Assert.False(exclusionParams[0].HasDefaultValue, $"{method.Name} 的 IssueExclusion 參數不得有預設值");
        }
    }

    // ── 接線 ──────────────────────────────────────────────

    [Fact]
    public void 接線_IssueRankingBuilder_靜音問題不在重點問題_同一次Build只取一次Current()
    {
        var d0 = DateTime.Today.AddDays(-1);
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", d0, RiskLevels.High, Issue("disk", 153), Issue("cron", 7));

        var exclusion = IssueExclusion.From(
            new[] { Profile("cron", 7, (DateTime.Today.AddDays(-2), DateTime.Today.AddDays(3))) }, DateTime.Today);
        var source = new FixedIssueExclusionSource(exclusion);
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);

        var muted = new IssueRankingBuilder(aggregates, _hosts, source).Build(d0, d0, null, totalHosts: 1);
        var unmuted = new IssueRankingBuilder(aggregates, _hosts, new FixedIssueExclusionSource(IssueExclusion.None))
            .Build(d0, d0, null, totalHosts: 1);

        Assert.Equal(new[] { "disk" }, muted.Select(r => r.Source));
        Assert.Equal(1, source.CallCount);
        Assert.Equal(new[] { "cron", "disk" }, unmuted.Select(r => r.Source).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void 接線_IssueTodoQuery_靜音問題不進待辦()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", DateTime.Today, RiskLevels.High, Issue("disk", 153), Issue("cron", 7));

        var exclusion = IssueExclusion.From(
            new[] { Profile("cron", 7, (DateTime.Today.AddDays(-2), DateTime.Today.AddDays(3))) }, DateTime.Today);
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var resolver = new OccurrenceStatusResolver(_hosts, new FakeIssueHandlingStore(), new FakeIssueCaseStore(), new FakeSystemSettingsStore());

        var muted = new IssueTodoQuery(aggregates, resolver, new FixedIssueExclusionSource(exclusion))
            .Build(DateTime.Today.AddDays(-6), DateTime.Today, null);
        var unmuted = new IssueTodoQuery(aggregates, resolver, new FixedIssueExclusionSource(IssueExclusion.None))
            .Build(DateTime.Today.AddDays(-6), DateTime.Today, null);

        Assert.Equal(1, muted.OpenIssueCount);
        Assert.Equal(2, unmuted.OpenIssueCount);
    }

    [Fact]
    public void 接線_IssueOwnerAdminService_近期問題仍列出靜音問題()
    {
        var d0 = DateTime.Today.AddDays(-1);
        var host = _hosts.Upsert(new WebHost { HostName = "A" });
        Add(host.HostId, "A", d0, RiskLevels.High, Issue("cron", 7));

        var owners = new FakeIssueOwnerStore();
        owners.Upsert(Profile("cron", 7, (DateTime.Today.AddDays(-2), DateTime.Today.AddDays(3))));
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);

        // 前提：套用靜音時這個問題確實會被排除（否則下面的斷言恆真）
        var exclusion = IssueExclusion.From(owners.GetAll(), DateTime.Today);
        Assert.Empty(aggregates.Aggregate(exclusion, DateTime.Today.AddDays(-29), DateTime.Today, null));

        var issueStore = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        var handlingStore = new FakeHandlingStore();
        var caseCoordinator = new IssueCaseCoordinator(cases, issueStore, handlingStore, _records, _hosts, owners);
        var workOrderStore = new FakeWorkOrderStore(cases);
        var workOrders = new WorkOrderCoordinator(workOrderStore, cases, issueStore, caseCoordinator, handlingStore, _hosts);
        var service = new IssueOwnerAdminService(
            owners, aggregates, new FakeUserStore(), new RecordingAuditService(),
            FakeCurrentUser.WithCapabilities(LogForesight.Web.Auth.Capability.Maintain),
            new UserDisplayNameService(new FakeSystemSettingsStore()), workOrderStore, workOrders);

        var recent = service.RecentIssues();

        Assert.Contains(recent, r => r.SourceName == "cron" && r.EventId == 7);
    }
}

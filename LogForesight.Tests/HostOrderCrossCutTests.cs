using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 T：主機與問題橫切面的後端。
/// - F05-1 主機的進行中交辦：管理員、處理人、受限使用者（主機不可見）、只經案件授與的使用者四種視角各自看到的列。
/// - F05-2 主機快速搜尋端點：名稱與 IP 比對、最多 10 筆、只回可見範圍。
/// - F06-1 交辦單詳情的處置參考、規則 Id、訊息樣本：有規則、無規則、無樣本三例。
/// - F15-3 代為結案帶案件清單：服務層驗證與稽核。
/// 沒有新增或改動 EF 查詢（沿用既有 store 方法），所以沒有新的 SQL Server ToQueryString 測試。
/// </summary>
public class HostOrderCrossCutTests
{
    private const string Source = "disk";
    private const int EventId = 153;
    private static readonly string IssueKey = IssueSignatureKey.For("System", Source, EventId, LogEntryType.Error);
    private static readonly string OtherLogKey = IssueSignatureKey.For("Application", Source, EventId, LogEntryType.Error);
    private static readonly string OtherIssueKey = IssueSignatureKey.For("System", "svc", 7, LogEntryType.Warning);
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);
    private static readonly DateTime D1 = DateTime.Today.AddDays(-1);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeRuleStore _rules = new();

    private readonly WebUser _alice;
    private readonly WebUser _bob;

    public HostOrderCrossCutTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲" });
        _bob = _users.Upsert(new WebUser { Account = "DOMAIN\\bob", DisplayName = "鮑伯" });
    }

    private WorkOrderQueryService Service(ICurrentUser user, IVisibilityService visibility) => new(
        _orders, _cases, _users, _hosts, _hostGroups, _rules, visibility, user,
        new UserDisplayNameService(_settings), new FixedIssueExclusionSource(IssueExclusion.None), _userGroups, _records);

    private static ICurrentUser As(long userId, params Capability[] caps) => FakeCurrentUser.ForUser(userId, caps);

    private WebHost AddHost(string name, string? ip = null) =>
        _hosts.Upsert(new WebHost { HostName = name, IpAddress = ip, Active = true });

    private long AddOrder(long handler, string? source = Source, int? eventId = EventId) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = source, EventId = eventId, IssueLabel = $"{source} {eventId}", HandlerId = handler,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin",
            CreatedAt = DateTime.Today.AddHours(-1)
        });

    private IssueCase AddCase(long orderId, string host, string issueKey, string status = IssueHandlingStatuses.InProgress,
        DateTime? closed = null, bool cancelled = false)
    {
        var order = _orders.Get(orderId)!;
        var c = new IssueCase
        {
            CaseId = $"{orderId}-{host}-{issueKey.GetHashCode():x}", HostName = host, IssueKey = issueKey, IssueLabel = order.IssueLabel,
            Status = status, HandlerId = order.HandlerId, FirstLinkedDate = D2, LastLinkedDate = D1, CreatedAt = D2,
            CreatedByAccount = "admin", UpdatedAt = D2, ClosedAt = closed, Cancelled = cancelled, WorkOrderId = orderId,
            SourceName = order.SourceName, EventId = order.EventId
        };
        _cases.Save(c);
        return c;
    }

    /// <summary>可設定「可見主機」與「只經案件授與」的可見性替身（四種視角用）</summary>
    private sealed class GrantVisibility : IVisibilityService
    {
        private readonly HashSet<long> _visible;
        private readonly Dictionary<long, HashSet<string>> _grants;
        private readonly List<WebHost> _all;

        public GrantVisibility(IEnumerable<WebHost> all, IEnumerable<long> visible, Dictionary<long, HashSet<string>>? grants = null)
        {
            _all = all.ToList();
            _visible = visible.ToHashSet();
            _grants = grants ?? new Dictionary<long, HashSet<string>>();
        }

        public IReadOnlySet<long> GetVisibleHostIds() => _visible;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => _visible;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => _visible;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => _visible;
        public IReadOnlyList<string> GetCaseGrantHostNames() => _all.Where(h => _grants.ContainsKey(h.HostId)).Select(h => h.HostName).ToList();
        public bool IsCaseGrantOnly(long hostId) => !_visible.Contains(hostId) && _grants.ContainsKey(hostId);
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => IsCaseGrantOnly(hostId) ? _grants[hostId] : null;
        public List<WebHost> GetVisibleHosts() => _all.Where(h => _visible.Contains(h.HostId)).ToList();

        public void EnsureVisible(long hostId)
        {
            if (_visible.Contains(hostId) || IsCaseGrantOnly(hostId)) return;
            throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
        }
    }

    private GrantVisibility VisibleTo(WebHost[] hosts, params WebHost[] visible) =>
        new(hosts, visible.Select(h => h.HostId));

    // ── F05-1 主機的進行中交辦：四種視角 ─────────────────────────────────────

    private (WebHost Host, WebHost Other) SeedHostWithTwoOrders()
    {
        var host = AddHost("SRV-1");
        var other = AddHost("SRV-2");
        var aliceOrder = AddOrder(_alice.UserId);
        var bobOrder = AddOrder(_bob.UserId, "svc", 7);
        AddCase(aliceOrder, host.HostName, IssueKey);
        AddCase(bobOrder, host.HostName, OtherIssueKey, IssueHandlingStatuses.Escalated);
        AddCase(aliceOrder, other.HostName, IssueKey);   // 別台主機的案件不能出現在這台
        return (host, other);
    }

    [Fact]
    public void 這台的交辦_管理員看到這台全部進行中案件_每列帶處理人狀態單號且不能回覆別人的單()
    {
        var (host, other) = SeedHostWithTwoOrders();
        var dto = Service(As(99, Capability.ViewAll, Capability.Assign, Capability.Handle), VisibleTo(new[] { host, other }, host, other)).HostOrders(host.HostId);

        Assert.Equal(2, dto.Items.Count);
        var alice = dto.Items.Single(r => r.HandlerId == _alice.UserId);
        Assert.StartsWith("愛麗絲", alice.HandlerName);
        Assert.Equal(IssueHandlingStatuses.InProgress, alice.Status);
        Assert.Equal("System", alice.LogName);
        Assert.False(alice.CanReply, "管理員不是這張單的處理人，不能回覆");
        var bob = dto.Items.Single(r => r.HandlerId == _bob.UserId);
        Assert.Equal(IssueHandlingStatuses.Escalated, bob.Status);
        Assert.All(dto.Items, r => Assert.False(r.CanReply));
    }

    [Fact]
    public void 這台的交辦_處理人看得到這台時每列都在_只有自己的單可回覆()
    {
        var (host, other) = SeedHostWithTwoOrders();
        var dto = Service(As(_alice.UserId, Capability.Handle), VisibleTo(new[] { host, other }, host)).HostOrders(host.HostId);

        Assert.Equal(2, dto.Items.Count);
        Assert.True(dto.Items.Single(r => r.HandlerId == _alice.UserId).CanReply);
        Assert.False(dto.Items.Single(r => r.HandlerId == _bob.UserId).CanReply);
    }

    [Fact]
    public void 這台的交辦_受限使用者主機不可見_404且不洩漏()
    {
        var (host, other) = SeedHostWithTwoOrders();

        var ex = Assert.Throws<DomainException>(() =>
            Service(As(_bob.UserId, Capability.Handle), VisibleTo(new[] { host, other }, other)).HostOrders(host.HostId));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void 這台的交辦_只經案件授與看得到的使用者_只回被交辦的問題()
    {
        var (host, other) = SeedHostWithTwoOrders();
        var visibility = new GrantVisibility(new[] { host, other }, new[] { other.HostId },
            new Dictionary<long, HashSet<string>> { [host.HostId] = new(IssueSignatureKeyComparer.Instance) { OtherIssueKey } });

        var dto = Service(As(_bob.UserId, Capability.Handle), visibility).HostOrders(host.HostId);

        var row = Assert.Single(dto.Items);
        Assert.Equal(_bob.UserId, row.HandlerId);
        Assert.True(row.CanReply);
        Assert.DoesNotContain(dto.Items, r => r.HandlerId == _alice.UserId);
    }

    [Fact]
    public void 這台的交辦_沒有進行中案件是零列_已結案與已取消的不算()
    {
        var host = AddHost("SRV-1");
        var order = AddOrder(_alice.UserId);
        AddCase(order, host.HostName, IssueKey, IssueHandlingStatuses.Resolved, closed: D1);
        AddCase(order, host.HostName, OtherLogKey, cancelled: true, closed: D1);
        var service = Service(As(1, Capability.ViewAll), VisibleTo(new[] { host }, host));

        Assert.Empty(service.HostOrders(host.HostId).Items);
        var lonely = AddHost("SRV-9");
        Assert.Empty(Service(As(1, Capability.ViewAll), VisibleTo(new[] { lonely }, lonely)).HostOrders(lonely.HostId).Items);
    }

    [Fact]
    public void 這台的交辦_同主機同問題不同登錄類型各一列並標示登錄類型()
    {
        var host = AddHost("SRV-1");
        var order = AddOrder(_alice.UserId);
        AddCase(order, host.HostName, IssueKey);
        AddCase(order, host.HostName, OtherLogKey);
        var other = AddOrder(_bob.UserId, "svc", 7);
        AddCase(other, host.HostName, OtherIssueKey);

        var dto = Service(As(1, Capability.ViewAll), VisibleTo(new[] { host }, host)).HostOrders(host.HostId);

        Assert.Equal(3, dto.Items.Count);
        var disk = dto.Items.Where(r => r.IssueLabel.Contains(Source)).ToList();
        Assert.Equal(2, disk.Count);
        Assert.All(disk, r => Assert.True(r.ShowLogName));
        Assert.Equal(new[] { "Application", "System" }, disk.Select(r => r.LogName).OrderBy(x => x).ToArray());
        Assert.False(dto.Items.Single(r => r.HandlerId == _bob.UserId).ShowLogName, "只有一列的問題不標登錄類型");
    }

    // ── F06-1 交辦單詳情：處置參考、規則 Id、訊息樣本 ──────────────────────────

    private void UseRule(string id = "custom-disk-153") =>
        _rules.Content = new RuleFileContent
        {
            Rules = new List<KnownIssueRule>
            {
                new()
                {
                    Id = id, Origin = "custom", Enabled = true, SourcePattern = Source, EventIds = new[] { EventId },
                    Category = IssueCategory.Storage, Severity = IssueSeverity.High, Description = "磁碟重試",
                    PlainExplanation = "磁碟讀寫一直重試", Impact = "資料可能遺失", LikelyCauses = new[] { "硬碟老化" }, NextSteps = new[] { "備份", "換碟" }
                }
            }
        };

    private void AddRecordWithSamples(WebHost host, DateTime date, params string[] samples) =>
        _records.Add(new DailyAnalysisRecord
        {
            Date = date, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.High,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = Source, EventId = EventId, EntryType = LogEntryType.Error, SampleMessages = samples.ToList() }
            }
        });

    [Fact]
    public void 交辦單詳情_命中規則_帶處置參考與規則Id_最近一天的樣本最多三則()
    {
        UseRule();
        var host = AddHost("SRV-1");
        var order = AddOrder(_alice.UserId);
        AddCase(order, host.HostName, IssueKey);
        AddRecordWithSamples(host, D2, "舊樣本");
        AddRecordWithSamples(host, D1, "新樣本一", "新樣本二", "新樣本三", "新樣本四");

        var dto = Service(As(1, Capability.Assign, Capability.Maintain), VisibleTo(new[] { host }, host)).Get(order);

        Assert.NotNull(dto.Guidance);
        Assert.Equal("資料可能遺失", dto.Guidance!.Impact);
        Assert.Equal(new[] { "備份", "換碟" }, dto.Guidance.NextSteps);
        Assert.Equal("custom-disk-153", dto.RuleId);
        Assert.Equal(new[] { "新樣本一", "新樣本二", "新樣本三" }, dto.SampleMessages);
    }

    [Fact]
    public void 交辦單詳情_未命中規則或多問題單_沒有處置參考與規則Id_但訊息樣本照常()
    {
        UseRule();
        var host = AddHost("SRV-1");
        // 未命中規則的問題（來源與事件都不在規則內）
        var unknownKey = IssueSignatureKey.For("System", "zzz-unknown", 99, LogEntryType.Warning);
        var unknown = AddOrder(_alice.UserId, "zzz-unknown", 99);
        AddCase(unknown, host.HostName, unknownKey);
        _records.Add(new DailyAnalysisRecord
        {
            Date = D1, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Medium,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "zzz-unknown", EventId = 99, EntryType = LogEntryType.Warning, SampleMessages = new List<string> { "沒有規則也看得到的樣本" } }
            }
        });
        var service = Service(As(1, Capability.Assign, Capability.Maintain), VisibleTo(new[] { host }, host));

        var dto = service.Get(unknown);
        Assert.Null(dto.Guidance);
        Assert.Null(dto.RuleId);
        Assert.Equal(new[] { "沒有規則也看得到的樣本" }, dto.SampleMessages);

        // 多問題單（沒有單一來源與事件）：沒有處置參考、規則 Id 與樣本
        var multi = AddOrder(_alice.UserId, null, null);
        AddCase(multi, host.HostName, IssueKey);
        var multiDto = service.Get(multi);
        Assert.Null(multiDto.Guidance);
        Assert.Null(multiDto.RuleId);
        Assert.Empty(multiDto.SampleMessages);
    }

    [Fact]
    public void 交辦單詳情_最近一天的樣本已清除_往前找有樣本的那天_都沒有則為空()
    {
        UseRule();
        var host = AddHost("SRV-1");
        var order = AddOrder(_alice.UserId);
        AddCase(order, host.HostName, IssueKey);
        AddRecordWithSamples(host, D2, "前天的樣本");
        AddRecordWithSamples(host, D1);   // 昨天詳情已清除：沒有樣本
        var service = Service(As(1, Capability.Assign), VisibleTo(new[] { host }, host));

        Assert.Equal(new[] { "前天的樣本" }, service.Get(order).SampleMessages);

        var bare = AddHost("SRV-2");
        var order2 = AddOrder(_bob.UserId);
        AddCase(order2, bare.HostName, IssueKey);
        AddRecordWithSamples(bare, D1);
        Assert.Empty(Service(As(1, Capability.Assign), VisibleTo(new[] { bare }, bare)).Get(order2).SampleMessages);
    }

    [Fact]
    public void 交辦單詳情_樣本只取檢視者看得到的成員主機()
    {
        UseRule();
        var visible = AddHost("SRV-1");
        var hidden = AddHost("SRV-2");
        var order = AddOrder(_alice.UserId);
        AddCase(order, visible.HostName, IssueKey);
        AddCase(order, hidden.HostName, IssueKey);
        AddRecordWithSamples(visible, D2, "看得到的樣本");
        AddRecordWithSamples(hidden, D1, "看不到的主機的樣本");

        // 非處理人、非 ViewAll 的指派者：成員依可見範圍過濾，樣本也不能跨範圍
        var dto = Service(As(_bob.UserId, Capability.Assign), VisibleTo(new[] { visible, hidden }, visible)).Get(order);

        Assert.Equal(new[] { "看得到的樣本" }, dto.SampleMessages);
    }

    [Fact]
    public void 交辦單詳情_上報中的進行中成員列出案件編號_供代為結案只有上報中的範圍()
    {
        UseRule();
        var h1 = AddHost("SRV-1");
        var h2 = AddHost("SRV-2");
        var h3 = AddHost("SRV-3");
        var order = AddOrder(_alice.UserId);
        var esc1 = AddCase(order, h1.HostName, IssueKey, IssueHandlingStatuses.Escalated);
        AddCase(order, h2.HostName, IssueKey);
        var esc3 = AddCase(order, h3.HostName, IssueKey, IssueHandlingStatuses.Escalated);

        var dto = Service(As(1, Capability.ViewAll, Capability.Assign), VisibleTo(new[] { h1, h2, h3 }, h1, h2, h3)).Get(order);

        Assert.Equal(new[] { esc1.CaseId, esc3.CaseId }.OrderBy(x => x, StringComparer.Ordinal), dto.EscalatedCaseIds);
    }

    // ── F05-2 主機快速搜尋端點（沿用 GET api/hosts?query=）─────────────────────

    private HostsController Controller(params WebHost[] visible) => new(new ScopedHostList(visible), _hostGroups);

    private sealed class ScopedHostList : IVisibilityService
    {
        private readonly List<WebHost> _visible;
        public ScopedHostList(IEnumerable<WebHost> visible) => _visible = visible.ToList();
        public IReadOnlySet<long> GetVisibleHostIds() => _visible.Select(h => h.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => Array.Empty<string>();
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public List<WebHost> GetVisibleHosts() => _visible;
        public void EnsureVisible(long hostId) { }
    }

    [Fact]
    public void 主機搜尋_名稱與IP各一例_受限使用者搜不到範圍外的主機()
    {
        var web = AddHost("WEB-01", "10.1.2.3");
        var db = AddHost("DB-01", "10.9.9.9");
        var secret = AddHost("SECRET-01", "10.1.2.99");

        var byName = Controller(web, db).GetVisibleHosts("web").Data!;
        Assert.Equal(new[] { "WEB-01" }, byName.Items.Select(h => h.HostName).ToArray());

        var byIp = Controller(web, db).GetVisibleHosts("10.1.2").Data!;
        Assert.Equal(new[] { "WEB-01" }, byIp.Items.Select(h => h.HostName).ToArray());
        Assert.Equal("10.1.2.3", byIp.Items.Single().IpAddress);

        // SECRET-01 不在可見範圍：名稱與 IP 都搜不到
        Assert.Empty(Controller(web, db).GetVisibleHosts("secret").Data!.Items);
        Assert.DoesNotContain(Controller(web, db).GetVisibleHosts("10.1.2").Data!.Items, h => h.HostId == secret.HostId);
    }

    [Fact]
    public void 主機搜尋_limit限縮筆數_超過時標示截斷與總數_limit不得超過二十()
    {
        var hosts = Enumerable.Range(1, 15).Select(i => AddHost($"SRV-{i:00}")).ToArray();

        var ten = Controller(hosts).GetVisibleHosts("srv", limit: 10).Data!;
        Assert.Equal(10, ten.Items.Count);
        Assert.True(ten.Truncated);
        Assert.Equal(15, ten.Total);

        var all = Controller(hosts).GetVisibleHosts("srv", limit: 15).Data!;
        Assert.Equal(15, all.Items.Count);
        Assert.False(all.Truncated, "limit 剛好等於符合筆數：沒有被截斷");

        var capped = Controller(Enumerable.Range(1, 30).Select(i => AddHost($"BIG-{i:00}")).ToArray()).GetVisibleHosts("big", limit: 500).Data!;
        Assert.Equal(HostsController.AutocompleteLimit, capped.Items.Count);
        Assert.Equal(30, capped.Total);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests.Authorization;

/// <summary>哨兵字串的種類：每台主機、每種資料各一個獨一無二的字串，出現在回應裡才算「資料有被取到」。</summary>
public enum SentinelKind
{
    /// <summary>主機名稱</summary>
    Host,
    /// <summary>主機 IP</summary>
    Ip,
    /// <summary>風險日整日敘事（Headline）</summary>
    Headline,
    /// <summary>問題 A（案件授與者被交辦的那一個）的來源與範例訊息</summary>
    IssueA,
    /// <summary>問題 B（同一天其他問題）</summary>
    IssueB,
    /// <summary>日層級處理備註與處理歷程</summary>
    Note,
    /// <summary>問題 A 先前結案紀錄的處理說明（問題層級歷史）</summary>
    IssueNote,
    /// <summary>風險報告全文</summary>
    Report,
    /// <summary>權限異動清單的目標</summary>
    Perm,
    /// <summary>權限異動報告全文</summary>
    PermReport,
    /// <summary>主機群組名稱</summary>
    Group
}

/// <summary>哨兵字串的唯一組法與偵測。所有哨兵都以 <c>SENTINEL-H{n}-</c> 開頭（IP 除外），一個前綴即可涵蓋「這台主機的任何資料」。</summary>
public static class Sentinels
{
    public static string Prefix(int n) => $"SENTINEL-H{n}-";

    public static string Ip(int n) => $"10.77.{n}.7";

    public static string Of(int n, SentinelKind kind) => kind switch
    {
        SentinelKind.Host => Prefix(n) + "HOST",
        SentinelKind.Ip => Ip(n),
        SentinelKind.Headline => Prefix(n) + "HEADLINE",
        SentinelKind.IssueA => Prefix(n) + "ISSUE-A",
        SentinelKind.IssueB => Prefix(n) + "ISSUE-B",
        SentinelKind.Note => Prefix(n) + "NOTE",
        SentinelKind.IssueNote => Prefix(n) + "ISSUEA-NOTE",
        SentinelKind.Report => Prefix(n) + "REPORT",
        SentinelKind.Perm => Prefix(n) + "PERM",
        SentinelKind.PermReport => Prefix(n) + "PERMREPORT",
        SentinelKind.Group => Prefix(n) + "GROUP",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>回應含這台主機任何一種哨兵（含前綴通配，抓到未列舉的新欄位）</summary>
    public static bool ContainsAny(string body, int n) =>
        body.Contains(Prefix(n), StringComparison.Ordinal) || body.Contains(Ip(n), StringComparison.Ordinal);

    public static bool Contains(string body, int n, SentinelKind kind) =>
        body.Contains(Of(n, kind), StringComparison.Ordinal);

    public static bool ContainsAnyOf(string body, int n, IEnumerable<SentinelKind> kinds) =>
        kinds.Any(k => Contains(body, n, k));
}

/// <summary>某身分對某台主機的可見程度（期望值，依規格 §7.1 推得）</summary>
public enum Visibility
{
    /// <summary>完整可見</summary>
    Full,
    /// <summary>只因案件授與看得到被交辦的那個問題</summary>
    CaseOnly,
    /// <summary>完全不可見（未授權、停用、或身分根本沒有業務資料能力）</summary>
    None
}

public sealed class IdentityFx
{
    public required string Name { get; init; }
    public required string Account { get; init; }
    public long UserId { get; init; }
    public required HashSet<Capability> Caps { get; init; }
    public bool Anonymous { get; init; }
    public string? Token { get; set; }

    /// <summary>主機序號（1~4）→ 期望可見程度；未列出者視為 None</summary>
    public Dictionary<int, Visibility> Vis { get; } = new();

    public Visibility VisOf(int n) => Vis.TryGetValue(n, out var v) ? v : Visibility.None;

    public bool Has(Capability c) => Caps.Contains(c);

    public override string ToString() => Name;
}

public sealed class HostFx
{
    public int N { get; init; }
    public long Id { get; set; }
    public string Name => Sentinels.Of(N, SentinelKind.Host);
    public string Ip => Sentinels.Ip(N);
    public bool Active { get; init; }
    public LogIssueSignature IssueA { get; init; } = new();
    public LogIssueSignature IssueB { get; init; } = new();
    public string IssueKeyA => IssueSignatureKey.For(IssueA);
}

public sealed class WorkOrderFx
{
    public required string Tag { get; init; }
    public long Id { get; set; }
    public long HandlerId { get; set; }
    public string Needle(string part) => $"SENTINEL-WO-{Tag}-{part}";
}

/// <summary>
/// 授權矩陣的 HTTP 基座：用 <see cref="WebApplicationFactory{TEntryPoint}"/> 起**真的站台組裝**
/// （<c>Program.cs</c>），走真的 JWT 驗證、真路由、真的 <c>[Permission]</c> 與 <c>VisibilityService</c>。
///
/// 刻意**不替換**可見範圍、目前使用者、權限過濾或驗證中介層——它們正是要驗的東西；
/// 唯一的替換是移除全部 <see cref="IHostedService"/>（排程、快照、恢復服務不該在測試裡跑）。
/// 資料一律經站台自己的 store 寫入（與生產路徑同一套寫入實作）。
///
/// 場景：四台主機（1、2、3 啟用，4 停用）、四種身分加上 serverAdmin 與匿名，
/// 每台主機昨天都有一筆高風險紀錄（兩個問題），並各放獨一無二的哨兵字串。
/// </summary>
public sealed class AuthorizationHttpFixture : IDisposable
{
    public const string AdminAccount = "auth-admin";
    public const string ServerAdminAccount = "svc-lfadmin";

    public static readonly DateTime Day = DateTime.Today.AddDays(-1);
    public static string DayText => Day.ToString("yyyy-MM-dd");

    private readonly string _dataRoot;
    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private TcpListener? _aiSink;

    public IReadOnlyList<HostFx> Hosts { get; }
    public IdentityFx A { get; private set; } = null!;
    public IdentityFx B { get; private set; } = null!;
    public IdentityFx C { get; private set; } = null!;
    public IdentityFx Admin { get; private set; } = null!;
    public IdentityFx ServerAdmin { get; private set; } = null!;
    public IdentityFx Anonymous { get; } = new()
    {
        Name = "anonymous",
        Account = string.Empty,
        Caps = new HashSet<Capability>(),
        Anonymous = true
    };

    public IReadOnlyList<IdentityFx> Identities { get; private set; } = Array.Empty<IdentityFx>();
    public WorkOrderFx WorkOrderA { get; private set; } = null!;
    public WorkOrderFx WorkOrderC { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;

    public HostFx Host(int n) => Hosts.Single(h => h.N == n);

    public IdentityFx Identity(string name) => Identities.Single(i => i.Name == name);

    public AuthorizationHttpFixture()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "lf-authz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        // 設定走環境變數：Development 的 appsettings.Development.json 會蓋過 UseSetting，
        // 環境變數是最後載入的來源，才確保資料根目錄與簽章金鑰是測試自己的值。
        SetEnv("Storage__Type", "Sqlite");
        SetEnv("Storage__DataRoot", _dataRoot);
        SetEnv("Storage__ConnectionString", "");
        SetEnv("Auth__Provider", "Stub");
        SetEnv("Jwt__SecretKey", "authz-matrix-test-secret-key-0123456789-abcdefghijklmnop");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Development");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
        _client.DefaultRequestHeaders.Add("X-Requested-By", "LogForesight");

        Hosts = new[] { 1, 2, 3, 4 }.Select(n => new HostFx
        {
            N = n,
            Active = n != 4,
            IssueA = MakeIssue(n, "A", 1001),
            IssueB = MakeIssue(n, "B", 1002)
        }).ToList();

        Seed();
        LoginAll();
    }

    private void SetEnv(string key, string value)
    {
        _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private static LogIssueSignature MakeIssue(int n, string tag, int eventId) => new()
    {
        LogName = "System",
        Source = Sentinels.Prefix(n) + "ISSUE-" + tag,
        EventId = eventId,
        EntryType = LogEntryType.Error,
        Severity = IssueSeverity.High,
        Count = 5,
        FirstSeen = "01:00",
        LastSeen = "02:00",
        DistinctMessageCount = 1,
        SampleMessages = new List<string> { Sentinels.Prefix(n) + "ISSUE-" + tag + " 範例訊息" }
    };

    // ── 資料建置：全部經站台自己的 store ────────────────────────────────────────
    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<IUserStore>();
        var userGroups = sp.GetRequiredService<IUserGroupStore>();
        var access = sp.GetRequiredService<IGroupAccessStore>();
        var hostStore = sp.GetRequiredService<IHostStore>();
        var hostGroups = sp.GetRequiredService<IHostGroupStore>();
        var storage = sp.GetRequiredService<StorageBackend>();

        // AI：預設位址是 localhost:8080（可能真有東西在聽）。指向本測試自己開的「接受連線後立刻關閉」的埠並關掉重試——
        // AI 讀取端點仍會走完授權路徑（AI 視為已設定），呼叫則立刻失敗，不對外連線也不拖慢測試
        // （連線被拒的本機埠在 Windows 上要等好幾秒才回報）
        _aiSink = new TcpListener(IPAddress.Loopback, 0);
        _aiSink.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var accepted = await _aiSink.AcceptTcpClientAsync();
                }
            }
            catch (Exception) { /* 站台關閉時 listener 停止，結束即可 */ }
        });
        var aiPort = ((IPEndPoint)_aiSink.LocalEndpoint).Port;
        sp.GetRequiredService<ISystemSettingsStore>().Update(s =>
        {
            s.AiBaseUrl = $"http://127.0.0.1:{aiPort}";
            s.AiRetryCount = 0;
            s.AiTimeoutSeconds = 5;
        });

        // 使用者群組：兩個一般部門（A 的有授權、其餘無授權）＋ 站台自己的 admin 種子群組
        var deptA = userGroups.Upsert(new UserGroup { GroupName = "authz-dept-A", Role = UserRole.User });
        var deptB = userGroups.Upsert(new UserGroup { GroupName = "authz-dept-B", Role = UserRole.User });
        var deptC = userGroups.Upsert(new UserGroup { GroupName = "authz-dept-C", Role = UserRole.User });
        var adminGroup = userGroups.GetAll().First(g => g.Role == UserRole.Admin && g.Active);

        var userA = users.Upsert(new WebUser { Account = "AUTHZ\\user-a", DisplayName = "受限者A", GroupIds = new List<long> { deptA.GroupId } });
        var userB = users.Upsert(new WebUser { Account = "AUTHZ\\user-b", DisplayName = "負責人B", GroupIds = new List<long> { deptB.GroupId } });
        var userC = users.Upsert(new WebUser { Account = "AUTHZ\\user-c", DisplayName = "案件授與C", GroupIds = new List<long> { deptC.GroupId } });
        var admin = users.Upsert(new WebUser { Account = AdminAccount, DisplayName = "測試管理者", GroupIds = new List<long> { adminGroup.GroupId } });

        // 主機群組與主機：A 的部門只授權主機群組 1；B 是主機 2 的負責人（第二條授權路徑）；C 沒有任何授權
        var groupIds = new Dictionary<int, long>();
        foreach (var h in Hosts)
        {
            var g = hostGroups.Upsert(new HostGroup { GroupName = Sentinels.Of(h.N, SentinelKind.Group) });
            groupIds[h.N] = g.GroupId;
        }

        foreach (var h in Hosts)
        {
            var saved = hostStore.Upsert(new WebHost
            {
                HostName = h.Name,
                IpAddress = h.Ip,
                Active = h.Active,
                GroupIds = new List<long> { groupIds[h.N] },
                OwnerUserIds = h.N == 2 ? new List<long> { userB.UserId } : new List<long>()
            });
            h.Id = saved.HostId;
        }

        access.SetForUserGroup(deptA.GroupId, new[] { groupIds[1] });

        // 每台主機昨天一筆高風險紀錄（兩個問題），含報告全文、日處理備註與歷程、問題先前結案說明
        var records = storage.RecordStore();
        var handlings = sp.GetRequiredService<IRecordHandlingStore>();
        var issueHandlings = sp.GetRequiredService<IIssueHandlingStore>();
        var reports = sp.GetRequiredService<EfReportStore>();
        foreach (var h in Hosts)
        {
            records.Append(new DailyAnalysisRecord
            {
                HostId = h.Id,
                Host = h.Name,
                Date = Day,
                RiskLevel = "高",
                Headline = Sentinels.Of(h.N, SentinelKind.Headline),
                Summary = "整日總覽",
                TrendAssessment = "趨勢判讀",
                Action = "建議處置",
                AiAnalyzed = true,
                ReportFile = $"{DayText}_risk.txt",
                ErrorCount = 10,
                TopIssues = new List<LogIssueSignature> { h.IssueA, h.IssueB }
            });

            var hostKey = new HostKey { HostId = h.Id, HostName = h.Name };
            reports.WriteAsync(ReportKind.DailyRisk, hostKey, $"{DayText}_risk.txt",
                Sentinels.Of(h.N, SentinelKind.Report) + " 報告全文", new ReportMeta("高")).GetAwaiter().GetResult();
            reports.WriteAsync(ReportKind.Permission, hostKey, $"{DayText}_permission.txt",
                Sentinels.Of(h.N, SentinelKind.PermReport) + " 權限異動報告全文").GetAwaiter().GetResult();

            handlings.Save(new RecordHandling
            {
                HostName = h.Name,
                Date = Day,
                Status = HandlingStatuses.InProgress,
                Note = Sentinels.Of(h.N, SentinelKind.Note),
                UpdatedAt = DateTime.Now
            });
            handlings.AppendLog(new RecordHandlingLog
            {
                HostName = h.Name,
                Date = Day,
                Status = HandlingStatuses.InProgress,
                Note = Sentinels.Of(h.N, SentinelKind.Note),
                ActorId = admin.UserId,
                ActorAccount = AdminAccount,
                Action = HandlingActions.NoteUpdate,
                CreatedAt = DateTime.Now
            });

            // 三天前已結案的同一個問題：問題層級歷史（issue-history）的資料來源
            issueHandlings.Save(new IssueHandling
            {
                HostName = h.Name,
                Date = Day.AddDays(-3),
                IssueKey = h.IssueKeyA,
                Status = IssueHandlingStatuses.Resolved,
                ActorId = admin.UserId,
                ActorAccount = AdminAccount,
                Note = Sentinels.Of(h.N, SentinelKind.IssueNote)
            });

            sp.GetRequiredService<PermissionChangeStore>().AppendChanges(new[]
            {
                new PermissionChangeRecord
                {
                    ChangeId = $"authz-change-{h.N}",
                    HostName = h.Name,
                    DetectedAt = Day.AddHours(3),
                    Target = Sentinels.Of(h.N, SentinelKind.Perm),
                    ChangeType = "成員新增",
                    Before = "-",
                    After = "-",
                    AlertText = Sentinels.Of(h.N, SentinelKind.Perm) + " 告警"
                }
            });
        }

        // 交辦單：A 名下一張（無成員）、C 名下一張（成員是主機 3 的案件）
        var orders = sp.GetRequiredService<IWorkOrderStore>();
        WorkOrderA = NewOrder(orders, "A", userA.UserId, "SENTINEL-WO-A-LABEL", null, admin);
        var h3 = Host(3);
        WorkOrderC = NewOrder(orders, "C", userC.UserId, h3.IssueA.SourceEventLabel, h3.IssueA, admin);

        // 案件授與 C：沒有任何主機授權，但主機 3 上有一個交辦給他的進行中問題案件
        // （形狀照 CaseGrantVisibilityTests）
        sp.GetRequiredService<IIssueCaseStore>().Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("N"),
            HostName = h3.Name,
            IssueKey = h3.IssueKeyA,
            IssueLabel = h3.IssueA.SourceEventLabel,
            Status = IssueHandlingStatuses.InProgress,
            HandlerId = userC.UserId,
            FirstLinkedDate = Day,
            LastLinkedDate = Day,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            CreatedByAccount = AdminAccount,
            WorkOrderId = WorkOrderC.Id
        });

        // 身分與期望可見度（規格 §7.1）
        var userCaps = new[] { Capability.Handle, Capability.ConfirmPermission };
        A = new IdentityFx { Name = "A受限", Account = userA.Account, UserId = userA.UserId, Caps = userCaps.ToHashSet() };
        A.Vis[1] = Visibility.Full;
        B = new IdentityFx { Name = "B負責人", Account = userB.Account, UserId = userB.UserId, Caps = userCaps.ToHashSet() };
        B.Vis[2] = Visibility.Full;
        C = new IdentityFx { Name = "C案件授與", Account = userC.Account, UserId = userC.UserId, Caps = userCaps.ToHashSet() };
        C.Vis[3] = Visibility.CaseOnly;
        Admin = new IdentityFx
        {
            Name = "Admin",
            Account = AdminAccount,
            UserId = admin.UserId,
            Caps = Enum.GetValues<Capability>().ToHashSet()
        };
        // 停用主機（4）一律不可見，連管理者也一樣
        Admin.Vis[1] = Admin.Vis[2] = Admin.Vis[3] = Visibility.Full;
        ServerAdmin = new IdentityFx
        {
            Name = "serverAdmin",
            Account = ServerAdminAccount,
            UserId = 0,
            Caps = new HashSet<Capability> { Capability.Maintain, Capability.ViewAudit }
        };
        Identities = new[] { A, B, C, Admin, ServerAdmin, Anonymous };
    }

    private WorkOrderFx NewOrder(IWorkOrderStore orders, string tag, long handlerId, string issueLabel,
        LogIssueSignature? issue, WebUser creator)
    {
        var order = new WorkOrder
        {
            HandlerId = handlerId,
            IssueLabel = issueLabel,
            SourceName = issue?.Source,
            EventId = issue?.EventId,
            Note = $"SENTINEL-WO-{tag}-NOTE",
            CreatedById = creator.UserId,
            CreatedByAccount = creator.Account,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        var id = orders.Insert(order);
        orders.AppendEvent(new WorkOrderEvent
        {
            WorkOrderId = id,
            Action = WorkOrderEventActions.Created,
            ActorId = creator.UserId,
            ActorAccount = creator.Account,
            MemberDelta = 0,
            Note = $"SENTINEL-WO-{tag}-EVENT",
            CreatedAt = DateTime.Now
        });
        return new WorkOrderFx { Tag = tag, Id = id, HandlerId = handlerId };
    }

    // ── 登入與請求 ────────────────────────────────────────────────────────────
    private void LoginAll()
    {
        foreach (var id in Identities.Where(i => !i.Anonymous))
        {
            using var response = _client.PostAsJsonAsync("api/auth/login", new { account = id.Account, password = "" })
                .GetAwaiter().GetResult();
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"登入 {id.Name}（{id.Account}）失敗：{(int)response.StatusCode} {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
            var cookie = response.Headers.GetValues("Set-Cookie").FirstOrDefault(c => c.StartsWith("lf_auth=", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(cookie), $"登入 {id.Name} 沒有回 lf_auth Cookie");
            id.Token = cookie!.Split(';')[0]["lf_auth=".Length..];
        }
    }

    /// <summary>以指定身分（匿名＝不帶 token）送 GET，回傳狀態碼與回應全文</summary>
    public async Task<(HttpStatusCode Status, string Body)> GetAsync(IdentityFx identity, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (identity.Token != null) request.Headers.Add("Cookie", "lf_auth=" + identity.Token);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _aiSink?.Stop();
        foreach (var (key, value) in _savedEnv) Environment.SetEnvironmentVariable(key, value);
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* SQLite 檔案可能仍被連線池占用，留在暫存目錄無妨 */ }
    }
}

/// <summary>整組授權測試共用一個站台（建置與登入只做一次）。
/// 不與其他測試並行：fixture 以行程層級的環境變數帶入站台設定（Program 在 builder.Build 前就讀組態，
/// WebApplicationFactory 的設定掛勾來不及），並行時其他起站台的測試可能讀到這些值</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthorizationHttpCollection : ICollectionFixture<AuthorizationHttpFixture>
{
    public const string Name = "AuthorizationHttp";
}

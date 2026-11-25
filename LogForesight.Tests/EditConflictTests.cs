using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 M（CU-11／CU-12）：管理員同時編輯同一條規則或同一位使用者。
/// 儲存請求帶「開啟編輯時讀到的版本」，伺服器在寫入的臨界區內比對，不同就整筆不寫（409、不記稽核）。
/// 什麼算一個版本：規則＝該條規則內容的雜湊（改別條不影響）；使用者＝顯示名稱、Email、啟用、群組的雜湊。
/// 編輯既有資料沒帶版本與版本不符同樣視為衝突；新增沒有舊版本。
/// </summary>
public class EditConflictTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-edit-conflict-" + Guid.NewGuid().ToString("N"));

    public EditConflictTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* SQLite 連線池可能仍占用檔案 */ }
        GC.SuppressFinalize(this);
    }

    // ── 規則 ────────────────────────────────────────────────────────────────

    private static KnownIssueRule Rule(string id, string description = "自訂應用程式錯誤") => new()
    {
        Id = id, Origin = "custom", Enabled = true, SourcePattern = "MyApp", EventIds = new[] { 9001 },
        Category = IssueCategory.Service, Severity = IssueSeverity.Medium, Description = description,
        CountThreshold = 3, PlainExplanation = "應用程式發生錯誤", Impact = "服務可能中斷",
        LikelyCauses = new[] { "設定錯誤" }, NextSteps = new[] { "檢查設定檔" }
    };

    private static SaveRuleRequest RuleRequest(string id, string? version, string description = "自訂應用程式錯誤",
        string plain = "應用程式發生錯誤", int count = 3) => new()
    {
        Id = id, Version = version, Enabled = true, SourcePattern = "MyApp", EventIds = new List<int> { 9001 },
        Category = "Service", Severity = "Medium", Description = description, CountThreshold = count,
        PlainExplanation = plain, Impact = "服務可能中斷",
        LikelyCauses = new List<string> { "設定錯誤" }, NextSteps = new List<string> { "檢查設定檔" }
    };

    private RuleAdminService RuleService(IKnownIssueRuleStore rules, RecordingAuditService audit)
    {
        var hosts = new FakeHostStore();
        return new RuleAdminService(rules, new FakeRuleSeedStore(), new FakeSuppressionStore(), new FakeUserStore(),
            FakeCurrentUser.WithCapabilities(Capability.Maintain), audit, new FakeHostGroupStore(), hosts,
            new FakeIssueAggregateQuery(), visibility: new FixedVisibilityService(Array.Empty<long>()));
    }

    private static FakeRuleStore SeededRuleStore(params KnownIssueRule[] rules) =>
        new() { Content = new RuleFileContent { SeedVersion = 1, Rules = rules.ToList() } };

    [Fact]
    public void 規則_兩個以同一版本出發的儲存_第二個衝突_資料為第一個的內容_只有一筆稽核()
    {
        var store = SeededRuleStore(Rule("custom-a"));
        var audit = new RecordingAuditService();
        var service = RuleService(store, audit);
        var version = service.GetRules().Single().Version;

        service.SaveRule(RuleRequest("custom-a", version, description: "A 先存"));
        Assert.Throws<ConcurrentUpdateException>(() =>
            service.SaveRule(RuleRequest("custom-a", version, plain: "B 後存的白話說明")));

        var saved = store.Content.Rules.Single();
        Assert.Equal("A 先存", saved.Description);
        Assert.Equal("應用程式發生錯誤", saved.PlainExplanation);
        Assert.Single(audit.Entries, e => e.Action == AuditActions.RuleUpdate);
    }

    [Fact]
    public void 規則_版本相同_照常儲存_且回應帶新版本()
    {
        var store = SeededRuleStore(Rule("custom-a"));
        var service = RuleService(store, new RecordingAuditService());
        var v1 = service.GetRules().Single().Version;

        var dto = service.SaveRule(RuleRequest("custom-a", v1, description: "改過了"));

        Assert.Equal("改過了", store.Content.Rules.Single().Description);
        Assert.NotEqual(v1, dto.Version);
        // 用新版本再存一次也成功（連續編輯不會自己撞自己）
        service.SaveRule(RuleRequest("custom-a", dto.Version, description: "再改一次"));
        Assert.Equal("再改一次", store.Content.Rules.Single().Description);
    }

    [Fact]
    public void 規則_編輯既有規則沒帶版本_視為衝突_資料不變()
    {
        var store = SeededRuleStore(Rule("custom-a"));
        var audit = new RecordingAuditService();
        var service = RuleService(store, audit);

        Assert.Throws<ConcurrentUpdateException>(() => service.SaveRule(RuleRequest("custom-a", null, description: "沒帶版本")));
        Assert.Throws<ConcurrentUpdateException>(() => service.SaveRule(RuleRequest("custom-a", "", description: "空版本")));

        Assert.Equal("自訂應用程式錯誤", store.Content.Rules.Single().Description);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void 規則_新增不需版本_同編號剛被別人建立則衝突()
    {
        var store = SeededRuleStore();
        var service = RuleService(store, new RecordingAuditService());

        service.SaveRule(RuleRequest("custom-new", null));
        Assert.Single(store.Content.Rules);

        // 第二個也以「新增」出發（沒有版本）：同編號已存在，不得產生兩條同編號規則
        Assert.Throws<ConcurrentUpdateException>(() => service.SaveRule(RuleRequest("custom-new", null, description: "第二個新增")));
        Assert.Single(store.Content.Rules);
    }

    [Fact]
    public void 規則_改別條規則不影響這條的版本()
    {
        var store = SeededRuleStore(Rule("custom-a"), Rule("custom-b"));
        var service = RuleService(store, new RecordingAuditService());
        var rules = service.GetRules();
        var versionA = rules.Single(r => r.Id == "custom-a").Version;
        var versionB = rules.Single(r => r.Id == "custom-b").Version;

        service.SaveRule(RuleRequest("custom-b", versionB, description: "只改 B"));

        service.SaveRule(RuleRequest("custom-a", versionA, description: "A 仍可用舊版本儲存"));
        Assert.Equal("A 仍可用舊版本儲存", store.Content.Rules.Single(r => r.Id == "custom-a").Description);
    }

    [Fact]
    public void 規則_停用啟用刪除匯入走既有路徑_不要求版本()
    {
        var store = SeededRuleStore(Rule("custom-a"), Rule("custom-b"));
        var service = RuleService(store, new RecordingAuditService());

        service.SetEnabled("custom-a", false);
        Assert.False(store.Content.Rules.Single(r => r.Id == "custom-a").Enabled);
        service.SetEnabled("custom-a", true);
        service.DeleteRule("custom-b");
        Assert.Single(store.Content.Rules);
        service.ApplyImport(overwriteBuiltin: false);
        Assert.Contains(store.Content.Rules, r => r.Id == "custom-a");
    }

    [Fact]
    public void 規則_兩個執行緒以同一版本同時儲存_恰好一個成功()
    {
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        var store = new KnownIssueRuleStore(backend.Blob("rules"));
        store.Save(new RuleFileContent { SeedVersion = 1, Rules = new List<KnownIssueRule> { Rule("custom-a") } });
        var audit = new RecordingAuditService();
        var service = RuleService(store, audit);
        var version = service.GetRules().Single().Version;

        for (var round = 0; round < 5; round++)
        {
            version = service.GetRules().Single().Version;
            using var gate = new Barrier(2);
            var outcomes = new string?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                gate.SignalAndWait();
                try
                {
                    service.SaveRule(RuleRequest("custom-a", version, description: $"執行緒{i}-回合{round}"));
                    outcomes[i] = "ok";
                }
                catch (ConcurrentUpdateException) { outcomes[i] = "conflict"; }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, outcomes.Count(o => o == "ok"));
            Assert.Equal(1, outcomes.Count(o => o == "conflict"));
            Assert.Contains("回合" + round, service.GetRules().Single().Description);
        }
        Assert.Equal(5, audit.Entries.Count(e => e.Action == AuditActions.RuleUpdate));
    }

    // ── 使用者 ──────────────────────────────────────────────────────────────

    private sealed class UserRig
    {
        public readonly IUserStore Users;
        public readonly FakeUserGroupStore Groups = new();
        public readonly RecordingAuditService Audit = new();
        public readonly PermissionVersionStamp Stamp = TestPermissionStamps.Create();
        public readonly UserAdminService Service;
        public readonly long GroupA, GroupB;

        public UserRig(IUserStore? users = null)
        {
            Users = users ?? new FakeUserStore();
            var hosts = new FakeHostStore();
            GroupA = Groups.Upsert(new UserGroup { GroupName = "群組A", Role = UserRole.User, Active = true }).GroupId;
            GroupB = Groups.Upsert(new UserGroup { GroupName = "群組B", Role = UserRole.User, Active = true }).GroupId;
            Service = new UserAdminService(
                Users, Groups, hosts, new FakeHostGroupStore(), new FakeIssueCaseStore(),
                new AlwaysVisibleService(hosts), Audit, new UserCapabilityResolver(Groups, hosts),
                new UserDisplayNameService(new FakeSystemSettingsStore()), Stamp, new FakeHandlingStore(), new FakeSystemSettingsStore());
        }

        public UserDto Seed() => Service.SaveUser(new SaveUserRequest { Account = "DOMAIN\\u1", DisplayName = "原名", Email = "a@x.com" });
    }

    private static SaveUserRequest UserRequest(string? version, string name = "原名", List<long>? groups = null, bool active = true) =>
        new() { Account = "DOMAIN\\u1", DisplayName = name, Email = "a@x.com", Active = active, Version = version, GroupIds = groups };

    [Fact]
    public void 使用者_兩個以同一版本出發的儲存_第二個衝突_資料為第一個的內容_只有一筆更新稽核()
    {
        var rig = new UserRig();
        var seeded = rig.Seed();
        rig.Audit.Entries.Clear();

        rig.Service.SaveUser(UserRequest(seeded.Version, name: "A 改的名稱"));
        Assert.Throws<ConcurrentUpdateException>(() =>
            rig.Service.SaveUser(UserRequest(seeded.Version, groups: new List<long> { rig.GroupA })));

        var user = rig.Users.FindByAccount("DOMAIN\\u1")!;
        Assert.Equal("A 改的名稱", user.DisplayName);
        Assert.Empty(user.GroupIds);
        Assert.Single(rig.Audit.Entries, e => e.Action == AuditActions.UserUpdate);
    }

    [Fact]
    public void 使用者_版本相同_基本資料與群組一次儲存_各記稽核且回應帶新版本()
    {
        var rig = new UserRig();
        var seeded = rig.Seed();
        rig.Audit.Entries.Clear();
        var stampBefore = rig.Stamp.Current;

        var dto = rig.Service.SaveUser(UserRequest(seeded.Version, name: "新名稱", groups: new List<long> { rig.GroupA, rig.GroupB }));

        var user = rig.Users.FindByAccount("DOMAIN\\u1")!;
        Assert.Equal("新名稱", user.DisplayName);
        Assert.Equal(new[] { rig.GroupA, rig.GroupB }, user.GroupIds.OrderBy(i => i));
        Assert.NotEqual(seeded.Version, dto.Version);
        Assert.Equal(2, rig.Audit.Entries.Count(e => e.Action == AuditActions.UserUpdate));
        Assert.Equal(stampBefore + 1, rig.Stamp.Current);
        rig.Service.SaveUser(UserRequest(dto.Version, name: "連續編輯", groups: new List<long> { rig.GroupA }));
        Assert.Equal("連續編輯", rig.Users.FindByAccount("DOMAIN\\u1")!.DisplayName);
    }

    [Fact]
    public void 使用者_只改一部分時_未改的部分沿用伺服器當下的值而不是開啟時的快照()
    {
        var rig = new UserRig();
        var seeded = rig.Seed();
        var userId = rig.Users.FindByAccount("DOMAIN\\u1")!.UserId;

        // 只改基本資料（不帶 GroupIds）：群組沿用伺服器當下的值，不會被清掉
        rig.Service.SetUserGroups(userId, new[] { rig.GroupA });
        var latest = rig.Service.GetUsers().Single();
        rig.Service.SaveUser(UserRequest(latest.Version, name: "又改名"));
        Assert.Equal(new[] { rig.GroupA }, rig.Users.FindByAccount("DOMAIN\\u1")!.GroupIds);

        // 只改群組（顯示名稱照伺服器當下值送出）：名稱不會被改回舊值
        var afterRename = rig.Service.GetUsers().Single();
        Assert.Equal("又改名", afterRename.DisplayName);
        rig.Service.SaveUser(UserRequest(afterRename.Version, name: afterRename.DisplayName, groups: new List<long> { rig.GroupB }));
        var current = rig.Users.FindByAccount("DOMAIN\\u1")!;
        Assert.Equal("又改名", current.DisplayName);
        Assert.Equal(new[] { rig.GroupB }, current.GroupIds);

        // 以開啟時的舊版本只改群組：整筆拒絕，名稱仍是伺服器上的新值
        Assert.Throws<ConcurrentUpdateException>(() =>
            rig.Service.SaveUser(UserRequest(seeded.Version, name: seeded.DisplayName, groups: new List<long> { rig.GroupA })));
        Assert.Equal("又改名", rig.Users.FindByAccount("DOMAIN\\u1")!.DisplayName);
    }

    [Fact]
    public void 使用者_群組變動後舊版本的儲存整筆被拒_名稱與群組都不寫()
    {
        var rig = new UserRig();
        var seeded = rig.Seed();
        rig.Service.SetUserGroups(rig.Users.FindByAccount("DOMAIN\\u1")!.UserId, new[] { rig.GroupB });

        Assert.Throws<ConcurrentUpdateException>(() =>
            rig.Service.SaveUser(UserRequest(seeded.Version, name: "過期名稱", groups: new List<long> { rig.GroupA })));

        var user = rig.Users.FindByAccount("DOMAIN\\u1")!;
        Assert.Equal("原名", user.DisplayName);
        Assert.Equal(new[] { rig.GroupB }, user.GroupIds);
    }

    [Fact]
    public void 使用者_編輯既有帳號沒帶版本_視為衝突_新增不需版本()
    {
        var rig = new UserRig();
        rig.Seed();   // 新增沒有版本
        rig.Audit.Entries.Clear();

        Assert.Throws<ConcurrentUpdateException>(() => rig.Service.SaveUser(UserRequest(null, name: "沒帶版本")));
        Assert.Throws<ConcurrentUpdateException>(() => rig.Service.SaveUser(UserRequest("", name: "空版本")));

        Assert.Equal("原名", rig.Users.FindByAccount("DOMAIN\\u1")!.DisplayName);
        Assert.Empty(rig.Audit.Entries);
    }

    [Fact]
    public void 使用者_未知群組整筆拒絕_不寫入()
    {
        var rig = new UserRig();
        var seeded = rig.Seed();

        Assert.Throws<DomainException>(() =>
            rig.Service.SaveUser(UserRequest(seeded.Version, name: "不會存", groups: new List<long> { 9999 })));

        Assert.Equal("原名", rig.Users.FindByAccount("DOMAIN\\u1")!.DisplayName);
    }

    [Fact]
    public void 使用者_兩個執行緒以同一版本同時儲存_恰好一個成功_真實UserStore()
    {
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        var rig = new UserRig(new UserStore(backend.Blob("users"), backend.Blob("user_last_login")));
        var version = rig.Seed().Version;

        for (var round = 0; round < 5; round++)
        {
            version = rig.Service.GetUsers().Single().Version;
            using var gate = new Barrier(2);
            var outcomes = new string?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                gate.SignalAndWait();
                try
                {
                    rig.Service.SaveUser(UserRequest(version, name: $"執行緒{i}-回合{round}",
                        groups: new List<long> { i == 0 ? rig.GroupA : rig.GroupB }));
                    outcomes[i] = "ok";
                }
                catch (ConcurrentUpdateException) { outcomes[i] = "conflict"; }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, outcomes.Count(o => o == "ok"));
            Assert.Equal(1, outcomes.Count(o => o == "conflict"));
            // 名稱與群組必須來自同一個成功的請求，不會一半一半
            var user = rig.Users.FindByAccount("DOMAIN\\u1")!;
            var winner = user.DisplayName.StartsWith("執行緒0") ? rig.GroupA : rig.GroupB;
            Assert.Equal(new[] { winner }, user.GroupIds);
        }
    }

    [Fact]
    public void 版本衝突回應409且訊息是固定說明()
    {
        var ex = ConcurrentUpdateException.StaleVersion();

        var (status, code, message) = ApiExceptionMapping.Map(ex, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(ApiErrorCodes.Conflict, code);
        Assert.Equal("這筆資料剛被其他人修改，請重新載入後再改。", message);
        Assert.Equal(message, UserFacingMessage.For(ex, "備用說明"));
    }
}

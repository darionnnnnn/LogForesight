using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Extensions;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

public class UserDisplayNameServiceTests
{
    private readonly FakeSystemSettingsStore _settingsStore = new();

    // 1. 規則 ^([^ ]+).* => $1 之下，Of("鄭孟瑋 Wayne2021 (Yuanta)") 回傳 鄭孟瑋
    [Fact]
    public void Of_套用正則規則_回傳符合群組的第一段名稱()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");
        var service = new UserDisplayNameService(_settingsStore);

        var result = service.Of("鄭孟瑋 Wayne2021 (Yuanta)");

        Assert.Equal("鄭孟瑋", result);
    }

    // 2. 規則為空字串時，Of 原樣回傳
    [Fact]
    public void Of_規則為空字串_原樣回傳()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "");
        var service = new UserDisplayNameService(_settingsStore);

        var result = service.Of("鄭孟瑋 Wayne2021 (Yuanta)");

        Assert.Equal("鄭孟瑋 Wayne2021 (Yuanta)", result);
    }

    // 3. WithAccount("鄭孟瑋 Wayne2021 (Yuanta)", "181035") 在上述規則下回傳 鄭孟瑋(181035)
    [Fact]
    public void WithAccount_套用規則_回傳顯示名稱串接半形括號帳號()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");
        var service = new UserDisplayNameService(_settingsStore);

        var result = service.WithAccount("鄭孟瑋 Wayne2021 (Yuanta)", "181035");

        Assert.Equal("鄭孟瑋(181035)", result);
    }

    // 4. WithAccount 在 displayName 為空時只回傳帳號
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithAccount_顯示名稱為空_只回傳帳號(string? displayName)
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");
        var service = new UserDisplayNameService(_settingsStore);

        var result = service.WithAccount(displayName, "181035");

        Assert.Equal("181035", result);
    }

    // 補充：記憶化效能驗證（設定字串沒變時沿用上次解析結果，變更時重新解析）
    [Fact]
    public void CurrentRules_設定內容未變_沿用快取同一個RuleSet實例()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");
        var service = new UserDisplayNameService(_settingsStore);

        var rules1 = service.CurrentRules();
        var rules2 = service.CurrentRules();

        Assert.Same(rules1, rules2);

        // 更新規則後應重新解析
        _settingsStore.Update(s => s.AccountDisplayRules = "^(.+) => $1");
        var rules3 = service.CurrentRules();
        Assert.NotSame(rules1, rules3);
    }

    // 5. /api/auth/me 回應的 DisplayName 已套用規則（經 DI 解析 controller）
    [Fact]
    public void AuthController_Me_經由DI容器解析_回應的DisplayName已套用規則()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var settings = new WebAppSettings();
        settings.Jwt.SecretKey = new string('k', 64);

        var settingsStore = new FakeSystemSettingsStore();
        settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");

        // 依系統慣例註冊後端與認證服務，並覆寫測試替身
        services.AddSingleton(settings);
        services.AddStorage(settings);
        services.AddSingleton<ISystemSettingsStore>(settingsStore);
        services.AddSingleton<IUserStore>(new FakeUserStore());
        services.AddSingleton<IUserGroupStore>(new FakeUserGroupStore());
        services.AddSingleton<IHostStore>(new FakeHostStore());
        services.AddSingleton<IAuditService>(new RecordingAuditService());

        services.AddLogForesightAuth(settings);
        services.AddLogForesightServices();

        var currentUser = new CustomDisplayNameCurrentUser(
            userId: 181035,
            account: "181035",
            displayName: "鄭孟瑋 Wayne2021 (Yuanta)");
        services.AddScoped<ICurrentUser>(_ => currentUser);

        // 註冊 Controller 走 DI 解析（不可直接 new AuthController）
        services.AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddControllersAsServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var controller = scope.ServiceProvider.GetRequiredService<AuthController>();
        var response = controller.Me();

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("181035", response.Data.Account);
        Assert.Equal("鄭孟瑋", response.Data.DisplayName);
    }

    // 6. UserAdminService 產出的 UserDto.DisplayName 已套用規則，且建立使用者後從 store 讀回的 DisplayName 仍是原值
    [Fact]
    public void UserAdminService_ToDto套用規則且寫入Store保留原值()
    {
        var settingsStore = new FakeSystemSettingsStore();
        settingsStore.Update(s => s.AccountDisplayRules = "^([^ ]+).* => $1");

        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var hosts = new FakeHostStore();
        var hostGroups = new FakeHostGroupStore();
        var access = new FakeGroupAccessStore();
        var cases = new FakeIssueCaseStore();
        var audit = new RecordingAuditService();

        var userDisplayNames = new UserDisplayNameService(settingsStore);
        var visibility = new VisibilityService(
            FakeCurrentUser.WithCapabilities(Capability.Maintain),
            users, groups, access, hosts, cases, settingsStore);
        var capabilities = new UserCapabilityResolver(groups, hosts);

        var service = new UserAdminService(
            users, groups, hosts, hostGroups, cases, visibility, audit, capabilities, userDisplayNames);

        // 1. 建立使用者（寫入端）
        var originalDisplayName = "鄭孟瑋 Wayne2021 (Yuanta)";
        var createdDto = service.SaveUser(new SaveUserRequest
        {
            Account = "181035",
            DisplayName = originalDisplayName,
            Active = true
        });

        // 驗證 1: SaveUser 回傳的 UserDto 已套用規則
        Assert.Equal("鄭孟瑋", createdDto.DisplayName);

        // 驗證 2: 從 Store 直接讀回的原始資料未受規則污染，仍為原值
        var storedUser = users.FindByAccount("181035");
        Assert.NotNull(storedUser);
        Assert.Equal(originalDisplayName, storedUser!.DisplayName);

        // 驗證 3: 經由 GetUsers() 讀出清單時，產出的 UserDto 亦套用規則
        var list = service.GetUsers();
        var userInList = list.Single(u => u.Account == "181035");
        Assert.Equal("鄭孟瑋", userInList.DisplayName);
    }

    private sealed class CustomDisplayNameCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long UserId { get; }
        public string Account { get; }
        public string DisplayName { get; }
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Maintain };
        public bool IsServerAdmin => false;

        public CustomDisplayNameCurrentUser(long userId, string account, string displayName)
        {
            UserId = userId;
            Account = account;
            DisplayName = displayName;
        }

        public bool Has(Capability capability) => Capabilities.Contains(capability);
    }

    // ── 置頂契約測試 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 置頂契約測試（FEEDBACK-10 §1.1）：
    /// 此測試守護前端以 OwnerNames 為鍵對照使用者清單的置頂契約。
    /// 前端日處理面板指派下拉選單（searchableUserSelect）依賴 HandlingDto.OwnerNames
    /// 與 /api/admin/users 回傳的 DisplayName 精確逐字比對來將負責人置頂。
    /// 若兩端套用的顯示名稱清洗規則不一致，置頂比對將會靜默失效。
    /// </summary>
    [Fact]
    public void 置頂契約_DayHandlingCommandService與UserAdminService之DisplayName完全相等()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var displayNameService = new UserDisplayNameService(_settingsStore);

        var users = new FakeUserStore();
        var user = users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var hosts = new FakeHostStore();
        var host = hosts.Upsert(new WebHost
        {
            HostId = 1,
            HostName = "HOST-01",
            Active = true,
            OwnerUserIds = new() { user.UserId }
        });

        // 1. 呼叫 /api/admin/users 底層 UserAdminService.GetUsers() 取得清單中該使用者的 DisplayName
        var adminService = new UserAdminService(
            users,
            new FakeUserGroupStore(),
            hosts,
            new FakeHostGroupStore(),
            new FakeIssueCaseStore(),
            new AlwaysVisibleService(hosts),
            new RecordingAuditService(),
            new UserCapabilityResolver(new FakeUserGroupStore(), hosts),
            displayNameService);
        var adminUser = adminService.GetUsers().Single(u => u.Account == "181035");

        // 2. 呼叫 DayHandlingCommandService.Get(...) 取得 HandlingDto.OwnerNames
        var dayService = CreateDayHandlingCommandService(hosts, users, _settingsStore, displayNameService);
        var handlingDto = dayService.Get(host.HostId, DateTime.Today);

        // 3. 斷言兩者完全相等且皆為清洗後的「鄭孟瑋」
        var ownerName = Assert.Single(handlingDto.OwnerNames);
        Assert.Equal("鄭孟瑋", adminUser.DisplayName);
        Assert.Equal("鄭孟瑋", ownerName);
        Assert.Equal(adminUser.DisplayName, ownerName);
    }

    // ── OfAccount 測試 ────────────────────────────────────────────────────────

    [Fact]
    public void OfAccount_有規則且使用者存在_回傳清洗後顯示名稱與半形括號帳號()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var service = new UserDisplayNameService(_settingsStore);
        var users = new FakeUserStore();
        users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var result = service.OfAccount(users, "181035");
        Assert.Equal("鄭孟瑋(181035)", result);
    }

    [Theory]
    [InlineData("DOMAIN\\unknown")]
    [InlineData("")]
    public void OfAccount_使用者不存在或帳號為空_回傳帳號本身(string account)
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var service = new UserDisplayNameService(_settingsStore);
        var users = new FakeUserStore();

        var result = service.OfAccount(users, account);
        Assert.Equal(account, result);
    }

    // ── 呼叫端修改測試 ────────────────────────────────────────────────────────

    [Fact]
    public void 呼叫端修改測試_IssueHandlingCommandService_他人案件衝突提示套用名稱清洗規則()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var displayNameService = new UserDisplayNameService(_settingsStore);

        var users = new FakeUserStore();
        var handler = users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var hosts = new FakeHostStore();
        var host = hosts.Upsert(new WebHost { HostId = 1, HostName = "HOST-01", Active = true });

        var issue = new LogIssueSignature
        {
            Source = "disk",
            EventId = 153,
            LogName = "System",
            EntryType = System.Diagnostics.EventLogEntryType.Error
        };
        var issueKey = IssueSignatureKey.For(issue);

        var caseStore = new FakeIssueCaseStore();
        caseStore.Save(new IssueCase
        {
            CaseId = "case-01",
            HostName = host.HostName,
            IssueKey = issueKey,
            HandlerId = handler.UserId,
            Status = "open"
        });

        var repository = new FakeRecordRepository(hosts);
        repository.AddRecord(host.HostName, DateTime.Today, issue);

        var currentUser = FakeCurrentUser.WithCapabilities(Capability.Handle); // UserId != handler.UserId
        var service = CreateIssueHandlingCommandService(hosts, users, caseStore, repository, _settingsStore, displayNameService, currentUser);

        var ex = Assert.Throws<DomainException>(() =>
            service.SetIssueStatus(host.HostId, DateTime.Today, new SetIssueStatusRequest
            {
                Status = IssueHandlingStatuses.InProgress,
                IssueKey = issueKey
            }));

        // 驗證他人案件衝突提示套用了規則（鄭孟瑋(181035) 而非 原始未清洗字串）
        Assert.Contains("此問題由 鄭孟瑋(181035) 的案件處理中", ex.Message);
        Assert.DoesNotContain("Wayne2021", ex.Message);
    }

    [Fact]
    public void 呼叫端修改測試_RunMonitorService_手動觸發之TriggerText套用名稱清洗規則()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var displayNameService = new UserDisplayNameService(_settingsStore);

        using var fx = new EfSqliteFixture();
        var runs = new BatchRunStore(fx.LogStore("runs"), fx.LogStore("run_logs"));
        var records = new EfAnalysisRecordStore(fx.NewContext, "test");
        var options = new ScheduleOptionsStore(fx.Blob("schedule_options"));

        var users = new FakeUserStore();
        users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var runId = runs.StartRun(new BatchRun
        {
            HostName = "SRV-LOCAL",
            StartedAt = DateTime.Now,
            Trigger = "manual:181035"
        });
        runs.FinishRun(new BatchRun
        {
            RunId = runId,
            HostName = "SRV-LOCAL",
            StartedAt = DateTime.Now,
            FinishedAt = DateTime.Now,
            ExitCode = 0,
            Trigger = "manual:181035"
        });

        var service = new RunMonitorService(runs, hosts, records, users, options, displayNameService);
        var detail = service.GetDetail(runId);

        // 驗證 TriggerText 套用規則為「手動（鄭孟瑋(181035)）」
        Assert.Equal("手動（鄭孟瑋(181035)）", detail.TriggerText);
    }

    [Fact]
    public void 呼叫端修改測試_HandlingHistoryQueryService_Workload之DisplayName套用名稱清洗規則()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var displayNameService = new UserDisplayNameService(_settingsStore);

        var users = new FakeUserStore();
        var user = users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var hosts = new FakeHostStore();
        var service = new HandlingHistoryQueryService(
            new FakeHandlingStore(),
            new FakeIssueHandlingStore(),
            new FakeIssueCaseStore(),
            hosts,
            users,
            new AlwaysVisibleService(hosts),
            _settingsStore,
            new FakeRecordRepository(hosts),
            new HandlingProgressCalculator(new FakeIssueHandlingStore(), new FakeHandlingStore(), new FakeIssueCaseStore(), _settingsStore),
            new FakeIssueAggregateQuery(),
            displayNameService);

        var workload = service.GetHandlerWorkload(user.UserId, false);

        // 驗證 HandlerWorkloadDto 的 DisplayName 套用規則
        Assert.Equal("鄭孟瑋", workload.DisplayName);
    }

    // ── OwnerNames 刪除保留測試 ──────────────────────────────────────────────

    [Fact]
    public void OwnerNames_主機負責人有已刪除使用者_保留已刪除文字未套用規則且不拋例外()
    {
        _settingsStore.Update(s => s.AccountDisplayRules = "(?i)\\s*[a-z0-9]+\\s*\\(yuanta\\) =>");
        var displayNameService = new UserDisplayNameService(_settingsStore);

        var users = new FakeUserStore();
        var validUser = users.Upsert(new WebUser
        {
            Account = "181035",
            DisplayName = "鄭孟瑋 Wayne2021 (Yuanta)",
            Active = true
        });

        var hosts = new FakeHostStore();
        var host = hosts.Upsert(new WebHost
        {
            HostId = 1,
            HostName = "HOST-01",
            Active = true,
            OwnerUserIds = new() { validUser.UserId, 999999L } // 999999L 為不存在/已刪除使用者
        });

        var dayService = CreateDayHandlingCommandService(hosts, users, _settingsStore, displayNameService);
        var handlingDto = dayService.Get(host.HostId, DateTime.Today);

        // 驗證存在使用者套用規則，不存在使用者輸出「（已刪除）」，不拋出例外，且「（已刪除）」字樣未被正則破壞
        Assert.Equal(2, handlingDto.OwnerNames.Count);
        Assert.Equal("鄭孟瑋", handlingDto.OwnerNames[0]);
        Assert.Equal("（已刪除）", handlingDto.OwnerNames[1]);
    }

    private static DayHandlingCommandService CreateDayHandlingCommandService(
        FakeHostStore hosts,
        FakeUserStore users,
        ISystemSettingsStore settingsStore,
        IUserDisplayNameService displayNameService)
    {
        var issueStore = new FakeIssueHandlingStore();
        var recordStore = new FakeHandlingStore();
        var caseStore = new FakeIssueCaseStore();
        var repository = new FakeRecordRepository(hosts);
        var coordinator = new IssueCaseCoordinator(caseStore, issueStore, recordStore, repository, hosts, new FakeIssueOwnerStore());
        var progress = new HandlingProgressCalculator(issueStore, recordStore, caseStore, settingsStore);
        var capabilities = new UserCapabilityResolver(new FakeUserGroupStore(), hosts);
        return new DayHandlingCommandService(
            recordStore,
            issueStore,
            coordinator,
            repository,
            hosts,
            users,
            new AlwaysVisibleService(hosts),
            FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            new RecordingAuditService(),
            settingsStore,
            progress,
            capabilities,
            displayNameService);
    }

    private static IssueHandlingCommandService CreateIssueHandlingCommandService(
        FakeHostStore hosts,
        FakeUserStore users,
        FakeIssueCaseStore caseStore,
        FakeRecordRepository repository,
        ISystemSettingsStore settingsStore,
        IUserDisplayNameService displayNameService,
        ICurrentUser currentUser)
    {
        var issueStore = new FakeIssueHandlingStore();
        var recordStore = new FakeHandlingStore();
        var coordinator = new IssueCaseCoordinator(caseStore, issueStore, recordStore, repository, hosts, new FakeIssueOwnerStore());
        var progress = new HandlingProgressCalculator(issueStore, recordStore, caseStore, settingsStore);
        var capabilities = new UserCapabilityResolver(new FakeUserGroupStore(), hosts);
        var issueOwnerAdmin = new IssueOwnerAdminService(
            new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(), users, new RecordingAuditService(), currentUser, displayNameService);
        return new IssueHandlingCommandService(
            recordStore,
            issueStore,
            caseStore,
            coordinator,
            new FakeNoiseMarkStore(),
            repository,
            hosts,
            users,
            new AlwaysVisibleService(hosts),
            currentUser,
            new RecordingAuditService(),
            progress,
            capabilities,
            issueOwnerAdmin,
            displayNameService);
    }
}

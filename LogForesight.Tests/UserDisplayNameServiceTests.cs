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
}

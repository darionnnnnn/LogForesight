using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 初始設定精靈十步與升級相容驗收測試（回饋五十輪批次F-1a）。
/// </summary>
public class SetupWizardV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-setup-v2-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly ScheduleOptionsStore _scheduleOptions;
    private readonly SetupWizardStateStore _stateStore;
    private readonly PrtgStructureSyncStatusStore _prtgSyncStore;
    private readonly FakeAiProbeService _aiProbe = new();

    public SetupWizardV2Tests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "s.db")}" }, _dir);
        _scheduleOptions = new ScheduleOptionsStore(_backend.Blob("schedule_options"));
        _stateStore = new SetupWizardStateStore(_backend.Blob("setup_wizard_state"));
        _prtgSyncStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SetupReadinessService CreateService(string authProvider = "Stub")
    {
        var appSettings = new WebAppSettings
        {
            Auth = new AuthSettings
            {
                Provider = authProvider,
                ServerAdmin = new ServerAdminSettings()
            }
        };
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            _scheduleOptions);
        var health = new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), NewMailService(), freshness);
        var identity = new IdentityService(
            _users, _groups, _hosts, new StubAuthenticationProvider(),
            new ServerAdminAuthenticator(appSettings),
            new RecordingAuditService(), new UserCapabilityResolver(_groups, _hosts));

        return new SetupReadinessService(
            health, identity, _settings, _sentinels, _hosts, _groupAccess, _groups,
            _scheduleOptions, _stateStore, appSettings, _prtgSyncStore, _aiProbe);
    }

    private MailNotificationService NewMailService()
    {
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            _scheduleOptions);
        return new(
            _settings, new FakeSmtpMailSender(), _hosts, _users,
            _groups, _groupAccess, new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness);
    }

    [Fact]
    public void 十步順序_正確依序排列()
    {
        var service = CreateService();
        var status = service.GetStatus();

        var expectedIds = new[]
        {
            "storage", "ad", "admin-account", "dept-groups", "netiq",
            "groups", "prtg", "mail", "ai", "schedule"
        };

        Assert.Equal(expectedIds, status.Steps.Select(s => s.Id).ToArray());

        // 驗證目標連結與特定標題
        Assert.Null(status.Steps.Single(s => s.Id == "storage").TargetUrl);
        Assert.Equal("/admin/settings#ad", status.Steps.Single(s => s.Id == "ad").TargetUrl);
        Assert.Equal("/admin/users", status.Steps.Single(s => s.Id == "admin-account").TargetUrl);
        Assert.Equal("/admin/groups", status.Steps.Single(s => s.Id == "dept-groups").TargetUrl);
        Assert.Equal("/admin/netiq", status.Steps.Single(s => s.Id == "netiq").TargetUrl);
        Assert.Equal("主機群組與授權", status.Steps.Single(s => s.Id == "groups").Title);
        Assert.Equal("/admin/groups", status.Steps.Single(s => s.Id == "groups").TargetUrl);
        Assert.Equal("/admin/prtg", status.Steps.Single(s => s.Id == "prtg").TargetUrl);
        Assert.Equal("/admin/settings#mail", status.Steps.Single(s => s.Id == "mail").TargetUrl);
        Assert.Equal("/admin/settings#ai", status.Steps.Single(s => s.Id == "ai").TargetUrl);
        Assert.Equal("/runs#settings", status.Steps.Single(s => s.Id == "schedule").TargetUrl);
    }

    [Fact]
    public void Stub可跳AD_Ldap不可跳()
    {
        // Provider = Stub 時可跳過
        var stubService = CreateService(authProvider: "Stub");
        var stubStatus = stubService.GetStatus();
        var stubAd = stubStatus.Steps.Single(s => s.Id == "ad");
        Assert.True(stubAd.CanSkip);

        stubService.SetSkipped("ad", true);
        Assert.True(stubService.GetStatus().Steps.Single(s => s.Id == "ad").Skipped);

        // Provider = Ldap (或 Ad) 時不可跳過
        var ldapService = CreateService(authProvider: "Ldap");
        var ldapStatus = ldapService.GetStatus();
        var ldapAd = ldapStatus.Steps.Single(s => s.Id == "ad");
        Assert.False(ldapAd.CanSkip);

        _stateStore.Update(s => s.SkippedSteps.Clear());
        ldapService.SetSkipped("ad", true);
        Assert.False(ldapService.GetStatus().Steps.Single(s => s.Id == "ad").Skipped);
    }

    [Fact]
    public void 未知id不寫_呼叫SetSkipped不落地()
    {
        var service = CreateService();
        service.SetSkipped("unknown-id-xyz", true);

        var state = _stateStore.Get();
        Assert.DoesNotContain("unknown-id-xyz", state.SkippedSteps);
    }

    [Fact]
    public void 新步id可跳_dept_groups與prtg()
    {
        var service = CreateService();

        service.SetSkipped("dept-groups", true);
        service.SetSkipped("prtg", true);

        var status = service.GetStatus();
        Assert.True(status.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.True(status.Steps.Single(s => s.Id == "prtg").Skipped);
    }

    [Fact]
    public void 升級Hidden一次性遷移_未完成且可跳過的新步自動加入Skipped()
    {
        // 模擬舊版狀態：Hidden = true, StepsVersion = 0
        _stateStore.Update(s =>
        {
            s.Hidden = true;
            s.StepsVersion = 0;
            s.SkippedSteps.Clear();
        });

        // 1. Stub 環境：ad, dept-groups, prtg 皆可跳過
        var serviceStub = CreateService(authProvider: "Stub");
        var statusStub = serviceStub.GetStatus();

        Assert.Equal(2, _stateStore.Get().StepsVersion);
        Assert.True(statusStub.Steps.Single(s => s.Id == "ad").Skipped);
        Assert.True(statusStub.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.True(statusStub.Steps.Single(s => s.Id == "prtg").Skipped);

        // 2. Ad 環境：ad 不可跳過，不能偽裝完成
        _stateStore.Update(s =>
        {
            s.Hidden = true;
            s.StepsVersion = 0;
            s.SkippedSteps.Clear();
        });

        var serviceAd = CreateService(authProvider: "Ad");
        var statusAd = serviceAd.GetStatus();

        Assert.Equal(2, _stateStore.Get().StepsVersion);
        Assert.False(statusAd.Steps.Single(s => s.Id == "ad").Skipped);
        Assert.False(statusAd.Steps.Single(s => s.Id == "ad").Done);
        Assert.True(statusAd.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.True(statusAd.Steps.Single(s => s.Id == "prtg").Skipped);
    }

    [Fact]
    public void 升級舊七步已完成_排程只有啟用沒有窗口_仍視為舊版已就緒並跳過新增步驟()
    {
        _stateStore.Update(s =>
        {
            s.Hidden = false;
            s.StepsVersion = 0;
            s.SkippedSteps.Clear();
            foreach (var id in new[] { "mail", "ai", "netiq", "groups" })
                s.SkippedSteps.Add(id);
        });
        _groups.Upsert(new UserGroup { GroupName = "Admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser
        {
            Account = "admin",
            DisplayName = "Admin",
            Active = true,
            GroupIds = new List<long> { _groups.GetAll().Single().GroupId }
        });
        _scheduleOptions.Update(o =>
        {
            o.Enabled = true;
            o.Windows = new List<ScheduleWindow>();
        });

        var status = CreateService(authProvider: "Stub").GetStatus();

        Assert.True(status.Steps.Single(s => s.Id == "ad").Skipped);
        Assert.True(status.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.True(status.Steps.Single(s => s.Id == "prtg").Skipped);
        Assert.False(status.Steps.Single(s => s.Id == "schedule").Done);
    }

    [Fact]
    public void 取消跳過後不復原_遷移不重複執行()
    {
        // 初始升級遷移
        _stateStore.Update(s =>
        {
            s.Hidden = true;
            s.StepsVersion = 0;
            s.SkippedSteps.Clear();
        });

        var service = CreateService(authProvider: "Stub");
        var status1 = service.GetStatus();
        Assert.True(status1.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.Equal(2, _stateStore.Get().StepsVersion);

        // 使用者手動取消跳過
        service.SetSkipped("dept-groups", false);
        var status2 = service.GetStatus();
        Assert.False(status2.Steps.Single(s => s.Id == "dept-groups").Skipped);

        // 再次讀取狀態，不會因為 Hidden=true 而重新將其加入 SkippedSteps
        var status3 = service.GetStatus();
        Assert.False(status3.Steps.Single(s => s.Id == "dept-groups").Skipped);
    }

    [Fact]
    public void 排程無窗口不完成_有窗口才算完成()
    {
        var service = CreateService();

        // 啟用但無窗口
        _scheduleOptions.Update(o =>
        {
            o.Enabled = true;
            o.Windows = new List<ScheduleWindow>();
        });

        var statusNoWindow = service.GetStatus();
        Assert.False(statusNoWindow.Steps.Single(s => s.Id == "schedule").Done);

        // 啟用且有窗口
        _scheduleOptions.Update(o =>
        {
            o.Enabled = true;
            o.Windows = new List<ScheduleWindow> { new ScheduleWindow { Start = "02:00", End = "06:00" } };
        });

        var statusWithWindow = service.GetStatus();
        Assert.True(statusWithWindow.Steps.Single(s => s.Id == "schedule").Done);
    }

    [Fact]
    public void 新安裝未完成不跳過_新步驟保持待設定()
    {
        // 全新安裝：Hidden = false, StepsVersion = 0, 舊步驟皆未完成
        _stateStore.Update(s =>
        {
            s.Hidden = false;
            s.StepsVersion = 0;
            s.SkippedSteps.Clear();
        });

        var service = CreateService(authProvider: "Stub");
        var status = service.GetStatus();

        Assert.Equal(2, _stateStore.Get().StepsVersion);
        Assert.False(status.Steps.Single(s => s.Id == "ad").Skipped);
        Assert.False(status.Steps.Single(s => s.Id == "dept-groups").Skipped);
        Assert.False(status.Steps.Single(s => s.Id == "prtg").Skipped);
    }

    [Fact]
    public void AD完成條件_需啟用旗標且伺服器非空()
    {
        var service = CreateService();

        // 1. 未啟用
        _settings.Update(s => { s.AdAuthEnabled = false; s.AdServers = new List<string> { "ldap://ad.example.com" }; });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "ad").Done);

        // 2. 已啟用但伺服器清單為空
        _settings.Update(s => { s.AdAuthEnabled = true; s.AdServers = new List<string>(); });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "ad").Done);

        // 3. 已啟用但伺服器皆空白字串
        _settings.Update(s => { s.AdAuthEnabled = true; s.AdServers = new List<string> { "  ", "" }; });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "ad").Done);

        // 4. 已啟用且伺服器非空
        _settings.Update(s => { s.AdAuthEnabled = true; s.AdServers = new List<string> { "ldap://ad.example.com" }; });
        Assert.True(service.GetStatus().Steps.Single(s => s.Id == "ad").Done);
    }

    [Fact]
    public void DeptGroups完成條件_需至少一個啟用User群組()
    {
        var service = CreateService();

        // 1. 無任何群組
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "dept-groups").Done);

        // 2. 只有 Admin 群組
        _groups.Upsert(new UserGroup { GroupName = "Admins", Role = UserRole.Admin, Active = true });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "dept-groups").Done);

        // 3. 有 User 群組但未啟用
        _groups.Upsert(new UserGroup { GroupName = "InactiveDept", Role = UserRole.User, Active = false });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "dept-groups").Done);

        // 4. 有啟用中 User 群組
        _groups.Upsert(new UserGroup { GroupName = "IT", Role = UserRole.User, Active = true });
        Assert.True(service.GetStatus().Steps.Single(s => s.Id == "dept-groups").Done);
    }

    [Fact]
    public void PRTG完成條件_需已啟用且最後同步成功且最近HostMap至少一筆Ok()
    {
        var service = CreateService();

        // 1. 未啟用
        _settings.Update(s => s.PrtgEnabled = false);
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 2. 啟用但無同步紀錄
        _settings.Update(s => s.PrtgEnabled = true);
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 3. 啟用且有同步紀錄但失敗
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = false;
            s.MapOk = 10;
        });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 4. 啟用且同步成功但 MapOk 為 0
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.MapOk = 0;
        });
        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 5. 啟用且同步成功且 MapOk >= 1
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.MapOk = 3;
        });
        Assert.True(service.GetStatus().Steps.Single(s => s.Id == "prtg").Done);
    }
}

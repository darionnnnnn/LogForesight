using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 首次啟動精靈的就緒度判定（回饋十八輪批次H）：七步混合制——done 全部自動判定，
/// skipped 由使用者手動決定。用真實 StorageBackend（同 HealthServiceTests 的既有手法），
/// 其餘依賴用最小可用替身。
/// </summary>
public class SetupReadinessServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-setup-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeGroupAccessStore _groupAccess = new();

    public SetupReadinessServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "s.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private SetupReadinessService Create()
    {
        var health = new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), NewMailService());
        var identity = new IdentityService(
            _users, _groups, _hosts, new StubAuthenticationProvider(),
            new ServerAdminAuthenticator(new WebAppSettings { Auth = new AuthSettings { ServerAdmin = new ServerAdminSettings() } }),
            new RecordingAuditService(), new UserCapabilityResolver(_groups, _hosts));
        return new SetupReadinessService(
            health, identity, _settings, _sentinels, _hosts, _groupAccess,
            new ScheduleOptionsStore(_backend.Blob("schedule_options")),
            new SetupWizardStateStore(_backend.Blob("setup_wizard_state")));
    }

    private MailNotificationService NewMailService() => new(
        _settings, new FakeSmtpMailSender(), _hosts, _users,
        _groups, _groupAccess, new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
        new MailNotifyStateStore(_backend.Blob("mail_notify_state")));

    private void EnsureAdmin()
    {
        var group = _groups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "admin1", Active = true, GroupIds = new List<long> { group.GroupId } });
    }

    [Fact]
    public void 全新環境_只有儲存體完成其餘皆未完成()
    {
        var status = Create().GetStatus();

        var storage = status.Steps.Single(s => s.Id == "storage");
        Assert.True(storage.Done);
        Assert.False(storage.CanSkip);

        Assert.False(status.Steps.Single(s => s.Id == "admin-account").Done);
        Assert.False(status.Steps.Single(s => s.Id == "mail").Done);
        Assert.False(status.Steps.Single(s => s.Id == "netiq").Done);
        Assert.False(status.Steps.Single(s => s.Id == "groups").Done);
        Assert.False(status.Steps.Single(s => s.Id == "schedule").Done);
        Assert.False(status.AllSettled);
    }

    /// <summary>AiBaseUrl 出廠預設非空（localhost:8080）——沒特別清空時這一步天生就是完成狀態，
    /// 與 AnalysisOrchestrator 判斷 useAi 的 settings.Ai.IsConfigured 同一套語意。</summary>
    [Fact]
    public void AI步驟_出廠預設值視為已完成()
    {
        var status = Create().GetStatus();

        Assert.True(status.Steps.Single(s => s.Id == "ai").Done);
    }

    [Fact]
    public void AI步驟_清空位址時視為未完成()
    {
        _settings.Update(s => s.AiBaseUrl = "");

        var status = Create().GetStatus();

        Assert.False(status.Steps.Single(s => s.Id == "ai").Done);
    }

    [Fact]
    public void 管理員帳號步驟_有啟用中admin時完成()
    {
        EnsureAdmin();

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "admin-account").Done);
    }

    [Fact]
    public void 郵件步驟_啟用且有SMTP伺服器時完成()
    {
        _settings.Update(s => { s.MailEnabled = true; s.SmtpServer = "smtp.local"; });

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "mail").Done);
    }

    [Fact]
    public void 郵件步驟_啟用但未設SMTP時未完成()
    {
        _settings.Update(s => s.MailEnabled = true);

        Assert.False(Create().GetStatus().Steps.Single(s => s.Id == "mail").Done);
    }

    [Fact]
    public void NetIQ步驟_有啟用Sentinel與可輪巡主機時完成()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "10.0.0.1", Source = "netiq", SentinelId = sentinel.SentinelId, IpAddress = "10.0.0.1", Active = true });

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "netiq").Done);
    }

    [Fact]
    public void NetIQ步驟_Sentinel停用時未完成()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1", Active = false });
        _hosts.Upsert(new WebHost { HostName = "10.0.0.1", Source = "netiq", SentinelId = sentinel.SentinelId, IpAddress = "10.0.0.1", Active = true });

        Assert.False(Create().GetStatus().Steps.Single(s => s.Id == "netiq").Done);
    }

    [Fact]
    public void 群組步驟_有授權矩陣時完成()
    {
        _groupAccess.ReplaceAll(new[] { new GroupAccess { UserGroupId = 1, HostGroupId = 1 } });

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "groups").Done);
    }

    [Fact]
    public void 群組步驟_有主機負責人時也算完成()
    {
        var owner = _users.Upsert(new WebUser { Account = "owner1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "H1", Active = true, OwnerUserIds = new List<long> { owner.UserId } });

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "groups").Done);
    }

    [Fact]
    public void 排程步驟_啟用時完成()
    {
        new ScheduleOptionsStore(_backend.Blob("schedule_options")).Update(o => o.Enabled = true);

        Assert.True(Create().GetStatus().Steps.Single(s => s.Id == "schedule").Done);
    }

    [Fact]
    public void 跳過步驟_列入AllSettled且可逆()
    {
        var service = Create();

        service.SetSkipped("mail", true);
        var afterSkip = service.GetStatus();
        Assert.True(afterSkip.Steps.Single(s => s.Id == "mail").Skipped);

        service.SetSkipped("mail", false);
        var afterUnskip = service.GetStatus();
        Assert.False(afterUnskip.Steps.Single(s => s.Id == "mail").Skipped);
    }

    /// <summary>不能跳過的步驟即使呼叫 SetSkipped 也不算跳過——CanSkip 由步驟定義決定，
    /// 不是使用者操作能繞過的（後端是最後防線，不能只靠前端不顯示跳過按鈕）。</summary>
    [Fact]
    public void 不能跳過的步驟即使呼叫SetSkipped仍不算跳過()
    {
        var service = Create();

        service.SetSkipped("storage", true);

        Assert.False(service.GetStatus().Steps.Single(s => s.Id == "storage").Skipped);
    }

    /// <summary>未知步驟 id 不落地（終檢輪補）：直接打 API 的任意字串不能靜默累積進
    /// SkippedSteps blob 變成垃圾。</summary>
    [Fact]
    public void 未知的步驟id呼叫SetSkipped_不落地()
    {
        var service = Create();

        service.SetSkipped("not-a-step", true);

        var state = new SetupWizardStateStore(_backend.Blob("setup_wizard_state")).Get();
        Assert.Empty(state.SkippedSteps);
    }

    [Fact]
    public void 全部完成或跳過時AllSettled為true()
    {
        EnsureAdmin();
        new ScheduleOptionsStore(_backend.Blob("schedule_options")).Update(o => o.Enabled = true);
        var owner = _users.Upsert(new WebUser { Account = "owner1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "H1", Active = true, OwnerUserIds = new List<long> { owner.UserId } });

        var service = Create();
        service.SetSkipped("mail", true);
        service.SetSkipped("netiq", true);

        Assert.True(service.GetStatus().AllSettled);
    }

    [Fact]
    public void Hidden_預設false且可設定()
    {
        var service = Create();
        Assert.False(service.GetStatus().Hidden);

        service.SetHidden(true);

        Assert.True(service.GetStatus().Hidden);
    }
}

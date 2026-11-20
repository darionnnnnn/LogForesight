using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public class SecurityHardeningTests
{
    private const string KnownDevPasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M=";

    // ── 1. 環境欄杆 ──────────────────────────────────────────────────────

    /// <summary>兩種出廠值各自單獨出現；其餘欄位皆合格</summary>
    public static IEnumerable<object[]> FactoryValueCases() => new[]
    {
        new object[] { "stub" },
        new object[] { "hash" }
    };

    private static WebAppSettings WithFactoryValue(string which) => new()
    {
        Jwt = new SessionSettings { ExpireHours = 8 },
        Auth = new AuthSettings
        {
            Provider = which == "stub" ? "Stub" : "Ldap",
            ServerAdmin = new ServerAdminSettings
            {
                Account = "svc-lfadmin",
                PasswordHash = which == "hash" ? KnownDevPasswordHash : PasswordHasher.Hash("Overridden-P@ssw0rd")
            }
        },
        Storage = new StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory }
    };

    [Theory]
    [MemberData(nameof(FactoryValueCases))]
    public void Staging環境_出廠值一律擋下且訊息指出環境與Development(string which)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WithFactoryValue(which).Validate(strict: true, "Staging"));

        Assert.Contains("Staging", ex.Message);
        Assert.Contains("Development", ex.Message);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", ex.Message);
    }

    [Theory]
    [MemberData(nameof(FactoryValueCases))]
    public void 非嚴格模式_出廠值放行(string which)
    {
        // 意圖：非嚴格模式下出廠值不擲例外
        var ex = Record.Exception(() => WithFactoryValue(which).Validate(strict: false, "Development"));
        Assert.Null(ex);
    }

    // ── 2. 網址機敏參數 ──────────────────────────────────────────────────

    [Fact]
    public void UrlSecrets_偵測與遮罩()
    {
        Assert.True(UrlSecrets.ContainsSecret("https://prtg/api?passhash=123&username=a"));
        Assert.True(UrlSecrets.ContainsSecret("https://x/?a=1&API_KEY=zz"));
        Assert.False(UrlSecrets.ContainsSecret("https://prtg/"));
        Assert.False(UrlSecrets.ContainsSecret("https://prtg/token/path?user=a"));
        Assert.False(UrlSecrets.ContainsSecret(null));

        var masked = UrlSecrets.Mask("https://prtg/api?passhash=123&username=a");
        Assert.DoesNotContain("123", masked);
        Assert.Equal("https://prtg/api?passhash=***&username=a", masked);
    }

    [Fact]
    public void 系統設定儲存_PRTG網址帶passhash_Validation例外()
    {
        var fx = new EfSqliteFixture();
        var store = new SystemSettingsStore(fx.Blob("system_settings"));
        var service = new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(store, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(fx.Blob("mail_state")),
                new ScheduleFreshnessService(new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs")),
                    new ScheduleOptionsStore(fx.Blob("schedule_options"))),
                new WorkOrderOutboxStore(fx.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore()),
            new FakeReportUsageQuery(), new FakeSentinelStore());

        var ex = Assert.Throws<DomainException>(() => service.UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = store.Get().Revision,
            PrtgUrl = "https://prtg.corp.local/api/table.json?username=a&PassHash=123456"
        }));

        Assert.Contains("網址不可包含帳密、密碼或金鑰參數", ex.Message);
        Assert.DoesNotContain("PassHash=123456", store.Get().PrtgUrl);
    }

    // ── 3. 權限變更：權限版本號推進（逐請求的就地更新見 CookieSessionEventsTests）────────

    private sealed class Rig
    {
        public readonly FakeUserStore Users = new();
        public readonly FakeUserGroupStore Groups = new();
        public readonly FakeHostStore Hosts = new();
        public readonly PermissionVersionStamp Stamp = TestPermissionStamps.Create();

        public WebUser AddAdmin()
        {
            var admin = Groups.Upsert(new UserGroup { GroupName = "admin", Role = UserRole.Admin, Builtin = true, Active = true });
            var user = Users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王", Active = true });
            Users.SetGroups(user.UserId, new[] { admin.GroupId });
            return Users.Get(user.UserId)!;
        }
    }

    [Fact]
    public void 以真實服務移除admin群組成員資格_權限版本加一()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();
        var service = new UserAdminService(
            rig.Users, rig.Groups, rig.Hosts, new FakeHostGroupStore(), new FakeIssueCaseStore(),
            new AlwaysVisibleService(rig.Hosts), new RecordingAuditService(),
            new UserCapabilityResolver(rig.Groups, rig.Hosts), new UserDisplayNameService(new FakeSystemSettingsStore()),
            rig.Stamp, new FakeHandlingStore(), new FakeSystemSettingsStore());
        var before = rig.Stamp.Current;

        service.SetUserGroups(user.UserId, Array.Empty<long>());

        Assert.Equal(before + 1, rig.Stamp.Current);
        Assert.Empty(rig.Users.Get(user.UserId)!.GroupIds);
    }
}

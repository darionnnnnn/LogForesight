using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理人登入落地、導覽順序、從風險日詳情與交辦單詳情導回交辦單（回饋第 50 輪批次 D-2）。
/// </summary>
public class HandlerLandingTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 落地判定 ────────────────────────────────────────────────────────────

    public static TheoryData<bool, Capability[], string> LandingPathCases() => new()
    {
        { false, new[] { Capability.Handle }, "/handlers/42" },
        { false, new[] { Capability.Handle, Capability.ConfirmPermission }, "/handlers/42" },
        { false, new[] { Capability.Handle, Capability.Maintain }, "/" },
        { false, new[] { Capability.Handle, Capability.Assign }, "/" },
        { false, new[] { Capability.Handle, Capability.ViewAll }, "/" },
        { false, new[] { Capability.ConfirmPermission }, "/" },
        { false, Array.Empty<Capability>(), "/" },
        { true, new[] { Capability.Handle }, "/" }
    };

    [Theory]
    [MemberData(nameof(LandingPathCases))]
    public void 落地判定_只有Handle且無管理能力才落到自己的工作頁(bool isServerAdmin, Capability[] capabilities, string expected)
    {
        Assert.Equal(expected, AuthController.LandingPathFor(42, isServerAdmin, capabilities.ToHashSet()));
    }

    // ── 登入 API：真實 controller＋JSON 序列化 ─────────────────────────────

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static AuthController LoginController(FakeUserStore users, FakeUserGroupStore groups)
    {
        var settings = new WebAppSettings();
        settings.Jwt.SecretKey = new string('k', 64);
        settings.Auth.ServerAdmin.Account = "serverAdmin";
        var hosts = new FakeHostStore();
        var audit = new RecordingAuditService();
        var provider = new StubAuthenticationProvider();
        var identity = new IdentityService(users, groups, hosts, provider,
            new ServerAdminAuthenticator(settings), audit, new UserCapabilityResolver(groups, hosts));
        return new AuthController(identity, new JwtTokenService(settings, TestPermissionStamps.Shared), provider,
            FakeCurrentUser.Anonymous(), audit, settings,
            new UserDisplayNameService(new FakeSystemSettingsStore()), new LoginThrottle(), new RevokedTokens())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static JsonElement LoginJson(AuthController controller, string account)
    {
        var result = controller.Login(new LoginRequest { Account = account });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        // 以宣告型別序列化（與 MVC 輸出一致），確認欄位真的出現在回應 JSON
        var json = JsonSerializer.Serialize(ok.Value, typeof(ApiResponse<CurrentUserDto>), WebJson);
        return JsonDocument.Parse(json).RootElement.GetProperty("data");
    }

    [Fact]
    public void 登入API_只有Handle的使用者_回應landingPath為自己的工作頁()
    {
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var group = groups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });   // User 角色＝Handle＋ConfirmPermission
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\handler", GroupIds = new List<long> { group.GroupId } });

        var data = LoginJson(LoginController(users, groups), "DOMAIN\\handler");

        Assert.Equal($"/handlers/{user.UserId}", data.GetProperty("landingPath").GetString());
    }

    [Fact]
    public void 登入API_管理者_回應landingPath為儀表板()
    {
        var users = new FakeUserStore();
        var groups = new FakeUserGroupStore();
        var admin = groups.Upsert(new UserGroup { GroupName = "管理", Role = UserRole.Admin });
        users.Upsert(new WebUser { Account = "DOMAIN\\admin", GroupIds = new List<long> { admin.GroupId } });

        var data = LoginJson(LoginController(users, groups), "DOMAIN\\admin");

        Assert.Equal("/", data.GetProperty("landingPath").GetString());
    }

    // ── 導覽順序 ────────────────────────────────────────────────────────────

    private static string ReadWeb(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "LogForesight.Web", "wwwroot" }.Concat(parts).ToArray()));
    }

    [Fact]
    public void 導覽第一組第一個項目是我的交辦()
    {
        var js = ReadWeb("js", "core", "layout.js");
        var firstSection = Regex.Match(js, @"const NAV_SECTIONS = \[\s*\{\s*label:\s*'[^']+',\s*items:\s*\[(?<items>[\s\S]*?)\]\s*\}");
        Assert.True(firstSection.Success);
        var firstLabel = Regex.Match(firstSection.Groups["items"].Value, @"label:\s*'(?<label>[^']+)'");
        Assert.Equal("我的交辦", firstLabel.Groups["label"].Value);
    }

    // ── 風險日詳情的交辦單號 ────────────────────────────────────────────────

    [Fact]
    public void 風險日詳情_問題的進行中案件屬於交辦單7_DTO帶WorkOrderId7()
    {
        var recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var users = new FakeUserStore();
        var userGroups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var hosts = new FakeHostStore();
        var cases = new FakeIssueCaseStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var settings = new FakeSystemSettingsStore();

        var group = userGroups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\wang", GroupIds = new List<long> { group.GroupId } });
        var host = hosts.Upsert(new WebHost { HostName = "SRV-OO", GroupIds = new List<long> { 10 } });
        access.SetForUserGroup(group.GroupId, new[] { 10L });

        var day = DateTime.Today.AddDays(-1);
        var issue = TestData.Issue("disk", 153);
        recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = day,
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { issue }
        });
        cases.Save(new IssueCase
        {
            CaseId = "case-1",
            HostName = host.HostName,
            IssueKey = IssueSignatureKey.For(issue),
            IssueLabel = "disk 153",
            HandlerId = user.UserId,
            Status = IssueHandlingStatuses.InProgress,
            FirstLinkedDate = day,
            LastLinkedDate = day,
            WorkOrderId = 7
        });

        var currentUser = FakeCurrentUser.ForUser(user.UserId, Capability.Handle);
        var visibility = new VisibilityService(currentUser, users, userGroups, access, hosts, cases, settings);
        var query = new RecordQueryServiceFacade(
            repository: new RecordRepository(recordStore, hosts, visibility, new FakeSystemSettingsService()),
            reports: new StubReportReader(),
            hosts: hosts,
            users: users,
            hostGroups: new FakeHostGroupStore(),
            visibility: visibility,
            handlings: new FakeHandlingStore(),
            issueHandlings: issueHandlings,
            cases: cases,
            noiseMarks: new FakeNoiseMarkStore(),
            rules: new FakeRuleStore(),
            currentUser: currentUser,
            settings: settings,
            aggregates: new EfIssueAggregateQuery(_fixture.NewContext, hosts),
            statusResolver: new OccurrenceStatusResolver(hosts, issueHandlings, cases, settings));

        var dto = Assert.Single(query.GetDetail(host.HostId, day).TopIssues);

        Assert.Equal(user.UserId, dto.CaseHandlerId);
        Assert.Equal(7, dto.WorkOrderId);
    }

    // ── 前端接線 ────────────────────────────────────────────────────────────

    [Fact]
    public void 前端_風險日詳情以workOrderId提示_交辦單詳情以viewerIsHandler開回覆入口()
    {
        var record = ReadWeb("js", "pages", "record-detail.js");
        Assert.Contains("i.workOrderId != null", record);
        Assert.Contains("?order=", record);

        var order = ReadWeb("js", "pages", "work-order-detail.js");
        Assert.Contains("detail.viewerIsHandler", order);
        Assert.Contains("回覆這張單", order);

        var login = ReadWeb("js", "pages", "login.js");
        Assert.Contains("result.landingPath", login);
    }
}

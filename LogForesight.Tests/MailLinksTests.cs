using LogForesight.Core.Time;
using System.Net;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Tests.Scenarios;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 信件連結（第 76-2 輪批次 C-3）：交辦摘要信每種種類的連結目的地、沒設定站台網址時不含 http、
/// 取消通知的連結真的能開（真實 HTTP）。上報／高風險／摘要信的連結見 MailNotificationServiceTests。
/// </summary>
public sealed class MailLinksTests
{
    private const string Base = "https://x.example/lf";

    private static WorkOrderOutboxItem Item(string kind, long id = 7, long recipient = 3) => new()
    {
        Kind = kind, WorkOrderId = id, RecipientUserId = recipient, ActorAccount = "admin", MemberDelta = 1,
        Status = IssueHandlingStatuses.Resolved, Reason = "原因", Until = new DateTime(2026, 12, 1)
    };

    private static WorkOrderDigestOrder Order(long id = 7) =>
        new(id, "Schannel 36888", null, 1, new[] { "host-a" }, null, null);

    private static readonly string[] HandlerKinds =
    {
        WorkOrderOutboxKinds.Created, WorkOrderOutboxKinds.Appended, WorkOrderOutboxKinds.Transferred,
        WorkOrderOutboxKinds.Cancelled, WorkOrderOutboxKinds.Returned, WorkOrderOutboxKinds.AdminClosed,
        WorkOrderOutboxKinds.MutePaused, WorkOrderOutboxKinds.MuteResumed
    };

    public static IEnumerable<object[]> HandlerKindCases() => HandlerKinds.Select(k => new object[] { k });

    /// <summary>C8-a（交辦類）：每種處理人通知都附「我的交辦」連結，保留子路徑</summary>
    [Theory]
    [MemberData(nameof(HandlerKindCases))]
    public void C8a_交辦類信件連到我的交辦(string kind)
    {
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) }, Base, 3);

        Assert.Contains($"{Base}/handlers/3", body);
        Assert.StartsWith(Base + "/", body[body.IndexOf("https://", StringComparison.Ordinal)..]);
    }

    /// <summary>C8-a（管理者提醒）：連到交辦總覽</summary>
    [Fact]
    public void C8a_管理者提醒連到交辦總覽()
    {
        var reminder = new WorkOrderOutboxItem { Kind = WorkOrderOutboxKinds.AdminReminder, RecipientUserId = 1, ReminderEscalated = 1 };

        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (reminder, (WorkOrderDigestOrder?)null) }, Base, 1);

        Assert.Contains($"{Base}/work-orders", body);
        Assert.DoesNotContain("/handlers/", body);
    }

    /// <summary>C8-b：沒填站台網址，每種交辦信與管理者提醒都不含 http</summary>
    [Fact]
    public void C8b_沒填站台網址_交辦類信件不含http()
    {
        foreach (var kind in HandlerKinds)
        {
            var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) });
            Assert.False(body.Contains("http", StringComparison.OrdinalIgnoreCase), kind);
            var (_, blank) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) }, "  ", 3);
            Assert.False(blank.Contains("http", StringComparison.OrdinalIgnoreCase), kind);
        }
        var reminder = new WorkOrderOutboxItem { Kind = WorkOrderOutboxKinds.AdminReminder, RecipientUserId = 1 };
        Assert.DoesNotContain("http", WorkOrderDigestBuilder.Build(new[] { (reminder, (WorkOrderDigestOrder?)null) }).Body);
    }

    /// <summary>C11：新交辦信寫明要回覆處理狀態；沒有新交辦時不寫</summary>
    [Fact]
    public void C11_新交辦信含請回覆處理狀態()
    {
        var (_, created) = WorkOrderDigestBuilder.Build(new[] { (Item(WorkOrderOutboxKinds.Created), (WorkOrderDigestOrder?)Order()) });
        var (_, cancelled) = WorkOrderDigestBuilder.Build(new[] { (Item(WorkOrderOutboxKinds.Cancelled), (WorkOrderDigestOrder?)Order()) });

        Assert.Contains("請回覆處理狀態", created);
        Assert.DoesNotContain("請回覆處理狀態", cancelled);
    }

    /// <summary>頁面名稱常數與 layout.js 導覽文字一致，路徑對得上 PagesController 路由</summary>
    [Fact]
    public void 頁面名稱與路徑對齊導覽與路由()
    {
        var root = FindRoot();
        var layout = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "layout.js"));
        var pages = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Controllers", "PagesController.cs"));

        foreach (var name in new[] { MailLinks.MyWorkOrdersName, MailLinks.WorkOrdersName, MailLinks.RecordsName })
            Assert.Contains($"label: '{name}'", layout);
        Assert.Contains("[HttpGet(\"/handlers/{userId:long}\")]", pages);
        Assert.Contains("[HttpGet(\"/work-orders\")]", pages);
        Assert.Contains("[HttpGet(\"/work-orders/{id:long}\")]", pages);
        Assert.Contains("[HttpGet(\"/records\")]", pages);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到專案根目錄");
    }
}

/// <summary>C9：取消通知的連結指到「我的交辦」，收件人以該連結的路徑取頁面回 200（整站＋真實路由與授權）</summary>
[Collection("NLogGlobalConfig")]
public sealed class MailLinksHttpTests
{
    [Fact]
    public async Task C9_取消通知連到我的交辦_收件人以連結路徑取頁面回200()
    {
        using var site = new ScenarioSite();
        var group = site.Get<IUserGroupStore>().Upsert(new UserGroup { GroupName = "ug-h1", Role = UserRole.User, Active = true });
        var handler = site.Get<IUserStore>().Upsert(new WebUser
        {
            Account = "h1", DisplayName = "h1", Email = "h1@scenario.test", Active = true, GroupIds = new List<long> { group.GroupId }
        });
        Assert.True(await site.LoginAsync("h1"));
        site.Get<ISystemSettingsStore>().Update(s =>
        {
            s.MailEnabled = true; s.MailNotifyWorkOrders = true;
            s.SmtpServer = "smtp.scenario.test"; s.MailFrom = "lf@scenario.test";
            s.SiteBaseUrl = "https://x.example/lf/";
        });
        site.Get<WorkOrderOutboxStore>().Enqueue(new WorkOrderOutboxItem
        {
            Kind = WorkOrderOutboxKinds.Cancelled, WorkOrderId = 9999, RecipientUserId = handler.UserId,
            EnqueuedAt = site.Clock.LocalNow(), ActorAccount = "admin", Immediate = true
        });

        await site.Get<MailNotificationService>().CheckAndSendWorkOrderDigestAsync(site.Clock.LocalNow());

        var mail = Assert.Single(site.SentMail);
        var linkLine = mail.Body.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("連結：", StringComparison.Ordinal));
        var link = linkLine["連結：".Length..];
        Assert.Equal($"https://x.example/lf/handlers/{handler.UserId}", link);

        var path = "/" + link["https://x.example/lf/".Length..];   // 站台掛在 /lf 子應用程式：伺服器端看到的是去掉前綴的路徑
        var result = await site.SendAsync("h1", HttpMethod.Get, path);
        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
    }
}

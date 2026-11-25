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

    /// <summary>站台網址不得內嵌帳密（https://user@host、user:pass@host）：信件連結會把它原樣帶給收件人</summary>
    [Theory]
    [InlineData("https://user@lf.example.com")]
    [InlineData("https://user:secret@lf.example.com/lf")]
    [InlineData("http://:secret@lf.example.com")]
    public void 站台網址含帳密_拒絕(string raw)
    {
        Assert.False(MailLinks.TryNormalize(raw, out var normalized, out var error));
        Assert.Equal("", normalized);
        Assert.Equal(MailLinks.FormatHint, error);
        Assert.Null(MailLinks.Build(raw, "work-orders"));
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
    // ── P-1（F07-3）：摘要信各段連結帶對應篩選 ───────────────────────────────────

    [Fact]
    public void P1_退回段的連結只看被退回的單()
    {
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(WorkOrderOutboxKinds.Returned), (WorkOrderDigestOrder?)Order()) }, Base, 3);

        Assert.Contains($"{Base}/handlers/3?flag=returned", body);
        Assert.DoesNotContain("flag=newToday", body);
    }

    [Theory]
    [InlineData(WorkOrderOutboxKinds.Created)]
    [InlineData(WorkOrderOutboxKinds.Appended)]
    public void P1_新交辦與新增成員段的連結不帶篩選_連到我的交辦本身(string kind)
    {
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) }, Base, 3);

        Assert.Contains($"{Base}/handlers/3", body);
        Assert.DoesNotContain("flag=", body);
    }

    /// <summary>白天之後派的單常在隔天 08:00 才寄：「今天新增」那天已過，帶 newToday 會點開空清單</summary>
    [Fact]
    public void P1_隔天寄出的信_新交辦段連結不帶今天新增篩選()
    {
        var sent = Item(WorkOrderOutboxKinds.Created);
        sent.EnqueuedAt = new DateTime(2026, 11, 20, 18, 0, 0);   // 前一天傍晚入列、隔天早上寄
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (sent, (WorkOrderDigestOrder?)Order(7)) }, Base, 3);

        Assert.DoesNotContain("newToday", body);
        Assert.Contains($"連結：{Base}/handlers/3", body);
    }

    [Theory]
    [InlineData(WorkOrderOutboxKinds.Transferred)]
    [InlineData(WorkOrderOutboxKinds.Cancelled)]
    [InlineData(WorkOrderOutboxKinds.AdminClosed)]
    [InlineData(WorkOrderOutboxKinds.MutePaused)]
    [InlineData(WorkOrderOutboxKinds.MuteResumed)]
    public void P1_其餘段沒有篩選_只有信尾的總連結(string kind)
    {
        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) }, Base, 3);

        Assert.DoesNotContain("flag=", body);
        Assert.Contains($"{Base}/handlers/3", body);
    }

    [Fact]
    public void P1_退回與新交辦同一封信_各段各自的篩選連結_信尾仍有總連結()
    {
        var rows = new[]
        {
            (Item(WorkOrderOutboxKinds.Created, id: 7), (WorkOrderDigestOrder?)Order(7)),
            (Item(WorkOrderOutboxKinds.Returned, id: 8), (WorkOrderDigestOrder?)Order(8))
        };

        var (_, body) = WorkOrderDigestBuilder.Build(rows, Base, 3);

        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var returnedHeading = lines.FindIndex(l => l == "【退回】");
                Assert.DoesNotContain(lines.Take(returnedHeading), l => l.Contains("flag="));   // 新交辦段不帶篩選
        Assert.Contains(lines.Skip(returnedHeading), l => l.Contains("flag=returned"));
        Assert.Equal($"連結：{Base}/handlers/3", lines.Last(l => l.StartsWith("連結：", StringComparison.Ordinal)));
    }

    [Fact]
    public void P1_沒填站台網址_各段不含任何連結與篩選()
    {
        foreach (var kind in HandlerKinds)
        {
            var (_, body) = WorkOrderDigestBuilder.Build(new[] { (Item(kind), (WorkOrderDigestOrder?)Order()) });

            Assert.DoesNotContain("http", body);
            Assert.DoesNotContain("flag=", body);
        }
    }

    [Fact]
    public void P1_MyWorkOrdersPath_已知標記帶查詢字串_未知或空值退回總連結_站台網址由Build統一相接()
    {
        Assert.Equal("handlers/3?flag=returned", MailLinks.MyWorkOrdersPath(3, WorkOrderQueries.FlagReturned));
        Assert.Equal("handlers/3?flag=newToday", MailLinks.MyWorkOrdersPath(3, WorkOrderQueries.FlagNewToday));
        Assert.Equal("handlers/3", MailLinks.MyWorkOrdersPath(3, null));
        Assert.Equal("handlers/3", MailLinks.MyWorkOrdersPath(3, "evil&x=1"));
        Assert.Equal($"{Base}/handlers/3?flag=returned", MailLinks.Build(Base, MailLinks.MyWorkOrdersPath(3, WorkOrderQueries.FlagReturned)));
        Assert.Null(MailLinks.Build(null, MailLinks.MyWorkOrdersPath(3, WorkOrderQueries.FlagReturned)));
    }

    [Fact]
    public void P1_信件連結用到的標記都是清單端點認得的標記()
    {
        // 清單端點對不認得的 flag 擲驗證錯誤——信件連結帶的值必須是它認得的
        Assert.Contains(WorkOrderQueries.FlagReturned, WorkOrderQueries.Flags);
        Assert.Contains(WorkOrderQueries.FlagNewToday, WorkOrderQueries.Flags);
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

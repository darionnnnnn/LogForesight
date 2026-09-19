using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 50 輪 C-4：沿用此問題上次的說明（store＋端點可見範圍）、個人常用語、全站預設常用語的消費端、
/// 新索引在全新資料庫只有一份。
/// </summary>
public class NoteReuseAndPhrasesTests : IDisposable
{
    private const string IssueKey = "System|7|disk-warning";

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueHandlingStore Store => new(_fx.NewContext);

    private void AddRow(string hostName, DateTime recordDate, string? note, DateTime updatedAt, string issueKey = IssueKey)
    {
        using var ctx = _fx.NewContext();
        ctx.IssueHandlings.Add(new IssueHandlingRow
        {
            HostName = hostName,
            HostNameKey = HostNameKey.Of(hostName),
            RecordDate = recordDate.Date,
            IssueKey = issueKey,
            Status = "resolved",
            Note = note,
            UpdatedAt = updatedAt
        });
        ctx.SaveChanges();
    }

    /// <summary>主機 A（可見）兩天前寫「甲」、主機 B（不可見）昨天寫「乙」</summary>
    private void SeedTwoHosts()
    {
        var today = DateTime.Today;
        AddRow("SRV-A", today.AddDays(-2), "甲", today.AddDays(-2).AddHours(9));
        AddRow("SRV-B", today.AddDays(-1), "乙", today.AddDays(-1).AddHours(9));
    }

    // ── store ────────────────────────────────────────────────────────────

    [Fact]
    public void GetLatestNote_只在可見集合內找_全域時取最新()
    {
        SeedTwoHosts();

        var visibleOnlyA = Store.GetLatestNote(IssueKey, new[] { HostNameKey.Of("SRV-A") });
        Assert.NotNull(visibleOnlyA);
        Assert.Equal("甲", visibleOnlyA!.Value.Note);
        Assert.Equal("SRV-A", visibleOnlyA.Value.HostName);

        var global = Store.GetLatestNote(IssueKey, null);
        Assert.NotNull(global);
        Assert.Equal("乙", global!.Value.Note);
    }

    [Fact]
    public void GetLatestNote_空白說明不會被選中()
    {
        var today = DateTime.Today;
        AddRow("SRV-A", today.AddDays(-3), "甲", today.AddDays(-3));
        AddRow("SRV-A", today.AddDays(-1), "   ", today.AddDays(-1));
        AddRow("SRV-B", today, null, today);
        AddRow("SRV-C", today, "", today.AddHours(1));

        var found = Store.GetLatestNote(IssueKey, null);

        Assert.NotNull(found);
        Assert.Equal("甲", found!.Value.Note);
    }

    [Fact]
    public void GetLatestNote_其他問題簽章與空可見集合都不回()
    {
        AddRow("SRV-A", DateTime.Today, "別的問題", DateTime.Today, issueKey: "System|9|other");

        Assert.Null(Store.GetLatestNote(IssueKey, null));
        SeedTwoHosts();
        Assert.Null(Store.GetLatestNote(IssueKey, Array.Empty<string>()));
    }

    // ── 端點（走真實 VisibilityService）──────────────────────────────────

    [Fact]
    public void LastNote端點_只能看見A的使用者_回甲絕不回乙()
    {
        SeedTwoHosts();

        var users = new FakeUserStore();
        var userGroups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var hosts = new FakeHostStore();
        var group = userGroups.Upsert(new UserGroup { GroupName = "A 部門", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\a", GroupIds = new List<long> { group.GroupId } });
        hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } });
        hosts.Upsert(new WebHost { HostName = "SRV-B", GroupIds = new List<long> { 20 } });
        access.ReplaceAll(new[] { new GroupAccess { UserGroupId = group.GroupId, HostGroupId = 10 } });

        var currentUser = FakeCurrentUser.ForUser(user.UserId, Capability.Handle);
        var visibility = new VisibilityService(currentUser, users, userGroups, access, hosts,
            new FakeIssueCaseStore(), _settings, new FakeIssueOwnerStore(), new FakeIssueAggregateQuery());
        var controller = new IssueNoteController(Store, visibility, currentUser);

        var result = controller.GetLastNote(IssueKey).Data;

        Assert.NotNull(result);
        Assert.Equal("甲", result!.Note);
        Assert.Equal("SRV-A", result.HostName);
        Assert.Equal(DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd"), result.Date);
        Assert.NotEqual("乙", result.Note);

        // 對照組：持有 ViewAll 的人不限範圍，拿到的是最新的「乙」——證明上面不是因為資料只有甲
        var admin = FakeCurrentUser.ForUser(user.UserId, Capability.Handle, Capability.ViewAll);
        var adminVisibility = new VisibilityService(admin, users, userGroups, access, hosts,
            new FakeIssueCaseStore(), _settings, new FakeIssueOwnerStore(), new FakeIssueAggregateQuery());
        Assert.Equal("乙", new IssueNoteController(Store, adminVisibility, admin).GetLastNote(IssueKey).Data!.Note);
    }

    // ── 常用語 ────────────────────────────────────────────────────────────

    private MeController Me(long userId) =>
        new(new UserPreferenceStore(_fx.Blob("user_prefs")), _settings, FakeCurrentUser.ForUser(userId));

    [Fact]
    public void 常用語_未設定個人清單_回全站預設()
    {
        _settings.Update(s => s.DefaultNotePhrases = new List<string> { "已確認為例行維護" });

        var result = Me(7).GetNotePhrases().Data!;

        Assert.True(result.IsDefault);
        Assert.Equal(new[] { "已確認為例行維護" }, result.Phrases);
    }

    [Fact]
    public void 常用語_儲存個人清單後改用個人清單_空陣列回到預設()
    {
        _settings.Update(s => s.DefaultNotePhrases = new List<string> { "預設一" });
        var me = Me(7);

        var saved = me.SetNotePhrases(new SetNotePhrasesRequest { Phrases = new List<string?> { " 甲 ", "", "甲", "乙" } }).Data!;
        Assert.False(saved.IsDefault);
        Assert.Equal(new[] { "甲", "乙" }, saved.Phrases);
        Assert.Equal(new[] { "甲", "乙" }, Me(7).GetNotePhrases().Data!.Phrases);
        // 別的使用者不受影響
        Assert.True(Me(8).GetNotePhrases().Data!.IsDefault);

        var reset = me.SetNotePhrases(new SetNotePhrasesRequest { Phrases = new List<string?>() }).Data!;
        Assert.True(reset.IsDefault);
        Assert.Equal(new[] { "預設一" }, reset.Phrases);
        Assert.True(Me(7).GetNotePhrases().Data!.IsDefault);
    }

    [Fact]
    public void 常用語_21條_擋下()
    {
        var phrases = Enumerable.Range(1, 21).Select(i => (string?)$"常用語 {i}").ToList();

        var ex = Assert.Throws<DomainException>(() => Me(7).SetNotePhrases(new SetNotePhrasesRequest { Phrases = phrases }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.True(Me(7).GetNotePhrases().Data!.IsDefault);
    }

    [Fact]
    public void 常用語_20條恰好可存()
    {
        var phrases = Enumerable.Range(1, 20).Select(i => (string?)$"常用語 {i}").ToList();

        Assert.Equal(20, Me(7).SetNotePhrases(new SetNotePhrasesRequest { Phrases = phrases }).Data!.Phrases.Count);
    }

    [Fact]
    public void 常用語_單條201字_擋下_200字可存()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Me(7).SetNotePhrases(new SetNotePhrasesRequest { Phrases = new List<string?> { new string('字', 201) } }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);

        Assert.False(Me(7).SetNotePhrases(new SetNotePhrasesRequest { Phrases = new List<string?> { new string('字', 200) } }).Data!.IsDefault);
    }

    [Fact]
    public void 常用語超過上限_經ApiExceptionFilter對應成400與中文訊息()
    {
        var phrases = Enumerable.Range(1, 21).Select(i => (string?)$"常用語 {i}").ToList();
        var exception = Assert.Throws<DomainException>(() => Me(7).SetNotePhrases(new SetNotePhrasesRequest { Phrases = phrases }));

        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>())
        { Exception = exception };
        new ApiExceptionFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("最多 20 條", exception.Message);
    }

    // ── 全站預設常用語的消費端 ───────────────────────────────────────────

    [Fact]
    public void 預設常用語經設定頁儲存流程寫入後_常用語端點讀得到()
    {
        var service = new SystemSettingsService(_settings, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(_settings, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_fx.Blob("mail_notify_state")),
                new ScheduleFreshnessService(new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
                    new ScheduleOptionsStore(_fx.Blob("schedule_options")))),
            new FakeReportUsageQuery());

        var request = SettingsRequest();
        request.DefaultNotePhrases = new List<string> { " 已通知廠商 ", "", "已通知廠商", "已確認為例行維護" };
        var dto = service.Update(request);

        Assert.Equal(new[] { "已通知廠商", "已確認為例行維護" }, dto.DefaultNotePhrases);
        var phrases = Me(7).GetNotePhrases().Data!;
        Assert.True(phrases.IsDefault);
        Assert.Equal(new[] { "已通知廠商", "已確認為例行維護" }, phrases.Phrases);

        var tooMany = SettingsRequest();
        tooMany.DefaultNotePhrases = Enumerable.Range(1, 21).Select(i => $"第 {i} 條").ToList();
        Assert.Throws<DomainException>(() => service.Update(tooMany));
    }

    private static UpdateSystemSettingsRequest SettingsRequest() => new()
    {
        UnhandledSeverities = new List<string> { "High" },
        SeverityDisplayMode = "DefaultHidden",
        VisibleDayRiskLevels = new List<string> { "高", "中" },
        AiProvider = "Local",
        AiBaseUrl = "",
        AiModel = "local-model",
        AiAzureDeployment = "",
        AiAzureApiVersion = "2024-10-21",
        InitialHistoryDays = 120,
        RetentionDays = 180,
        RunLogRetentionDays = 120,
        AuditRetentionDays = 730,
        RawEventRetentionDays = 120,
        AiTimeoutSeconds = 600,
        AiRetryCount = 3,
        AiRetryDelaySeconds = 10,
        AiJsonRetryCount = 2,
        AiMaxTokens = 1536,
        AiDeepDiveMaxTokens = 8192,
        AiFrequencyPenalty = 0.8,
        AiPresencePenalty = 0.8,
        AiExtraRequestFieldsJson = """{"rep_pen":1.3}""",
        CheckupIntervalDays = 7,
        ImportMaxFileSizeKb = 2048,
        ImportMaxRows = 5000,
        PrtgEnabled = false,
        PrtgUrl = "",
        PrtgAuthMode = PrtgAuthModes.Token,
        PrtgUsername = "",
        PrtgIgnoreSslErrors = false,
        PrtgTimeoutSeconds = 60,
        PrtgFetchConcurrency = 2,
        PrtgBackfillDays = 30,
        PrtgRetentionDays = 180
    };

    // ── 索引 ────────────────────────────────────────────────────────────

    [Fact]
    public void 全新SQLite資料庫_issue_key_updated_at索引只有一份()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).Options))
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            SchemaUpgrader.Upgrade(ctx);   // 再跑一次：冪等
        }

        var matching = new List<string>();
        var indexNames = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_index_list('lf_issue_handling')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) indexNames.Add(reader.GetString(0));
        }
        foreach (var name in indexNames)
        {
            var columns = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_index_info($name) ORDER BY seqno";
            cmd.Parameters.AddWithValue("$name", name);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(0));
            if (columns.SequenceEqual(new[] { "issue_key", "updated_at" })) matching.Add(name);
        }

        Assert.Equal(new[] { "IX_lf_issue_handling_issue_key_updated_at" }, matching);
    }
}

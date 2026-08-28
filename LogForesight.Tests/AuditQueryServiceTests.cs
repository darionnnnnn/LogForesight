using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-8-PLAN.md #6：稽核紀錄「帳號」欄補上顯示名稱素材，讓前端能統一顯示
/// 「顯示名稱(帳號)」。登入失敗等情境常見帳號打錯／查無使用者，必須優雅退回、不出錯。
/// </summary>
public class AuditQueryServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly AuditLogStore _store;
    private readonly FakeUserStore _users = new();
    private readonly AuditQueryService _service;

    public AuditQueryServiceTests()
    {
        _store = new AuditLogStore(_fixture.LogStore("audit"));
        _service = new AuditQueryService(_store, _users, new UserDisplayNameService(new FakeSystemSettingsStore()));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void 查得到使用者時補上顯示名稱()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "小美" });
        _store.Append(new AuditEntry { Action = "login", OccurredAt = DateTime.Now, Account = "DOMAIN\\alice" });

        var result = _service.Query(new AuditQuery { Page = 1, PageSize = 10 });

        var entry = Assert.Single(result.Items);
        Assert.Equal("小美", entry.AccountDisplayName);
    }

    /// <summary>登入失敗時帳號常打錯或根本不存在——查無使用者要退回 null，不能整筆出錯</summary>
    [Fact]
    public void 查無使用者時顯示名稱為null()
    {
        _store.Append(new AuditEntry { Action = "login_failed", OccurredAt = DateTime.Now, Account = "DOMAIN\\typo-user" });

        var result = _service.Query(new AuditQuery { Page = 1, PageSize = 10 });

        var entry = Assert.Single(result.Items);
        Assert.Null(entry.AccountDisplayName);
        Assert.Equal("DOMAIN\\typo-user", entry.Account);
    }

    /// <summary>
    /// 回饋二十輪作業 A：起訖顛倒時由 controller 的 ParseDateRange 自動交換。
    /// 這支刻意走 controller 而非直接呼叫 ParseDateRange——只測工具函式的話，
    /// controller 忘了接上也照樣全綠。
    /// </summary>
    [Fact]
    public void Controller查詢_起訖顛倒_自動交換後查得到資料()
    {
        _store.Append(new AuditEntry
        {
            Action = "login",
            OccurredAt = DateTime.Today.AddDays(-3).AddHours(9),
            Account = "DOMAIN\\alice"
        });
        var controller = new LogForesight.Web.Controllers.Api.AuditController(_service);

        var 顛倒 = controller.Query(
            from: DateTime.Today.ToString("yyyy-MM-dd"),
            to: DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"),
            userId: null, actions: null, targetKind: null, result: null);

        Assert.Single(顛倒.Data!.Items);
    }
}

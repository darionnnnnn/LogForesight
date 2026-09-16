using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪 B7 C5：權限異動查詢的每頁筆數由網址參數決定（<c>?pageSize=100000</c>），
/// 不夾上限就等於開放「一次把整張表搬回來」的端點。
///
/// 正規化在 **Web 層**（PermissionChangeService.BuildFilter 走 <c>Paging.Normalize</c>）：
/// store 在 Core、Paging 在 Web 且是 internal，不為了這件事把 helper 搬家或改可見性。
/// 這組測試把它釘住——以往這種上限是「某次改動時順手加的」，沒有守門就會在下一次
/// 重構掉進沒有人發現的靜默退化。
/// </summary>
public sealed class PermissionChangePageSizeBoundTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly PermissionChangeStore _store;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();

    public PermissionChangePageSizeBoundTests()
    {
        _store = new PermissionChangeStore(_fixture.NewContext);
    }

    public void Dispose() => _fixture.Dispose();

    private PermissionChangeService CreateService()
    {
        var user = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var visibility = new VisibilityService(
            user, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(), _hosts,
            new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        return new PermissionChangeService(
            _store, _hosts, visibility, user, new RecordingAuditService(), _users,
            new NullReportReader(), new FakeSystemSettingsStore());
    }

    [Theory]
    [InlineData(100000, 200)]
    [InlineData(201, 200)]
    [InlineData(200, 200)]
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void 每頁筆數被夾在合理上限內(int requested, int expected)
    {
        var service = CreateService();

        var filter = service.BuildFilter(new PermissionChangeQueryRequest { Page = 1, PageSize = requested });

        Assert.Equal(expected, filter.PageSize);
    }

    [Fact]
    public void 頁碼最小為1()
    {
        var service = CreateService();

        Assert.Equal(1, service.BuildFilter(new PermissionChangeQueryRequest { Page = 0, PageSize = 20 }).Page);
        Assert.Equal(1, service.BuildFilter(new PermissionChangeQueryRequest { Page = -3, PageSize = 20 }).Page);
    }

    [Fact]
    public void 超過上限的每頁筆數不會一次取回更多資料()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(Enumerable.Range(0, 220)
            .Select(i => new PermissionChangeRecord
            {
                ChangeId = $"c{i}",
                HostName = "SRV-01",
                DetectedAt = baseTime.AddMinutes(i),
                Target = $"Group{i}"
            })
            .ToList());

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest { Page = 1, PageSize = 100000 });

        Assert.Equal(200, result.PageSize);
        Assert.Equal(200, result.Items.Count);
        Assert.Equal(220, result.Total);
    }
}

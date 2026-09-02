using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 第 4 輪體檢抓到的授權缺口：<c>GET /api/host-detail/{id}/prtg</c> 原本不做可見性檢查，
/// 任何登入者都能用任意 hostId 列出別人主機的 PRTG device 與 sensor。
/// 同 controller 的另外兩個端點是在 RecordDetailQueryService 內做 EnsureVisible，
/// 這支不經 service，必須在 controller 自己檢查——本測試釘住這一條。
/// </summary>
public class HostDetailPrtgAuthorizationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>只放行指定主機的可見性替身；其餘成員本測試用不到</summary>
    private sealed class OnlyVisible : IVisibilityService
    {
        private readonly long _visibleHostId;
        public OnlyVisible(long visibleHostId) => _visibleHostId = visibleHostId;

        public void EnsureVisible(long hostId)
        {
            if (hostId != _visibleHostId)
                throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
        }

        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { _visibleHostId };
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => throw new NotSupportedException("測試未使用此方法");
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => throw new NotSupportedException("測試未使用此方法");
        public List<WebHost> GetVisibleHosts() => throw new NotSupportedException("測試未使用此方法");
        public IReadOnlyDictionary<string, IReadOnlySet<string>> GetCaseGrants() => throw new NotSupportedException("測試未使用此方法");
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
    }

    // RecordDetailQueryService 在 /prtg 這條路徑完全用不到，傳 null 只為了建構 controller；
    // 這不是替身假值進斷言——斷言的是授權例外與回應內容，與 service 無關。
    private HostDetailController CreateController(long visibleHostId) =>
        new(null!, new EfPrtgStore(_fx.NewContext), new OnlyVisible(visibleHostId));

    [Fact]
    public void 不可見的主機_查詢PRTG對應擲NotFound()
    {
        var controller = CreateController(visibleHostId: 10);

        var ex = Assert.Throws<DomainException>(() => controller.Prtg(999));
        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void 可見的主機_無對應資料時回空物件不擲例外()
    {
        var controller = CreateController(visibleHostId: 10);

        var response = controller.Prtg(10);

        Assert.NotNull(response.Data);
        Assert.Empty(response.Data!.Devices);
    }
}

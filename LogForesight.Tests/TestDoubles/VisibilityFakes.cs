using LogForesight;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：IVisibilityService／ISystemSettingsService ────────────────────
// AlwaysVisibleService 是「不限制可見範圍」的預設替身，用在不關心授權邊界的測試；
// 需要驗證邊界本身時用 HostIdentityTests 內的 FixedVisibility（單一測試檔使用，
// 刻意不搬到這裡）。兩者用途不同，不是誰涵蓋誰的重複。

internal class AlwaysVisibleService : IVisibilityService
{
    private readonly FakeHostStore _hosts;

    public AlwaysVisibleService(FakeHostStore hosts) => _hosts = hosts;

    public IReadOnlySet<long> GetVisibleHostIds() => _hosts.GetAll().Select(h => h.HostId).ToHashSet();

    public List<WebHost> GetVisibleHosts() => _hosts.GetAll();

    public void EnsureVisible(long hostId) { }
}

internal class FakeSystemSettingsService : ISystemSettingsService
{
    public HashSet<string>? VisibleSeverities { get; set; }

    /// <summary>預設 null（未設定，等同全顯示、不過濾）——與 GetVisibleDayRiskLevels 的
    /// 全勾回 null 慣例一致，不設定這個屬性的既有測試行為不受影響</summary>
    public IReadOnlySet<string>? VisibleDayRiskLevels { get; set; }

    public SystemSettingsDto Get() => throw new NotSupportedException("測試未使用此方法");

    public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException("測試未使用此方法");

    public HashSet<string>? GetVisibleSeverities() => VisibleSeverities;

    public IReadOnlySet<string>? GetVisibleDayRiskLevels() => VisibleDayRiskLevels;

    public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) =>
        throw new NotSupportedException("測試未使用此方法");
}

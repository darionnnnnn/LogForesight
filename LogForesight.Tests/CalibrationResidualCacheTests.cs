using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 45 輪 B7 C4：殘差候選列快取的過期判定讀錯欄位。
///
/// 原本殘差快取**寫入時沒有更新自己的時間戳**，過期判定卻讀整體判定摘要那份（_cachedAt）。
/// 兩個方向都會出錯：沒先算過整體摘要時 _cachedAt 恆為 default，殘差快取永遠不命中
/// （每次匯出都把整批 blob 再反序列化一次）；剛算過整體摘要時，十分鐘前算的殘差資料
/// 會被判成新鮮。兩種錯法都不會有任何錯誤訊息，只會表現成「慢」或「數字不對」。
///
/// 快取是 private static、時間來源是 DateTime.Now，沒有注入點，因此以反射直接呼叫
/// 私有方法並撥動時間戳——這裡刻意不為了測試改動正式碼的可見性。
/// </summary>
[Collection("CalibrationCacheState")]
public sealed class CalibrationResidualCacheTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();

    public CalibrationResidualCacheTests()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.RawEventRetentionDays = 120;
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationService CreateService()
    {
        CalibrationService.ClearAssessmentCache();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        return new CalibrationService(_fx.NewContext, prtgStore, issueQuery, _settingsStore, _ruleStore);
    }

    private static readonly MethodInfo BuildRows =
        typeof(CalibrationService).GetMethod("BuildResidualCandidateRows", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 BuildResidualCandidateRows");

    private static object Rows(CalibrationService service, DateTime anchor) =>
        BuildRows.Invoke(service, new object[] { anchor, 120 })!;

    private static void SetStatic(string field, DateTime value)
    {
        var f = typeof(CalibrationService).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"找不到欄位 {field}");
        f.SetValue(null, value);
    }

    private static DateTime GetStatic(string field)
    {
        var f = typeof(CalibrationService).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"找不到欄位 {field}");
        return (DateTime)f.GetValue(null)!;
    }

    [Fact]
    public void 未先算整體狀態就連續取兩次殘差候選列_第二次命中快取()
    {
        var service = CreateService();
        var anchor = new DateTime(2026, 8, 31);

        var first = Rows(service, anchor);
        var second = Rows(service, anchor);

        // 命中時回的是同一份 List 實例；沒命中會重算出一份新的
        Assert.Same(first, second);
        Assert.NotEqual(default, GetStatic("_cachedResidualAt"));
    }

    [Fact]
    public void 超過TTL後殘差快取不再命中()
    {
        var service = CreateService();
        var anchor = new DateTime(2026, 8, 31);

        var first = Rows(service, anchor);
        SetStatic("_cachedResidualAt", DateTime.Now.AddMinutes(-11));

        var second = Rows(service, anchor);

        Assert.NotSame(first, second);
    }

    /// <summary>反例：整體判定摘要的時間戳不得讓過期的殘差資料復活</summary>
    [Fact]
    public void 剛算過整體狀態不會讓過期的殘差資料被判為新鮮()
    {
        var service = CreateService();
        var anchor = new DateTime(2026, 8, 31);

        var first = Rows(service, anchor);

        // 殘差是十一分鐘前算的（已過期），但整體判定摘要是「剛剛」算的
        SetStatic("_cachedResidualAt", DateTime.Now.AddMinutes(-11));
        SetStatic("_cachedAt", DateTime.Now);

        var second = Rows(service, anchor);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void 清空判定快取時殘差時間戳一併歸零()
    {
        var service = CreateService();
        Rows(service, new DateTime(2026, 8, 31));
        Assert.NotEqual(default, GetStatic("_cachedResidualAt"));

        CalibrationService.ClearAssessmentCache();

        Assert.Equal(default, GetStatic("_cachedResidualAt"));
    }
}

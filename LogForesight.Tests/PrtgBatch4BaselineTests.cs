using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 4 的「結果不變」基準（A1、A3）：這兩組基準字串是在批次 4 改動前的建置上錄下的
/// （測試台 <see cref="PrtgBatch4Rig"/> 只用不隨本批改動的 API），改動後必須逐字相同。
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgBatch4BaselineTests : IDisposable
{
    private readonly PrtgBatch4Rig _rig = new();

    public PrtgBatch4BaselineTests() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _rig.Dispose();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task A1_每日路徑逐日finding與改動前逐筆相同(int lookbackDays, bool withPolicy)
    {
        _rig.EnablePrtg(seedPolicy: withPolicy);
        _rig.Seed();

        var actual = await _rig.RunPipelineAsync(lookbackDays);

        var expected = PrtgBatch4Golden.Pipeline[(lookbackDays, withPolicy)];
        Assert.True(expected == actual, $"實際簽名：\n{actual}");
    }

    [Fact]
    public void A1_基準涵蓋各類情境_避免空基準讓測試空轉()
    {
        var all = string.Join("\n", PrtgBatch4Golden.Pipeline.Values);
        Assert.Contains("prtg:down:101", all);     // 跨午夜持續失聯
        Assert.Contains("prtg:down:205", all);     // 回望邊界前一天進入 Down
        Assert.Contains("prtg:warning:203", all);  // 跨午夜 Warning
        Assert.Contains("prtg:flapping:202", all); // 同裝置另一顆感測器
        Assert.Contains("prtg:silent", all);       // 沉默裝置
        Assert.Contains("prtg:down:999", PrtgBatch4Golden.Pipeline[(5, false)]);
        Assert.DoesNotContain("prtg:down:999", PrtgBatch4Golden.Pipeline[(5, true)]);   // 政策外感測器
    }

}

/// <summary>校準的判定快取是行程層級靜態欄位，要與其他碰它的測試序列化。</summary>
[Collection("CalibrationCacheState")]
public sealed class PrtgBatch4CalibrationBaselineTests
{
    [Fact]
    public void A3_校準匯出的規則門檻資料與改動前相同()
    {
        using var fx = new EfSqliteFixture();
        var actual = PrtgBatch4Rig.BuildCalibrationSignature(fx.NewContext);

        Assert.True(PrtgBatch4Golden.Calibration == actual, $"實際簽名：\n{actual}");
        Assert.DoesNotContain("|1003|", actual);   // 政策外感測器不進分佈
    }
}

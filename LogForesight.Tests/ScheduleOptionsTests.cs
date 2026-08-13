using System.Text.Json;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ScheduleOptions.LocalAnalysisEnabled（回饋十八輪批次D）：預設 true——零行為變化，
/// 舊版存下的 blob 沒有這個欄位，反序列化後也要落在 true，不能讓既有使用者升級後
/// 「本機分析突然停了」。
/// </summary>
public class ScheduleOptionsTests
{
    [Fact]
    public void 新建時LocalAnalysisEnabled預設為true()
    {
        var options = new ScheduleOptions();

        Assert.True(options.LocalAnalysisEnabled);
    }

    /// <summary>舊版 blob（升級前存下、沒有這個欄位的 JSON）反序列化後仍要是 true。</summary>
    [Fact]
    public void 反序列化沒有此欄位的舊JSON時預設為true()
    {
        const string legacyJson = """
        {
          "Enabled": true,
          "Windows": [{ "Start": "01:00", "End": "07:00" }],
          "DebugDump": false
        }
        """;

        var options = JsonSerializer.Deserialize<ScheduleOptions>(legacyJson, LfJsonOptions.Pretty);

        Assert.NotNull(options);
        Assert.True(options!.LocalAnalysisEnabled);
        Assert.True(options.Enabled);
    }
}

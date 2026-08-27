using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報告保留天數（<see cref="SystemSettings.ReportRetentionDays"/> 與
/// <see cref="RetentionOptions.ReportRetentionDays"/>）的設定行為。
///
/// 核心約束：**報告保留天數不可大於 <see cref="SystemSettings.RetentionDays"/>**——
/// 報告全文存在資料庫，超過歷史資料保留天數之後對應的分析紀錄已被清除，
/// 那些報告在站上不再有任何入口可以點開，留著只是佔空間。
/// 既有部署可能存著大於 RetentionDays 的值，讀取時**取小**而不是退回預設值。
/// </summary>
public class ReportRetentionSettingTests
{
    private readonly FakeSystemSettingsStore _store = new();

    [Fact]
    public void SystemSettings預設值_ReportRetentionDays與RetentionDays同值()
    {
        var settings = new SystemSettings();
        Assert.Equal(SystemSettings.DefaultRetentionDays, settings.ReportRetentionDays);
        Assert.Equal(SystemSettings.DefaultRetentionDays, SystemSettings.DefaultReportRetentionDays);
    }

    [Fact]
    public void 舊設定JSON未包含ReportRetentionDays_反序列化後為出廠預設()
    {
        // 舊版設定 blob 沒有 ReportRetentionDays 鍵，System.Text.Json 應沿用 C# 屬性初始值
        var json = """
        {
            "RetentionDays": 180,
            "RunLogRetentionDays": 120,
            "AuditRetentionDays": 730,
            "RawEventRetentionDays": 120
        }
        """;

        var settings = JsonSerializer.Deserialize<SystemSettings>(json);

        Assert.NotNull(settings);
        Assert.Equal(SystemSettings.DefaultReportRetentionDays, settings.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值在合法範圍內_正確套用()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 500;
            s.ReportRetentionDays = 400;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(400, retention.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值低於下限90_維持內建預設值()
    {
        _store.Update(s => s.ReportRetentionDays = 10);

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(SystemSettings.DefaultReportRetentionDays, retention.ReportRetentionDays);
    }

    /// <summary>
    /// 升級路徑：既有部署可能存著 1095（早期版本兩者互不約束）。
    /// **取小**而不是退回預設值——退回預設會把使用者刻意調短的設定悄悄調長，方向剛好相反。
    /// </summary>
    [Fact]
    public void ApplySystemSettingsOverrides_DB值大於RetentionDays_取小收斂為RetentionDays()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 180;
            s.ReportRetentionDays = 1095;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(180, retention.RetentionDays);
        Assert.Equal(180, retention.ReportRetentionDays);
    }

    /// <summary>
    /// 取小不得反過來把「刻意設得比較短」的值調長：使用者把報告設成 90 天、紀錄留 180 天時，
    /// 報告就該是 90 天。
    /// </summary>
    [Fact]
    public void ApplySystemSettingsOverrides_DB值小於RetentionDays_照使用者設定不被拉長()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 180;
            s.ReportRetentionDays = 90;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(90, retention.ReportRetentionDays);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(3650)]
    public void ApplySystemSettingsOverrides_邊界值_受RetentionDays約束後套用(int boundaryDays)
    {
        _store.Update(s =>
        {
            s.RetentionDays = 3650;
            s.ReportRetentionDays = boundaryDays;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(boundaryDays, retention.ReportRetentionDays);
    }

    [Fact]
    public void RetentionOptions預設實例_ReportRetentionDays為出廠預設()
    {
        var retention = new RetentionOptions();
        Assert.Equal(SystemSettings.DefaultReportRetentionDays, retention.ReportRetentionDays);
    }
}

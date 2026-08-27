using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// B1：報告檔保留天數（<see cref="SystemSettings.ReportRetentionDays"/> 與
/// <see cref="RetentionOptions.ReportRetentionDays"/>）獨立設定測試。
/// 驗證出廠預設值、舊 JSON 反序列化相容性、DB 覆寫、上下限越界防護，
/// 以及「報告保留天數大於資料庫 RetentionDays 時不互相約束、不被夾住」之核心行為。
/// </summary>
public class ReportRetentionSettingTests
{
    private readonly FakeSystemSettingsStore _store = new();

    [Fact]
    public void SystemSettings預設值_ReportRetentionDays為1095()
    {
        var settings = new SystemSettings();
        Assert.Equal(1095, settings.ReportRetentionDays);
        Assert.Equal(1095, SystemSettings.DefaultReportRetentionDays);
    }

    [Fact]
    public void 舊設定JSON未包含ReportRetentionDays_反序列化後預設為1095()
    {
        // 舊版設定 blob 沒有 ReportRetentionDays 鍵，System.Text.Json 應沿用 C# 屬性初始值（1095）
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
        Assert.Equal(1095, settings.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值在合法範圍內_正確套用()
    {
        _store.Update(s => s.ReportRetentionDays = 400);

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(400, retention.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值低於下限90_維持內建預設值1095()
    {
        _store.Update(s => s.ReportRetentionDays = 10);

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(1095, retention.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值高於上限3650_維持內建預設值1095()
    {
        _store.Update(s => s.ReportRetentionDays = 4000);

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(1095, retention.ReportRetentionDays);
    }

    [Fact]
    public void ApplySystemSettingsOverrides_DB值大於RetentionDays_照樣採用不被夾到RetentionDays()
    {
        // 核心需求：報告檔保留天數可獨立留存比 DB 紀錄更久，不與 RetentionDays 做大小約束
        _store.Update(s =>
        {
            s.RetentionDays = 180;
            s.ReportRetentionDays = 1095;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(180, retention.RetentionDays);
        Assert.Equal(1095, retention.ReportRetentionDays);
        Assert.True(retention.ReportRetentionDays > retention.RetentionDays);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(3650)]
    public void ApplySystemSettingsOverrides_邊界值_正確套用(int boundaryDays)
    {
        _store.Update(s => s.ReportRetentionDays = boundaryDays);

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(boundaryDays, retention.ReportRetentionDays);
    }

    [Fact]
    public void RetentionOptions預設實例_ReportRetentionDays為1095()
    {
        var retention = new RetentionOptions();
        Assert.Equal(1095, retention.ReportRetentionDays);
    }
}

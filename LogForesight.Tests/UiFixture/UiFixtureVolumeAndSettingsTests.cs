using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Tests.Scenarios;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

/// <summary>量體資料集與模擬設定值的不變式守門（只動測試工具，不涉正式碼）</summary>
public sealed class UiFixtureVolumeAndSettingsTests
{
    /// <summary>量體資料集由舊到新建單：每張單 LastAppendedAt（若有）不早於 CreatedAt</summary>
    [Fact]
    public void 量體資料集_每張單最近新增不早於建立時間()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-ui-volume-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var data = UiFixtureDataSet.Generate(root, UiFixtureVariant.AiConfigured, volumeHosts: 60);
            var orders = OrderRef.AllOrders(data.Backend.WorkOrderStore());
            Assert.NotEmpty(orders);
            var bad = orders.Where(o => o.LastAppendedAt != null && o.LastAppendedAt < o.CreatedAt)
                .Select(o => $"#{o.WorkOrderId} created {o.CreatedAt:O} appended {o.LastAppendedAt:O}").ToList();
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* 暫存目錄 */ }
        }
    }

    /// <summary>AI 數值欄位逐一過設定頁存檔請求上的 [Range]（與 API 邊界模型驗證同一份屬性）</summary>
    private static List<ValidationResult> ValidateAiNumbers(SystemSettings s)
    {
        var request = new UpdateSystemSettingsRequest();
        var results = new List<ValidationResult>();
        void Check(string property, object value) =>
            Validator.TryValidateProperty(value, new ValidationContext(request) { MemberName = property }, results);
        Check(nameof(request.AiTimeoutSeconds), s.AiTimeoutSeconds);
        Check(nameof(request.AiRetryCount), s.AiRetryCount);
        Check(nameof(request.AiRetryDelaySeconds), s.AiRetryDelaySeconds);
        Check(nameof(request.AiJsonRetryCount), s.AiJsonRetryCount);
        Check(nameof(request.AiMaxTokens), s.AiMaxTokens);
        Check(nameof(request.AiDeepDiveMaxTokens), s.AiDeepDiveMaxTokens);
        return results;
    }

    [Fact]
    public void 情境站台寫入的AI設定_通過設定儲存的驗證()
    {
        var s = new SystemSettings();
        ScenarioSite.ApplyAiIsolation(s);
        Assert.True(s.AiRetryCount >= 1);
        Assert.Empty(ValidateAiNumbers(s));
    }

    [Fact]
    public void UI資料集寫入的設定_通過設定儲存的驗證()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-ui-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var data = UiFixtureDataSet.Generate(root, UiFixtureVariant.AiConfigured);
            var s = new SystemSettingsStore(data.Backend.Blob("system_settings")).Get();
            Assert.Empty(ValidateAiNumbers(s));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* 暫存目錄 */ }
        }
    }
}

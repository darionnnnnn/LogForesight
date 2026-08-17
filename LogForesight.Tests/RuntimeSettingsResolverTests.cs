using LogForesight.Core.Configuration;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// §12（回饋第九輪）：AI 進階參數／權限監控資料夾／分析參數自 appsettings 遷入 DB 後，
/// <see cref="RuntimeSettingsResolver.ApplySystemSettingsOverrides"/> 是**唯一**把 DB 值套進
/// <see cref="AppSettings"/> 的地方（排程與立即執行每次觸發都呼叫）。這裡釘住兩件事：
/// (1) DB 值確實會蓋過內建出廠值；(2) 壞值（手改 DB、舊 blob 缺欄位反序列化成 0）時保留出廠值，
/// 不讓「0 秒逾時」這種更糟的狀態上線——與其餘設定「壞值不擋執行」的一貫作法一致。
/// </summary>
public class RuntimeSettingsResolverTests
{
    private readonly FakeSystemSettingsStore _store = new();

    [Fact]
    public void DB值覆寫AI進階參數與分析參數()
    {
        _store.Update(s =>
        {
            s.AiTimeoutSeconds = 300;
            s.AiRetryCount = 5;
            s.AiMaxTokens = 2048;
            s.AiDeepDiveMaxTokens = 4096;
            s.AiFrequencyPenalty = 1.1;
            s.AiPresencePenalty = 1.2;
            s.AiExtraRequestFieldsJson = """{"rep_pen":1.5}""";
            s.WatchedFolders = new List<string> { @"C:\inetpub\wwwroot" };
            s.ServerDescription = "AD 網域控制站";
            s.CheckupIntervalDays = 14;
            s.AnalysisChannels = new List<string> { "System" };
        });

        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Equal(300, settings.Ai.TimeoutSeconds);
        Assert.Equal(5, settings.Ai.RetryCount);
        Assert.Equal(2048, settings.Ai.MaxTokens);
        Assert.Equal(4096, settings.Ai.DeepDiveMaxTokens);
        Assert.Equal(1.1, settings.Ai.FrequencyPenalty);
        Assert.Equal(1.2, settings.Ai.PresencePenalty);
        Assert.Equal(1.5m, settings.Ai.ExtraRequestFields!["rep_pen"].GetDecimal());
        Assert.Equal(new[] { @"C:\inetpub\wwwroot" }, settings.Permissions.WatchedFolders);
        Assert.Equal("AD 網域控制站", settings.Analysis.ServerDescription);
        Assert.Equal(14, settings.Analysis.CheckupIntervalDays);
        Assert.Equal(new[] { "System" }, settings.Analysis.Channels);
    }

    [Fact]
    public void 未設定過的DB_沿用內建出廠值()
    {
        // 全新環境（blob 從未寫過）：SystemSettings 的預設值＝原 appsettings 的出廠值，
        // 套用後行為與退役前完全相同（升級零遷移的關鍵保證）
        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Equal(1200, settings.Ai.TimeoutSeconds);
        Assert.Equal(3, settings.Ai.RetryCount);
        Assert.Equal(2048, settings.Ai.MaxTokens);
        Assert.Equal(8192, settings.Ai.DeepDiveMaxTokens);
        Assert.Equal(7, settings.Analysis.CheckupIntervalDays);
        Assert.Empty(settings.Analysis.Channels);   // 空＝預設六頻道
    }

    [Fact]
    public void 壞值不套用_保留出廠值()
    {
        // 舊 blob 缺欄位反序列化成 0：0 秒逾時／負數懲罰會讓 AI 呼叫直接失效，比不改更糟
        _store.Update(s =>
        {
            s.AiTimeoutSeconds = 0;
            s.AiFrequencyPenalty = -1;
            s.CheckupIntervalDays = 0;
        });

        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Equal(1200, settings.Ai.TimeoutSeconds);
        Assert.Equal(0.8, settings.Ai.FrequencyPenalty);
        Assert.Equal(7, settings.Analysis.CheckupIntervalDays);
    }

    [Fact]
    public void DB值覆寫AI提供者與新欄位()
    {
        _store.Update(s =>
        {
            s.UpdatedAt = DateTime.Now;
            s.AiProvider = "AzureOpenAi";
            s.AiBaseUrl = "https://my-resource.openai.azure.com/";
            s.AiModel = "gpt-4o";
            s.AiAzureDeployment = "gpt-4o-prod";
            s.AiAzureApiVersion = "2024-12-01-preview";
        });

        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Equal("AzureOpenAi", settings.Ai.Provider);
        Assert.Equal("https://my-resource.openai.azure.com/", settings.Ai.BaseUrl);
        Assert.Equal("gpt-4o", settings.Ai.Model);
        Assert.Equal("gpt-4o-prod", settings.Ai.AzureDeployment);
        Assert.Equal("2024-12-01-preview", settings.Ai.AzureApiVersion);
    }

    [Fact]
    public void 未設定Provider的既有設定_傳遞到執行期設定預設為本機()
    {
        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Equal("Local", settings.Ai.Provider);
        Assert.Equal("local-model", settings.Ai.Model);
        Assert.Equal("", settings.Ai.AzureDeployment);
        Assert.Equal("2024-10-21", settings.Ai.AzureApiVersion);
    }

    [Fact]
    public void 額外請求欄位為壞JSON時不附加_且不擲例外()
    {
        _store.Update(s => s.AiExtraRequestFieldsJson = "{ 這不是 JSON");

        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Null(settings.Ai.ExtraRequestFields);
    }

    [Fact]
    public void 額外請求欄位留空_不附加任何欄位()
    {
        _store.Update(s => s.AiExtraRequestFieldsJson = "");

        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _store);

        Assert.Null(settings.Ai.ExtraRequestFields);
    }

    // ── 保留天數解析（ResolveRetention） ───────────────────────────────────────────

    [Fact]
    public void ResolveRetention_合理範圍內的值正確套用()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 120;
            s.DetailRetentionDays = 90;
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(90, retention.DetailRetentionDays);
    }

    [Fact]
    public void ResolveRetention_大於歷史資料保留天數時不套用_沿用預設值()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 120;
            s.DetailRetentionDays = 200; // 超出範圍
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        // RetentionOptions 的出廠預設為 120
        Assert.Equal(120, retention.DetailRetentionDays);
    }

    [Fact]
    public void ResolveRetention_小於1時不套用_沿用預設值()
    {
        _store.Update(s =>
        {
            s.RetentionDays = 120;
            s.DetailRetentionDays = 0; // 不合法下限
        });

        var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(new AppSettings(), _store);

        Assert.Equal(120, retention.DetailRetentionDays);
    }
}

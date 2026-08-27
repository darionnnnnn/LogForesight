using LogForesight.Core.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AiProviders 提供者識別字正規化與共用判定測試（§D3）。
/// </summary>
public class AiProvidersTests
{
    /// <summary>
    /// 無法辨識的 provider 字串退回 Local，生產程式碼在此情境會記錄 WARN 警告日誌。
    /// </summary>
    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("custom_ai")]
    [InlineData("claude")]
    [InlineData("123")]
    public void Normalize_無法辨識的字串_回傳Local並記錄警告(string unknown)
    {
        var result = AiProviders.Normalize(unknown);

        Assert.Equal(AiProviders.Local, result);
    }

    /// <summary>
    /// null 或空白字串退回 Local 是正常出廠或未設定情況，不視為異常、不記錄 WARN 警告日誌。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Normalize_Null或空白字串_回傳Local且不記錄警告(string? emptyOrWhitespace)
    {
        var result = AiProviders.Normalize(emptyOrWhitespace);

        Assert.Equal(AiProviders.Local, result);
    }

    [Theory]
    [InlineData("Local", AiProviders.Local)]
    [InlineData("local", AiProviders.Local)]
    [InlineData("LOCAL", AiProviders.Local)]
    [InlineData("OpenAi", AiProviders.OpenAi)]
    [InlineData("openai", AiProviders.OpenAi)]
    [InlineData("OPENAI", AiProviders.OpenAi)]
    [InlineData("AzureOpenAi", AiProviders.AzureOpenAi)]
    [InlineData("azureopenai", AiProviders.AzureOpenAi)]
    [InlineData("AZUREOPENAI", AiProviders.AzureOpenAi)]
    public void Normalize_已知提供者名稱_不分大小寫正確正規化(string input, string expected)
    {
        var result = AiProviders.Normalize(input);

        Assert.Equal(expected, result);
    }
}

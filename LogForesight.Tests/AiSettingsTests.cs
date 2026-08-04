using Xunit;

namespace LogForesight.Tests;

public class AiSettingsTests
{
    /// <summary>
    /// AiSettings.IsConfigured：排程/立即執行短路統計模式的判斷依據（docs/WEB-SPEC.md）。
    /// </summary>
    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AiSettings_IsConfigured依BaseUrl是否有值判斷(string baseUrl, bool expected)
    {
        var settings = new AiSettings { BaseUrl = baseUrl };

        Assert.Equal(expected, settings.IsConfigured);
    }
}

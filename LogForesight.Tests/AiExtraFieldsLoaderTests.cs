using System.Text.Json;
using LogForesight.Web.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="AiExtraFieldsLoader"/>：繞過 ConfigurationBinder 直接重讀
/// Ai:ExtraRequestFields（docs/archive/FEEDBACK-7-PLAN.md，見 <see cref="AIServiceExtraRequestFieldsTests"/>
/// 對炸點本身的重現）。
/// </summary>
public class AiExtraFieldsLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lf-ai-extra-test").FullName;

    private string PathOf(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void 兩份設定檔都不存在_回傳null()
    {
        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.Null(result);
    }

    [Fact]
    public void 基礎檔有節點_正確解析且支援註解與尾逗號()
    {
        File.WriteAllText(PathOf("appsettings.json"), """
            {
              // 支援註解
              "Ai": {
                "BaseUrl": "http://localhost:8080",
                "ExtraRequestFields": {
                  "chat_template_kwargs": { "enable_thinking": false },
                  "rep_pen": 1.3,
                },
              },
            }
            """);

        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result!["chat_template_kwargs"].ValueKind);
        Assert.Equal(1.3, result["rep_pen"].GetDouble());
    }

    [Fact]
    public void Ai區段存在但無ExtraRequestFields_回傳null()
    {
        File.WriteAllText(PathOf("appsettings.json"), """
            { "Ai": { "BaseUrl": "http://localhost:8080" } }
            """);

        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.Null(result);
    }

    [Fact]
    public void 環境檔有節點時覆蓋基礎檔()
    {
        File.WriteAllText(PathOf("appsettings.json"), """
            { "Ai": { "ExtraRequestFields": { "rep_pen": 1.3 } } }
            """);
        File.WriteAllText(PathOf("appsettings.Development.json"), """
            { "Ai": { "ExtraRequestFields": { "rep_pen": 9.9 } } }
            """);

        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.NotNull(result);
        Assert.Equal(9.9, result!["rep_pen"].GetDouble());
    }

    [Fact]
    public void 環境檔無ExtraRequestFields節點_沿用基礎檔()
    {
        File.WriteAllText(PathOf("appsettings.json"), """
            { "Ai": { "ExtraRequestFields": { "rep_pen": 1.3 } } }
            """);
        File.WriteAllText(PathOf("appsettings.Development.json"), """
            { "Ai": { "BaseUrl": "http://localhost:9999" } }
            """);

        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.NotNull(result);
        Assert.Equal(1.3, result!["rep_pen"].GetDouble());
    }

    [Fact]
    public void 檔案格式錯誤_回傳null不擲例外()
    {
        File.WriteAllText(PathOf("appsettings.json"), "{ 這不是合法 JSON");

        var result = AiExtraFieldsLoader.Load(_dir, "Development");

        Assert.Null(result);
    }
}

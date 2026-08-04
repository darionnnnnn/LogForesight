using System.Text.Json;
using LogForesight;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回歸測試（docs/FEEDBACK-7-PLAN.md）：Web 排程／立即執行原本必炸的 bug。
///
/// 根因：<c>IConfiguration</c> 的 <c>ConfigurationBinder</c> 綁不出
/// <c>Dictionary&lt;string, JsonElement&gt;</c>——JsonElement 沒有可寫屬性、也沒有
/// TypeConverter，binder 對這種欄位只能產生 <c>default(JsonElement)</c>
/// （<c>ValueKind=Undefined</c>）。舊版 <see cref="AIService"/> 建構子對這種空值呼叫
/// <c>GetRawText()</c> 會丟 <see cref="InvalidOperationException"/>，導致整個排程/立即
/// 執行在分析開始前就中止（本機與 NetIQ 全部主機都不會跑）。
/// </summary>
public class AIServiceExtraRequestFieldsTests
{
    /// <summary>
    /// 用與 Web appsettings.json 相同形狀的 JSON，走真正的 ConfigurationBinder
    /// （<c>IConfiguration.Get&lt;AiSettings&gt;()</c>）重現炸點的前提。
    ///
    /// 實測結果比原先設想的更細緻：巢狀物件（<c>chat_template_kwargs</c>）binder 能正確
    /// 綁出合法的 JsonElement；**純量值**（<c>rep_pen: 1.3</c> 這種沒有子節點的葉節點）
    /// 才會被綁成 <c>default(JsonElement)</c>（ValueKind=Undefined）——這正是實際 log
    /// 炸掉的那個欄位形態，<c>GetRawText()</c> 對它呼叫必定擲例外。
    /// </summary>
    [Fact]
    public void ConfigurationBinder_把純量ExtraRequestField綁成Undefined元素()
    {
        var json = """
            {
              "Ai": {
                "BaseUrl": "http://localhost:8080",
                "ExtraRequestFields": {
                  "chat_template_kwargs": { "enable_thinking": false },
                  "rep_pen": 1.3
                }
              }
            }
            """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var aiSettings = config.GetSection("Ai").Get<AiSettings>();

        Assert.NotNull(aiSettings);
        Assert.NotNull(aiSettings!.ExtraRequestFields);
        // 巢狀物件：binder 綁得出合法 JsonElement
        Assert.Equal(JsonValueKind.Object, aiSettings.ExtraRequestFields!["chat_template_kwargs"].ValueKind);
        // 純量值：binder 只能產生 Undefined，對它呼叫 GetRawText() 必炸——這正是事故重現
        var scalar = aiSettings.ExtraRequestFields["rep_pen"];
        Assert.Equal(JsonValueKind.Undefined, scalar.ValueKind);
        Assert.Throws<InvalidOperationException>(() => scalar.GetRawText());
    }

    /// <summary>
    /// 本次修復的主張：即使 ExtraRequestFields 裡混著 ConfigurationBinder 產生的
    /// Undefined 元素，AIService 建構子也不應該擲例外——這是縱深防禦（治本在 Web 端
    /// 改用 AiExtraFieldsLoader 重讀該欄位，但 AIService 本身也不該對壞組態束手無策）。
    /// </summary>
    [Fact]
    public void ExtraRequestFields含Undefined元素_建構不擲例外()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1",
            RetryCount = 1,
            TimeoutSeconds = 1,
            ExtraRequestFields = new Dictionary<string, JsonElement>
            {
                ["bad"] = default, // ValueKind = Undefined，模擬 ConfigurationBinder 的產物
                ["good"] = JsonSerializer.SerializeToElement(1.3)
            }
        };

        var ex = Record.Exception(() => new AIService(settings));

        Assert.Null(ex);
    }

    /// <summary>正常路徑（JsonSerializer 反序列化產生的合法 JsonElement）維持可用，不受本次修復影響。</summary>
    [Fact]
    public void ExtraRequestFields全部合法_建構照常成功()
    {
        var settings = new AiSettings
        {
            BaseUrl = "http://localhost:1",
            RetryCount = 1,
            TimeoutSeconds = 1,
            ExtraRequestFields = new Dictionary<string, JsonElement>
            {
                ["rep_pen"] = JsonSerializer.SerializeToElement(1.3)
            }
        };

        var ex = Record.Exception(() => new AIService(settings));

        Assert.Null(ex);
    }
}

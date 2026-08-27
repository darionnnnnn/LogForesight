using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端送出的 JSON 能不能被 MVC 綁定成 <see cref="TriggerRunRequest"/>（第三十一輪批次D）。
///
/// 這是「接線層」測試：前端 runs.js 送的是 <c>rerunMode: "All"</c> 這種**字串**，
/// 而 DTO 欄位是 enum。System.Text.Json 預設不接受字串轉 enum，站台又沒有全域註冊
/// JsonStringEnumConverter——不鎖住的話，UI 選了重新分析模式會被 API 以 400 擋掉，
/// 而所有後端單元測試都照樣全綠（它們直接建物件，不經過 JSON）。
/// </summary>
public class TriggerRunRequestBindingTests
{
    /// <summary>MVC 的預設 JSON 選項（ASP.NET Core 對 controller 用的就是 Web 預設值）</summary>
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("None", RerunMode.None)]
    [InlineData("Unhandled", RerunMode.Unhandled)]
    [InlineData("UnhandledAndAssigned", RerunMode.UnhandledAndAssigned)]
    [InlineData("All", RerunMode.All)]
    public void 前端送的字串模式能綁定成列舉(string sent, RerunMode expected)
    {
        var json = $$"""
        {
          "scope": "all",
          "segment": null,
          "backfillDays": 30,
          "onlyMissingOrFailed": false,
          "rerunMode": "{{sent}}"
        }
        """;

        var request = JsonSerializer.Deserialize<TriggerRunRequest>(json, WebDefaults);

        Assert.NotNull(request);
        Assert.Equal(expected, request!.RerunMode);
        Assert.Equal(30, request.BackfillDays);
    }

    /// <summary>沒帶 rerunMode 的舊呼叫端（或前端預設路徑）仍要落在 None。</summary>
    [Fact]
    public void 未帶重新分析欄位時落在預設值()
    {
        const string json = """{ "scope": "all", "onlyMissingOrFailed": true }""";

        var request = JsonSerializer.Deserialize<TriggerRunRequest>(json, WebDefaults);

        Assert.NotNull(request);
        Assert.Equal(RerunMode.None, request!.RerunMode);
        Assert.Null(request.BackfillDays);
    }
}

using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 觸發來源文字的單一判定（<see cref="RunTriggerText"/>）：已知觸發字串對應的中文，未知字串原樣回傳。
/// null 不在這裡處理：即時狀態講「閒置」、歷史紀錄講「工作排程器」，由呼叫端決定。
/// </summary>
public class RunTriggerTextTests
{
    private readonly FakeUserStore _users = new();
    private readonly UserDisplayNameService _displayNames = new(new FakeSystemSettingsStore());

    public RunTriggerTextTests()
    {
        _users.Upsert(new WebUser { Account = "alice", DisplayName = "愛麗絲", Active = true });
    }

    [Theory]
    [InlineData(BatchRunTriggers.FetchFollowUp, "AI 跟隨取數")]
    [InlineData("", "工作排程器")]
    [InlineData("console", "工作排程器")]
    [InlineData(BatchRunTriggers.Schedule, "排程")]
    [InlineData("xyz", "xyz")]
    public void 已知觸發字串轉成對應文字(string trigger, string expected) =>
        Assert.Equal(expected, RunTriggerText.Of(trigger, _displayNames, _users));

    [Fact]
    public void 手動觸發帶出顯示名稱()
    {
        var text = RunTriggerText.Of("manual:alice", _displayNames, _users);

        Assert.Contains("手動（", text);
        Assert.Contains("愛麗絲", text);
    }
}

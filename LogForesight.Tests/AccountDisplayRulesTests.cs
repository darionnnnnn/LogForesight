using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 使用者名稱顯示規則（回饋三十四輪 B1）：管理員可用正則表達式調整畫面上顯示的帳號，
/// 例如把使用者名稱裡的公司名稱前綴去掉。規則套在**顯示層**——資料庫存的仍是原始帳號，
/// 所以規則調整後既有紀錄立即套用，寫錯也救得回來。
/// </summary>
public class AccountDisplayRulesTests
{
    private static string Format(string? account, string rulesText) =>
        AccountDisplayFormatter.Format(account, AccountDisplayFormatter.ParseRules(rulesText));

    [Theory]
    [InlineData("CN=王小明,OU=Users,DC=corp", "王小明")]
    [InlineData("CONTOSO\\AdminUser", "CONTOSO\\AdminUser")]
    [InlineData("user@corp.example.com", "user@corp.example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 無規則時輸出與短名化完全相同(string? account, string expected)
    {
        Assert.Equal(expected, AccountDisplayFormatter.ToShortName(account));
        Assert.Equal(expected, Format(account, ""));
        Assert.Equal(expected, AccountDisplayFormatter.Format(account, AccountDisplayRuleSet.Empty));
    }

    [Fact]
    public void 規則可刪除公司名稱前綴()
    {
        Assert.Equal("王小明", Format("CN=台積電-王小明,OU=Users,DC=corp", "^台積電-\\s*=>"));
    }

    [Fact]
    public void 規則可替換為指定文字()
    {
        Assert.Equal("王小明（外包）", Format("CN=VENDOR_王小明,OU=Users,DC=corp", "^VENDOR_(.+)$ => $1（外包）"));
    }

    [Fact]
    public void 多條規則依序套用且後條吃到前條的輸出()
    {
        const string rules = """
            ^台積電- =>
            ^\s*(.+?)\s*$ => 【$1】
            """;

        Assert.Equal("【王小明】", Format("CN=台積電-王小明,OU=Users,DC=corp", rules));
    }

    [Fact]
    public void 註解行與空白行被略過()
    {
        const string rules = """
            # 這行是註解，不該被當成規則

            ^台積電- =>
            """;

        var parsed = AccountDisplayFormatter.ParseRules(rules);

        Assert.Single(parsed.Rules);
        Assert.Equal("王小明", AccountDisplayFormatter.Format("CN=台積電-王小明,OU=x", parsed));
    }

    [Fact]
    public void 取代文字可為空即刪除符合的片段()
    {
        Assert.Equal("王小明", Format("CN=王小明(離職),OU=x", @"\(離職\) =>"));
    }

    [Fact]
    public void 不合法的規則在驗證時被擋下且訊息含行號()
    {
        const string rules = """
            ^台積電- =>
            [unclosed => X
            """;

        var ok = AccountDisplayFormatter.TryValidateRules(rules, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("第 2 行", error);
    }

    [Fact]
    public void 缺少分隔符號的規則在驗證時被擋下()
    {
        var ok = AccountDisplayFormatter.TryValidateRules("台積電-", out var error);

        Assert.False(ok);
        Assert.Contains("第 1 行", error!);
    }

    [Fact]
    public void 空白或全註解的規則文字視為合法()
    {
        Assert.True(AccountDisplayFormatter.TryValidateRules("", out _));
        Assert.True(AccountDisplayFormatter.TryValidateRules(null, out _));
        Assert.True(AccountDisplayFormatter.TryValidateRules("# 只有註解", out _));
    }

    [Fact]
    public void 既存設定中的不合法規則不會讓顯示路徑爆掉其餘規則照常生效()
    {
        // 設定可能是舊資料或被手動改壞——顯示路徑必須容忍，略過壞的那條就好
        const string rules = """
            [unclosed => X
            ^台積電- =>
            """;

        Assert.Equal("王小明", Format("CN=台積電-王小明,OU=x", rules));
    }
}

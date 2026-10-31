using System.ComponentModel.DataAnnotations;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

public class AccountNamesTests
{
    [Theory]
    [InlineData("CORP\\bob", "bob")]
    [InlineData(" bob ", "bob")]
    [InlineData("a\\b\\c", "b\\c")]
    [InlineData(null, "")]
    [InlineData("bob", "bob")]
    public void Canonical_正規化(string? input, string expected)
        => Assert.Equal(expected, AccountNames.Canonical(input));

    [Fact]
    public void 節流_網域前綴變體合併為同一帳號鍵()
    {
        var t = new LoginThrottle();
        var now = new DateTime(2026, 9, 19, 10, 0, 0);
        var ip = System.Net.IPAddress.Parse("10.0.0.1");
        var variants = new[] { "A\\bob", "B\\bob", "bob" };
        for (var i = 0; i < LoginThrottle.AccountThreshold; i++)
            t.RecordFailure(AccountNames.Canonical(variants[i % 3]), ip, now);

        Assert.True(t.IsBlocked(AccountNames.Canonical("C\\bob"), null, now, out _));
    }

    [Fact]
    public void LoginRequest_帳號與密碼有長度上限256()
    {
        foreach (var name in new[] { nameof(LoginRequest.Account), nameof(LoginRequest.Password) })
        {
            var attr = typeof(LoginRequest).GetProperty(name)!
                .GetCustomAttributes(typeof(StringLengthAttribute), false).Cast<StringLengthAttribute>().Single();
            Assert.Equal(256, attr.MaximumLength);
        }
    }
}

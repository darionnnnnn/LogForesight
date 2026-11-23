using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// UrlSecrets 的使用者資訊比對不得平方級回溯，且比對逾時有防線：
/// 最壞輸入（scheme:// 後接大量「a:」）以往在 1 秒逾時擲出 RegexMatchTimeoutException，沒有人接。
/// </summary>
public class UrlSecretsRegexTests
{
    [Theory]
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void 最壞輸入_不擲例外且在200毫秒內完成(int repeat)
    {
        var input = "http://" + string.Concat(Enumerable.Repeat("a:", repeat));

        var sw = Stopwatch.StartNew();
        var masked = UrlSecrets.Mask(input);
        var contains = UrlSecrets.ContainsSecret(input);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"耗時 {sw.ElapsedMilliseconds} ms");
        Assert.Equal(input, masked);       // 沒有 @，沒有帳密
        Assert.False(contains);
    }

    [Fact]
    public void 最壞輸入_結尾有at時仍遮蔽()
    {
        var input = "http://" + string.Concat(Enumerable.Repeat("a:", 50_000)) + "@host/x";
        var masked = UrlSecrets.Mask(input);
        Assert.Equal("http://***@host/x", masked);
        Assert.True(UrlSecrets.ContainsSecret(input));
    }

    // 以下四例＋其餘等價案例的預期值取自未修改的正規式實作

    [Theory]
    [InlineData("https://user:p@ss@host/x", "https://***@host/x", true)]                         // 密碼含 @
    [InlineData("ftp://user@host/x", "ftp://user@host/x", false)]                                  // 只有帳號沒有密碼不遮
    [InlineData("a http://u1:p1@h1/x b https://h2/y?token=abc c ftp://u3:p3@h3:21/z",
        "a http://***@h1/x b https://h2/y?token=*** c ftp://***@h3:21/z", true)]                  // 一段文字三個網址
    [InlineData("https://h/p?a=1&Pass%77ord=zz&x=2", "https://h/p?a=1&Pass%77ord=***&x=2", true) ] // 查詢參數含機敏名稱（百分比編碼）
    [InlineData("see https://a:b@h/p ok", "see https://***@h/p ok", true)]
    [InlineData("1http://u:p@h/x", "1http://u:p@h/x", false)]                                      // scheme 前緊接字元（\b）不成立
    [InlineData("http://u:p@h?x=1@y", "http://***@h?x=1@y", true)]                                 // ? 之後的 @ 不屬於使用者資訊
    [InlineData("http://u:p@h http://v:w@i", "http://***@h http://***@i", true)]
    [InlineData("http://:@h/", "http://***@h/", true)]                                             // 空帳號空密碼，冒號仍在
    [InlineData("http://a@b:c@d/", "http://***@d/", true)]                                         // 冒號在中間的 @ 之後
    [InlineData("http://h:80/x@y", "http://h:80/x@y", false)]                                      // @ 在路徑
    public void 等價案例(string input, string expectedMask, bool expectedContains)
    {
        Assert.Equal(expectedMask, UrlSecrets.Mask(input));
        Assert.Equal(expectedContains, UrlSecrets.ContainsSecret(input));
    }

    // ── 逾時防線 ─────────────────────────────────────────────────────────

    // 查詢參數正規式以極短逾時（1 毫秒）比對大量參數，必定逾時
    private static readonly string HeavyQuery = "http://h/p" + string.Concat(Enumerable.Repeat("&a=b", 500_000));

    [Fact]
    public void 逾時時Mask回固定遮蔽字串且不含輸入原文()
    {
        var masked = UrlSecrets.Mask(HeavyQuery, TimeSpan.FromMilliseconds(1));
        Assert.Equal(UrlSecrets.TimeoutMask, masked);
        Assert.DoesNotContain("http://h", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void 逾時時ContainsSecret回true()
        => Assert.True(UrlSecrets.ContainsSecret(HeavyQuery, TimeSpan.FromMilliseconds(1)));

    [Fact]
    public void 不逾時時同一輸入走正常路徑()
    {
        // 邊界另一側：逾時值充裕時，同樣的輸入照常遮蔽、不誤判
        var small = "http://h/p?a=b&token=zz";
        Assert.Equal("http://h/p?a=b&token=***", UrlSecrets.Mask(small, TimeSpan.FromSeconds(1)));
        Assert.True(UrlSecrets.ContainsSecret(small, TimeSpan.FromSeconds(1)));
        Assert.False(UrlSecrets.ContainsSecret("http://h/p?a=b", TimeSpan.FromSeconds(1)));
    }
}

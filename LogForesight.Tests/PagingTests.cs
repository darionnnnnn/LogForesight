using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>分頁參數正規化（<see cref="Paging.Normalize"/>）：所有分頁端點共用的頁碼與頁大小邊界。</summary>
public class PagingTests
{
    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(3, 500, 3, 200)]
    public void 頁碼與頁大小夾在合法範圍(int page, int pageSize, int expectedPage, int expectedSize)
    {
        Assert.Equal((expectedPage, expectedSize), Paging.Normalize(page, pageSize));
    }

    /// <summary>極大頁碼：位移不得溢位成負數（Skip 會把負數當 0、回第 1 頁而不是空頁）</summary>
    [Theory]
    [InlineData(int.MaxValue, 200)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue / 200 + 2, 200)]
    public void 極大頁碼的位移不溢位(int page, int pageSize)
    {
        var (p, size) = Paging.Normalize(page, pageSize);

        Assert.True((long)(p - 1) * size <= int.MaxValue);
        Assert.True((p - 1) * size >= 0);
        Assert.True(p >= int.MaxValue / size, "夾住的頁碼仍遠在任何實際資料之後，會得到空頁");
    }
}

using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/FEEDBACK-8-PLAN.md #7：Sqlite 連線池關閉的連線字串處理——單純字串轉換，
/// 不需要真的開連線驗證（連線池行為本身是 Microsoft.Data.Sqlite 內部機制）。
/// </summary>
public class StorageFactorySqlitePoolingTests
{
    [Fact]
    public void 未指定Pooling時補上Pooling等於False()
    {
        var result = StorageFactory.DisableSqlitePoolingIfUnset("Data Source=C:\\data\\logforesight.db");

        Assert.Contains("Pooling=False", result);
    }

    [Theory]
    [InlineData("Data Source=C:\\data\\logforesight.db;Pooling=True")]
    [InlineData("Data Source=C:\\data\\logforesight.db;Pooling=False")]
    public void 已明寫Pooling時尊重原設定不覆寫(string original)
    {
        var result = StorageFactory.DisableSqlitePoolingIfUnset(original);

        // SqliteConnectionStringBuilder 正規化大小寫與順序，這裡只驗證「沒被我們的邏輯改動」——
        // 直接比對輸入字串是否原樣通過（我們的分支在偵測到已設定時直接 return，不經 builder）
        Assert.Equal(original, result);
    }
}

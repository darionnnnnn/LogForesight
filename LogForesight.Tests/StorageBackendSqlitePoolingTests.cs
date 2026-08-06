using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-8-PLAN.md #7：Sqlite 連線池關閉的連線字串處理——單純字串轉換，
/// 不需要真的開連線驗證（連線池行為本身是 Microsoft.Data.Sqlite 內部機制）。
/// </summary>
public class StorageBackendSqlitePoolingTests
{
    [Fact]
    public void 未指定Pooling時補上Pooling等於False()
    {
        var result = StorageBackend.DisableSqlitePoolingIfUnset("Data Source=C:\\data\\logforesight.db");

        Assert.Contains("Pooling=False", result);
    }

    [Theory]
    [InlineData("Data Source=C:\\data\\logforesight.db;Pooling=True")]
    [InlineData("Data Source=C:\\data\\logforesight.db;Pooling=False")]
    public void 已明寫Pooling時尊重原設定不覆寫(string original)
    {
        var result = StorageBackend.DisableSqlitePoolingIfUnset(original);

        // SqliteConnectionStringBuilder 正規化大小寫與順序，這裡只驗證「沒被我們的邏輯改動」——
        // 直接比對輸入字串是否原樣通過（我們的分支在偵測到已設定時直接 return，不經 builder）
        Assert.Equal(original, result);
    }
}

/// <summary>
/// SqlServer 背景工作的連線池隔離（docs/SCALE-FIX-PLAN-2026-08-06.md S-3）。
///
/// 這件事的重點常被誤解成「限制分析只能用 4 條連線」；真正的效果是**連線字串一改，
/// ADO.NET 就把它當成另一個池**——分析與前景站台從此不共用同一組連線。
/// 沒有這個差異，夜間分析連續數小時佔用連線時，使用者的請求會排隊等連線，
/// 而症狀是「整站變慢」，從站台這端完全查不出原因。
/// </summary>
public class StorageBackendPoolIsolationTests
{
    private const string Cs = "Server=sql01;Database=LogForesight;Integrated Security=true";

    [Fact]
    public void 指定池上限時寫進連線字串()
    {
        var result = StorageBackend.ApplyMaxPoolSizeIfUnset(Cs, 4);

        Assert.Contains("Max Pool Size=4", result);
        // 池以連線字串為鍵：改過的字串必須與原字串不同，否則兩邊仍是同一個池
        Assert.NotEqual(Cs, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void 未指定或無效時原字串不動(int? maxPoolSize)
    {
        Assert.Equal(Cs, StorageBackend.ApplyMaxPoolSizeIfUnset(Cs, maxPoolSize));
    }

    [Fact]
    public void 使用者已自行指定池上限時尊重其設定()
    {
        var original = Cs + ";Max Pool Size=50";

        Assert.Equal(original, StorageBackend.ApplyMaxPoolSizeIfUnset(original, 4));
    }

    /// <summary>連線字串鍵允許省略空白（MaxPoolSize），偵測不能只比對含空白的寫法</summary>
    [Fact]
    public void 使用者以無空白寫法指定時也視為已設定()
    {
        var original = Cs + ";MaxPoolSize=50";

        Assert.Equal(original, StorageBackend.ApplyMaxPoolSizeIfUnset(original, 4));
    }
}

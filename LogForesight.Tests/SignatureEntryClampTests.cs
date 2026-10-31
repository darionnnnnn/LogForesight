using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 外部字串在入口截斷（第 59 輪 B-1）：來源超過欄位上限時，記憶體中衍生的問題鍵必須與
/// 存檔（source_name 255）後讀回重建的鍵相同，否則處理標記、案件與首見日永遠對不上。
/// </summary>
public class SignatureEntryClampTests
{
    [Fact]
    public void 來源與日誌名稱超過欄位上限_入口截斷到255()
    {
        var sig = new LogIssueSignature { LogName = new string('L', 300), Source = new string('S', 300), EventId = 1 };

        Assert.Equal(WorkOrderIssueKey.SourceMaxLength, sig.Source.Length);
        Assert.Equal(WorkOrderIssueKey.SourceMaxLength, sig.LogName.Length);
    }

    [Fact]
    public void 超長來源衍生的問題鍵_與讀回截斷後來源重建的鍵相同()
    {
        var longSource = new string('S', 300);
        var inMemory = new LogIssueSignature { LogName = "System", Source = longSource, EventId = 7 };
        var readBack = new LogIssueSignature { LogName = "System", Source = longSource[..WorkOrderIssueKey.SourceMaxLength], EventId = 7 };

        Assert.Equal(IssueSignatureKey.For(readBack), IssueSignatureKey.For(inMemory));
        Assert.Equal(WorkOrderIssueKey.SourceKeyOf(longSource), WorkOrderIssueKey.SourceKeyOf(inMemory.Source));
    }

    [Fact]
    public void Null與短字串_維持原值或空字串()
    {
        var sig = new LogIssueSignature { Source = null!, LogName = "Security" };

        Assert.Equal(string.Empty, sig.Source);
        Assert.Equal("Security", sig.LogName);
    }
}

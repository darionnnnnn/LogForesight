using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDataTransferTests
{
    [Fact]
    public void CurrentFormatVersion_維持2()
    {
        Assert.Equal(2, PrtgDataTransfer.CurrentFormatVersion);
    }

    [Fact]
    public void SourceHintFor_空白回空字串_同位址同摘要_不同位址不同摘要_格式錯誤擲例外()
    {
        Assert.Equal(string.Empty, PrtgDataTransfer.SourceHintFor(null));
        Assert.Equal(string.Empty, PrtgDataTransfer.SourceHintFor("  "));

        var a = PrtgDataTransfer.SourceHintFor("https://prtg.example.com/");
        Assert.Equal(a, PrtgDataTransfer.SourceHintFor("HTTPS://PRTG.example.com:443"));
        Assert.NotEqual(a, PrtgDataTransfer.SourceHintFor("https://other.example.com/"));
        // 摘要不得夾帶位址本身（不保存 URL、query 或 userinfo）
        Assert.DoesNotContain("example", a, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() => PrtgDataTransfer.SourceHintFor("not a url"));
        Assert.Throws<ArgumentException>(() => PrtgDataTransfer.SourceHintFor("ftp://prtg.example.com"));
    }
}

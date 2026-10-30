using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

public sealed class CorrelationPatternIdsTests
{
    [Fact]
    public void 關聯模式共二十三種_Windows十八_Linux二_PRTG三且不含來源對齊()
    {
        Assert.Equal(23, CorrelationPatternIds.All.Length);
        Assert.Equal(18, CorrelationPatternIds.All.Count(id => !id.StartsWith("linux-", StringComparison.Ordinal) &&
            !id.StartsWith("prtg-", StringComparison.Ordinal)));
        Assert.Equal(2, CorrelationPatternIds.All.Count(id => id.StartsWith("linux-", StringComparison.Ordinal)));
        Assert.Equal(3, CorrelationPatternIds.All.Count(id => id.StartsWith("prtg-", StringComparison.Ordinal)));
        Assert.All(CorrelationPatternIds.All, id => Assert.DoesNotContain("evidence-aligned", id));
        Assert.False(CorrelationPatternIds.IsValid("prtg-storage-evidence-aligned"));
    }
}

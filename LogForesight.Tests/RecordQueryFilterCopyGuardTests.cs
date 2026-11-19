using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <c>RecordRepository.CountLightweightAtLeast</c> 以逐欄複製的篩選副本計數，供「依處理狀態篩選」在載入前先擋上限。
/// 篩選型別新增欄位而副本沒跟著複製時，計數的母體會比實際載入的大——不再是下界，使用者會被誤擋。
/// 這裡釘住欄位清單：新增欄位時這條會紅，提醒同步那份副本。
/// </summary>
public class RecordQueryFilterCopyGuardTests
{
    [Fact]
    public void 篩選欄位清單與計數副本複製的欄位一致()
    {
        var actual = typeof(RecordQueryFilter).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var copied = new[] { "Categories", "EventId", "From", "Hosts", "MinSeverity", "RiskLevels", "Source", "To" };
        Assert.Equal(copied, actual);

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web", "Repositories", "RecordRepository.cs"));
        var start = source.IndexOf("public int? CountLightweightAtLeast(", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 CountLightweightAtLeast");
        var body = source.Substring(start, source.IndexOf("ApplyVisibility(probe)", start, StringComparison.Ordinal) - start);
        foreach (var name in copied)
            Assert.Contains($"{name} = filter.{name}", body);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}

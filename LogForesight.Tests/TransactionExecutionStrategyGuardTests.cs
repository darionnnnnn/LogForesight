using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 自開交易必須包在 EF 執行策略裡（回饋第 47 輪收尾體檢）。
///
/// SQL Server 後端啟用了連線重試（<c>EnableRetryOnFailure</c>），在執行策略之外呼叫
/// <c>Database.BeginTransaction()</c> 會直接擲 <c>InvalidOperationException</c>；SQLite 沒有重試策略，
/// 所以測試全綠、只有正式機才爆。交辦單回填器與「刪除指定主機日」都曾因此在 SQL Server 上必定失敗。
///
/// 守門方式刻意簡單：每個檔案裡 <c>Database.BeginTransaction(</c> 的次數不得多於
/// <c>CreateExecutionStrategy(</c> 的次數。新增自開交易的地方同時要寫出執行策略，
/// 寫法見 <c>EfJsonBlobStore.Mutate</c>。
/// </summary>
public class TransactionExecutionStrategyGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 自開交易的檔案都有對應的執行策略()
    {
        var root = FindRepoRoot();
        var sources = new[] { "LogForesight.Core", "LogForesight.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        var withTransactions = 0;
        var offenders = new List<string>();
        foreach (var path in sources)
        {
            // 去掉註解行再數：註解裡提到 CreateExecutionStrategy() 不能算一筆
            var text = string.Join("\n", File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//")));
            var transactions = Regex.Matches(text, @"Database\.BeginTransaction\(").Count;
            if (transactions == 0) continue;

            withTransactions++;
            var strategies = Regex.Matches(text, @"CreateExecutionStrategy\(").Count;
            if (transactions > strategies)
            {
                offenders.Add($"{Path.GetRelativePath(root, path)}：自開交易 {transactions} 處、執行策略 {strategies} 處");
            }
        }

        // 掃不到任何自開交易＝路徑或比對字串失效，這條測試等於沒測
        Assert.True(withTransactions > 0, "沒有掃到任何 Database.BeginTransaction，守門本身失效");
        Assert.True(offenders.Count == 0, "以下檔案有未包在執行策略裡的自開交易：\n" + string.Join("\n", offenders));
    }
}

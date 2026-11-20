using System.Text.RegularExpressions;
using LogForesight.Core.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// J11：<see cref="LogAnalysisService"/>／<see cref="RiskReportService"/> 只經 <see cref="AnalysisServiceFactory"/> 組裝，
/// 深入分析 MaxTokens 由設定一律傳入；正式碼不得另行 new。
/// </summary>
public sealed class AnalysisServiceFactoryTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    public void Dispose() { _fixture.Dispose(); GC.SuppressFinalize(this); }
    private EfAnalysisRecordStore Store() => new(_fixture.NewContext, $"factory-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(1234)]
    [InlineData(8192)]
    [InlineData(32768)]
    public void 報告服務收到設定的深入分析MaxTokens(int tokens)
    {
        var ai = new AiSettings { DeepDiveMaxTokens = tokens };

        var report = AnalysisServiceFactory.CreateReportService(new FakeAiService(), new FakeReportSink(), ai);

        Assert.Equal(tokens, report.DeepDiveMaxTokens);
    }

    [Fact]
    public void 工廠建出的分析服務帶著傳入的報告服務與主機綁定()
    {
        var ai = new AiSettings { DeepDiveMaxTokens = 4321 };
        var report = AnalysisServiceFactory.CreateReportService(new FakeAiService(), new FakeReportSink(), ai, hostOs: "linux");

        var service = AnalysisServiceFactory.Create(new EventLogService(), new FakeAiService(),
            Store(), new FakeSuppressionStore(), report, host: "H1", hostId: 7);

        Assert.Same(report, service.ReportService);
        Assert.Equal(4321, service.ReportService!.DeepDiveMaxTokens);
    }

    [Fact]
    public void 不帶報告服務時分析服務沒有報告服務()
    {
        var service = AnalysisServiceFactory.Create(new EventLogService(), new FakeAiService(),
            Store(), new FakeSuppressionStore(), reportService: null);

        Assert.Null(service.ReportService);
    }

    private static readonly Regex NewAnalysis = new(@"\bnew\s+(LogAnalysisService|RiskReportService)\s*\(", RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 正式碼只有AnalysisServiceFactory能直接建立分析與報告服務()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                scanned++;
                if (rel == "LogForesight.Core/Service/AnalysisServiceFactory.cs") continue;
                if (NewAnalysis.IsMatch(File.ReadAllText(file))) offenders.Add(rel);
            }
        }

        Assert.True(scanned > 100, $"掃描檔數異常偏少（{scanned}），守門可能已失效");
        Assert.True(offenders.Count == 0,
            "下列檔案直接 new LogAnalysisService／RiskReportService，深入分析 MaxTokens 可能漏接：" + string.Join("、", offenders));
    }

    [Fact]
    public void 工廠自身確實含建構呼叫_守門不會因掃不到而空過()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Core/Service/AnalysisServiceFactory.cs"));
        Assert.True(NewAnalysis.Matches(text).Count >= 2, "工廠內應各有一處 new LogAnalysisService／RiskReportService");
    }
}

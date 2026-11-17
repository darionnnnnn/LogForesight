using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門：交辦、靜音、觀察中這條路徑的「現在」「今天」只能取自 <see cref="TimeProvider"/>（經 <c>ClockExtensions</c>），
/// 範圍內的檔案不得直接讀真實時鐘，否則情境驅動器的假時鐘介入不了、到期與起算日又退回依真實時間。
/// 範圍清單寫在這裡；新增屬於這條路徑的檔案要有意識地加進清單。範圍外（分析、PRTG、排程等）不受此限。
/// 一次性回填 <c>WorkOrderBackfiller</c> 不在範圍內（只在升級時跑一次，不參與日常到期判定）。
/// </summary>
public class HandoffClockGuardTests
{
    /// <summary>範圍清單（相對專案根目錄、正斜線）</summary>
    internal static readonly string[] Scope =
    {
        "LogForesight.Web/Services/Mail/MailNotificationService.cs",
        "LogForesight.Web/Services/HandlingHistoryQueryService.cs",
        "LogForesight.Web/Services/IssueOwnerAdminService.cs",
        "LogForesight.Web/Services/DayHandlingCommandService.cs",
        "LogForesight.Web/Services/WorkOrderQueryService.cs",
        "LogForesight.Web/Services/IssueHandlingCommandService.cs",
        "LogForesight.Web/Services/WorkOrderBoardService.cs",
        "LogForesight.Web/Services/WorkOrderReplyService.cs",
        "LogForesight.Web/Services/WorkOrderCommandService.cs",
        "LogForesight.Web/Services/WorkOrderCandidateService.cs",
        "LogForesight.Web/Services/WorkOrderActorExtensions.cs",
        "LogForesight.Web/Services/IssueStatusValidation.cs",
        "LogForesight.Web/Services/IssueExclusionProvider.cs",
        "LogForesight.Web/Services/AuditService.cs",
        "LogForesight.Web/Services/RecordListQueryService.cs",
        "LogForesight.Web/Controllers/Api/IssueOwnersController.cs",
        "LogForesight.Core/Persistence/Sql/EfWorkOrderStore.cs",
        "LogForesight.Core/Persistence/Sql/EfIssueCaseStore.cs",
        "LogForesight.Core/Persistence/Sql/EfIssueHandlingStore.cs",
        "LogForesight.Core/Persistence/Sql/EfRecordHandlingStore.cs",
        "LogForesight.Core/Persistence/IIssueOwnerStore.cs",
        "LogForesight.Core/Persistence/WorkOrderOutboxStore.cs",
        "LogForesight.Core/Persistence/AuditLogStore.cs",
        "LogForesight.Core/Persistence/WorkOrderCoordinator.cs",
        "LogForesight.Core/Persistence/Dispatch/DispatchContext.cs",
        "LogForesight.Core/Persistence/Dispatch/WorkOrderDispatcher.cs",
        "LogForesight.Core/Models/WorkOrder.cs",
        "LogForesight.Core/Models/IssueProfile.cs",
    };

    private static readonly Regex DirectClock = new(
        @"\bDateTime(Offset)?\s*\.\s*(Now|Today|UtcNow)\b", RegexOptions.Compiled);

    private static readonly Regex ProviderRead = new(@"\.\s*(GetLocalNow|GetUtcNow)\s*\(", RegexOptions.Compiled);

    /// <summary>判準：回傳非註解行中直接讀真實時鐘的行（行號從 1 起）</summary>
    internal static List<int> DirectClockLines(string source) => MatchLines(source, DirectClock);

    private static List<int> MatchLines(string source, Regex pattern)
    {
        var hits = new List<int>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].TrimStart();
            if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("*", StringComparison.Ordinal)
                || text.StartsWith("/*", StringComparison.Ordinal)) continue;
            if (pattern.IsMatch(text)) hits.Add(i + 1);
        }
        return hits;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Theory]
    [InlineData("var x = DateTime.Now;")]
    [InlineData("if (due < DateTime.Today) return;")]
    [InlineData("var cutoff = DateTime.UtcNow.AddDays(-1);")]
    [InlineData("var o = DateTimeOffset.UtcNow;")]
    public void 判準比得到三種直接讀時鐘的寫法(string line)
    {
        Assert.Single(DirectClockLines("class C\n{\n    void M() { " + line + " }\n}"));
        Assert.Empty(DirectClockLines("    // " + line));
    }

    [Fact]
    public void 範圍內的檔案不得直接讀真實時鐘()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var rel in Scope)
        {
            var path = Path.Combine(root, rel);
            Assert.True(File.Exists(path), $"範圍清單的檔案不存在：{rel}（改名或搬動後請同步更新清單）");
            foreach (var line in DirectClockLines(File.ReadAllText(path))) offenders.Add($"{rel}:{line}");
        }

        Assert.True(offenders.Count == 0,
            "下列位置直接讀真實時鐘，請改用 TimeProvider（ClockExtensions.LocalNow／LocalToday／UtcNow）：" + string.Join("、", offenders));
    }

    [Fact]
    public void 共用函式是範圍內唯一呼叫GetLocalNow與GetUtcNow的地方()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var rel in Scope)
            foreach (var line in MatchLines(File.ReadAllText(Path.Combine(root, rel)), ProviderRead)) offenders.Add($"{rel}:{line}");

        Assert.True(offenders.Count == 0,
            "範圍內只有 ClockExtensions 可以呼叫 GetLocalNow／GetUtcNow，其餘改呼叫共用函式：" + string.Join("、", offenders));
        Assert.Contains("GetLocalNow", File.ReadAllText(Path.Combine(root, "LogForesight.Core/Time/ClockExtensions.cs")));
    }

    /// <summary>
    /// 分析與 PRTG 管線整檔有與交辦無關的 DateTime.Now（計時、檢查時間），無法整檔納入 <see cref="Scope"/>；
    /// 改守「交辦呼叫點」：呼叫 AttachCase 必須傳 clock、呼叫 DispatchContext.Build／FlushRun 不得直接讀真實時鐘。
    /// </summary>
    [Fact]
    public void 管線的交辦呼叫點一律傳入時鐘()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "LogForesight.Core", "Service"), "*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (var (name, needsClock) in new[] { ("HostDayPostProcessor.AttachCase(", true), ("DispatchContext.Build(", false), (".FlushRun(", false) })
            {
                var from = 0;
                while ((from = text.IndexOf(name, from, StringComparison.Ordinal)) >= 0)
                {
                    var end = text.IndexOf(';', from);
                    var call = text.Substring(from, end - from);
                    var bad = needsClock ? !Regex.IsMatch(call, @"\bclock\s*:|,\s*\w+\.Clock\s*\)") : DirectClock.IsMatch(call);
                    if (bad) offenders.Add($"{Path.GetFileName(path)}:{text.Take(from).Count(c => c == '\n') + 1} {name}");
                    from = end;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "交辦呼叫點漏接 backend.Clock（或直接讀真實時鐘）：" + string.Join("、", offenders));
    }
}

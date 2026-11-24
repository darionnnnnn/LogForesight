using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 守門：交辦、靜音、觀察中這條路徑的「現在」「今天」只能取自 <see cref="TimeProvider"/>（經 <c>ClockExtensions</c>），
/// 範圍內的檔案不得直接讀真實時鐘，否則情境驅動器的假時鐘介入不了、到期與起算日又退回依真實時間。
/// 範圍＝明列清單 ∪ 依檔名樣式自動納入的檔案（<see cref="NamePatterns"/>）：新增的交辦、派工、靜音、案件日同步類檔案
/// 不必有人記得加清單就會被掃到（<c>CaseDaySyncHostedService</c> 曾因不在清單內而直接讀時鐘）。範圍外（分析、PRTG、排程等）不受此限。
/// 一次性回填 <c>WorkOrderBackfiller</c>／<c>WorkOrderBackfillHostedService</c> 不在範圍內（只在升級時跑一次，不參與日常到期判定）。
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
        "LogForesight.Web/Services/AdminReminderService.cs",
        "LogForesight.Web/Services/DismissedIssueService.cs",
        "LogForesight.Web/Services/HealthAttention.cs",
        "LogForesight.Web/Services/IssueTodoQuery.cs",
        "LogForesight.Web/Services/Mail/MailIssueDigest.cs",
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
        "LogForesight.Core/Persistence/IssueRecurrence.cs",
        "LogForesight.Web/Services/ObservationRecurrence.cs",
        "LogForesight.Core/Persistence/Dispatch/DispatchContext.cs",
        "LogForesight.Core/Persistence/Dispatch/WorkOrderDispatcher.cs",
        "LogForesight.Core/Persistence/Dispatch/NightlyDispatch.cs",
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

    /// <summary>依檔名自動納入範圍的樣式（只看檔名，不含路徑）</summary>
    internal static readonly Regex NamePatterns = new(
        @"^(WorkOrder|CaseDay|Dispatch|NightlyDispatch|IssueCase|IssueOwner|DayHandling|IssueHandling|RecordHandling|Dismissed|MuteAware|EfWorkOrder|EfIssueCase|EfIssueHandling|EfRecordHandling).*\.cs$",
        RegexOptions.Compiled);

    /// <summary>不納入的檔名（一次性回填，見類別說明）</summary>
    private static readonly HashSet<string> NameExclusions = new(StringComparer.Ordinal)
    {
        "WorkOrderBackfiller.cs", "WorkOrderBackfillHostedService.cs"
    };

    private static readonly string[] ScannedDirectories =
    {
        "LogForesight.Web/Services", "LogForesight.Web/Controllers/Api",
        "LogForesight.Core/Persistence", "LogForesight.Core/Persistence/Sql", "LogForesight.Core/Persistence/Dispatch"
    };

    /// <summary>實際掃描的檔案：明列清單 ∪ 掃描目錄內檔名符合樣式者</summary>
    internal static IReadOnlyList<string> EffectiveScope(string root)
    {
        var set = new SortedSet<string>(Scope, StringComparer.Ordinal);
        foreach (var dir in ScannedDirectories)
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (NamePatterns.IsMatch(name) && !NameExclusions.Contains(name)) set.Add(dir + "/" + name);
            }
        return set.ToList();
    }

    [Theory]
    [InlineData("CaseDaySyncHostedService.cs", true)]
    [InlineData("WorkOrderSomethingNew.cs", true)]
    [InlineData("WorkOrderBackfiller.cs", false)]
    [InlineData("PrtgFetchService.cs", false)]
    public void 檔名樣式會自動納入新的交辦類檔案(string name, bool expected)
    {
        Assert.Equal(expected, NamePatterns.IsMatch(name) && !NameExclusions.Contains(name));
    }

    [Fact]
    public void 範圍內的檔案不得直接讀真實時鐘()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var rel in Scope)
            Assert.True(File.Exists(Path.Combine(root, rel)), $"範圍清單的檔案不存在：{rel}（改名或搬動後請同步更新清單）");
        foreach (var rel in EffectiveScope(root))
        {
            foreach (var line in DirectClockLines(File.ReadAllText(Path.Combine(root, rel)))) offenders.Add($"{rel}:{line}");
        }

        Assert.True(offenders.Count == 0,
            "下列位置直接讀真實時鐘，請改用 TimeProvider（ClockExtensions.LocalNow／LocalToday／UtcNow）：" + string.Join("、", offenders));
    }

    /// <summary>
    /// A6 反例：在暫時目錄放一個「清單外、但檔名符合樣式」且直讀系統時鐘的新檔，
    /// 守門必須把它納入範圍並判為違規（證明守門對新檔真的會紅，不只是現有程式碼剛好乾淨）。
    /// </summary>
    [Fact]
    public void 範圍內新增直讀系統時鐘的檔案_守門判定為違規()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-clock-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in ScannedDirectories) Directory.CreateDirectory(Path.Combine(root, dir));
            const string rel = "LogForesight.Web/Services/WorkOrderTemporaryNewFeature.cs";
            File.WriteAllText(Path.Combine(root, rel),
                "namespace X;\nclass WorkOrderTemporaryNewFeature\n{\n    DateTime Due() => DateTime.Now.AddDays(1);\n}\n");
            // 同目錄放一個範圍外的檔案：它即使直讀時鐘也不該被納入
            File.WriteAllText(Path.Combine(root, "LogForesight.Web/Services/PrtgUnrelated.cs"), "class P { DateTime T() => DateTime.Now; }");

            Assert.DoesNotContain(rel, Scope);
            var scope = EffectiveScope(root);
            Assert.Contains(rel, scope);
            Assert.DoesNotContain("LogForesight.Web/Services/PrtgUnrelated.cs", scope);

            var offenders = scope.Where(r => File.Exists(Path.Combine(root, r))).SelectMany(r => DirectClockLines(File.ReadAllText(Path.Combine(root, r))).Select(l => $"{r}:{l}")).ToList();
            Assert.Equal(new[] { rel + ":4" }, offenders);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 共用函式是範圍內唯一呼叫GetLocalNow與GetUtcNow的地方()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var rel in EffectiveScope(root))
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

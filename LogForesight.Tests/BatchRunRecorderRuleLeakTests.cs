using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="BatchRunRecorder"/> 附掛的 NLog 規則在卸除後必須一併移除，
/// 否則每趟執行留下一條 Targets.Count==0 的空殼規則，長時間執行的站台會無限累積。
/// 這裡用獨立的 <see cref="LoggingConfiguration"/>（Attach/Detach 的測試用覆寫參數），
/// 不動全域設定，避免與並行的其他測試互相干擾。
/// </summary>
public class BatchRunRecorderRuleLeakTests
{
    private static LoggingConfiguration BuildConfigWithFinalRules()
    {
        var config = new LoggingConfiguration();
        var memory = new MemoryTarget("mem");
        config.AddTarget(memory);
        // 對應 nlog.config 的兩條 final 規則：沒有 writeTo，Targets.Count==0，但是有意為之
        // 注意 LoggingRule(string) 建構子的參數是規則名稱而非 logger 名稱，要用屬性指定
        foreach (var pattern in new[] { "Microsoft.*", "System.Net.Http.*" })
        {
            var rule = new LoggingRule { LoggerNamePattern = pattern, Final = true };
            rule.EnableLoggingForLevels(LogLevel.Trace, LogLevel.Info);
            config.LoggingRules.Add(rule);
        }
        config.AddRule(LogLevel.Info, LogLevel.Fatal, memory);
        return config;
    }

    private static BatchRunRecorder.BatchRunNLogTarget NewTarget(BatchRunStore store, long id, Action<string, string>? onRecorded = null)
        => new(store, id, "tok" + id, onRecorded ?? ((_, _) => { }));

    private static bool FinalRulesStillPresent(LoggingConfiguration config)
        => config.LoggingRules.Count(r => r.Final && r.Targets.Count == 0
               && (r.LoggerNamePattern == "Microsoft.*" || r.LoggerNamePattern == "System.Net.Http.*")) == 2;

    [Fact]
    public void 附掛卸除五次後規則數回到起點且final規則仍在()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var config = BuildConfigWithFinalRules();
        var start = config.LoggingRules.Count;

        for (var i = 0; i < 5; i++)
        {
            var target = NewTarget(store, 100 + i);
            target.Attach(config);
            Assert.Equal(start + 1, config.LoggingRules.Count);
            target.Detach(config);
        }

        Assert.Equal(start, config.LoggingRules.Count);
        Assert.True(FinalRulesStillPresent(config));
    }

    [Fact]
    public void 附掛時清掉舊版殘留空殼但不動final規則()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var config = BuildConfigWithFinalRules();
        var start = config.LoggingRules.Count;

        for (var i = 0; i < 3; i++)
        {
            var shell = new LoggingRule { LoggerNamePattern = "*" };
            shell.EnableLoggingForLevels(LogLevel.Warn, LogLevel.Fatal);
            config.LoggingRules.Add(shell);
        }
        Assert.Equal(start + 3, config.LoggingRules.Count);

        var target = NewTarget(store, 200);
        target.Attach(config);
        // 3 條空殼被清掉，只剩自己新增的 1 條
        Assert.Equal(start + 1, config.LoggingRules.Count);
        Assert.True(FinalRulesStillPresent(config));

        target.Detach(config);
        Assert.Equal(start, config.LoggingRules.Count);
        Assert.True(FinalRulesStillPresent(config));
    }

    [Fact]
    public void 兩個target交錯附掛卸除後規則數回到起點()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var config = BuildConfigWithFinalRules();
        var start = config.LoggingRules.Count;

        var a = NewTarget(store, 301);
        var b = NewTarget(store, 302);
        a.Attach(config);
        b.Attach(config);
        Assert.Equal(start + 2, config.LoggingRules.Count);
        a.Detach(config);
        Assert.Equal(start + 1, config.LoggingRules.Count);
        Assert.Contains(config.LoggingRules, r => r.Targets.Contains(b));
        b.Detach(config);

        Assert.Equal(start, config.LoggingRules.Count);
        Assert.True(FinalRulesStillPresent(config));
    }

    [Fact]
    public async Task 交錯的兩個recorderA卸除後B仍收得到Warn()
    {
        LogManager.Configuration ??= new LoggingConfiguration();
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("RuleLeakTestLogger");

        // 兩趟執行各自在獨立的非同步流程（正式環境即如此）；同一流程內巢狀建立會共用 ScopeContext 同名屬性
        var aCreated = new TaskCompletionSource();
        var bCreated = new TaskCompletionSource();
        var aDisposed = new TaskCompletionSource();
        var aTask = Task.Run(async () =>
        {
            var a = new BatchRunRecorder(store, "host-a", Array.Empty<string>());
            aCreated.SetResult();
            await bCreated.Task;
            a.Finish(0);
            a.Dispose();
            aDisposed.SetResult();
        });
        await aCreated.Task;
        var bTask = Task.Run(async () =>
        {
            var b = new BatchRunRecorder(store, "host-b", Array.Empty<string>());
            bCreated.SetResult();
            await aDisposed.Task;
            logger.Warn("A 卸除之後 B 的警告");
            b.Finish(0);
            var id = b.RunId;
            b.Dispose();
            return id;
        });
        await aTask;
        var runId = await bTask;

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.WarnCount);
    }
}

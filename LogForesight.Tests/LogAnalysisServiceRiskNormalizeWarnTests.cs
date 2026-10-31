using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>C-5：AI 回覆的風險等級無法歸一化時要出聲（Warn），但不改判定。</summary>
[Collection("NLogGlobalConfig")]
public class LogAnalysisServiceRiskNormalizeWarnTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public async Task AI風險等級歸一化失敗時記Warn且風險維持規則判定()
    {
        var ai = new FakeAiService
        {
            NextContent = """{"risk_level":"不明","headline":"h","story":"s","trend_story":"","action":"a"}"""
        };
        var service = new LogAnalysisService(new EventLogService(), ai, new EfAnalysisRecordStore(_fx.NewContext, "test"),
            new FakeSuppressionStore(), reportService: new RiskReportService(ai, new FakeReportSink()), riskyEventStore: null);
        var pending = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 7, 31), HostId = 1, Host = "HOST-A", RiskLevel = "中", AiPending = true,
            TopIssues = new List<LogIssueSignature>(), TrendAlerts = new List<string>(),
            CorrelationAlerts = new List<string>(), UncoveredChecks = new List<string>()
        };

        var target = new MemoryTarget("lf-risk-normalize-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Warn, target, typeof(LogAnalysisService).FullName);
        LogManager.ReconfigExistingLoggers();
        try
        {
            var outcome = await service.RetryAiAsync(pending, historyDays: 14);

            Assert.Equal("中", outcome.RiskLevel);
            Assert.Contains(target.Logs, l => l.Contains("無法歸一化") && l.Contains("不明") && l.Contains("2026-07-31"));
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }
}

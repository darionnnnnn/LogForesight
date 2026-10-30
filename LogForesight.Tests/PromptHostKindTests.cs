using LogForesight.Core.Analysis;
using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="PromptGuidelines.HostKindText"/>：prompt 的主機種類稱呼依 hostOs 切換，
/// Linux 主機不得被稱為 Windows Server（syslog 不是 Windows 事件日誌）。
/// </summary>
public class PromptHostKindTests
{
    [Theory]
    [InlineData("linux")]
    [InlineData("Linux")]
    [InlineData("LINUX")]
    public void HostKindText_Linux_ReturnsLinuxHost(string hostOs)
    {
        Assert.Equal("Linux 主機", PromptGuidelines.HostKindText(hostOs));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("Windows")]
    [InlineData("")]
    [InlineData(null)]
    public void HostKindText_NonLinux_ReturnsWindowsServer(string? hostOs)
    {
        Assert.Equal("Windows Server", PromptGuidelines.HostKindText(hostOs));
    }

    [Fact]
    public void SystemPromptFor_Linux_ContainsLinuxHostNotWindowsServer()
    {
        var prompt = AnalysisPromptBuilder.SystemPromptFor("linux");
        Assert.Contains("Linux 主機", prompt);
        Assert.DoesNotContain("Windows Server", prompt);
    }

    [Fact]
    public void SystemPromptFor_Null_ContainsWindowsServer()
    {
        Assert.Contains("Windows Server", AnalysisPromptBuilder.SystemPromptFor(null));
        Assert.Equal(AnalysisPromptBuilder.SystemPrompt, AnalysisPromptBuilder.SystemPromptFor(null));
    }

    /// <summary>記錄 system prompt 的 AI 替身（共用 FakeAiService 不記 system prompt）。</summary>
    private sealed class SystemPromptRecordingAi : IAiService
    {
        private readonly FakeAiService _inner = new();
        public List<string?> SystemPrompts { get; } = new();
        public string NextContent { set => _inner.NextContent = value; }

        public Task<AiResponse> ChatAsync(string prompt, string? systemPrompt = null, bool jsonMode = false,
            string? model = null, double temperature = 0.2, int? maxTokens = null, string label = "chat",
            CancellationToken ct = default)
        {
            lock (SystemPrompts) SystemPrompts.Add(systemPrompt);
            return _inner.ChatAsync(prompt, systemPrompt, jsonMode, model, temperature, maxTokens, label, ct);
        }

        public Task<AiJsonResult<T>> ChatJsonAsync<T>(string prompt, string? systemPrompt = null,
            Func<T, bool>? validate = null, string? model = null, double temperature = 0.2, int? maxTokens = null,
            string label = "chat-json", CancellationToken ct = default) where T : class
        {
            lock (SystemPrompts) SystemPrompts.Add(systemPrompt);
            return _inner.ChatJsonAsync(prompt, systemPrompt, validate, model, temperature, maxTokens, label, ct);
        }
    }

    private static LogIssueSignature OtherIssue() => new()
    {
        LogName = "Application", Source = "demo", EventId = 9999,
        EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High,
        Category = IssueCategory.Other, Count = 3
    };

    [Theory]
    [InlineData("linux", "Linux 主機")]
    [InlineData(null, "Windows Server")]
    public async Task RiskReportService_DeepDive_SystemPromptFollowsHostOs(string? hostOs, string expected)
    {
        var ai = new SystemPromptRecordingAi();
        ai.NextContent = """{"analyses":[{"problem":"p","likely_causes":["c"],"impact":"i","next_steps":["n"]}]}""";
        var service = new RiskReportService(ai, new FakeReportSink(), hostOs: hostOs);
        var record = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 7, 31), Host = "H", RiskLevel = "高", AiAnalyzed = true,
            TopIssues = new List<LogIssueSignature> { OtherIssue() }, Summary = "s"
        };

        await service.PrepareAsync(record, new List<EventLogEntryData>());

        var system = Assert.Single(ai.SystemPrompts);
        Assert.Contains(expected, system);
        if (hostOs == "linux") Assert.DoesNotContain("Windows Server", system);
    }

    [Fact]
    public async Task LogAnalysisService_RetryAi_Linux_MainSystemPromptContainsLinuxHost()
    {
        using var fx = new EfSqliteFixture();
        var ai = new SystemPromptRecordingAi();
        ai.NextContent = """{"risk_level":"中","headline":"h","story":"s","trend_story":"","action":"a"}""";
        var service = new LogAnalysisService(new EventLogService(), ai,
            new EfAnalysisRecordStore(fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(ai, new FakeReportSink(), hostOs: "linux"),
            riskyEventStore: null, hostOs: "linux");
        var pending = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 7, 31), HostId = 1, Host = "HOST-L", RiskLevel = "中", AiPending = true,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error,
                        Severity = IssueSeverity.High, Category = IssueCategory.Storage,
                        RuleId = "builtin-disk-error", Count = 3 }
            },
            TrendAlerts = new List<string>(), CorrelationAlerts = new List<string>(),
            UncoveredChecks = new List<string>()
        };

        await service.RetryAiAsync(pending, historyDays: 14);

        Assert.NotEmpty(ai.SystemPrompts);
        Assert.Contains("Linux 主機", ai.SystemPrompts[0]);
        Assert.DoesNotContain("Windows Server", ai.SystemPrompts[0]);
    }
}

using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>J12：恢復服務的游標讀不出或格式不對時視為新游標從頭走、留一行警告，不得永遠擲例外；正常游標照舊接續。</summary>
[Collection("NLogGlobalConfig")]
public sealed class WorkflowRecoveryCursorCorruptionTests : IDisposable
{
    private const string LoggerName = "LogForesight.Web.Services.HostDayWorkflowRecoveryHostedService";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-recovery-cursor-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly MemoryTarget _target = new("lf-recovery-cursor-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
    private readonly LoggingConfiguration _config;

    public WorkflowRecoveryCursorCorruptionTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
        _config = LogManager.Configuration ??= new LoggingConfiguration();
        _config.AddTarget(_target);
        _config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, _target, LoggerName);
        LogManager.ReconfigExistingLoggers();
    }

    public void Dispose()
    {
        _config.RemoveTarget(_target.Name);
        _config.LoggingRules.Where(r => r.Targets.Contains(_target)).ToList().ForEach(r => _config.LoggingRules.Remove(r));
        LogManager.ReconfigExistingLoggers();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Today = new(2026, 10, 6);

    private (HostDayWorkflowRecoveryHostedService Service, List<long> Afters) NewService()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value => { value.RetentionDays = 10; value.PrtgEnabled = false; });
        var afters = new List<long>();
        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (_, _, after, _) =>
            {
                afters.Add(after);
                var id = after + 1;
                return new WorkflowRecoveryPage([], [new WorkflowRecoveryWaitingHostDay(id, 42, Today.AddDays(-1),
                    "recovery-payload-over-limit", WorkflowRecoveryPage.MaximumPayloadPrefixCharacters,
                    0, DateTime.UtcNow)], 1, id, WorkflowRecoveryPage.MaximumPayloadBytes);
            }
        };
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var service = new HostDayWorkflowRecoveryHostedService(query, _backend, settings, new Ai(),
            new HostDayWorkflowService(new HostDayWorkflowStore(_backend)), budget, NullAiRequestGate.Instance);
        return (service, afters);
    }

    private int CursorWarnings() => _target.Logs.Count(l => l.StartsWith("Warn|") && l.Contains("cursor is unreadable"));

    private void WriteRaw(string raw) =>
        _backend.Blob(HostDayWorkflowStore.RecoveryCheckpointBlobKey).Mutate(_ => (raw, true));

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"字串\"")]
    public async Task 游標損壞時視為新游標從頭走並留一行警告(string corrupt)
    {
        WriteRaw(corrupt);
        var (service, afters) = NewService();

        await service.RunSliceAsync(CancellationToken.None, Today);

        Assert.Equal(0, afters[0]);
        Assert.Equal(1, CursorWarnings());
        // 損壞內容已被新游標覆蓋，下一輪不再警告
        var raw = _backend.Blob(HostDayWorkflowStore.RecoveryCheckpointBlobKey).Read();
        using var doc = JsonDocument.Parse(raw!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var (second, _) = NewService();
        await second.RunSliceAsync(CancellationToken.None, Today);
        Assert.Equal(1, CursorWarnings());
    }

    [Fact]
    public async Task 游標為空時從頭走且不警告()
    {
        var (service, afters) = NewService();

        await service.RunSliceAsync(CancellationToken.None, Today);

        Assert.Equal(0, afters[0]);
        Assert.Equal(0, CursorWarnings());
    }

    [Fact]
    public async Task 正常游標重啟後照舊接續且不警告()
    {
        var (first, firstAfters) = NewService();
        await first.RunSliceAsync(CancellationToken.None, Today);
        var advanced = firstAfters.Max() + 1;

        var (restarted, afters) = NewService();
        await restarted.RunSliceAsync(CancellationToken.None, Today);

        Assert.Equal(advanced, afters[0]);
        Assert.Equal(0, CursorWarnings());
    }

    private sealed class Ai : IWebAiService
    {
        public bool Available => false;
        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class => Task.FromResult<T?>(null);
        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) => Task.FromResult<string?>(null);
    }
}

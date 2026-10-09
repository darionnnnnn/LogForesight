using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureModeReplayHostedServiceTests : IDisposable
{
    private const long HostId = 94_001;
    private const long SensorId = 94_101;
    private const string SourceGeneration = "1234567890abcdef1234567890abcdef";
    private const string ResourceGeneration = "abcdef1234567890abcdef1234567890";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-mode-replay-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgResourcePressureModeReplayHostedServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "mode-replay.db")}"
        }, _directory);
    }

    [Fact]
    public void DisableReplayWithdrawsEveryRetainedDayWithoutProfileOrPrtgSettingsAndKeepsModeVersionStable()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = false; value.PrtgUrl = string.Empty; });
        var manual = new LogIssueSignature
        {
            LogName = "System", Source = "manual", EventId = 41, EventKey = "manual:keep",
            Severity = IssueSeverity.Medium, Count = 1
        };
        var days = new[] { new DateTime(2026, 10, 4), new DateTime(2026, 10, 5) };
        foreach (var day in days)
        {
            var prtg = PressureSignature(day);
            _backend.RecordStore(new HostKey { HostId = HostId, HostName = "mode-replay-host" }).Append(
                new DailyAnalysisRecord
                {
                    HostId = HostId, Host = "mode-replay-host", Date = day,
                    LogSource = AnalysisLogSource.Netiq, RiskLevel = RiskLevels.High, RiskBasis = "PRTG",
                    PrtgBaselineRiskLevel = RiskLevels.Low, PrtgBaselineRiskBasis = "NetIQ baseline",
                    TopIssues = [prtg, manual]
                });
        }

        var modeStore = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(Grant()));
        Assert.True(modeStore.DisableFormalMode(HostId, SensorId, DateTime.UtcNow));
        var queued = modeStore.ReadHostSnapshot(HostId);
        var job = Assert.Single(queued.ReplayJobs);
        var versionAtEnqueue = queued.BlobVersion;

        var service = new PrtgResourcePressureModeReplayHostedService(_backend, settings,
            new BackgroundWorkGate(new SchedulerRunState(), TimeSpan.Zero), new DataVersionStamp(), null!,
            new FakeWebAi { Available = false }, null!, null!, null!, null!, null!);
        service.RunOnce(CancellationToken.None);

        var finalMode = modeStore.ReadHostSnapshot(HostId);
        Assert.Equal(versionAtEnqueue, finalMode.BlobVersion);
        var progress = new PrtgResourcePressureModeReplayProgressStore(_backend.Blob(
            PrtgResourcePressureModeReplayProgressStore.BlobKey(HostId))).Find(job);
        Assert.Equal(PrtgModeReplayStatus.Completed, progress?.Status);
        foreach (var day in days)
        {
            var row = Assert.Single(_backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "mode-replay-host" }).ReadRecent(day, 1));
            Assert.DoesNotContain(row.TopIssues, PrtgFindingMapper.IsPrtg);
            Assert.Contains(row.TopIssues, issue => issue.EventKey == manual.EventKey);
            Assert.Equal(RiskLevels.Low, row.RiskLevel);
        }
    }

    [Fact]
    public void ProgressRetryKeepsOriginalDeadlineAndNeverChangesModeAuthorityVersion()
    {
        var modeStore = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(Grant()));
        var now = DateTime.UtcNow;
        Assert.True(modeStore.DisableFormalMode(HostId, SensorId, now));
        var snapshot = modeStore.ReadHostSnapshot(HostId);
        var job = Assert.Single(snapshot.ReplayJobs);
        var modeVersion = snapshot.BlobVersion;
        var progressStore = new PrtgResourcePressureModeReplayProgressStore(_backend.Blob(
            PrtgResourcePressureModeReplayProgressStore.BlobKey(HostId)));

        progressStore.Update(job, old => old with
        {
            Status = PrtgModeReplayStatus.Waiting, LastReason = "fixture-waiting",
            AfterRecordId = 8, UnresolvedHostDays = 2
        });
        var first = Assert.IsType<PrtgResourcePressureModeReplayProgress>(progressStore.Find(job));
        progressStore.Update(job, old => old with { Status = PrtgModeReplayStatus.Running });
        var retried = Assert.IsType<PrtgResourcePressureModeReplayProgress>(progressStore.Find(job));

        Assert.Equal(job.DueAtUtc, first.DueAtUtc);
        Assert.Equal(first.DueAtUtc, retried.DueAtUtc);
        Assert.Equal(8, retried.AfterRecordId);
        Assert.Equal(2, retried.UnresolvedHostDays);
        Assert.Equal(modeVersion, modeStore.ReadHostSnapshot(HostId).BlobVersion);
    }

    private static PrtgResourcePressureModeGrant Grant()
    {
        var now = DateTime.UtcNow;
        return new(HostId, SensorId, PrtgResourceFamily.Cpu, Hash("profile"),
            PrtgResourcePressureEvaluator.RulesVersion, SourceGeneration, ResourceGeneration,
            "channel-generation-v1", "1", "semantic-v1", "strategy-v1", "cpu-rule", Hash("rule"),
            Hash("trial"), true, true, now, now.AddHours(24), TrialConsumed: true);
    }

    private static LogIssueSignature PressureSignature(DateTime day)
    {
        var rule = KnownIssueSeed.CreateRules().Single(item =>
            item.PrtgRuleCode == PrtgRuleEvaluator.RuleResourceCpuPressure);
        var finding = new PrtgFinding(95_001, SensorId, PrtgRuleEvaluator.RuleResourceCpuPressure,
            "CPU pressure fixture", 95, rule)
        {
            SourceGeneration = SourceGeneration,
            ResourceGeneration = ResourceGeneration,
            SensorCategory = PrtgSensorCategories.Cpu
        };
        return PrtgFindingMapper.ToSignature(finding, day, HostId);
    }

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

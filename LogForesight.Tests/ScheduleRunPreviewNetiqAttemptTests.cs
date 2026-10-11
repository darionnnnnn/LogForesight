using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class ScheduleRunPreviewNetiqAttemptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LogForesight-preview-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public ScheduleRunPreviewNetiqAttemptTests()
    {
        Directory.CreateDirectory(_root);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "preview.db")};Pooling=False"
        }, _root);
    }

    [Fact]
    public void Missing_only_preview_includes_hosts_with_failed_receipts_and_excludes_successful_receipts()
    {
        SeedParentRows(new long[] { 1, 2, 3, 4, 5 }, DateTime.Today.AddDays(-1));
        NetiqSourceAttempt.RecordBatch(_backend, new long[] { 1, 2, 3, 4 }, DateTime.Today.AddDays(-1), 10, "failed");
        NetiqSourceAttempt.RecordBatch(_backend, new long[] { 5 }, DateTime.Today.AddDays(-1), 10, "success");

        var response = Controller(new[] { 1L, 2, 3, 4, 5 }).RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1);

        Assert.True(response.Success);
        Assert.Equal(4, response.Data!.HostCount);
    }

    [Fact]
    public void Missing_only_preview_counts_all_pipeline_failure_receipt_states()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedParentRows(new long[] { 1, 2, 3, 4, 5, 6 }, day);
        var statuses = new[] { "failed", "running", "partial", "retained-incomplete", "stopped" };
        for (var i = 0; i < statuses.Length; i++)
            NetiqSourceAttempt.RecordBatch(_backend, new[] { (long)i + 1 }, day, 11, statuses[i]);
        NetiqSourceAttempt.RecordBatch(_backend, new long[] { 6 }, day, 11, "success");

        var response = Controller(new long[] { 1, 2, 3, 4, 5, 6 }).RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1);

        Assert.True(response.Success);
        Assert.Equal(5, response.Data!.HostCount);
    }

    [Fact]
    public void Missing_only_preview_counts_local_missing_host_and_failed_Netiq_but_not_successful_Netiq()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedParentRows(new long[] { 1, 2 }, day);
        NetiqSourceAttempt.RecordBatch(_backend, new long[] { 1 }, day, 12, "failed");
        NetiqSourceAttempt.RecordBatch(_backend, new long[] { 2 }, day, 12, "success");

        var response = Controller(new long[] { 1, 2 }).RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1);

        Assert.True(response.Success);
        Assert.Equal(1, response.Data!.HostCount);
    }

    [Fact]
    public void Missing_only_preview_returns_explicit_error_for_malformed_receipt()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedParentRows(new long[] { 1 }, day);
        using (var db = _backend.CreateContext())
        {
            db.LogLines.Add(new LogLineRow
            {
                LogKey = NetiqSourceAttempt.LogKey(day, 1),
                Line = "{malformed",
                CreatedAt = DateTime.Now
            });
            db.SaveChanges();
        }

        var response = Controller(new long[] { 1 }).RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1);

        Assert.False(response.Success);
        Assert.Equal("netiq_attempt_ledger_invalid", response.Error!.Code);
        Assert.Contains("無法準確預覽", response.Error.Message);
        Assert.Null(response.Data);
    }

    private void SeedParentRows(IEnumerable<long> ids, DateTime date)
    {
        var store = _backend.RecordStore();
        store.Append(new DailyAnalysisRecord
        {
            Date = date,
            HostId = 0,
            Host = Environment.MachineName,
            RiskLevel = "低",
            AiAnalyzed = false,
            AiPending = false
        });
        foreach (var id in ids)
        {
            store.Append(new DailyAnalysisRecord
            {
                Date = date,
                HostId = id,
                Host = "preview-host-" + id,
                LogSource = AnalysisLogSource.Netiq,
                RiskLevel = "低",
                AiAnalyzed = false,
                AiPending = false
            });
        }
    }

    private ScheduleController Controller(IEnumerable<long> ids)
    {
        var hosts = new FakeHostStore();
        foreach (var id in ids)
            hosts.Upsert(new WebHost
            {
                HostId = id,
                HostName = "preview-host-" + id,
                IpAddress = $"10.0.0.{id}",
                Os = WebHost.OsWindows,
                SentinelId = 1,
                NetiqServer = "S1",
                Source = "netiq",
                Active = true
            });
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });
        var settings = new FakeSystemSettingsStore();
        return new ScheduleController(
            new ScheduleOptionsStore(_backend.Blob("schedule_options")), null!, new SchedulerRunState(),
            hosts, sentinels, new RecordingAuditService(), new FakeCurrentUser(), new FakeUserStore(),
            _backend.RecordStore(), settings, new UserDisplayNameService(settings), null!, new AiAnalysisRunState(),
            storageBackend: _backend);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

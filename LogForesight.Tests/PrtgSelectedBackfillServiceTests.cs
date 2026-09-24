using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSelectedBackfillServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lf-selected-backfill-{Guid.NewGuid():N}");
    private readonly StorageBackend _backend;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly HostStore _hosts;
    private readonly PrtgBackfillService _service;
    private readonly PrtgBackfillRunState _runState;

    public PrtgSelectedBackfillServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _hosts.Upsert(new WebHost { HostName = "host-11", Active = true });
        _settings.Update(s =>
        {
            s.PrtgSensorTypeWhitelist = new List<string> { "diskfree" };
            s.PrtgRetentionDays = 180;
            s.RetentionDays = 180;
            s.PrtgEnabled = true;
            s.PrtgUrl = "http://127.0.0.1:1";
            s.PrtgApiTokenEnc = "test-token";
        });
        var state = _runState = new PrtgBackfillRunState();
        var scheduler = new SchedulerRunState();
        var syncState = new PrtgStructureSyncRunState();
        var sync = new PrtgStructureSyncService(_settings, _backend, syncState, scheduler, _hosts,
            new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)), state,
            new FakeSentinelStore(), new DataVersionStamp());
        _service = new PrtgBackfillService(_settings, _backend, state, new PrtgProbeRunState(), _hosts,
            scheduler, syncState, new FakeSentinelStore(), sync);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private PrtgSelectedBackfillRequest Request(DateTime day) => new()
    {
        HostIds = new long[] { 1 }, FromDate = day, ToDate = day
    };

    [Fact]
    public void Preview_單一主機單日只估算有效歷史映射上的白名單感測器()
    {
        var day = DateTime.Today.AddDays(-1);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 200, HostId = 22, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 100, Name = "Disk", SensorType = "diskfree" },
            new PrtgSensorRow { Objid = 1002, DeviceObjid = 100, Name = "Other type", SensorType = "cpu" },
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 200, Name = "Other", SensorType = "diskfree" }
        }, DateTime.Now);
        var yesterdayStart = day.Date;
        using (var context = new LfDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False").Options))
        {
            context.PrtgValues.AddRange(
                new PrtgValueRow { SensorObjid = 1001, PeriodStart = yesterdayStart.AddHours(2), Quality = "sampled" },
                new PrtgValueRow { SensorObjid = 1001, PeriodStart = yesterdayStart.AddHours(3), Quality = "ok" },
                new PrtgValueRow { SensorObjid = 1001, PeriodStart = yesterdayStart.AddDays(1), Quality = "sampled" },
                new PrtgValueRow { SensorObjid = 1002, PeriodStart = yesterdayStart.AddHours(4), Quality = "sampled" },
                new PrtgValueRow { SensorObjid = 2001, PeriodStart = yesterdayStart.AddHours(5), Quality = "sampled" });
            context.SaveChanges();
        }

        var preview = _service.PreviewSelected(Request(day));

        Assert.Equal(1, preview.EstimatedRequests);
        Assert.Equal(1, preview.DaysWithTargets);
        Assert.Equal(1, preview.EstimatedSampledRowsToReplace);
        Assert.True(preview.HasTargets);
    }

    [Fact]
    public void Preview_沒有既存sampled列時替換估計為零()
    {
        var day = DateTime.Today.AddDays(-2);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 100, Name = "Disk", SensorType = "diskfree" }
        }, DateTime.Now);

        var preview = _service.PreviewSelected(Request(day));

        Assert.Equal(0, preview.EstimatedSampledRowsToReplace);
    }

    [Fact]
    public void Preview_缺少當日對應明確回報零目標()
    {
        var preview = _service.PreviewSelected(Request(DateTime.Today.AddDays(-2)));

        Assert.Equal(0, preview.EstimatedRequests);
        Assert.False(preview.HasTargets);
        Assert.Contains("不會發出 PRTG 請求", preview.Message);
    }

    [Fact]
    public void TryStartSelected_僅選取歷史日有對應時預覽與啟動成功()
    {
        var day = DateTime.Today.AddDays(-100);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 100, Name = "Disk", SensorType = "diskfree" }
        }, DateTime.Now);

        var started = _service.TryStartSelected(Request(day), out var preview, out var error, out var isConflict);

        Assert.True(preview.HasTargets);
        Assert.Equal(day.Date, preview.FromDate);
        Assert.True(started, error);
        Assert.False(isConflict);
    }

    [Fact]
    public void TryStartSelected_沒有選取日期目標時拒絕啟動()
    {
        var started = _service.TryStartSelected(Request(DateTime.Today.AddDays(-100)),
            out var preview, out var error, out var isConflict);

        Assert.False(preview.HasTargets);
        Assert.False(started);
        Assert.False(isConflict);
        Assert.Contains("不會發出 PRTG 請求", error);
    }

    [Fact]
    public void Preview_空白名單及超出保留期間拒絕()
    {
        _settings.Update(s => s.PrtgSensorTypeWhitelist.Clear());
        var emptyWhitelist = Assert.Throws<DomainException>(() => _service.PreviewSelected(Request(DateTime.Today.AddDays(-1))));
        Assert.Contains("白名單為空", emptyWhitelist.Message);

        _settings.Update(s => s.PrtgSensorTypeWhitelist.Add("diskfree"));
        var outsideRetention = Assert.Throws<DomainException>(() =>
            _service.PreviewSelected(Request(DateTime.Today.AddDays(-181))));
        Assert.Contains("保留範圍", outsideRetention.Message);
    }

    [Fact]
    public void 重載狀態可辨識指定回填並以runId停止()
    {
        Assert.True(_runState.TryBeginIdentifiedRun("selected", out var token));
        var runId = _runState.GetRunIdentity().RunId;

        var status = _service.GetStatus();
        Assert.True(status.IsRunning);
        Assert.Equal("selected", status.RunKind);
        Assert.Equal(runId, status.RunId);
        Assert.True(_service.TryCancelSelected(status.RunId));
        Assert.True(token.IsCancellationRequested);
        _runState.FinishRun(false, cancelled: true);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("tail")]
    public void 局部停止不會取消一般或接續回填(string runKind)
    {
        Assert.True(_runState.TryBeginIdentifiedRun(runKind, out var token));
        var runId = _runState.GetRunIdentity().RunId;

        Assert.False(_service.TryCancelSelected(runId));
        Assert.False(token.IsCancellationRequested);
        _runState.FinishRun(false, cancelled: false);
    }
}

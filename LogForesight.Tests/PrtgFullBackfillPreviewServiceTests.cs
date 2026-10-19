using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgFullBackfillPreviewServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lf-full-backfill-preview-{Guid.NewGuid():N}");
    private readonly StorageBackend _backend;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly HostStore _hosts;
    private readonly PrtgBackfillRunState _runState = new();
    private readonly SchedulerRunState _scheduler = new();
    private readonly ManualTimeProvider _clock;
    private readonly PrtgBackfillService _service;
    private readonly List<(long SensorId, DateTime Day)> _offlineHistoricRequests = new();

    public PrtgFullBackfillPreviewServiceTests()
    {
        _clock = new ManualTimeProvider(DateTime.Today.AddHours(12));
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _hosts.Upsert(new WebHost { HostName = "host-11", Active = true });
        _hosts.Upsert(new WebHost { HostName = "host-22", Active = true });
        _settings.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = "http://127.0.0.1:1";
            settings.PrtgApiTokenEnc = "test-token";
            settings.PrtgBackfillDays = 2;
            settings.PrtgSensorTypeWhitelist = new List<string> { "diskfree" };
            settings.PrtgValueFetchScope = PrtgValueFetchScope.Triggered;
        });
        var syncState = new PrtgStructureSyncRunState();
        var sentinels = new FakeSentinelStore();
        var sync = new PrtgStructureSyncService(_settings, _backend, syncState, _scheduler, _hosts,
            new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)), _runState,
            sentinels, new DataVersionStamp());
        _service = new PrtgBackfillService(_settings, _backend, _runState, new PrtgProbeRunState(), _hosts,
            _scheduler, syncState, sentinels, sync, _clock, _ => CreateOfflinePrtgClient(_offlineHistoricRequests));
        SeedTwoDays();
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(_directory); attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(50); }
        }
    }

    [Fact]
    public void PreviewFull_UsesStoredDaysAndActualDailyTargetsWithoutOpeningRun()
    {
        var preview = _service.PreviewFull();

        Assert.Equal(DateTime.Today.AddDays(-2), preview.FromDate);
        Assert.Equal(DateTime.Today.AddDays(-1), preview.ToDate);
        Assert.Equal(2, preview.DayCount);
        Assert.Equal(3, preview.EstimatedHistoricRequests);
        Assert.Equal(2, preview.DaysWithTargets);
        Assert.Equal(new[] { 2, 1 }, preview.Days.Select(day => day.TargetSensors));
        Assert.Equal(0, preview.HistoricQuotaLowerBoundSeconds);
        Assert.Equal(1, preview.StateChangeObjects);
        Assert.NotEmpty(preview.PreviewId);
        Assert.NotEmpty(preview.SettingsRevision);
        Assert.NotEmpty(preview.ScopeRevision);
        Assert.NotEmpty(preview.BuildRevision);
        Assert.Equal(PrtgBackfillService.FullBackfillPreviewContractVersion, preview.ContractVersion);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public async Task TryStartFull_ConfirmedPreviewRunsOnceAgainstOfflineSource()
    {
        var preview = _service.PreviewFull();

        Assert.True(_service.TryStartFull(Confirmed(preview), out var error, out _), error);
        Assert.False(_service.TryStartFull(Confirmed(preview), out var reused, out _));
        Assert.Contains("已使用", reused);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_runState.Snapshot().IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        if (_runState.Snapshot().IsRunning)
        {
            _service.TryCancel();
            var stopDeadline = DateTime.UtcNow.AddSeconds(5);
            while (_runState.Snapshot().IsRunning && DateTime.UtcNow < stopDeadline)
                await Task.Delay(20);
        }

        var status = _service.GetStatus();
        Assert.False(status.IsRunning);
        Assert.True(status.Success, string.Join(Environment.NewLine, status.Output));
        var expectedRequests = new[]
        {
            (2001L, DateTime.Today.AddDays(-2)),
            (2002L, DateTime.Today.AddDays(-2)),
            (1001L, DateTime.Today.AddDays(-1))
        };
        Assert.Equal(expectedRequests, _offlineHistoricRequests.OrderBy(row => row.Day).ThenBy(row => row.SensorId));
        var saved = _backend.PrtgStore().GetValues(DateTime.Today.AddDays(-2), DateTime.Today);
        Assert.Equal(3, saved.Count);
        Assert.Equal(expectedRequests, saved.Select(row => (row.SensorObjid, row.PeriodStart.Date)).OrderBy(row => row.Date).ThenBy(row => row.SensorObjid));
    }

    [Fact]
    public void TryStartFull_RejectsMissingOrUnconfirmedPreviewBeforeRunLock()
    {
        Assert.False(_service.TryStartFull(null, out var missing, out _));
        Assert.Contains("先取得最新成本預覽", missing);

        var preview = _service.PreviewFull();
        Assert.False(_service.TryStartFull(new PrtgFullBackfillStartRequest
        { PreviewId = preview.PreviewId, Confirmed = false }, out var unconfirmed, out _));
        Assert.Contains("尚未確認", unconfirmed);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public void TryStartFull_RejectsRecordMapOrSettingsDriftBeforeRunLock()
    {
        var byRecord = _service.PreviewFull();
        _backend.RecordStore().Append(new DailyAnalysisRecord
        { HostId = 22, Host = "host-22", Date = DateTime.Today.AddDays(-1), RiskLevel = "中" });
        Assert.False(_service.TryStartFull(Confirmed(byRecord), out var recordError, out _));
        Assert.Contains("歷史記錄或對應已在預覽後變更", recordError);
        Assert.False(_runState.Snapshot().IsRunning);

        var bySettings = _service.PreviewFull();
        _settings.Update(settings => settings.PrtgBackfillDays = 1);
        Assert.False(_service.TryStartFull(Confirmed(bySettings), out var settingsError, out _));
        Assert.Contains("歷史記錄或對應已在預覽後變更", settingsError);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public void TryStartFull_RejectsMapDriftBeforeRunLock()
    {
        var preview = _service.PreviewFull();
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today.AddDays(-1), new[]
        {
            new PrtgHostMapRow { DeviceObjid = 101, HostId = 1, MapStatus = PrtgMapStatus.Ok }
        });

        Assert.False(_service.TryStartFull(Confirmed(preview), out var error, out _));
        Assert.Contains("歷史記錄或對應已在預覽後變更", error);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public void PreviewFull_BoundsOutstandingSingleUseTokens()
    {
        _settings.Update(settings => settings.PrtgBackfillDays = 1);
        for (var i = 0; i < 128; i++) _service.PreviewFull();

        var ex = Assert.Throws<DomainException>(() => _service.PreviewFull());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Contains("太多尚未使用", ex.Message);
    }

    [Fact]
    public void TryStartFull_ConfirmedPreviewIsOneUseAndRejectsSecondSubmission()
    {
        var preview = _service.PreviewFull();
        Assert.True(_scheduler.TryBeginRun("test", out _));

        Assert.False(_service.TryStartFull(Confirmed(preview), out _, out var conflict));
        Assert.True(conflict);
        Assert.False(_runState.Snapshot().IsRunning);
        Assert.False(_service.TryStartFull(Confirmed(preview), out var reused, out _));
        Assert.Contains("已使用", reused);
    }

    [Fact]
    public void TryStartFull_RejectsPreviewAfterLocalMidnightOrExpiry()
    {
        var byDate = _service.PreviewFull();
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.False(_service.TryStartFull(Confirmed(byDate), out var dateError, out _));
        Assert.Contains("日期已跨日", dateError);

        var byExpiry = _service.PreviewFull();
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(_service.TryStartFull(Confirmed(byExpiry), out var expiryError, out _));
        Assert.Contains("已過期", expiryError);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public void TryStartFull_RechecksLocalDateAfterPlanRecomputeBeforeClaimingRun()
    {
        var preview = _service.PreviewFull();
        _clock.AdvanceLocalOnRead(3, TimeSpan.FromDays(1)); // initial admission passes; final pre-claim local-date read crosses midnight

        Assert.False(_service.TryStartFull(Confirmed(preview), out var error, out _));
        Assert.Contains("預覽計算期間日期已跨日", error);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    [Fact]
    public void TryStartFull_RechecksExpiryAfterPlanRecomputeBeforeClaimingRun()
    {
        var preview = _service.PreviewFull();
        _clock.AdvanceUtcOnRead(4, TimeSpan.FromMinutes(6)); // initial admission passes; final pre-claim read expires the token

        Assert.False(_service.TryStartFull(Confirmed(preview), out var error, out _));
        Assert.Contains("預覽計算期間已過期", error);
        Assert.False(_runState.Snapshot().IsRunning);
    }

    private static PrtgFullBackfillStartRequest Confirmed(PrtgFullBackfillPreviewDto preview) => new()
    { PreviewId = preview.PreviewId, Confirmed = true };

    private void SeedTwoDays()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var twoDaysAgo = DateTime.Today.AddDays(-2);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(yesterday, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok }
        });
        store.ReplaceHostMapForDate(twoDaysAgo, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 200, HostId = 1, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 201, HostId = 2, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[]
        {
            Sensor(1001, 100), Sensor(2001, 200), Sensor(2002, 201)
        }, DateTime.Now);
        var records = _backend.RecordStore();
        records.Append(new DailyAnalysisRecord { HostId = 1, Host = "host-11", Date = yesterday, RiskLevel = "高" });
        records.Append(new DailyAnalysisRecord { HostId = 1, Host = "host-11", Date = twoDaysAgo, RiskLevel = "中" });
        records.Append(new DailyAnalysisRecord { HostId = 2, Host = "host-22", Date = twoDaysAgo, RiskLevel = "中" });
    }

    private static PrtgSensorRow Sensor(long id, long deviceId) => new()
    { Objid = id, DeviceObjid = deviceId, Name = $"sensor-{id}", SensorType = "diskfree" };

    private sealed class ManualTimeProvider(DateTime localNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified)));
        private int _utcReadCount;
        private int? _advanceUtcRead;
        private TimeSpan _advanceUtcBy;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        public override DateTimeOffset GetUtcNow()
        {
            _utcReadCount++;
            if (_utcReadCount == _advanceUtcRead) _utcNow = _utcNow.Add(_advanceUtcBy);
            return _utcNow;
        }
        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
        public void AdvanceUtcOnRead(int readsFromNow, TimeSpan value) { _advanceUtcRead = _utcReadCount + readsFromNow; _advanceUtcBy = value; }
        public void AdvanceLocalOnRead(int readsFromNow, TimeSpan value) => AdvanceUtcOnRead(readsFromNow, value);
    }

    private static PrtgClient CreateOfflinePrtgClient(List<(long SensorId, DateTime Day)> historicRequests) => new("http://prtg.test", "token", 5, true,
        new OfflinePrtgHandler(historicRequests), PrtgAuthModes.Token, "", "", "", new PrtgRequestBudget());

    private sealed class OfflinePrtgHandler(List<(long SensorId, DateTime Day)> historicRequests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string body;
            if (uri.AbsolutePath.Contains("historicdata", StringComparison.OrdinalIgnoreCase))
            {
                var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                        parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "", StringComparer.OrdinalIgnoreCase);
                var sensorId = long.Parse(query["id"], System.Globalization.CultureInfo.InvariantCulture);
                var day = DateTime.ParseExact(query["sdate"][..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                lock (historicRequests) historicRequests.Add((sensorId, day));
                body = $"{{\"histdata\":[{{\"datetime\":\"{day:yyyy-MM-dd} 01:00:00\",\"value_\":12.5,\"coverage\":100}}]}}";
            }
            else body = "{\"treesize\":0,\"messages\":[]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}

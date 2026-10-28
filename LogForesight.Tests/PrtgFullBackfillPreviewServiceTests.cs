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

    /// <summary>離線來源對哪些 (sensor, 日) 回 HTTP 500；預設全部成功。</summary>
    private Func<long, DateTime, bool> _failHistoric = (_, _) => false;

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
            _scheduler, syncState, sentinels, sync, _clock,
            _ => CreateOfflinePrtgClient(_offlineHistoricRequests, (id, day) => _failHistoric(id, day)));
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

    [Theory]
    [InlineData(PrtgFetchStrategy.Conservative, 2)]
    [InlineData(PrtgFetchStrategy.Aggressive, 1)]
    public void PreviewFull_保守策略補全部已對應主機_激進策略照取數對象(string strategy, int yesterdayTargets)
    {
        // 昨天另有一台已對應、但當天沒有高／中風險的主機（host 2 的 device 300）
        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(yesterday, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 300, HostId = 2, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[] { Sensor(3001, 300) }, DateTime.Now);
        _settings.Update(s => s.PrtgFetchStrategy = strategy);

        var preview = _service.PreviewFull();

        Assert.Equal(yesterdayTargets, preview.Days.Single(day => day.Day == yesterday).TargetSensors);
    }

    [Fact]
    public async Task 全量回填執行_保守策略實際補到當天沒有風險的已對應主機()
    {
        // 與預覽同一個範圍判定：執行端若改回只補觸發主機，sensor 3001 不會被請求
        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(yesterday, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { DeviceObjid = 300, HostId = 2, MapStatus = PrtgMapStatus.Ok }
        });
        store.UpsertSensors(new[] { Sensor(3001, 300) }, DateTime.Now);
        _settings.Update(s => s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative);

        var requests = await RunFullAsync();

        Assert.Contains(requests, r => r.SensorId == 3001 && r.Day == yesterday);
        Assert.Equal("全部已對應主機（保守策略）", _service.PreviewFull().HostScopeText);
    }

    [Fact]
    public void PreviewFull_保守策略白名單為空時明確拒絕()
    {
        _settings.Update(s =>
        {
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var error = Assert.Throws<DomainException>(() => _service.PreviewFull());
        Assert.Contains("白名單", error.Message);
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

    private static PrtgFullBackfillStartRequest Confirmed(PrtgFullBackfillPreviewDto preview, bool resume = true) => new()
    { PreviewId = preview.PreviewId, Confirmed = true, Resume = resume };

    private PrtgBackfillWatermarkStore Watermark() => new(_backend.Blob(PrtgBackfillWatermarkStore.BlobKey));

    private async Task WaitForBackfillAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_runState.Snapshot().IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.False(_runState.Snapshot().IsRunning, "回填未在期限內結束");
    }

    /// <summary>預覽→確認→等它跑完；回傳本趟發出的 historicdata 請求。</summary>
    private async Task<List<(long SensorId, DateTime Day)>> RunFullAsync(bool resume = true)
    {
        lock (_offlineHistoricRequests) _offlineHistoricRequests.Clear();
        var preview = _service.PreviewFull();
        Assert.True(_service.TryStartFull(Confirmed(preview, resume), out var error, out _), error);
        await WaitForBackfillAsync();
        lock (_offlineHistoricRequests) return _offlineHistoricRequests.OrderBy(r => r.Day).ThenBy(r => r.SensorId).ToList();
    }

    [Fact]
    public async Task 完成水位_中斷或部分失敗後續跑只補剩下的日子()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var twoDaysAgo = DateTime.Today.AddDays(-2);
        // 第一趟：兩天前有一顆 sensor 失敗（該日不算完成），昨天完整
        _failHistoric = (id, _) => id == 2001;
        await RunFullAsync();

        _failHistoric = (_, _) => false;
        var preview = _service.PreviewFull();
        Assert.Equal(1, preview.CompletedDays);
        Assert.True(preview.Days.Single(d => d.Day == yesterday).AlreadyCompleted);
        Assert.False(preview.Days.Single(d => d.Day == twoDaysAgo).AlreadyCompleted);
        Assert.Equal(2, preview.RemainingHistoricRequests);
        Assert.Contains("1 天先前已完整回填", preview.Message);

        lock (_offlineHistoricRequests) _offlineHistoricRequests.Clear();
        Assert.True(_service.TryStartFull(Confirmed(preview), out var error, out _), error);
        await WaitForBackfillAsync();
        lock (_offlineHistoricRequests)
            Assert.Equal(new[] { (2001L, twoDaysAgo), (2002L, twoDaysAgo) },
                _offlineHistoricRequests.OrderBy(r => r.SensorId).ToArray());
    }

    [Fact]
    public async Task 完成水位_目標變了的日子不算完成()
    {
        await RunFullAsync();
        Assert.Equal(2, _service.PreviewFull().CompletedDays);

        // 昨天多一個中風險主機日：那天要取的目標換了一批
        _backend.RecordStore().Append(new DailyAnalysisRecord
        { HostId = 2, Host = "host-22", Date = DateTime.Today.AddDays(-1), RiskLevel = "中" });

        var preview = _service.PreviewFull();
        Assert.Equal(1, preview.CompletedDays);
        Assert.False(preview.Days.Single(d => d.Day == DateTime.Today.AddDays(-1)).AlreadyCompleted);
    }

    [Fact]
    public async Task 完成水位_全部完成時拒絕空跑_全部重做會清掉水位重來()
    {
        await RunFullAsync();
        var allDone = _service.PreviewFull();
        Assert.Equal(2, allDone.CompletedDays);
        Assert.False(_service.TryStartFull(Confirmed(allDone), out var error, out _));
        Assert.Contains("全部 2 天先前已完整回填", error);
        Assert.False(_runState.Snapshot().IsRunning);

        // 全部重做：昨天這次失敗。水位若沒有在開始時清掉，昨天仍會被當成已完成
        _failHistoric = (id, _) => id == 1001;
        var redo = await RunFullAsync(resume: false);
        Assert.Equal(3, redo.Count);
        var after = _service.PreviewFull();
        Assert.Equal(1, after.CompletedDays);
        Assert.False(after.Days.Single(d => d.Day == DateTime.Today.AddDays(-1)).AlreadyCompleted);
    }

    /// <summary>
    /// 體檢輪：回填天數內較早的日子常沒有主機對應（上線前）。這種日子不記完成的話「全部完成」永遠湊不齊：
    /// 續跑只剩它時整趟回失敗（「沒有任何一天有主機對應」）還重翻狀態變更區間，拒絕空跑的防線被繞過。
    /// 指紋含對應，之後有了對應那天就不算完成。
    /// </summary>
    [Fact]
    public async Task 完成水位_無主機對應的日子也算完成_全部完成後拒絕空跑()
    {
        var threeDaysAgo = DateTime.Today.AddDays(-3);
        _settings.Update(settings => settings.PrtgBackfillDays = 3);
        Assert.Equal(3, _service.PreviewFull().DayCount);

        await RunFullAsync();
        Assert.True(_service.GetStatus().Success, string.Join(Environment.NewLine, _service.GetStatus().Output));

        var preview = _service.PreviewFull();
        Assert.Equal(3, preview.CompletedDays);
        Assert.True(preview.Days.Single(d => d.Day == threeDaysAgo).AlreadyCompleted);
        Assert.False(_service.TryStartFull(Confirmed(preview), out var error, out _));
        Assert.Contains("全部 3 天先前已完整回填", error);
        Assert.False(_runState.Snapshot().IsRunning);

        // 那天之後有了對應：目標變了，不再算完成
        _backend.PrtgStore().ReplaceHostMapForDate(threeDaysAgo, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 100, HostId = 1, MapStatus = PrtgMapStatus.Ok }
        });
        _backend.RecordStore().Append(new DailyAnalysisRecord { HostId = 1, Host = "host-11", Date = threeDaysAgo, RiskLevel = "高" });
        var changed = _service.PreviewFull();
        Assert.Equal(2, changed.CompletedDays);
        Assert.False(changed.Days.Single(d => d.Day == threeDaysAgo).AlreadyCompleted);
    }

    [Fact]
    public async Task 完成水位_超過七天未更新就不採信()
    {
        await RunFullAsync();
        Watermark().Update(mark => mark.UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime - PrtgBackfillWatermarkStore.MaxAge - TimeSpan.FromHours(1));

        Assert.Equal(0, _service.PreviewFull().CompletedDays);
    }

    [Fact]
    public async Task 完成水位_指定主機補值不讀也不寫()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        Assert.True(_service.TryStartSelected(new PrtgSelectedBackfillRequest
        { HostIds = new long[] { 1 }, FromDate = yesterday, ToDate = yesterday }, out _, out var error, out _), error);
        await WaitForBackfillAsync();

        Assert.Empty(Watermark().Get().CompletedDays);
    }

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

    private static PrtgClient CreateOfflinePrtgClient(List<(long SensorId, DateTime Day)> historicRequests,
        Func<long, DateTime, bool> failHistoric) => new("http://prtg.test", "token", 5, true,
        new OfflinePrtgHandler(historicRequests, failHistoric), PrtgAuthModes.Token, "", "", "", new PrtgRequestBudget());

    private sealed class OfflinePrtgHandler(List<(long SensorId, DateTime Day)> historicRequests,
        Func<long, DateTime, bool> failHistoric) : HttpMessageHandler
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
                if (failHistoric(sensorId, day))
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
                body = $"{{\"histdata\":[{{\"datetime\":\"{day:yyyy-MM-dd} 01:00:00\",\"value_\":12.5,\"coverage\":100}}]}}";
            }
            else body = "{\"treesize\":0,\"messages\":[]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}

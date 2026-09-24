using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

public sealed record PrtgDiskVerificationStart(long SensorObjid, DateTime DataDate, string RequestId = "");
public sealed record PrtgDiskVerificationBatchStart(IReadOnlyList<long> SensorObjids, DateTime DataDate, string RequestId);
public sealed record PrtgDiskManualConfirmation(long SensorObjid, string ChannelIdentifier, string ChannelName,
    string Unit, double Scale, string Direction, string Reason);
public sealed record PrtgDiskVerificationStatus(bool IsRunning, long? SensorObjid,
    IReadOnlyCollection<PrtgDiskVerificationResult> Results, string? Message,
    bool IsBatch = false, int BatchTotal = 0, int BatchCompleted = 0, string? RequestId = null);
public sealed record PrtgDiskRuleTrial(string Status, string Message, long SensorObjid, DateOnly CompletedDay,
    int UsableDays, int RequiredDays, int UsableHours, int RequiredHoursPerDay, string DataQuality,
    bool SemanticVerified, string? Exclusion, double? LowWaterPercent, double? MinimumDeclinePerDay,
    double? MaximumDaysToDepletion, double? CurrentAvailablePercent, double? DeclinePerDay,
    double? EstimatedDaysToDepletion, bool PredictedHit, string RealPositiveStatus, string? RuleId, bool? RuleEnabled);

public sealed class PrtgDiskVerificationService
{
    public const string ParserSemanticVersion = "disk-semantic-v1";
    private readonly ISystemSettingsStore _settings;
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hosts;
    private readonly PrtgProbeRunState _run;
    private readonly PrtgBackfillRunState _backfill;
    private readonly PrtgStructureSyncRunState _structure;
    private readonly SchedulerRunState _scheduler;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore _results;
    private readonly IKnownIssueRuleStore _ruleStore;
    private long? _runningSensor;
    private int _ownsRun;
    private readonly object _requestGate = new();
    private readonly Dictionary<string, (string Signature, DateTime Expires)> _acceptedRequests = new(StringComparer.Ordinal);
    private const int RequestCacheLimit = 512;
    private static readonly TimeSpan RequestCacheLifetime = TimeSpan.FromMinutes(15);
    private bool _isBatch;
    private int _batchTotal;
    private int _batchCompleted;
    private string? _activeRequestId;

    public PrtgDiskVerificationService(ISystemSettingsStore settings, EfPrtgStore store, IHostStore hosts,
        PrtgProbeRunState run, PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore results,
        PrtgBackfillRunState backfill, PrtgStructureSyncRunState structure, SchedulerRunState scheduler,
        IKnownIssueRuleStore ruleStore)
    { _settings = settings; _store = store; _hosts = hosts; _run = run; _evidence = evidence; _results = results;
        _backfill = backfill; _structure = structure; _scheduler = scheduler; _ruleStore = ruleStore; }

    public PrtgDiskRuleTrial AssessRuleTrial(long sensorObjid)
    {
        if (sensorObjid <= 0) throw new ArgumentOutOfRangeException(nameof(sensorObjid));
        var completedDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var (content, usedFallback) = RuleBootstrapper.LoadContent(_ruleStore);
        var validation = RuleValidator.Validate(content.Rules);
        var diskRules = validation.ValidRules.Where(r =>
            string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.PrtgRuleCode, PrtgDiskRuleDecision.RuleCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.PrtgSensorCategory, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase)).ToArray();
        KnownIssueRule? rule = null;
        if (!usedFallback)
        {
            rule = diskRules.Where(r => r.Enabled).OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            rule ??= diskRules.OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
        }
        var assessment = new PrtgDiskAssessmentService(_store, _hosts, _settings, _evidence, _results)
            .Assess(completedDay, rule, PrtgDiskDecisionMode.Preview, 1, 0, selectedSensorObjids: new[] { sensorObjid });
        var row = assessment.Rows.FirstOrDefault();
        if (row is null)
            return new("sensor-unavailable", "所選感測器目前未對應至啟用主機，無法試算。", sensorObjid, completedDay,
                0, PrtgValueReadiness.WindowDays, 0, PrtgValueReadiness.MinDailyUsableHours, "資料不可用", false,
                "SensorUnavailable", null, null, null, null, null, null, false, "真實正向尚未觀察", rule?.Id, rule?.Enabled);
        var ready = row.Readiness;
        var trend = row.Decision.Trend;
        var quality = ready.Status == PrtgValueReadinessStatus.Ready ? "28 日窗口每日達可用品質門檻" : ready.Status == PrtgValueReadinessStatus.InsufficientData
            ? $"僅 {ready.UsableDays}/{ready.RequiredDays} 個完整可用日；需每日至少 {ready.RequiredHoursPerDay} 個可用小時" : ready.Reason;
        var eligible = ready.Status == PrtgValueReadinessStatus.Ready
            && row.EvidenceValidity is { IsValid: true }
            && row.Decision.Exclusion is PrtgDiskDecisionExclusion.None or PrtgDiskDecisionExclusion.TrendNoHit;
        var status = rule is null ? "no-configured-rule" : ready.Status == PrtgValueReadinessStatus.InsufficientData
            ? "insufficient-data" : eligible
                ? row.Decision.WouldHit ? "ready-hit" : "ready-no-hit" : "excluded";
        var message = status switch
        {
            "no-configured-rule" => "沒有已儲存的 disk_free_trend 規則可供試算。",
            "insufficient-data" => $"僅 {ready.UsableDays}/{ready.RequiredDays} 天，28 天窗口資料不足。",
            "ready-hit" => $"規則試算命中；這只代表已落盤資料符合目前門檻，真實正向尚未觀察。{row.Decision.Reason}",
            "ready-no-hit" => "試算流程已就緒，目前未命中；真實正向效果尚待觀察。",
            _ => row.Decision.Reason
        };
        var thresholds = rule?.PrtgDiskTrendThresholds;
        return new(status, message, sensorObjid, completedDay, ready.UsableDays, ready.RequiredDays, ready.UsableHours,
            ready.RequiredHoursPerDay, quality, row.EvidenceValidity is { IsValid: true }, row.Decision.Exclusion.ToString(),
            thresholds?.LowWaterPercent, thresholds?.MinimumDeclinePercentagePointsPerDay, thresholds?.MaximumDaysToDepletion,
            trend?.CurrentAvailablePercent, trend?.RobustDeclinePercentagePointsPerDay, trend?.EstimatedDaysToDepletion,
            row.Decision.WouldHit, "真實正向尚未觀察", rule?.Id, rule?.Enabled);
    }

    public PrtgDiskVerificationStatus GetStatus(long? sensorObjid = null) => new(Volatile.Read(ref _ownsRun) != 0, _runningSensor,
        sensorObjid.HasValue
            ? new[] { _results.Get(sensorObjid.Value) }.Where(x => x != null).Cast<PrtgDiskVerificationResult>().ToArray()
            : _results.GetRecent(), null, _isBatch, _batchTotal, _batchCompleted, _activeRequestId);
    public bool Cancel() => Volatile.Read(ref _ownsRun) != 0 && _run.TryCancel();

    public bool TryStart(PrtgDiskVerificationStart request, out string? error)
    {
        return TryStartCore(request, out error);
    }

    public bool TryStartBatch(PrtgDiskVerificationBatchStart request, out string? error)
    {
        error = null;
        if (request.SensorObjids is null || request.SensorObjids.Count is < 1 or > 5)
        { error = "批次必須包含 1 至 5 顆感測器。"; return false; }
        if (request.SensorObjids.Any(id => id <= 0))
        { error = "感測器編號必須是大於 0 的有效數字。"; return false; }
        if (request.SensorObjids.Distinct().Count() != request.SensorObjids.Count)
        { error = "批次感測器不可重複。"; return false; }
        if (!ValidateDateAndConnection(request.DataDate, out error)) return false;
        var ids = request.SensorObjids.ToArray();
        var settings = _settings.Get();
        var eligible = ValidateEligibleSensors(ids, settings, requireReady: true, out error);
        if (!eligible) return false;
        if (_scheduler.IsRunning || _backfill.Snapshot().IsRunning || _structure.Snapshot().IsRunning)
        { error = "取數排程、回填或結構同步正在執行，請完成後再進行語意驗證。"; return false; }
        if (!TryBeginIdempotentRun(request.RequestId, string.Join(',', ids) + "|" + request.DataDate.ToString("O"), out var ct, out var duplicate, out error)) return false;
        if (duplicate) return true;
        _runningSensor = null;
        Volatile.Write(ref _ownsRun, 1);
        _isBatch = true; _batchTotal = ids.Length; _batchCompleted = 0; _activeRequestId = request.RequestId;
        _ = Task.Run(async () =>
        {
            var anySuccess = false; var cancelled = false;
            try
            {
                foreach (var id in ids)
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    _runningSensor = id;
                    var sensor = FindCurrentSensor(id);
                    try
                    {
                        await ProbeAndSaveAsync(sensor, request.DataDate, settings, ct);
                        anySuccess = true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        SaveOutcome(sensor, request.DataDate, "Cancelled", "語意驗證已由使用者停止。", true);
                    }
                    catch (Exception ex)
                    {
                        SaveOutcome(sensor, request.DataDate, ex is TaskCanceledException or TimeoutException ? "TimedOut" : "Failed",
                            ex is TaskCanceledException or TimeoutException ? "PRTG 語意驗證逾時。" : "此感測器驗證失敗；批次會繼續下一顆。");
                    }
                    finally { _batchCompleted++; }
                    if (cancelled) break;
                }
            }
            finally
            {
                _runningSensor = null; Volatile.Write(ref _ownsRun, 0);
                _run.FinishRun(anySuccess, cancelled);
            }
        });
        return true;
    }

    private bool TryStartCore(PrtgDiskVerificationStart request, out string? error)
    {
        error = null;
        if (request.SensorObjid <= 0) { error = "感測器編號必須是大於 0 的有效數字。"; return false; }
        if (!ValidateDateAndConnection(request.DataDate, out error)) return false;
        var settings = _settings.Get();
        if (_scheduler.IsRunning || _backfill.Snapshot().IsRunning || _structure.Snapshot().IsRunning)
        { error = "取數排程、回填或結構同步正在執行，請完成後再進行語意驗證。"; return false; }
        var sensor = FindCurrentSensor(request.SensorObjid);
        if (sensor.Objid == 0 || sensor.Paused || sensor.DevicePaused || sensor.Category != PrtgSensorCategories.Disk ||
            !settings.PrtgSensorTypeWhitelist.Contains(sensor.SensorType, StringComparer.OrdinalIgnoreCase))
        { error = "感測器必須是目前映射至啟用主機、未暫停且在白名單內的磁碟感測器。"; return false; }
        if (!TryBeginIdempotentRun(request.RequestId, request.SensorObjid + "|" + request.DataDate.ToString("O"), out var ct, out var duplicate, out error)) return false;
        if (duplicate) return true;
        _runningSensor = sensor.Objid; _isBatch = false; _batchTotal = 1; _batchCompleted = 0; _activeRequestId = request.RequestId;
        Volatile.Write(ref _ownsRun, 1);
        _ = Task.Run(async () =>
        {
            var success = false; var cancelled = false;
            try { await ProbeAndSaveAsync(sensor, request.DataDate, settings, ct); success = true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { cancelled = true; SaveOutcome(sensor, request.DataDate, "Cancelled", "語意驗證已由使用者停止。", true); }
            catch (Exception ex)
            { SaveOutcome(sensor, request.DataDate, ex is TaskCanceledException or TimeoutException ? "TimedOut" : "Failed",
                ex is TaskCanceledException or TimeoutException ? "PRTG 語意驗證逾時。" : "PRTG 語意驗證失敗或回應無法解析。"); }
            finally { _batchCompleted = 1; _runningSensor = null; Volatile.Write(ref _ownsRun, 0); _run.FinishRun(success, cancelled); }
        });
        return true;
    }

    private bool ValidateDateAndConnection(DateTime date, out string? error)
    {
        error = null;
        if (date.Date != date || date.Date != DateTime.Today.AddDays(-1)) { error = "語意驗證只允許查詢昨天的單日資料。"; return false; }
        var today = DateTime.Today;
        var settings = _settings.Get();
        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings))
        { error = "PRTG 連線尚未設定完整。"; return false; }
        return true;
    }

    private bool ValidateEligibleSensors(long[] ids, SystemSettings settings, bool requireReady, out string? error)
    {
        error = null;
        foreach (var id in ids)
        {
            var sensor = FindCurrentSensor(id);
            if (sensor.Objid == 0 || sensor.Category != PrtgSensorCategories.Disk || sensor.Paused || sensor.DevicePaused ||
                !settings.PrtgSensorTypeWhitelist.Contains(sensor.SensorType, StringComparer.OrdinalIgnoreCase))
            { error = $"Sensor {id} 必須是已對應啟用主機、未暫停、白名單內的磁碟感測器。"; return false; }
            if (requireReady)
            {
                var assessed = new PrtgDiskAssessmentService(_store, _hosts, _settings, _evidence, _results)
                    .Assess(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), null, PrtgDiskDecisionMode.Preview, 5, 0,
                        selectedSensorObjids: new[] { id });
                if (assessed.Rows.FirstOrDefault()?.Readiness.Status != PrtgValueReadinessStatus.Ready)
                { error = $"Sensor {id} 尚未具備所需的 28 日可用資料。"; return false; }
            }
        }
        return true;
    }

    private bool TryBeginIdempotentRun(string? requestId, string signature, out CancellationToken token, out bool duplicate, out string? error)
    {
        duplicate = false; error = null; token = default;
        if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
        { error = "requestId 必須是有效且非空的 GUID。"; return false; }
        var key = parsed.ToString("N"); var now = DateTime.UtcNow;
        lock (_requestGate)
        {
            foreach (var expired in _acceptedRequests.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _acceptedRequests.Remove(expired);
            if (_acceptedRequests.TryGetValue(key, out var prior))
            {
                if (!string.Equals(prior.Signature, signature, StringComparison.Ordinal)) { error = "此 requestId 已用於不同的驗證內容。"; return false; }
                duplicate = true; return true;
            }
            if (!_run.TryBeginRun(out token)) { error = "其他 PRTG 探測、回填、同步或快照工作正在執行。"; return false; }
            if (_acceptedRequests.Count >= RequestCacheLimit)
            {
                var oldest = _acceptedRequests.OrderBy(x => x.Value.Expires).First(); _acceptedRequests.Remove(oldest.Key);
            }
            _acceptedRequests[key] = (signature, now.Add(RequestCacheLifetime));
        }
        return true;
    }

    private async Task ProbeAndSaveAsync((long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) sensor,
        DateTime date, SystemSettings settings, CancellationToken ct)
    {
        var localStart = DateTime.SpecifyKind(date.Date, DateTimeKind.Local);
        var points = _store.GetValuesForSensor(sensor.Objid, localStart, localStart.AddDays(1))
            .Where(v => v.AvgValue.HasValue && (v.Quality == PrtgDataQuality.Ok ||
                v.Quality == PrtgDataQuality.Sampled && v.Coverage >= PrtgValueUsability.SampledMinCoverage))
            .Take(5).Select(v => new PrtgDiskSemanticPersistedPoint(
                DateTime.SpecifyKind(v.PeriodStart, DateTimeKind.Local).ToUniversalTime(), v.AvgValue!.Value)).ToArray();
        using var client = PrtgClientFactory.Create(settings);
        var result = await new PrtgDiskSemanticProbe(client).ProbeAsync(sensor.Objid,
            localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime(), points, ct);
        var checkedAtUtc = DateTime.UtcNow;
        _results.Save(new PrtgDiskVerificationResult(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.SensorType,
            result.Status.ToString(), Safe(result.Summary) ?? "語意驗證完成，請查看型別化結果。", Safe(result.ChannelIdentifier), Safe(result.ChannelName),
            Safe(result.Unit), result.Scale, result.Direction, result.ComparedPointCount, result.ValuesMatch,
            checkedAtUtc, date.Date, ParserSemanticVersion));
        if (result.Status == PrtgDiskSemanticProbeStatus.Verified && result.ChannelIdentifier != null && result.ChannelName != null &&
            result.Unit != null && result.Scale is > 0 && result.Direction != null)
            _evidence.RecordAutomatedVerification(Context(sensor, result.ChannelIdentifier, result.ChannelName, result.Unit,
                result.Scale.Value, result.Direction), true, "PRTG 主頻道為明確百分比可用空間，且 historicdata 與已落地樣本一致。", checkedAtUtc, ParserSemanticVersion);
    }

    private void SaveOutcome((long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) sensor,
        DateTime date, string state, string message, bool cancelled = false) =>
        _results.Save(new(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.SensorType, state, message,
            null, null, null, null, null, 0, null, DateTime.UtcNow, date.Date, ParserSemanticVersion, Cancelled: cancelled));

    public PrtgDiskSemanticEvidence ConfirmManually(PrtgDiskManualConfirmation request, long userId)
    {
        var previous = _results.Get(request.SensorObjid) ?? throw new InvalidOperationException("該感測器尚無語意探測結果，不能人工確認。");
        var now = DateTime.UtcNow;
        if (previous.CheckedAtUtc == default || previous.CheckedAtUtc > now || now - previous.CheckedAtUtc > TimeSpan.FromHours(24) ||
            previous.Cancelled || previous.DataDate != DateTime.Today.AddDays(-1))
            throw new InvalidOperationException("最近的語意探測無有效候選或資料已過期，請重新探測。");
        if (previous.Status != nameof(PrtgDiskSemanticProbeStatus.NeedsManualReview) ||
            previous.ValuesMatch != true || previous.ComparedPointCount <= 0)
            throw new InvalidOperationException("最近探測未建立可供人工覆核的成功比對候選；不匹配、無落地資料點或其他探測狀態均須重新探測。");
        var current = FindCurrentSensor(request.SensorObjid);
        if (current.Objid == 0) throw new InvalidOperationException("感測器已不再映射至啟用主機。");
        if (previous.DeviceObjid != current.DeviceObjid || previous.HostId != current.HostId || !Same(previous.SensorType, current.SensorType))
            throw new InvalidOperationException("探測後感測器的主機／裝置對應或類型已變更，請重新探測。");
        if (string.IsNullOrWhiteSpace(request.ChannelIdentifier) || string.IsNullOrWhiteSpace(request.ChannelName) ||
            string.IsNullOrWhiteSpace(request.Unit) || string.IsNullOrWhiteSpace(request.Direction) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500 || !double.IsFinite(request.Scale) || request.Scale <= 0)
            throw new ArgumentException("頻道、單位、尺度、方向與確認理由均為必要欄位。");
        if (!IsPercent(request.Unit) || !IsFreeChannel(request.ChannelName) ||
            !Same(request.Direction, "descending-danger") || request.Scale != 1)
            throw new ArgumentException("人工確認僅接受尺度為 1 的明確百分比可用空間頻道，方向必須是可用空間下降代表風險。");
        if ((previous.ChannelIdentifier != null && !Same(previous.ChannelIdentifier, request.ChannelIdentifier)) ||
            (previous.ChannelName != null && !Same(previous.ChannelName, request.ChannelName)) ||
            (previous.Unit != null && !Same(previous.Unit, request.Unit)) ||
            (previous.Scale.HasValue && previous.Scale.Value != request.Scale) ||
            (previous.Direction != null && !Same(previous.Direction, request.Direction)))
            throw new InvalidOperationException("人工確認必須對應最近探測的同一候選頻道與量測內容。");
        if (previous.ComparedPointCount == 0)
            throw new InvalidOperationException("沒有可比對的落地資料點，不能人工確認空白候選。");
        var summary = $"人工確認：{request.Reason.Trim()}；探測摘要：{previous.Summary}";
        return _evidence.ConfirmManually(Context(current, request.ChannelIdentifier, request.ChannelName, request.Unit,
            request.Scale, request.Direction), userId, summary, DateTime.UtcNow, ParserSemanticVersion);
    }

    public PrtgDiskSemanticEvidenceValidity CheckEvidence(long sensorObjid)
    {
        var s = FindCurrentSensor(sensorObjid);
        if (s.Objid == 0) return _evidence.CheckValidity(sensorObjid, null, ParserSemanticVersion);
        var e = _evidence.Get(sensorObjid);
        var latest = _results.Get(sensorObjid);
        if (e is null) return _evidence.CheckValidity(sensorObjid, null, ParserSemanticVersion);
        // 24 小時限制只適用於執行人工確認的當下。之後沿用最近的型別化探測，
        // 並在有新探測時要求它仍與確認內容一致；鏡像本身沒有 channel metadata 可持續重驗。
        if (latest is null || latest.CheckedAtUtc == default || latest.CheckedAtUtc > DateTime.UtcNow ||
            latest.Cancelled || latest.ValuesMatch != true || latest.ComparedPointCount <= 0 ||
            latest.Status is not (nameof(PrtgDiskSemanticProbeStatus.Verified) or nameof(PrtgDiskSemanticProbeStatus.NeedsManualReview) or nameof(PrtgDiskSemanticProbeStatus.Mismatch)) ||
            (latest.Status == nameof(PrtgDiskSemanticProbeStatus.Mismatch)) || !ProbeMatchesEvidence(latest, e) ||
            (e.Source == PrtgDiskSemanticEvidenceSource.Manual && latest.CheckedAtUtc <= e.VerifiedAtUtc &&
                (e.VerifiedAtUtc - latest.CheckedAtUtc > TimeSpan.FromHours(24) || latest.Status != nameof(PrtgDiskSemanticProbeStatus.NeedsManualReview))) ||
            (e.Source == PrtgDiskSemanticEvidenceSource.Automated &&
                (latest.CheckedAtUtc < e.VerifiedAtUtc || latest.Status != nameof(PrtgDiskSemanticProbeStatus.Verified) ||
                 latest.ChannelIdentifier is null || latest.ChannelName is null || latest.Unit is null || latest.Scale is not > 0 || latest.Direction is null)))
            return new PrtgDiskSemanticEvidenceValidity
            {
                IsValid = false,
                InvalidReason = "目前 PRTG 鏡像沒有頻道中繼資料，且最近的有效探測與已確認內容不符；請重新探測並覆核。",
                Evidence = e
            };
        var context = Context(s, e.MainChannelIdentifier, e.MainChannelName, e.Unit, e.Scale, e.Direction);
        if (!MatchesVerifiedPercent(latest, e, s))
            return new PrtgDiskSemanticEvidenceValidity
            {
                IsValid = false,
                InvalidReason = "最近的型別化探測無法確認已驗證的百分比可用空間語意。",
                Evidence = e
            };
        return _evidence.CheckValidity(sensorObjid, context, ParserSemanticVersion);
    }

    private static bool ProbeMatchesEvidence(PrtgDiskVerificationResult probe, PrtgDiskSemanticEvidence evidence) =>
        (probe.ChannelIdentifier is null || Same(probe.ChannelIdentifier, evidence.MainChannelIdentifier)) &&
        (probe.ChannelName is null || Same(probe.ChannelName, evidence.MainChannelName)) &&
        (probe.Unit is null || Same(probe.Unit, evidence.Unit)) &&
        (!probe.Scale.HasValue || probe.Scale.Value == evidence.Scale) &&
        (probe.Direction is null || Same(probe.Direction, evidence.Direction));

    // Keep aligned with Core PrtgDiskAssessmentService.MatchesVerifiedPercent. The Core helper is
    // internal to Core's consumers, so this service applies the same contract before reporting validity.
    private static bool MatchesVerifiedPercent(PrtgDiskVerificationResult? probe, PrtgDiskSemanticEvidence evidence,
        (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) sensor)
    {
        static bool Reported(string? value) => !string.IsNullOrWhiteSpace(value);
        if (probe is null || probe.Cancelled || probe.ValuesMatch != true || probe.ComparedPointCount <= 0 ||
            probe.SensorObjid != sensor.Objid || probe.DeviceObjid != sensor.DeviceObjid || probe.HostId != sensor.HostId ||
            !Same(probe.SensorType, sensor.SensorType) || (Reported(sensor.Unit) && !Same(sensor.Unit, evidence.Unit)) ||
            !string.Equals(probe.ParserSemanticVersion, PrtgDiskAssessmentService.ParserSemanticVersion, StringComparison.Ordinal) ||
            (Reported(probe.ChannelIdentifier) && !Same(probe.ChannelIdentifier, evidence.MainChannelIdentifier)) ||
            (Reported(probe.ChannelName) && !Same(probe.ChannelName, evidence.MainChannelName)) ||
            (Reported(probe.Unit) && !Same(probe.Unit, evidence.Unit)) ||
            (probe.Scale.HasValue && probe.Scale.Value != evidence.Scale) ||
            (Reported(probe.Direction) && !Same(probe.Direction, evidence.Direction))) return false;
        var unit = evidence.Unit?.Trim();
        var name = evidence.MainChannelName?.Trim();
        var freeChannel = name is not null && (Same(name, "free") || Same(name, "free space") || Same(name, "available") || Same(name, "available space"));
        var percentUnit = unit is not null && (unit == "%" || Same(unit, "percent") || Same(unit, "percentage"));
        return freeChannel && percentUnit && Same(evidence.Direction, "descending-danger") && evidence.Scale == 1;
    }

    private (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) FindCurrentSensor(long id)
    {
        var today = DateTime.Today;
        return _store.GetCurrentReadinessSensorById(id,
            _hosts.GetAll().Where(h => h.Active && h.MergedInto == null).Select(h => h.HostId).ToArray(),
            today, today.AddDays(-30))
            ?? default;
    }
    private static PrtgDiskSemanticContext Context((long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) s,
        string channelId, string channelName, string unit, double scale, string direction) =>
        new(s.Objid, s.DeviceObjid, s.HostId, s.SensorType, channelId, channelName, unit, scale, direction);
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool IsPercent(string unit) => unit.Trim() == "%" || Same(unit, "percent") || Same(unit, "percentage");
    private static bool IsFreeChannel(string name)
    {
        var normalized = string.Join(' ', name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Contains("used") || normalized.Contains("total") || normalized.Contains("utiliz") || normalized.Contains("consumed")) return false;
        return normalized is "free" or "free space" or "free capacity"
            or "available" or "available space" or "available capacity";
    }
    private static string? Safe(string? value) => value == null ? null : new(value.Where(c => !char.IsControl(c)).Take(120).ToArray());
}

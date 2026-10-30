using System.Diagnostics;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>Repairs recent workflow versions frequently and advances older retention/orphan work in bounded slices.</summary>
public sealed class HostDayWorkflowRecoveryHostedService : BackgroundService
{
    private const string CheckpointKey = HostDayWorkflowStore.RecoveryCheckpointBlobKey;
    private const string LeaseName = "Global\\LogForesight.Workflow.Recovery";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IAnalysisRecordQuery _records;
    private readonly StorageBackend _backend;
    private readonly ISystemSettingsStore _settings;
    private readonly IWebAiService _ai;
    private readonly HostDayWorkflowService _workflow;
    private readonly BackgroundWorkGate? _gate;
    private readonly IAiService? _reportAiService;
    private readonly IAiRequestGate? _aiGate;
    private readonly RecoveryBudget _budget;
    private readonly NamedMutexGate _lease = new(LeaseName);
    private const int MaxPendingReportsPerPage = 4;

    public HostDayWorkflowRecoveryHostedService(IAnalysisRecordQuery records, StorageBackend backend,
        ISystemSettingsStore settings, IWebAiService ai, HostDayWorkflowService workflow,
        BackgroundWorkGate? gate = null, IAiService? reportAiService = null, IAiRequestGate? aiGate = null)
        : this(records, backend, settings, ai, workflow, RecoveryBudget.Default, gate: gate,
            reportAiService: reportAiService, aiGate: aiGate) { }

    internal HostDayWorkflowRecoveryHostedService(IAnalysisRecordQuery records, StorageBackend backend,
        ISystemSettingsStore settings, IWebAiService ai, HostDayWorkflowService workflow, RecoveryBudget budget,
        BackgroundWorkGate? gate = null, IAiService? reportAiService = null, IAiRequestGate? aiGate = null)
    {
        _aiGate = aiGate;
        if (budget.PageSize is < 1 or > WorkflowRecoveryPage.MaximumRows || budget.HotPagesPerSlice < 0 ||
            budget.DeepPagesPerSlice < 0 || budget.OrphanPagesPerSlice < 0 || budget.HotTimeBudget < TimeSpan.Zero ||
            budget.TotalTimeBudget < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget), "Recovery work limits must be non-negative and each page must respect the SQL payload cap.");
        _records = records;
        _backend = backend;
        _settings = settings;
        _ai = ai;
        _workflow = workflow;
        _gate = gate;
        _reportAiService = reportAiService;
        _budget = budget;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStarted = Stopwatch.StartNew();
            try
            {
                RecoverySliceResult result;
                if (_gate == null)
                    result = await RunSliceAsync(stoppingToken);
                else
                {
                    RecoverySliceResult? gatedResult = null;
                    await _gate.RunAsync("主機日與風險報告恢復", async () =>
                        gatedResult = await RunSliceAsync(stoppingToken), stoppingToken);
                    result = gatedResult ?? throw new InvalidOperationException("Recovery gate completed without a slice result.");
                }
                if (result.WaitingRows > 0)
                    Log.Warn("Workflow recovery slice advanced past {Rows} bounded rows awaiting review; cumulative waiting={Cumulative}",
                        result.WaitingRows, result.CumulativeWaitingRows);
                Log.Info("Workflow recovery slice: hot={Hot} deep={Deep} orphan={Orphans} waiting={Waiting} payloadBytes={Bytes}",
                    result.HotRows, result.DeepRows, result.OrphanRows, result.WaitingRows, result.PayloadBytesRead);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Warn(ex, "Workflow recovery slice failed; durable page cursor will resume after retry");
            }

            var delay = PollInterval - cycleStarted.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }

    /// <summary>Runs one bounded hot-first slice. Internal visibility allows deterministic worker-level tests.</summary>
    internal Task<RecoverySliceResult> RunSliceAsync(CancellationToken cancellationToken, DateTime? localToday = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowUtc = DateTime.UtcNow;
        var today = (localToday ?? DateTime.Today).Date;
        var currentSettings = _settings.Get();
        var retentionDays = Math.Max(1, currentSettings.RetentionDays);
        var hotTo = today.AddDays(-1);
        var hotFrom = hotTo.AddDays(-2);
        var retentionFrom = hotTo.AddDays(-retentionDays + 1);
        var deepTo = hotFrom.AddDays(-1);
        var timer = Stopwatch.StartNew();
        var result = new MutableSliceResult();

        for (var page = 0; page < _budget.HotPagesPerSlice && timer.Elapsed < _budget.HotTimeBudget; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = ProcessHotPage(hotFrom, hotTo, retentionFrom, deepTo, currentSettings.PrtgEnabled,
                _ai.Available, nowUtc, cancellationToken);
            if (!outcome.Acquired || !outcome.Worked) break;
            result.HotRows += outcome.RawCount;
            result.Add(outcome);
            if (outcome.CompletedPass) break;
        }

        if (timer.Elapsed < _budget.TotalTimeBudget && retentionFrom <= deepTo && _budget.DeepPagesPerSlice > 0)
        {
            for (var page = 0; page < _budget.DeepPagesPerSlice && timer.Elapsed < _budget.TotalTimeBudget; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = ProcessDeepPage(retentionFrom, deepTo, currentSettings.PrtgEnabled, _ai.Available,
                    nowUtc, cancellationToken);
                if (!outcome.Acquired || !outcome.Worked) break;
                result.DeepRows += outcome.RawCount;
                result.Add(outcome);
                if (outcome.CompletedPass) break;
            }
        }

        if (timer.Elapsed < _budget.TotalTimeBudget && _budget.OrphanPagesPerSlice > 0)
        {
            for (var page = 0; page < _budget.OrphanPagesPerSlice && timer.Elapsed < _budget.TotalTimeBudget; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = ProcessOrphanPage(retentionFrom, hotTo, nowUtc);
                if (!outcome.Acquired || !outcome.Worked) break;
                result.OrphanRows += outcome.RawCount;
                if (outcome.CompletedPass) break;
            }
        }

        var cursor = ReadCursor();
        result.CumulativeWaitingRows = cursor.WaitingRows;
        return Task.FromResult(result.ToImmutable());
    }

    private PageOutcome ProcessHotPage(DateTime from, DateTime to, DateTime retentionFrom, DateTime deepTo,
        bool prtgEnabled, bool aiEnabled, DateTime nowUtc, CancellationToken cancellationToken)
    {
        PageOutcome? outcome = null;
        var acquired = _lease.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = ReadCursor();
            EnsureCursor(cursor, from, to, retentionFrom, deepTo);
            if (cursor.HotCompletedAtUtc != null)
            {
                cursor.HotAfterRecordId = 0;
                cursor.HotCompletedAtUtc = null;
                cursor.HotPassNumber++;
            }

            var page = _records.QueryWorkflowRecoveryPage(from, to, cursor.HotAfterRecordId, _budget.PageSize);
            if (page.RawCount == 0)
            {
                cursor.HotCompletedAtUtc = nowUtc;
                cursor.HotAfterRecordId = 0;
                WriteCursor(cursor);
                outcome = new PageOutcome(true, true, true, 0, page.WaitingHostDays.Count, page.PayloadBytesRead);
                return;
            }

            ValidateAdvance(cursor.HotAfterRecordId, page);
            Reconcile(page, prtgEnabled, aiEnabled, cursor.HotPassNumber, cancellationToken);
            Advance(cursor, page, nowUtc);
            WriteCursor(cursor);
            outcome = new PageOutcome(true, true, false, page.RawCount, page.WaitingHostDays.Count, page.PayloadBytesRead);
        }, TimeSpan.FromSeconds(1), runActionWhenNotAcquired: false);
        return acquired ? outcome ?? PageOutcome.NoWork : PageOutcome.NoWork;
    }

    private PageOutcome ProcessDeepPage(DateTime from, DateTime to, bool prtgEnabled, bool aiEnabled,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        PageOutcome? outcome = null;
        var acquired = _lease.RunExclusive(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = ReadCursor();
            var page = _records.QueryWorkflowRecoveryPage(from, to, cursor.DeepAfterRecordId, _budget.PageSize);
            if (page.RawCount == 0)
            {
                cursor.DeepAfterRecordId = 0;
                cursor.DeepPassNumber++;
                cursor.DeepLastCompletedAtUtc = nowUtc;
                WriteCursor(cursor);
                outcome = new PageOutcome(true, true, true, 0, page.WaitingHostDays.Count, page.PayloadBytesRead);
                return;
            }

            ValidateAdvance(cursor.DeepAfterRecordId, page);
            Reconcile(page, prtgEnabled, aiEnabled, cursor.DeepPassNumber, cancellationToken);
            Advance(cursor, page, nowUtc, deep: true);
            WriteCursor(cursor);
            outcome = new PageOutcome(true, true, false, page.RawCount, page.WaitingHostDays.Count, page.PayloadBytesRead);
        }, TimeSpan.FromSeconds(1), runActionWhenNotAcquired: false);
        return acquired ? outcome ?? PageOutcome.NoWork : PageOutcome.NoWork;
    }

    private PageOutcome ProcessOrphanPage(DateTime retentionFrom, DateTime retentionTo, DateTime nowUtc)
    {
        PageOutcome? outcome = null;
        var acquired = _lease.RunExclusive(() =>
        {
            var cursor = ReadCursor();
            if (cursor.OrphanNextSweepAtUtc is { } due && nowUtc < due)
            {
                outcome = PageOutcome.NoWork;
                return;
            }
            var page = new HostDayWorkflowStore(_backend).GetExistingHostDaysPage(cursor.AfterWorkflowKey, _budget.PageSize);
            if (page.RawCount == 0)
            {
                cursor.AfterWorkflowKey = null;
                cursor.OrphanNextSweepAtUtc = nowUtc.AddDays(1);
                cursor.OrphanPassNumber++;
                WriteCursor(cursor);
                outcome = new PageOutcome(true, true, true, 0, 0, 0);
                return;
            }

            var inWindow = page.Items.Where(key => key.Date >= retentionFrom && key.Date <= retentionTo).ToArray();
            var existing = _records.ExistingHostDays(inWindow.Select(key => (key.HostId, key.Date)).ToArray());
            var purged = 0;
            var store = new HostDayWorkflowStore(_backend);
            foreach (var key in page.Items)
                if (key.Date < retentionFrom || key.Date > retentionTo || !existing.Contains((key.HostId, key.Date.Date)))
                {
                    if (!_workflow.ParentDeletedIfMissingOrOutsideRetention(key.HostId, key.Date,
                            retentionFrom, retentionTo))
                        Log.Info("Workflow orphan snapshot was stale; retained the current parent: hostId={HostId} date={Date:yyyy-MM-dd}",
                            key.HostId, key.Date);
                    // 保留期外（不含未來日、不含保留期內的孤兒）的狀態 blob 在重置後一併刪除。
                    if (key.Date < retentionFrom)
                    {
                        try
                        {
                            if (store.Delete(key.HostId, key.Date)) purged++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(ex, "清除保留期外工作流狀態失敗：hostId={HostId} date={Date:yyyy-MM-dd}", key.HostId, key.Date);
                        }
                    }
                }
            if (purged > 0) Log.Info("清除保留期外工作流狀態 {Count} 筆", purged);
            cursor.AfterWorkflowKey = page.NextCursor;
            WriteCursor(cursor);
            outcome = new PageOutcome(true, true, false, page.RawCount, 0, 0);
        }, TimeSpan.FromSeconds(1), runActionWhenNotAcquired: false);
        return acquired ? outcome ?? PageOutcome.NoWork : PageOutcome.NoWork;
    }

    private void Reconcile(WorkflowRecoveryPage page, bool prtgEnabled, bool aiEnabled,
        long retryPassNumber, CancellationToken cancellationToken)
    {
        foreach (var waiting in page.WaitingHostDays)
        {
            Log.Warn("Workflow recovery leaves authoritative row pending: recordId={RecordId} hostId={HostId} date={Date:yyyy-MM-dd} reason={Reason} chars={Characters}",
                waiting.RecordId, waiting.HostId, waiting.Date, waiting.ReasonCode, waiting.ReportedPayloadCharacters);
            if (waiting.HostId > 0 && !_workflow.MarkRecoveryWaiting(waiting))
                Log.Info("Workflow recovery waiting snapshot was stale; preserved the newer workflow: recordId={RecordId} hostId={HostId} date={Date:yyyy-MM-dd}",
                    waiting.RecordId, waiting.HostId, waiting.Date);
        }
        foreach (var record in page.Records)
        {
            if (page.CapturedWriteRevisions == null || !page.CapturedWriteRevisions.TryGetValue(record.RecordId, out var revision))
            {
                Log.Warn("Workflow recovery skipped row without its captured SQL revision: recordId={RecordId}", record.RecordId);
                continue;
            }
            if (!_workflow.ReconcileAuthorityRecord(record, revision, prtgEnabled, aiEnabled))
                Log.Warn("Workflow recovery row changed after bounded read; it will be retried on a later pass: recordId={RecordId}", record.RecordId);
        }
        RetryPendingReports(page.Records, cancellationToken, retryPassNumber);
    }

    internal int RetryPendingReports(IReadOnlyList<DailyAnalysisRecord> records, CancellationToken cancellationToken,
        long passNumber = 0)
    {
        // This queue is independent from AiPending: while AI is configured, let its own consumer
        // finish an in-flight parent first. When AI is unavailable, still write the useful
        // non-AI report from the saved parent; neither path clears AiPending.
        var pending = SelectPendingReports(records, passNumber, _ai.Available);
        if (pending.Length == 0) return 0;

        var appSettings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(appSettings, _settings);
        // The retry renderer is explicitly called with allowAiDeepDive:false; this service is only
        // a dependency for report composition and never sends an AI request.
        var aiService = _reportAiService ?? new AIService(appSettings.Ai,
            usageMeter: new AiUsageStore(_backend.Blob(AiUsageStore.BlobKey)), gate: _aiGate);
        var reportStore = _backend.ReportStore();
        var suppressionStore = new MuteAwareSuppressionStore(
            new SuppressionStore(_backend.Blob("suppressions")),
            new IssueOwnerStore(_backend.Blob("issue_owners")));
        var hosts = new HostStore(_backend.Blob("hosts")).GetAll()
            .GroupBy(host => host.HostId).ToDictionary(group => group.Key, group => group.First());
        var completed = 0;
        foreach (var record in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var host = hosts.GetValueOrDefault(record.HostId);
                var analysis = new LogAnalysisService(new EventLogService(), aiService,
                    _backend.RecordStore(new HostKey { HostId = record.HostId, HostName = record.Host }),
                    suppressionStore, reportService: new RiskReportService(aiService, reportStore,
                        appSettings.Ai.DeepDiveMaxTokens),
                    host: record.Host, hostId: record.HostId, riskyEventStore: _backend.RiskyEventStore(),
                    hostGroupIds: host?.GroupIds);
                if (analysis.FinalizePendingRiskReportAsync(record, cancellationToken).GetAwaiter().GetResult())
                    completed++;
                else
                    Log.Warn("Pending report remains safely unattached for host-day record {RecordId}", record.RecordId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn(ex, "Pending report retry failed for host-day record {RecordId}; durable marker remains", record.RecordId);
            }
        }
        return completed;
    }

    internal static DailyAnalysisRecord[] SelectPendingReports(IReadOnlyList<DailyAnalysisRecord> records,
        long passNumber, bool aiAvailable)
    {
        if (passNumber < 0) throw new ArgumentOutOfRangeException(nameof(passNumber));
        var pending = records.Where(record => record.RiskReportPending && record.RecordId > 0 &&
                (!record.AiPending || !aiAvailable))
            .OrderBy(record => record.RecordId)
            .ToArray();
        if (pending.Length <= MaxPendingReportsPerPage) return pending;

        // The pass ordinal is already durable in the recovery cursor. Rotate a bounded page-sized
        // window so a repeatedly failing low RecordId cannot starve later pending reports.
        var first = (int)(((passNumber % pending.Length) * MaxPendingReportsPerPage) % pending.Length);
        var selected = new DailyAnalysisRecord[MaxPendingReportsPerPage];
        for (var index = 0; index < selected.Length; index++)
            selected[index] = pending[(first + index) % pending.Length];
        return selected;
    }

    private static void EnsureCursor(WorkflowRecoveryCursor cursor, DateTime hotFrom, DateTime hotTo,
        DateTime retentionFrom, DateTime deepTo)
    {
        if (cursor.Version != 2)
        {
            cursor.Version = 2;
            cursor.HotAfterRecordId = 0;
            cursor.DeepAfterRecordId = 0;
            cursor.AfterWorkflowKey = null;
            cursor.HotCompletedAtUtc = null;
            cursor.OrphanNextSweepAtUtc = null;
        }
        if (cursor.HotFrom != hotFrom || cursor.HotTo != hotTo)
        {
            cursor.HotFrom = hotFrom;
            cursor.HotTo = hotTo;
            cursor.HotAfterRecordId = 0;
            cursor.HotCompletedAtUtc = null;
        }
        // The deep keyset is deliberately retained across rolling range changes. Older additions are picked up
        // when the current pass wraps; keeping the cursor prevents a daily lower-bound shift from starving it.
        cursor.RetentionFrom = retentionFrom;
        cursor.DeepTo = deepTo;
    }

    private static void ValidateAdvance(long afterRecordId, WorkflowRecoveryPage page)
    {
        var representedIds = page.Records.Select(record => record.RecordId)
            .Concat(page.WaitingHostDays.Select(row => row.RecordId)).ToArray();
        var maximumPayloadBytes = (long)WorkflowRecoveryPage.MaximumRows *
            WorkflowRecoveryPage.MaximumPayloadPrefixCharacters * 4;
        var missingRevision = page.Records.Any(record => page.CapturedWriteRevisions == null ||
            !page.CapturedWriteRevisions.TryGetValue(record.RecordId, out var revision) || revision < 0);
        var invalidWaitingFence = page.WaitingHostDays.Any(waiting => waiting.CapturedWriteRevision < 0 ||
            waiting.CapturedAtUtc == default || waiting.CapturedAtUtc.Kind != DateTimeKind.Utc);
        if (page.RawCount is < 1 or > WorkflowRecoveryPage.MaximumRows || representedIds.Length != page.RawCount ||
            representedIds.Distinct().Count() != page.RawCount || representedIds.Any(id => id <= afterRecordId) ||
            page.NextRecordId is not { } next || next <= afterRecordId || representedIds.Max() != next ||
            page.PayloadBytesRead < 0 || page.PayloadBytesRead > maximumPayloadBytes || missingRevision || invalidWaitingFence)
            throw new InvalidDataException("Workflow recovery query returned a page without a strictly advancing bounded cursor.");
    }

    private static void Advance(WorkflowRecoveryCursor cursor, WorkflowRecoveryPage page, DateTime nowUtc, bool deep = false)
    {
        var next = page.NextRecordId!.Value;
        if (deep) cursor.DeepAfterRecordId = next;
        else cursor.HotAfterRecordId = next;
        cursor.ScannedRows += page.RawCount;
        cursor.PayloadBytesRead += page.PayloadBytesRead;
        cursor.WaitingRows += page.WaitingHostDays.Count;
        if (page.WaitingHostDays.Count > 0)
        {
            var last = page.WaitingHostDays[^1];
            cursor.LastWaitingRecordId = last.RecordId;
            cursor.LastWaitingHostId = last.HostId;
            cursor.LastWaitingDate = last.Date;
            cursor.LastWaitingReason = last.ReasonCode;
        }
        cursor.LastProgressAtUtc = nowUtc;
    }

    private WorkflowRecoveryCursor ReadCursor()
    {
        var raw = _backend.Blob(CheckpointKey).Read();
        return string.IsNullOrWhiteSpace(raw)
            ? new WorkflowRecoveryCursor()
            : JsonSerializer.Deserialize<WorkflowRecoveryCursor>(raw) ?? new WorkflowRecoveryCursor();
    }

    private void WriteCursor(WorkflowRecoveryCursor cursor) => _backend.Blob(CheckpointKey)
        .Mutate(raw => (JsonSerializer.Serialize(MergeCursor(
            string.IsNullOrWhiteSpace(raw) ? new WorkflowRecoveryCursor() :
                JsonSerializer.Deserialize<WorkflowRecoveryCursor>(raw) ?? new WorkflowRecoveryCursor(), cursor)), true));

    private static WorkflowRecoveryCursor MergeCursor(WorkflowRecoveryCursor current, WorkflowRecoveryCursor incoming)
    {
        if (current.Version != 2 || incoming.Version != 2) return incoming;
        MergeHot(current, incoming);
        MergeDeep(current, incoming);
        MergeOrphans(current, incoming);
        current.ScannedRows = Math.Max(current.ScannedRows, incoming.ScannedRows);
        current.PayloadBytesRead = Math.Max(current.PayloadBytesRead, incoming.PayloadBytesRead);
        current.WaitingRows = Math.Max(current.WaitingRows, incoming.WaitingRows);
        if (incoming.LastWaitingRecordId > current.LastWaitingRecordId)
        {
            current.LastWaitingRecordId = incoming.LastWaitingRecordId;
            current.LastWaitingHostId = incoming.LastWaitingHostId;
            current.LastWaitingDate = incoming.LastWaitingDate;
            current.LastWaitingReason = incoming.LastWaitingReason;
        }
        if (incoming.LastProgressAtUtc > current.LastProgressAtUtc) current.LastProgressAtUtc = incoming.LastProgressAtUtc;
        return current;
    }

    private static void MergeHot(WorkflowRecoveryCursor current, WorkflowRecoveryCursor incoming)
    {
        if (incoming.HotFrom > current.HotFrom || incoming.HotTo > current.HotTo)
        {
            CopyHot(current, incoming);
            return;
        }
        if (incoming.HotFrom != current.HotFrom || incoming.HotTo != current.HotTo) return;
        if (incoming.HotPassNumber > current.HotPassNumber)
        {
            CopyHot(current, incoming);
            return;
        }
        if (incoming.HotPassNumber < current.HotPassNumber || current.HotCompletedAtUtc != null && incoming.HotCompletedAtUtc == null)
            return;
        if (incoming.HotCompletedAtUtc != null)
        {
            current.HotCompletedAtUtc = incoming.HotCompletedAtUtc;
            current.HotAfterRecordId = 0;
        }
        else current.HotAfterRecordId = Math.Max(current.HotAfterRecordId, incoming.HotAfterRecordId);
    }

    private static void CopyHot(WorkflowRecoveryCursor target, WorkflowRecoveryCursor source)
    {
        target.HotFrom = source.HotFrom;
        target.HotTo = source.HotTo;
        target.HotAfterRecordId = source.HotAfterRecordId;
        target.HotCompletedAtUtc = source.HotCompletedAtUtc;
        target.HotPassNumber = source.HotPassNumber;
    }

    private static void MergeDeep(WorkflowRecoveryCursor current, WorkflowRecoveryCursor incoming)
    {
        if (incoming.DeepPassNumber > current.DeepPassNumber)
        {
            CopyDeep(current, incoming);
            return;
        }
        if (incoming.DeepPassNumber < current.DeepPassNumber) return;
        current.DeepAfterRecordId = Math.Max(current.DeepAfterRecordId, incoming.DeepAfterRecordId);
        if (incoming.DeepLastCompletedAtUtc > current.DeepLastCompletedAtUtc)
            current.DeepLastCompletedAtUtc = incoming.DeepLastCompletedAtUtc;
    }

    private static void CopyDeep(WorkflowRecoveryCursor target, WorkflowRecoveryCursor source)
    {
        target.DeepAfterRecordId = source.DeepAfterRecordId;
        target.DeepPassNumber = source.DeepPassNumber;
        target.DeepLastCompletedAtUtc = source.DeepLastCompletedAtUtc;
    }

    private static void MergeOrphans(WorkflowRecoveryCursor current, WorkflowRecoveryCursor incoming)
    {
        if (incoming.OrphanPassNumber > current.OrphanPassNumber)
        {
            CopyOrphans(current, incoming);
            return;
        }
        if (incoming.OrphanPassNumber < current.OrphanPassNumber ||
            current.OrphanNextSweepAtUtc != null && incoming.OrphanNextSweepAtUtc == null) return;
        if (incoming.OrphanNextSweepAtUtc != null)
        {
            current.AfterWorkflowKey = null;
            current.OrphanNextSweepAtUtc = incoming.OrphanNextSweepAtUtc;
        }
        else if (StringComparer.Ordinal.Compare(incoming.AfterWorkflowKey, current.AfterWorkflowKey) > 0)
            current.AfterWorkflowKey = incoming.AfterWorkflowKey;
    }

    private static void CopyOrphans(WorkflowRecoveryCursor target, WorkflowRecoveryCursor source)
    {
        target.AfterWorkflowKey = source.AfterWorkflowKey;
        target.OrphanPassNumber = source.OrphanPassNumber;
        target.OrphanNextSweepAtUtc = source.OrphanNextSweepAtUtc;
    }

    internal sealed record RecoveryBudget(int PageSize, int HotPagesPerSlice, int DeepPagesPerSlice,
        int OrphanPagesPerSlice, TimeSpan HotTimeBudget, TimeSpan TotalTimeBudget)
    {
        public static RecoveryBudget Default { get; } = new(100, 90, 1, 1,
            TimeSpan.FromMinutes(6.5), TimeSpan.FromMinutes(7.5));
    }

    internal sealed record RecoverySliceResult(int HotRows, int DeepRows, int OrphanRows, int WaitingRows,
        long PayloadBytesRead, long CumulativeWaitingRows);

    private sealed class MutableSliceResult
    {
        public int HotRows;
        public int DeepRows;
        public int OrphanRows;
        public int WaitingRows;
        public long PayloadBytesRead;
        public long CumulativeWaitingRows;
        public void Add(PageOutcome outcome)
        {
            WaitingRows += outcome.WaitingRows;
            PayloadBytesRead += outcome.PayloadBytesRead;
        }
        public RecoverySliceResult ToImmutable() => new(HotRows, DeepRows, OrphanRows, WaitingRows,
            PayloadBytesRead, CumulativeWaitingRows);
    }

    private sealed record PageOutcome(bool Acquired, bool Worked, bool CompletedPass, int RawCount,
        int WaitingRows, long PayloadBytesRead)
    {
        public static PageOutcome NoWork { get; } = new(false, false, false, 0, 0, 0);
    }

    private sealed class WorkflowRecoveryCursor
    {
        public int Version { get; set; }
        public DateTime HotFrom { get; set; }
        public DateTime HotTo { get; set; }
        public long HotAfterRecordId { get; set; }
        public DateTime? HotCompletedAtUtc { get; set; }
        public long HotPassNumber { get; set; }
        public DateTime RetentionFrom { get; set; }
        public DateTime DeepTo { get; set; }
        public long DeepAfterRecordId { get; set; }
        public long DeepPassNumber { get; set; }
        public DateTime? DeepLastCompletedAtUtc { get; set; }
        public string? AfterWorkflowKey { get; set; }
        public DateTime? OrphanNextSweepAtUtc { get; set; }
        public long OrphanPassNumber { get; set; }
        public long ScannedRows { get; set; }
        public long PayloadBytesRead { get; set; }
        public long WaitingRows { get; set; }
        public long? LastWaitingRecordId { get; set; }
        public long? LastWaitingHostId { get; set; }
        public DateTime? LastWaitingDate { get; set; }
        public string? LastWaitingReason { get; set; }
        public DateTime? LastProgressAtUtc { get; set; }
    }
}

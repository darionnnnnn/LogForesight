using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>Durable job overview and 100-sensor pages; the overview shares the historic-admission row.</summary>
public sealed class PrtgQualificationJobStateStore(StorageBackend backend)
{
    public const int PageSize = 100;
    public const int MaximumSensors = 15_000;
    public const int MaximumOverviewBytes = PrtgHistoricAdmissionStore.MaximumJsonBytes;
    public const int MaximumPageBytes = 64 * 1024;
    private readonly EfJsonBlobStore overviewBlob = backend.Blob(PrtgHistoricAdmissionStore.BlobKey, serializeSqlServerWriters: true);

    public sealed class Job
    {
        public string JobId { get; set; } = "";
        public string Status { get; set; } = "initializing";
        public string ScopeFingerprint { get; set; } = "";
        public string SourceGeneration { get; set; } = "";
        public string SettingsRevision { get; set; } = "";
        public string PolicyRevision { get; set; } = "";
        public string AuthorityContextFingerprint { get; set; } = "";
        public string Owner { get; set; } = "";
        public long Version { get; set; }
        /// <summary>Physical overview version attached to read snapshots; distinct from the lease/wave version.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public long ReadStateVersion { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public DateTimeOffset LeaseUntilUtc { get; set; }
        public int DurationHours { get; set; }
        public int Wave { get; set; }
        public int PageCount { get; set; }
        public int InitializedPages { get; set; }
        public int InitializationCursor { get; set; }
        public int AdmittedEligibleSensorCount { get; set; }
        public int InitializedEligibleSensorCount { get; set; }
        public int Cursor { get; set; }
        public int Selected { get; set; }
        public int Eligible { get; set; }
        public int Qualified { get; set; }
        public int Waiting { get; set; }
        public int Failed { get; set; }
        public int Attempts { get; set; }
        public int TotalFailed { get; set; }
        public int MaximumAttempts { get; set; } = 3;
        public string? WaitingReason { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public bool CancelRequested { get; set; }
    }

public sealed class Sensor
    {
        public long SensorObjid { get; set; }
        public long BindingRevision { get; set; }
        public string BindingFingerprint { get; set; } = "";
        public string Status { get; set; } = "waiting";
        public string Reason { get; set; } = "qualification-required";
        public int Attempts { get; set; }
        public int WaveAttempts { get; set; }
        public bool HasQualificationProof { get; set; }
        public long QualifiedIdentityEpoch { get; set; }
        public string QualifiedChannelGeneration { get; set; } = "";
        public string? PreviousReason { get; set; }
        public DateTimeOffset? NextAttemptUtc { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
    }

    public sealed class Page
    {
        public string JobId { get; set; } = "";
        public int Index { get; set; }
        public List<Sensor> Sensors { get; set; } = [];
    }

    public Job? ReadCurrent()
    {
        var state = ReadOverview();
        return state.Job;
    }

    public void ExpireIfDeadlinePassed(DateTimeOffset nowUtc)
    {
        overviewBlob.MutateWithContext((_, raw) =>
        {
            var state = ParseOverview(raw);
            var job = state.Job;
            if (job is not null && (job.Status is "initializing" or "running" or "waiting-capacity") && job.DeadlineUtc <= nowUtc)
            {
                job.Status = "expired";
                job.CompletedAtUtc = nowUtc;
                job.UpdatedAtUtc = nowUtc;
                job.LeaseUntilUtc = nowUtc;
                state.ActiveQualification = null;
                state.Pending.RemoveAll(row => row.JobId == job.JobId);
            }
            return (Serialize(state), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
    }

    public PrtgHistoricAdmissionStore.State ReadOverview()
    {
        using var ctx = backend.CreateContext();
        var rows = ReadBoundedRows(ctx, [PrtgHistoricAdmissionStore.BlobKey], MaximumOverviewBytes,
            "qualification-job-overview-cap-exceeded");
        var json = rows.TryGetValue(PrtgHistoricAdmissionStore.BlobKey, out var row) ? row.Content : null;
        var state = ParseOverview(json);
        if (state.Job is { } job) job.ReadStateVersion = row.Version;
        return state;
    }

    public Page ReadPage(string jobId, int pageIndex)
    {
        if (!SafeJobId(jobId) || pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var ctx = backend.CreateContext();
        var rows = ReadBoundedRows(ctx, [PageKey(jobId, pageIndex)], MaximumPageBytes,
            "qualification-job-page-cap-exceeded");
        var json = rows.TryGetValue(PageKey(jobId, pageIndex), out var row) ? row.Content : null;
        var page = string.IsNullOrWhiteSpace(json) ? new Page() : ParsePage(json);
        if (page.JobId != jobId || page.Index != pageIndex || page.Sensors.Count > PageSize)
            throw new InvalidDataException("qualification-job-page-fence-invalid");
        return page;
    }

    public static bool IsRunnableNow(Sensor sensor, Job job, DateTimeOffset nowUtc) =>
        sensor.BindingRevision > 0 && sensor.Status == "waiting" && sensor.WaveAttempts < job.MaximumAttempts &&
        (!sensor.NextAttemptUtc.HasValue || sensor.NextAttemptUtc.Value <= nowUtc);

    public (Page Page, long Version) ReadPageWithVersion(string jobId, int pageIndex)
    {
        if (!SafeJobId(jobId) || pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var ctx = backend.CreateContext();
        var key = PageKey(jobId, pageIndex);
        var rows = ReadBoundedRows(ctx, [key], MaximumPageBytes, "qualification-job-page-cap-exceeded");
        var exists = rows.TryGetValue(key, out var row);
        var json = exists ? row.Content : null;
        var version = exists ? row.Version : 0;
        var page = string.IsNullOrWhiteSpace(json) ? new Page() : ParsePage(json);
        if (page.JobId != jobId || page.Index != pageIndex || page.Sensors.Count > PageSize)
            throw new InvalidDataException("qualification-job-page-fence-invalid");
        return (page, version);
    }

    public Job Start(string jobId, string owner, string scopeFingerprint, string sourceGeneration,
        string settingsRevision, string policyRevision, string authorityContextFingerprint,
        int selected, int admittedEligibleSensorCount, int durationHours, DateTimeOffset nowUtc, int maximumAttempts = 3)
    {
        if (!SafeJobId(jobId) || string.IsNullOrWhiteSpace(owner) || owner.Length > 64 ||
            !Hex64(scopeFingerprint) || !SafeOpaque(sourceGeneration) || string.IsNullOrWhiteSpace(settingsRevision) ||
            string.IsNullOrWhiteSpace(policyRevision) || !Hex64(authorityContextFingerprint) ||
            selected is < 1 or > MaximumSensors || admittedEligibleSensorCount < 0 || admittedEligibleSensorCount > selected ||
            durationHours is < 1 or > 720 || maximumAttempts is < 1 or > 3 ||
            nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("qualification-job-start-invalid");
        Job? started = null;
        overviewBlob.MutateWithContext((ctx, raw) =>
        {
            var overview = ParseOverview(raw);
            if (overview.Job is { } existing && existing.Status is "initializing" or "running" or "waiting-capacity" or "cancelling")
                throw new InvalidOperationException("qualification-job-already-active");
            if (overview.Job is { } previous && previous.JobId != jobId)
                ctx.Blobs.Where(row => row.BlobKey.StartsWith($"prtg_qualjob_{previous.JobId}_")).ExecuteDelete();
            var version = checked((overview.Job?.Version ?? 0) + 1);
            started = new Job
            {
                JobId = jobId, Status = "initializing", ScopeFingerprint = scopeFingerprint,
                SourceGeneration = sourceGeneration, SettingsRevision = settingsRevision,
                PolicyRevision = policyRevision, AuthorityContextFingerprint = authorityContextFingerprint,
                Owner = owner, Version = version, StartedAtUtc = nowUtc, UpdatedAtUtc = nowUtc,
                DeadlineUtc = nowUtc.AddHours(durationHours), LeaseUntilUtc = nowUtc.AddSeconds(120),
                DurationHours = durationHours, PageCount = (selected + PageSize - 1) / PageSize,
                Selected = selected, AdmittedEligibleSensorCount = admittedEligibleSensorCount,
                MaximumAttempts = maximumAttempts, Wave = 1
            };
            overview.Job = started;
            overview.ActiveQualification = new PrtgHistoricAdmissionStore.ActiveQualification
            {
                JobId = jobId, Owner = owner, LeaseVersion = version,
                DeadlineUtc = started.DeadlineUtc, LeaseUntilUtc = started.LeaseUntilUtc,
                QualificationLimit = 1, OtherLimit = 4
            };
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
        return started!;
    }

    public bool TryAcquireLease(string owner, DateTimeOffset nowUtc, TimeSpan duration, out Job? acquired)
    {
        Job? snapshot = null;
        var ok = overviewBlob.MutateWithContext((_, raw) =>
        {
            var overview = ParseOverview(raw);
            var job = overview.Job;
            if (job is null || job.Status is not ("initializing" or "running" or "waiting-capacity") ||
                job.CancelRequested || job.DeadlineUtc <= nowUtc || job.LeaseUntilUtc > nowUtc)
                return (Serialize(overview), false);
            job.Version = checked(job.Version + 1);
            job.Owner = owner;
            job.LeaseUntilUtc = Min(nowUtc + duration, job.DeadlineUtc);
            job.UpdatedAtUtc = nowUtc;
            if (overview.ActiveQualification is { } quota && quota.JobId == job.JobId)
            { quota.Owner = owner; quota.LeaseVersion = job.Version; quota.DeadlineUtc = job.DeadlineUtc; quota.LeaseUntilUtc = job.LeaseUntilUtc; }
            snapshot = Copy(job);
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
        acquired = snapshot;
        return ok;
    }

    private static string? ReadResumeAuthority(LfDbContext ctx, string key)
    {
        const int cap = PrtgTrustedSamplingBindingStore.MaximumAuthorityCharacters;
        var row = EfJsonBlobStore.ReadBoundedInContext(ctx,key,cap);
        if (row.Version == 0 && row.ReportedLength == 0 && row.Prefix is null) return null;
        if (row.ReportedLength > cap || row.Prefix is null)
            throw new InvalidDataException("qualification-job-authority-cap-exceeded");
        return row.Prefix;
    }

    /// <summary>Starts a new explicitly approved wave on the same durable job and existing pages.</summary>
    public Job ResumeExisting(string jobId, long expectedVersion, string owner, string expectedScopeFingerprint,
        string expectedSourceGeneration, string expectedSettingsRevision, string expectedPolicyRevision,
        string expectedAuthorityContextFingerprint, int admittedEligibleSensorCount, int durationHours, int maximumAttempts,
        DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        if (admittedEligibleSensorCount < 0 || admittedEligibleSensorCount > MaximumSensors ||
            durationHours is < 1 or > 720 || maximumAttempts is < 1 or > 3 ||
            nowUtc.Offset != TimeSpan.Zero || leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(durationHours));
        Job? resumed = null;
        overviewBlob.MutateWithContext((ctx, raw) =>
        {
            var overview = ParseOverview(raw);
            var job = overview.Job ?? throw new InvalidOperationException("qualification-job-missing");
            if (job.JobId != jobId || job.Version != expectedVersion ||
                job.Status is not ("completed" or "completed-with-waiting" or "expired" or "cancelled") ||
                job.ScopeFingerprint != expectedScopeFingerprint || job.SourceGeneration != expectedSourceGeneration ||
                job.SettingsRevision != expectedSettingsRevision ||
                job.AuthorityContextFingerprint != expectedAuthorityContextFingerprint ||
                job.InitializedPages < 0 || job.InitializedPages > job.PageCount ||
                job.InitializationCursor != job.InitializedPages)
                throw new InvalidOperationException("qualification-job-resume-contract-changed");

            var settingsRaw = ReadResumeAuthority(ctx, "system_settings");
            var policyRaw = ReadResumeAuthority(ctx, PrtgMonitoringPolicyStore.BlobKey);
            if (!string.IsNullOrWhiteSpace(settingsRaw)) StrictAuthorityJsonDocument.Validate<SystemSettings>(settingsRaw);
            if (!string.IsNullOrWhiteSpace(policyRaw)) StrictAuthorityJsonDocument.Validate<PrtgMonitoringPolicy>(policyRaw);
            var settings = string.IsNullOrWhiteSpace(settingsRaw) ? new SystemSettings() :
                JsonSerializer.Deserialize<SystemSettings>(settingsRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("system-settings-invalid");
            var policy = string.IsNullOrWhiteSpace(policyRaw) ? new PrtgMonitoringPolicy() :
                JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("prtg-policy-invalid");
            var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
            var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
            var contextFingerprint = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                policy, strategyName, strategyMinutes);
            if (!settings.PrtgEnabled || settings.Revision != expectedSettingsRevision ||
                !policy.Ready(settings.PrtgUrl) || policy.Revision != expectedPolicyRevision ||
                policy.SourceGeneration != expectedSourceGeneration || contextFingerprint != expectedAuthorityContextFingerprint)
                throw new InvalidOperationException("qualification-job-resume-source-context-changed");

            if (PrtgRawWorkScope.ReadInContext(ctx, policy) != expectedScopeFingerprint)
                throw new InvalidOperationException("qualification-job-resume-scope-changed");
            var expectedIds = policy.SensorIds.Where(id => id > 0).Distinct().Order().ToArray();
            if (expectedIds.Length != job.Selected || expectedIds.Length > MaximumSensors)
                throw new InvalidOperationException("qualification-job-resume-scope-changed");
            var qualified = 0; var waiting = 0; var eligible = 0; var failed = 0; var ordinal = 0;
            for (var pageIndex = 0; pageIndex < job.InitializedPages; pageIndex++)
            {
                var pageKey = PageKey(jobId, pageIndex);
                var pageRows = ReadBoundedRows(ctx, [pageKey], MaximumPageBytes,
                    "qualification-job-page-cap-exceeded");
                if (!pageRows.TryGetValue(pageKey, out var boundedPage))
                    throw new InvalidOperationException("qualification-job-resume-page-missing");
                var pageRow = AttachBoundedRow(ctx, pageKey, boundedPage, "qualification-job-page-version-changed");
                EnsureSameBoundedRow(pageRow, boundedPage, "qualification-job-page-version-changed");
                var page = ParsePage(pageRow.Content);
                var expectedCount = Math.Min(PageSize, job.Selected - ordinal);
                if (page.JobId != jobId || page.Index != pageIndex || page.Sensors.Count != expectedCount)
                    throw new InvalidDataException("qualification-job-resume-page-shape-invalid");
                var pageIds = page.Sensors.Select(sensor => sensor.SensorObjid).ToArray();
                var bindingKeys = pageIds.Select(sensorId => PrtgTrustedSamplingBinding.StorePrefix +
                    sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var bindingRows = ReadBoundedRows(ctx, bindingKeys, PrtgTrustedSamplingBinding.MaximumSerializedBytes,
                    "qualification-binding-cap-exceeded");
                var identityKeys = pageIds.Select(sensorId => PrtgResourceIdentityStore.Prefix +
                    sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var identityRows = ReadBoundedRows(ctx, identityKeys, PrtgResourceIdentityStore.MaxLedgerBytes,
                    "qualification-identity-cap-exceeded");
                for (var local = 0; local < page.Sensors.Count; local++, ordinal++)
                {
                    var sensor = page.Sensors[local];
                    if (sensor.SensorObjid != expectedIds[ordinal])
                        throw new InvalidOperationException("qualification-job-resume-scope-changed");
                    var bindingKey = PrtgTrustedSamplingBinding.StorePrefix +
                        sensor.SensorObjid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    PrtgTrustedSamplingBinding? binding = null;
                    if (bindingRows.TryGetValue(bindingKey, out var bindingRow))
                    {
                        if (bindingRow.Content is null) throw new InvalidDataException("qualification-binding-cap-exceeded");
                        binding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(bindingRow.Content,
                            LfJsonOptions.Pretty) ?? throw new InvalidDataException("qualification-binding-invalid");
                        if (binding.SensorObjid != sensor.SensorObjid)
                            throw new InvalidDataException("qualification-binding-sensor-mismatch");
                        binding.Validate();
                    }
                    if (binding is null)
                    {
                        if (sensor.BindingRevision != 0 || sensor.HasQualificationProof)
                            throw new InvalidOperationException("qualification-job-resume-binding-missing");
                        if (sensor.Status == "waiting") waiting++;
                        else failed++;
                        continue;
                    }
                    var identityKey = PrtgResourceIdentityStore.Prefix +
                        sensor.SensorObjid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    BlobRow? identityBlob = null;
                    if (identityRows.TryGetValue(identityKey, out var identityRow))
                    {
                        if (identityRow.Content is null) throw new InvalidDataException("qualification-identity-cap-exceeded");
                        identityBlob = new BlobRow { BlobKey = identityKey, Content = identityRow.Content,
                            Version = identityRow.Version, UpdatedAt = identityRow.UpdatedAt };
                    }
                    var identity = PrtgResourceIdentityStore.ReadLoaded(sensor.SensorObjid, identityBlob);
                    if (sensor.BindingRevision != binding.BindingRevision || sensor.BindingFingerprint != binding.BindingFingerprint ||
                        sensor.HasQualificationProof != !string.IsNullOrWhiteSpace(binding.QualificationProofReference) ||
                        !identity.Active || identity.PendingReconciliation || identity.SourceGeneration != expectedSourceGeneration ||
                        identity.ChannelFingerprint != binding.BindingFingerprint ||
                        !policy.HostIds.Contains(identity.HostId) ||
                        !binding.Matches(sensor.SensorObjid, expectedAuthorityContextFingerprint, expectedSourceGeneration,
                            identity.Generation, identity.Epoch, identity.ChannelGeneration) ||
                        binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
                        binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId ||
                        binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference)
                        throw new InvalidOperationException("qualification-job-resume-binding-or-identity-changed");

                    if (sensor.HasQualificationProof && sensor.Status == "qualified") { qualified++; continue; }
                    if (sensor.HasQualificationProof) { failed++; continue; }
                    sensor.PreviousReason = sensor.Reason;
                    sensor.Status = "waiting";
                    sensor.WaveAttempts = 0;
                    sensor.NextAttemptUtc = null;
                    sensor.Reason = "explicit-resume-new-wave";
                    waiting++; eligible++;
                }
                pageRow.Content = SerializePage(page);
                pageRow.UpdatedAt = nowUtc.UtcDateTime;
                pageRow.Version++;
            }
            if (ordinal != Math.Min(job.Selected, job.InitializedPages * PageSize))
                throw new InvalidOperationException("qualification-job-resume-scope-count-changed");
            job.Version = checked(job.Version + 1);
            job.Wave = checked(job.Wave + 1);
            job.Owner = owner;
            job.LeaseUntilUtc = Min(nowUtc + leaseDuration, nowUtc.AddHours(durationHours));
            job.DeadlineUtc = nowUtc.AddHours(durationHours);
            job.DurationHours = durationHours;
            job.MaximumAttempts = maximumAttempts;
            job.Status = job.InitializedPages == job.PageCount ? "running" : "initializing";
            job.StartedAtUtc = nowUtc;
            job.CompletedAtUtc = null;
            job.UpdatedAtUtc = nowUtc;
            job.CancelRequested = false;
            job.WaitingReason = null;
            job.Qualified = qualified;
            job.Waiting = waiting;
            job.Eligible = eligible;
            if (eligible > admittedEligibleSensorCount)
                throw new InvalidOperationException("qualification-job-capacity-contract-changed");
            job.AdmittedEligibleSensorCount = admittedEligibleSensorCount;
            job.InitializedEligibleSensorCount = eligible;
            job.Failed = failed;
            overview.Pending.RemoveAll(row => row.JobId == jobId);
            overview.ActiveQualification = new PrtgHistoricAdmissionStore.ActiveQualification
            {
                JobId = jobId, Owner = owner, LeaseVersion = job.Version, DeadlineUtc = job.DeadlineUtc,
                LeaseUntilUtc = job.LeaseUntilUtc,
                QualificationLimit = 1, OtherLimit = 4
            };
            resumed = Copy(job);
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
        return resumed!;
    }

    public void Renew(string jobId, string owner, long version, DateTimeOffset nowUtc, TimeSpan duration)
    {
        MutateFenced(jobId, owner, version, nowUtc, overview =>
        {
            overview.Job!.LeaseUntilUtc = Min(nowUtc + duration, overview.Job.DeadlineUtc);
            overview.Job.UpdatedAtUtc = nowUtc;
            if (overview.ActiveQualification is { } quota && quota.JobId == jobId)
            { quota.DeadlineUtc = overview.Job.DeadlineUtc; quota.LeaseUntilUtc = overview.Job.LeaseUntilUtc; }
        });
    }

    public void ReleaseLease(string jobId, string owner, long version, DateTimeOffset nowUtc)
    {
        MutateFenced(jobId, owner, version, nowUtc, overview =>
        {
            overview.Job!.Owner = "";
            overview.Job.LeaseUntilUtc = nowUtc;
            overview.Job.UpdatedAtUtc = nowUtc;
            if (overview.ActiveQualification is { } quota && quota.JobId == jobId)
            { quota.LeaseVersion = overview.Job.Version; quota.LeaseUntilUtc = nowUtc; }
        });
    }

    public void SetCursor(string jobId, string owner, long version, DateTimeOffset nowUtc, int cursor)
    {
        MutateFenced(jobId, owner, version, nowUtc, overview =>
        {
            if (cursor < 0 || cursor >= Math.Max(1, overview.Job!.Selected)) throw new ArgumentOutOfRangeException(nameof(cursor));
            overview.Job.Cursor = cursor;
            overview.Job.UpdatedAtUtc = nowUtc;
        });
    }

    public void SetStatus(string jobId, string owner, long version, DateTimeOffset nowUtc,
        string status, string? reason = null, bool releaseQuota = false)
    {
        if (status is not ("running" or "waiting-capacity" or "failed-stale" or "completed" or
            "completed-with-waiting" or "expired"))
            throw new ArgumentOutOfRangeException(nameof(status));
        MutateFenced(jobId, owner, version, nowUtc, overview =>
        {
            var job = overview.Job!;
            job.Status = status;
            job.WaitingReason = SafeReason(reason ?? "");
            job.UpdatedAtUtc = nowUtc;
            if (status is "failed-stale" or "completed" or "completed-with-waiting" or "expired") job.CompletedAtUtc = nowUtc;
            if (releaseQuota)
            {
                overview.ActiveQualification = null;
                overview.Pending.RemoveAll(row => row.JobId == jobId);
            }
        });
    }

    public void RecordAttemptResult(PrtgQualificationWriteFence fence, DateTimeOffset nowUtc,
        string reason, bool permanentFailure = false)
    {
        overviewBlob.MutateWithContext((ctx, raw) =>
        {
            var overview = ParseOverview(raw);
            EnsureLease(overview.Job, fence.JobId, fence.Owner, fence.LeaseVersion, nowUtc);
            var job = overview.Job!;
            if (job.Status != "running" || job.ScopeFingerprint != fence.ScopeFingerprint ||
                job.SourceGeneration != fence.SourceGeneration || job.SettingsRevision != fence.SettingsRevision ||
                job.AuthorityContextFingerprint != fence.AuthorityContextFingerprint)
                throw new InvalidOperationException("qualification-job-contract-changed");
            var pageKey = PageKey(fence.JobId, fence.PageIndex);
            var boundedRows = ReadBoundedRows(ctx, [pageKey], MaximumPageBytes, "qualification-job-page-cap-exceeded");
            if (!boundedRows.TryGetValue(pageKey, out var boundedPage) || boundedPage.Version != fence.PageVersion)
                throw new InvalidOperationException("qualification-job-page-version-changed");
            var pageRow = AttachBoundedRow(ctx, pageKey, boundedPage, "qualification-job-page-version-changed");
            EnsureSameBoundedRow(pageRow, boundedPage, "qualification-job-page-version-changed");
            var page = ParsePage(pageRow.Content);
            var sensor = page.Sensors.SingleOrDefault(row => row.SensorObjid == fence.SensorObjid)
                ?? throw new InvalidOperationException("qualification-job-sensor-missing");
            if (sensor.BindingRevision != fence.BindingRevision || sensor.BindingFingerprint != fence.BindingFingerprint ||
                sensor.Status != "waiting" || sensor.WaveAttempts >= job.MaximumAttempts || sensor.NextAttemptUtc > nowUtc)
                throw new InvalidOperationException("qualification-job-sensor-fence-changed");
            sensor.Attempts++;
            sensor.WaveAttempts++;
            sensor.LastAttemptUtc = nowUtc;
            sensor.Reason = SafeReason(reason);
            job.Attempts++;
            if (permanentFailure || sensor.WaveAttempts >= job.MaximumAttempts)
            {
                sensor.Status = "failed";
                sensor.NextAttemptUtc = null;
                job.Failed++;
                job.TotalFailed++;
                job.Waiting = Math.Max(0, job.Waiting - 1);
            }
            else
            {
                sensor.Status = "waiting";
                sensor.NextAttemptUtc = nowUtc.Add(sensor.WaveAttempts == 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10));
            }
            job.Cursor = (fence.PageIndex * PageSize + page.Sensors.FindIndex(row => row.SensorObjid == fence.SensorObjid) + 1) %
                Math.Max(1, job.Selected);
            job.UpdatedAtUtc = nowUtc;
            pageRow.Content = SerializePage(page);
            pageRow.UpdatedAt = nowUtc.UtcDateTime;
            pageRow.Version++;
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
    }

    public void Cancel(string jobId, string owner, long expectedVersion, DateTimeOffset nowUtc, int? expectedWave = null)
    {
        overviewBlob.MutateWithContext((_, raw) =>
        {
            var overview = ParseOverview(raw);
            var job = overview.Job ?? throw new InvalidOperationException("qualification-job-missing");
            if (job.JobId != jobId || (expectedWave.HasValue ? job.Wave != expectedWave.Value : job.Version != expectedVersion) || job.DeadlineUtc <= nowUtc ||
                job.Status is not ("initializing" or "running" or "waiting-capacity"))
                throw new InvalidOperationException("qualification-job-stale");
            job.CancelRequested = true;
            job.Status = "cancelled";
            job.Owner = owner;
            job.LeaseUntilUtc = nowUtc;
            job.UpdatedAtUtc = nowUtc;
            job.CompletedAtUtc = nowUtc;
            overview.ActiveQualification = null;
            overview.Pending.RemoveAll(row => row.JobId == jobId);
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
    }

    /// <summary>Closes a stale active job only when an explicitly admitted new scope replaces it.</summary>
    public void InitializePage(string jobId, string owner, long version, DateTimeOffset nowUtc,
        int pageIndex, IReadOnlyList<Sensor> sensors)
    {
        if (sensors.Count is < 1 or > PageSize || pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        overviewBlob.MutateWithContext((ctx, raw) =>
        {
            var overview = ParseOverview(raw);
            EnsureLease(overview.Job, jobId, owner, version, nowUtc);
            var job = overview.Job!;
            if (job.Status != "initializing" || pageIndex >= job.PageCount) throw new InvalidOperationException("qualification-job-page-invalid");
            var key = PageKey(jobId, pageIndex);
            var boundedRows = ReadBoundedRows(ctx, [key], MaximumPageBytes, "qualification-job-page-cap-exceeded");
            var hasBoundedCurrent = boundedRows.TryGetValue(key, out var boundedCurrent);
            var current = hasBoundedCurrent
                ? AttachBoundedRow(ctx, key, boundedCurrent, "qualification-job-page-version-changed") : null;
            Page page;
            if (current is not null)
            {
                if (!hasBoundedCurrent) throw new InvalidOperationException("qualification-job-page-version-changed");
                EnsureSameBoundedRow(current, boundedCurrent, "qualification-job-page-version-changed");
                page = ParsePage(current.Content);
                if (page.JobId != jobId || page.Index != pageIndex ||
                !page.Sensors.Select(row => (row.SensorObjid, row.BindingRevision, row.BindingFingerprint))
                    .SequenceEqual(sensors.Select(row => (row.SensorObjid, row.BindingRevision, row.BindingFingerprint))))
                    throw new InvalidOperationException("qualification-job-page-reinitialize-mismatch");
            }
            else
            {
                var addedEligible = sensors.Count(row => row.BindingRevision > 0 && row.Status == "waiting");
                if (job.InitializedEligibleSensorCount + addedEligible > job.AdmittedEligibleSensorCount)
                    throw new InvalidOperationException("qualification-job-capacity-contract-changed");
                page = new Page { JobId = jobId, Index = pageIndex, Sensors = sensors.ToList() };
                var pageJson = SerializePage(page);
                ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = pageJson, UpdatedAt = nowUtc.UtcDateTime, Version = 1 });
                job.InitializedPages++;
                job.InitializationCursor = pageIndex + 1;
                job.InitializedEligibleSensorCount += addedEligible;
                job.Eligible += addedEligible;
                job.Waiting += sensors.Count(row => row.Status == "waiting");
                job.Qualified += sensors.Count(row => row.Status == "qualified");
                job.Failed += sensors.Count(row => row.Status == "failed");
            }
            if (job.InitializedPages == job.PageCount) job.Status = "running";
            job.UpdatedAtUtc = nowUtc;
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);
    }

    public void RecordQualificationInContext(Microsoft.EntityFrameworkCore.DbContext context,
        PrtgQualificationWriteFence fence, PrtgTrustedSamplingBinding qualified, DateTimeOffset nowUtc)
    {
        var db = (LfDbContext)context;
        // The binding proof caller takes this lock before any binding read. Reacquiring
        // in the same transaction also protects direct callers; commit releases both grants.
        EfJsonBlobStore.SerializeSqlServerWriters(db, PrtgHistoricAdmissionStore.BlobKey);
        var boundedOverviewRows = ReadBoundedRows(db, [PrtgHistoricAdmissionStore.BlobKey], MaximumOverviewBytes,
            "qualification-job-overview-cap-exceeded");
        if (!boundedOverviewRows.TryGetValue(PrtgHistoricAdmissionStore.BlobKey, out var boundedOverview))
            throw new InvalidOperationException("qualification-job-overview-missing");
        var overviewRow = AttachBoundedRow(db, PrtgHistoricAdmissionStore.BlobKey, boundedOverview,
            "qualification-job-overview-version-changed");
        EnsureSameBoundedRow(overviewRow, boundedOverview, "qualification-job-overview-version-changed");
        var overview = ParseOverview(overviewRow.Content);
        EnsureLease(overview.Job, fence.JobId, fence.Owner, fence.LeaseVersion, nowUtc);
        var job = overview.Job!;
        if (job.Status != "running" || job.ScopeFingerprint != fence.ScopeFingerprint ||
            job.SourceGeneration != fence.SourceGeneration || job.SettingsRevision != fence.SettingsRevision ||
            job.AuthorityContextFingerprint != fence.AuthorityContextFingerprint ||
            fence.PageIndex < 0 || fence.PageIndex >= job.PageCount)
            throw new InvalidOperationException("qualification-job-contract-changed");
        EnsureLiveRawJobAuthority(db, job);
        var pageKey = PageKey(fence.JobId, fence.PageIndex);
        var boundedPageRows = ReadBoundedRows(db, [pageKey], MaximumPageBytes, "qualification-job-page-cap-exceeded");
        if (!boundedPageRows.TryGetValue(pageKey, out var boundedPage) || boundedPage.Version != fence.PageVersion)
            throw new InvalidOperationException("qualification-job-page-version-changed");
        var pageRow = AttachBoundedRow(db, pageKey, boundedPage, "qualification-job-page-version-changed");
        EnsureSameBoundedRow(pageRow, boundedPage, "qualification-job-page-version-changed");
        var page = ParsePage(pageRow.Content);
        var sensor = page.Sensors.SingleOrDefault(row => row.SensorObjid == fence.SensorObjid)
            ?? throw new InvalidOperationException("qualification-job-sensor-missing");
        if (sensor.BindingRevision != fence.BindingRevision || sensor.BindingFingerprint != fence.BindingFingerprint ||
            sensor.Status != "waiting" || sensor.WaveAttempts >= job.MaximumAttempts ||
            sensor.NextAttemptUtc > nowUtc)
            throw new InvalidOperationException("qualification-job-sensor-fence-changed");
        if (qualified.SensorObjid != sensor.SensorObjid || qualified.BindingRevision != sensor.BindingRevision ||
            qualified.BindingFingerprint == sensor.BindingFingerprint || string.IsNullOrWhiteSpace(qualified.QualificationProofReference))
            throw new InvalidOperationException("qualification-job-proof-binding-revision-changed");
        sensor.Attempts++;
        sensor.WaveAttempts++;
        sensor.LastAttemptUtc = nowUtc;
        sensor.Status = "qualified";
        sensor.HasQualificationProof = true;
        sensor.QualifiedIdentityEpoch = qualified.IdentityEpoch;
        sensor.QualifiedChannelGeneration = qualified.ChannelGeneration;
        sensor.BindingFingerprint = qualified.BindingFingerprint;
        sensor.Reason = "raw-proof-recorded";
        sensor.NextAttemptUtc = null;
        job.Attempts++;
        job.Qualified++;
        job.Waiting = Math.Max(0, job.Waiting - 1);
            job.Cursor = (fence.PageIndex * PageSize + page.Sensors.FindIndex(row => row.SensorObjid == fence.SensorObjid) + 1) %
                Math.Max(1, job.Selected);
        job.UpdatedAtUtc = nowUtc;
        pageRow.Content = SerializePage(page);
        pageRow.UpdatedAt = nowUtc.UtcDateTime;
        pageRow.Version++;
        overviewRow.Content = Serialize(overview);
        overviewRow.UpdatedAt = nowUtc.UtcDateTime;
        overviewRow.Version++;
    }

    public static string PageKey(string jobId, int pageIndex) => $"prtg_qualjob_{jobId}_{pageIndex:D4}";

    private void MutateFenced(string jobId, string owner, long version, DateTimeOffset now, Action<PrtgHistoricAdmissionStore.State> edit) =>
        overviewBlob.MutateWithContext((_, raw) =>
        {
            var overview = ParseOverview(raw);
            EnsureLease(overview.Job, jobId, owner, version, now);
            edit(overview);
            return (Serialize(overview), true);
        }, MaximumOverviewBytes, System.Data.IsolationLevel.Serializable);

    private static void EnsureLease(Job? job, string id, string owner, long version, DateTimeOffset now)
    {
        if (job is null || job.JobId != id || job.Owner != owner || job.Version != version ||
            job.LeaseUntilUtc <= now || job.DeadlineUtc <= now || job.CancelRequested)
            throw new OperationCanceledException("qualification-job-lease-lost");
    }
    private static PrtgHistoricAdmissionStore.State ParseOverview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        if (Encoding.UTF8.GetByteCount(json) > MaximumOverviewBytes)
            throw new InvalidDataException("qualification-job-overview-cap-exceeded");
        var state = JsonSerializer.Deserialize<PrtgHistoricAdmissionStore.State>(json)
            ?? throw new InvalidDataException("qualification-job-overview-invalid");
        if (state.Job is { } job && (job.Selected is < 1 or > MaximumSensors ||
            job.PageCount != (job.Selected + PageSize - 1) / PageSize ||
            job.InitializedPages < 0 || job.InitializedPages > job.PageCount ||
            job.InitializationCursor != job.InitializedPages ||
            job.AdmittedEligibleSensorCount < 0 || job.AdmittedEligibleSensorCount > job.Selected ||
            job.InitializedEligibleSensorCount < 0 ||
            job.InitializedEligibleSensorCount > job.AdmittedEligibleSensorCount))
            throw new InvalidDataException("qualification-job-overview-fence-invalid");
        return state;
    }
    private static Page ParsePage(string json)
    {
        if (json.Length > MaximumPageBytes || Encoding.UTF8.GetByteCount(json) > MaximumPageBytes)
            throw new InvalidDataException("qualification-job-page-cap-exceeded");
        return JsonSerializer.Deserialize<Page>(json) ?? throw new InvalidDataException("qualification-job-page-invalid");
    }
    private static string Serialize(PrtgHistoricAdmissionStore.State value) => SerializeBounded(value, MaximumOverviewBytes, "qualification-job-overview-cap-exceeded");
    private static string SerializePage(Page value) => SerializeBounded(value, MaximumPageBytes, "qualification-job-page-cap-exceeded");
    private static string SerializeBounded<T>(T value, int max, string reason)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > max) throw new InvalidDataException(reason);
        return json;
    }
    private static void EnsureLiveRawJobAuthority(LfDbContext context, Job job)
    {
        var settingsRaw = ReadResumeAuthority(context, "system_settings")
            ?? throw new InvalidDataException("qualification-settings-missing");
        var policyRaw = ReadResumeAuthority(context, PrtgMonitoringPolicyStore.BlobKey)
            ?? throw new InvalidDataException("qualification-policy-missing");
        StrictAuthorityJsonDocument.Validate<SystemSettings>(settingsRaw);
        StrictAuthorityJsonDocument.Validate<PrtgMonitoringPolicy>(policyRaw);
        var settings = JsonSerializer.Deserialize<SystemSettings>(settingsRaw, LfJsonOptions.Pretty)
            ?? throw new InvalidDataException("qualification-settings-invalid");
        var policy = JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty)
            ?? throw new InvalidDataException("qualification-policy-invalid");
        var strategy = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var authority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy, strategy,
            PrtgFetchStrategy.Profile(strategy).SnapshotIntervalMinutes);
        if (!settings.PrtgEnabled || settings.Revision != job.SettingsRevision || !policy.Ready(settings.PrtgUrl) ||
            policy.SourceGeneration != job.SourceGeneration || authority != job.AuthorityContextFingerprint ||
            PrtgRawWorkScope.ReadInContext(context, policy) != job.ScopeFingerprint)
            throw new InvalidOperationException("qualification-job-live-raw-authority-changed");
    }

    private static Dictionary<string,(string? Content,long Version,int Length,DateTime UpdatedAt)> ReadBoundedRows(
        LfDbContext ctx, IEnumerable<string> keys, int maximumBytes, string reason)
    {
        var rows = BoundedJsonBlobBatchReader.Read(ctx, keys, maximumBytes);
        foreach (var row in rows.Values)
        {
            if (row.Content is null || row.Length > maximumBytes ||
                Encoding.UTF8.GetByteCount(row.Content) > maximumBytes)
                throw new InvalidDataException(reason);
        }
        return rows;
    }
    private static BlobRow AttachBoundedRow(LfDbContext context, string key,
        (string? Content, long Version, int Length, DateTime UpdatedAt) bounded, string reason)
    {
        var tracked = context.Blobs.Local.FirstOrDefault(row => row.BlobKey == key);
        if (tracked is not null) { EnsureSameBoundedRow(tracked, bounded, reason); return tracked; }
        if (bounded.Content is null) throw new InvalidDataException(reason);
        var snapshot = new BlobRow { BlobKey = key, Content = bounded.Content,
            Version = bounded.Version, UpdatedAt = bounded.UpdatedAt };
        context.Blobs.Attach(snapshot);
        return snapshot;
    }

    private static void EnsureSameBoundedRow(BlobRow tracked,
        (string? Content,long Version,int Length,DateTime UpdatedAt) bounded, string reason)
    {
        if (bounded.Content is null || tracked.Version != bounded.Version || tracked.UpdatedAt != bounded.UpdatedAt ||
            !StringComparer.Ordinal.Equals(tracked.Content, bounded.Content))
            throw new InvalidOperationException(reason);
    }
    private static bool SafeJobId(string value) => value.Length is >= 16 and <= 64 && value.All(char.IsAsciiLetterOrDigit);
    private static bool Hex64(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool SafeOpaque(string value) => value.Length <= 128 && !string.IsNullOrWhiteSpace(value) &&
        value.All(ch => !char.IsControl(ch)) && !value.Contains("://", StringComparison.Ordinal);
    private static string SafeReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "probe-unavailable" :
        new string(reason.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.').Take(128).ToArray());
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static Job Copy(Job job) => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
}

/// <summary>Typed capability passed only inside the server to atomically bind proof to the active page/lease.</summary>
public sealed record PrtgQualificationWriteFence(string JobId, string Owner, long LeaseVersion,
    string ScopeFingerprint, string SourceGeneration, string SettingsRevision, string PolicyRevision,
    string AuthorityContextFingerprint, int PageIndex, long PageVersion, long SensorObjid,
    long BindingRevision, string BindingFingerprint);

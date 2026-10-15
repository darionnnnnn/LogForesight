using System.Data;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Services;

/// <summary>Small durable overview plus independently bounded pages of current per-sensor state.</summary>
public sealed class PrtgTrustedSamplingProfileRefreshStateStore(StorageBackend backend)
{
    public const string OverviewKey = "prtg_trusted_profile_refresh_v1";
    public const int MaximumOverviewBytes = 16 * 1024;
    public const int MaximumPageBytes = 64 * 1024;
    public const int MaximumProofRefreshNoticeBytes = 4096;
    public const int MaximumPendingProofRefreshNotices = PrtgTrustedSamplingProofRefreshRequest.MaximumPending;
    public const string ProofRefreshNoticePrefix = PrtgTrustedSamplingProofRefreshRequest.BlobKeyPrefix;
    private readonly EfJsonBlobStore overviewBlob = backend.Blob(OverviewKey);

    public sealed class Overview
    {
        public string ScopeFingerprint { get; set; } = "";
        public List<string> PreviousScopeFingerprints { get; set; } = [];
        public int Cursor { get; set; }
        public int SelectedSensors { get; set; }
        public int EligibleSensors { get; set; }
        public int Qualified { get; set; }
        public int Unavailable { get; set; }
        public int Failed { get; set; }
        public int Waiting { get; set; }
        public int RawRequestCount { get; set; }
        public double ObservedRequestSeconds { get; set; }
        public string LastOutcome { get; set; } = "not-started";
        public string? LastWaitingReason { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public DateTimeOffset? NextSweepAtUtc { get; set; }
        public string LeaseOwner { get; set; } = "";
        public long LeaseVersion { get; set; }
        public DateTimeOffset? LeaseUntilUtc { get; set; }
    }

    public sealed class SensorState
    {
        public long SensorObjid { get; set; }
        public bool Eligible { get; set; }
        public string ContractFingerprint { get; set; } = "";
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public string Status { get; set; } = "waiting";
        public string Reason { get; set; } = "not-probed";
        public DateTimeOffset? LastOutcomeAtUtc { get; set; }
    }

    public sealed class SensorPage
    {
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public List<SensorState> Sensors { get; set; } = [];
    }

    public void QueueProofRefresh(PrtgTrustedSamplingProofRefreshRequest notice)
    {
        ValidateNotice(notice);
        var blob = backend.Blob(ProofRefreshNoticeKey(notice.SensorObjid));
        var stored = blob.MutateWithContext((ctx, current) =>
        {
            PrtgTrustedSamplingProofRefreshRequest? prior = null;
            if (!string.IsNullOrWhiteSpace(current))
            {
                prior = ParseNotice(current, current.Length);
            }
            // Do not let an older notification replace a newer binding/proof revision.
            if (prior is not null && (prior.BindingRevision > notice.BindingRevision ||
                prior.BindingRevision == notice.BindingRevision && prior.QueuedAtUtc > notice.QueuedAtUtc))
                return (current!, false);
            if (prior is null)
            {
                var policyRows = BoundedJsonBlobBatchReader.Read(ctx, [PrtgMonitoringPolicyStore.BlobKey],
                    PrtgSharedAuthoritySnapshot.MaximumDocumentCharacters);
                if (!policyRows.TryGetValue(PrtgMonitoringPolicyStore.BlobKey, out var policyMeta) ||
                    policyMeta.Content is null || policyMeta.Length > PrtgSharedAuthoritySnapshot.MaximumDocumentCharacters)
                    throw new InvalidDataException("profile-refresh-policy-size-invalid");
                using var policyDocument = JsonDocument.Parse(policyMeta.Content,
                    new JsonDocumentOptions { MaxDepth = 32 });
                var policy = JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyMeta.Content)
                    ?? throw new InvalidDataException("profile-refresh-policy-invalid");
                if (policyDocument.RootElement.ValueKind != JsonValueKind.Object ||
                    policy.Revision != notice.PolicyRevision || !policy.SensorIds.Contains(notice.SensorObjid) ||
                    policy.SensorIds.Count > MaximumPendingProofRefreshNotices)
                    throw new InvalidOperationException("profile-refresh-proof-notice-policy-changed");
                PrtgTrustedSamplingProofRefreshRequest.MakeRoomForNewRequest(ctx, notice.SensorObjid,
                    policy.SensorIds);
            }
            var serialized = JsonSerializer.Serialize(notice);
            if (Encoding.UTF8.GetByteCount(serialized) > MaximumProofRefreshNoticeBytes)
                throw new InvalidDataException("profile-refresh-proof-notice-cap-exceeded");
            return (serialized, true);
        }, MaximumProofRefreshNoticeBytes, isolationLevel: System.Data.IsolationLevel.Serializable);
        if (!stored) return;
        // The notice itself is durable. This overview nudge makes it visible immediately and also
        // survives a process restart if the in-memory wake signal is lost.
        var now = DateTimeOffset.UtcNow;
        overviewBlob.MutateWithContext((_, raw) =>
        {
            var overview = ParseOverview(raw);
            overview.Cursor = 0;
            overview.NextSweepAtUtc = now;
            overview.UpdatedAtUtc = now;
            return (SerializeBounded(overview, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded"), true);
        }, MaximumOverviewBytes);
    }

    public IReadOnlyDictionary<long, PrtgTrustedSamplingProofRefreshRequest> ReadProofRefreshNotices(IReadOnlyCollection<long> sensorIds)
    {
        if (sensorIds.Count is < 1 or > 100 || sensorIds.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(sensorIds));
        var keys = sensorIds.Distinct().Select(ProofRefreshNoticeKey).ToArray();
        using var ctx = backend.CreateContext();
        var rows = BoundedJsonBlobBatchReader.Read(ctx, keys, MaximumProofRefreshNoticeBytes);
        var result = new Dictionary<long, PrtgTrustedSamplingProofRefreshRequest>();
        foreach (var row in rows)
        {
            if (row.Value.Content is null)
                throw new InvalidDataException("profile-refresh-proof-notice-cap-exceeded");
            var notice = ParseNotice(row.Value.Content, row.Value.Length);
            if (row.Key != ProofRefreshNoticeKey(notice.SensorObjid) || !sensorIds.Contains(notice.SensorObjid))
                throw new InvalidDataException("profile-refresh-proof-notice-key-invalid");
            result[notice.SensorObjid] = notice;
        }
        return result;
    }

    public bool CompleteProofRefresh(PrtgTrustedSamplingProofRefreshRequest expected) => MutateProofRefreshNotice(expected, null);

    public bool DeferProofRefresh(PrtgTrustedSamplingProofRefreshRequest expected, DateTimeOffset notBeforeUtc)
    {
        if (notBeforeUtc < expected.QueuedAtUtc || notBeforeUtc > DateTimeOffset.UtcNow.AddDays(2))
            throw new ArgumentOutOfRangeException(nameof(notBeforeUtc));
        return MutateProofRefreshNotice(expected, expected with { NotBeforeUtc = notBeforeUtc });
    }

    public DateTimeOffset? EarliestPendingProofRefresh(IReadOnlyCollection<long> currentSelectedSensorIds)
    {
        if (currentSelectedSensorIds.Count > 15_000 || currentSelectedSensorIds.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(currentSelectedSensorIds));
        var ids = currentSelectedSensorIds.Distinct().ToArray();
        if (ids.Length == 0) return null;
        using var ctx = backend.CreateContext();
        if (!ctx.Blobs.AsNoTracking().Any(row => row.BlobKey.StartsWith(ProofRefreshNoticePrefix))) return null;
        DateTimeOffset? earliest = null;
        foreach (var batch in ids.Chunk(100))
        {
            var keys = batch.Select(ProofRefreshNoticeKey).ToArray();
            var rows = BoundedJsonBlobBatchReader.Read(ctx, keys, MaximumProofRefreshNoticeBytes);
            foreach (var row in rows)
            {
                if (row.Value.Content is null)
                    throw new InvalidDataException("profile-refresh-proof-notice-cap-exceeded");
                var notice = ParseNotice(row.Value.Content, row.Value.Length);
                if (row.Key != ProofRefreshNoticeKey(notice.SensorObjid) || !batch.Contains(notice.SensorObjid))
                    throw new InvalidDataException("profile-refresh-proof-notice-key-invalid");
                if (earliest is null || notice.NotBeforeUtc < earliest) earliest = notice.NotBeforeUtc;
            }
        }
        return earliest;
    }

    /// <summary>Delete a small bounded batch of notices for sensors no longer in the current policy scope.</summary>
    public int PurgeOutOfScopeProofRefreshNotices(IReadOnlyCollection<long> currentSelectedSensorIds, int maximum = 100)
    {
        if (currentSelectedSensorIds.Count > 15_000 || currentSelectedSensorIds.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(nameof(currentSelectedSensorIds));
        var keep = currentSelectedSensorIds.ToHashSet();
        using var ctx = backend.CreateContext();
        var staleKeys = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(ProofRefreshNoticePrefix))
            .OrderBy(row => row.BlobKey).Select(row => row.BlobKey)
            .Take(Math.Clamp(maximum, 1, 1000) + keep.Count).ToArray()
            .Where(key => !long.TryParse(key.AsSpan(ProofRefreshNoticePrefix.Length),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || !keep.Contains(id))
            .Take(Math.Clamp(maximum, 1, 1000)).ToArray();
        if (staleKeys.Length == 0) return 0;
        return ctx.Blobs.Where(row => staleKeys.Contains(row.BlobKey) && row.BlobKey.StartsWith(ProofRefreshNoticePrefix))
            .ExecuteDelete();
    }

    private bool MutateProofRefreshNotice(PrtgTrustedSamplingProofRefreshRequest expected,
        PrtgTrustedSamplingProofRefreshRequest? replacement)
    {
        ValidateNotice(expected);
        using var ctx = backend.CreateContext();
        var key = ProofRefreshNoticeKey(expected.SensorObjid);
        var expectedJson = JsonSerializer.Serialize(expected);
        if (replacement is null)
            return ctx.Blobs.Where(item => item.BlobKey == key && item.Content == expectedJson)
                .ExecuteDelete() == 1;
        ValidateNotice(replacement);
        var serialized = JsonSerializer.Serialize(replacement);
        if (Encoding.UTF8.GetByteCount(serialized) > MaximumProofRefreshNoticeBytes)
            throw new InvalidDataException("profile-refresh-proof-notice-cap-exceeded");
        var now = DateTime.UtcNow;
        return ctx.Blobs.Where(item => item.BlobKey == key && item.Content == expectedJson)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.Content, serialized)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.Version, item => item.Version + 1)) == 1;
    }

    private static string ProofRefreshNoticeKey(long sensorObjid) =>
        ProofRefreshNoticePrefix + sensorObjid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateNotice(PrtgTrustedSamplingProofRefreshRequest? notice)
    {
        if (notice is null || notice.SensorObjid <= 0 || notice.BindingRevision <= 0 || !Hash(notice.BindingFingerprint) ||
            !Hash(notice.QualificationProofReference) || notice.IdentityEpoch <= 0 ||
            !Opaque(notice.ResourceGeneration, 128) || !Opaque(notice.SourceGeneration, 128) ||
            !Opaque(notice.ChannelGeneration, 128) || !Hash(notice.AuthorityContextFingerprint) ||
            !Opaque(notice.SettingsRevision, 128) || !Opaque(notice.PolicyRevision, 128) ||
            notice.QueuedAtUtc == default || notice.NotBeforeUtc < notice.QueuedAtUtc ||
            notice.QueuedAtUtc.Offset != TimeSpan.Zero || notice.NotBeforeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid bounded raw-proof refresh notice.", nameof(notice));
    }

    private static PrtgTrustedSamplingProofRefreshRequest ParseNotice(string prefix, int length)
    {
        if (length > MaximumProofRefreshNoticeBytes || prefix.Length != length ||
            Encoding.UTF8.GetByteCount(prefix) > MaximumProofRefreshNoticeBytes)
            throw new InvalidDataException("profile-refresh-proof-notice-cap-exceeded");
        using var document = JsonDocument.Parse(prefix, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("profile-refresh-proof-notice-invalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException("profile-refresh-proof-notice-duplicate-field");
        var notice = JsonSerializer.Deserialize<PrtgTrustedSamplingProofRefreshRequest>(prefix)
            ?? throw new InvalidDataException("profile-refresh-proof-notice-invalid");
        try { ValidateNotice(notice); }
        catch (ArgumentException ex) { throw new InvalidDataException("profile-refresh-proof-notice-invalid", ex); }
        return notice;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Opaque(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Contains("://", StringComparison.Ordinal) && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    public Overview ReadOverview()
    {
        var (content, _, length) = overviewBlob.ReadBoundedWithVersion(MaximumOverviewBytes);
        if (length > MaximumOverviewBytes || content is not null && Encoding.UTF8.GetByteCount(content) > MaximumOverviewBytes)
            throw new InvalidDataException("profile-refresh-overview-cap-exceeded");
        return string.IsNullOrWhiteSpace(content) ? new() : JsonSerializer.Deserialize<Overview>(content) ?? new();
    }

    public SensorPage ReadPage(string scope, int pageIndex)
    {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var key = PageKey(scope, pageIndex);
        var (content, _, length) = backend.Blob(key).ReadBoundedWithVersion(MaximumPageBytes);
        if (length > MaximumPageBytes || content is not null && Encoding.UTF8.GetByteCount(content) > MaximumPageBytes)
            throw new InvalidDataException("profile-refresh-page-cap-exceeded");
        var page = string.IsNullOrWhiteSpace(content) ? new SensorPage() : JsonSerializer.Deserialize<SensorPage>(content) ?? new();
        if (page.Sensors.Count > 100) throw new InvalidDataException("profile-refresh-page-count-exceeded");
        return page;
    }

    public T MutateOverview<T>(string owner, long version, Func<Overview, T> change)
    {
        return overviewBlob.MutateWithContext((_, raw) =>
        {
            var value = ParseOverview(raw);
            EnsureLease(value, owner, version);
            var result = change(value);
            value.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return (SerializeBounded(value, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded"), result);
        }, MaximumOverviewBytes);
    }

    public bool TryAcquire(string owner, DateTimeOffset now, TimeSpan duration, out long version)
    {
        var acquiredVersion = 0L;
        var acquired = overviewBlob.MutateWithContext((ctx, raw) =>
        {
            var value = ParseOverview(raw);
            if (value.LeaseUntilUtc > now) return (raw ?? SerializeBounded(value, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded"), false);
            if (!string.IsNullOrWhiteSpace(raw) && Encoding.UTF8.GetByteCount(raw) > MaximumOverviewBytes)
                MigrateLegacySensorPages(ctx, raw, value);
            value.LeaseOwner = owner;
            value.LeaseVersion++;
            value.LeaseUntilUtc = now + duration;
            value.UpdatedAtUtc = now;
            acquiredVersion = value.LeaseVersion;
            return (SerializeBounded(value, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded"), true);
        }, 8 * 1024 * 1024);
        version = acquiredVersion;
        return acquired;
    }

    private static void MigrateLegacySensorPages(LfDbContext ctx, string raw, Overview overview)
    {
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 24 });
        if (!document.RootElement.TryGetProperty("Sensors", out var sensorsElement) ||
            sensorsElement.ValueKind != JsonValueKind.Object) return;
        var legacy = JsonSerializer.Deserialize<Dictionary<long, SensorState>>(sensorsElement.GetRawText()) ?? [];
        var ordered = legacy.Values.OrderBy(row => row.SensorObjid).ToArray();
        // The former dictionary did not serialize objids inside its values; recover them from property names.
        if (ordered.Any(row => row.SensorObjid <= 0))
            ordered = sensorsElement.EnumerateObject().Select(pair =>
            {
                var row = JsonSerializer.Deserialize<SensorState>(pair.Value.GetRawText()) ?? new();
                row.SensorObjid = long.TryParse(pair.Name, out var id) ? id : 0;
                return row;
            }).Where(row => row.SensorObjid > 0).OrderBy(row => row.SensorObjid).ToArray();
        for (var start = 0; start < ordered.Length; start += 100)
        {
            var rows = ordered.Skip(start).Take(100).ToArray();
            var key = PageKey(overview.ScopeFingerprint, start / 100);
            if (ctx.Blobs.Any(row => row.BlobKey == key)) continue;
            var page = new SensorPage { UpdatedAtUtc = DateTimeOffset.UtcNow, Sensors = rows.ToList() };
            var content = SerializeBounded(page, MaximumPageBytes, "profile-refresh-legacy-page-cap-exceeded");
            ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, UpdatedAt = DateTime.UtcNow, Version = 1 });
        }
        var counts = Counts(ordered);
        overview.EligibleSensors = counts.Eligible;
        overview.Qualified = counts.Qualified;
        overview.Unavailable = counts.Unavailable;
        overview.Failed = counts.Failed;
        overview.Waiting = counts.Waiting;
    }

    public bool Renew(string owner, long version, DateTimeOffset until)
    {
        try { return MutateOverview(owner, version, value => { value.LeaseUntilUtc = until; return true; }); }
        catch (OperationCanceledException) { return false; }
    }

    public void Release(string owner, long version)
    {
        overviewBlob.MutateWithContext((_, raw) =>
        {
            var value = ParseOverview(raw);
            if (value.LeaseOwner == owner && value.LeaseVersion == version)
            { value.LeaseOwner = ""; value.LeaseUntilUtc = null; value.UpdatedAtUtc = DateTimeOffset.UtcNow; }
            return (SerializeBounded(value, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded"), true);
        }, MaximumOverviewBytes);
    }

    /// <summary>Page replacement and aggregate deltas commit together after verifying lease ownership.</summary>
    public void SavePage(string scope, int pageIndex, IReadOnlyList<SensorState> sensors,
        string owner, long version, int requestDelta = 0, double requestSecondsDelta = 0,
        string? outcome = null, string? waitingReason = null)
    {
        if (sensors.Count > 100) throw new ArgumentOutOfRangeException(nameof(sensors));
        var pageKey = PageKey(scope, pageIndex);
        using var probe = backend.CreateContext();
        var strategy = probe.Database.CreateExecutionStrategy();
        strategy.Execute(() =>
        {
            using var ctx = backend.CreateContext();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            var overviewRows = BoundedJsonBlobBatchReader.Read(ctx, [OverviewKey], MaximumOverviewBytes);
            if (!overviewRows.TryGetValue(OverviewKey, out var overviewSnapshot))
                throw new InvalidDataException("profile-refresh-overview-missing");
            if (overviewSnapshot.Content is null)
                throw new InvalidDataException("profile-refresh-overview-cap-exceeded");
            EnsureBounded(overviewSnapshot.Content, overviewSnapshot.Length, MaximumOverviewBytes,
                "profile-refresh-overview-cap-exceeded");
            var overviewRow = AttachBoundedRow(ctx, OverviewKey, overviewSnapshot);
            var overview = ParseOverview(overviewRow.Content);
            EnsureLease(overview, owner, version);
            if (overview.ScopeFingerprint != scope) throw new OperationCanceledException("profile-refresh-scope-lost");
            var pageRows = BoundedJsonBlobBatchReader.Read(ctx, [pageKey], MaximumPageBytes);
            BlobRow? pageRow = null;
            SensorPage oldPage;
            if (pageRows.TryGetValue(pageKey, out var pageSnapshot))
            {
                if (pageSnapshot.Content is null)
                    throw new InvalidDataException("profile-refresh-page-cap-exceeded");
                EnsureBounded(pageSnapshot.Content, pageSnapshot.Length, MaximumPageBytes,
                    "profile-refresh-page-cap-exceeded");
                pageRow = AttachBoundedRow(ctx, pageKey, pageSnapshot);
                oldPage = ParsePage(pageRow.Content);
            }
            else
            {
                oldPage = new SensorPage();
            }
            if (oldPage.Sensors.Count > 100) throw new InvalidDataException("profile-refresh-page-count-exceeded");
            var newPage = new SensorPage { UpdatedAtUtc = DateTimeOffset.UtcNow, Sensors = sensors.ToList() };
            var serialized = SerializeBounded(newPage, MaximumPageBytes, "profile-refresh-page-cap-exceeded");
            var oldCounts = Counts(oldPage.Sensors);
            var newCounts = Counts(newPage.Sensors);
            overview.EligibleSensors += newCounts.Eligible - oldCounts.Eligible;
            overview.Qualified += newCounts.Qualified - oldCounts.Qualified;
            overview.Unavailable += newCounts.Unavailable - oldCounts.Unavailable;
            overview.Failed += newCounts.Failed - oldCounts.Failed;
            overview.Waiting += newCounts.Waiting - oldCounts.Waiting;
            overview.RawRequestCount += requestDelta;
            overview.ObservedRequestSeconds += requestSecondsDelta;
            if (outcome is not null) overview.LastOutcome = outcome;
            overview.LastWaitingReason = waitingReason;
            overview.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var overviewJson = SerializeBounded(overview, MaximumOverviewBytes, "profile-refresh-overview-cap-exceeded");
            if (pageRow is null) ctx.Blobs.Add(new BlobRow { BlobKey = pageKey, Content = serialized, UpdatedAt = DateTime.UtcNow, Version = 1 });
            else { pageRow.Content = serialized; pageRow.UpdatedAt = DateTime.UtcNow; pageRow.Version++; }
            if (overviewRow is null) ctx.Blobs.Add(new BlobRow { BlobKey = OverviewKey, Content = overviewJson, UpdatedAt = DateTime.UtcNow, Version = 1 });
            else { overviewRow.Content = overviewJson; overviewRow.UpdatedAt = DateTime.UtcNow; overviewRow.Version++; }
            ctx.SaveChanges();
            tx.Commit();
        });
    }

    public void PurgeExpiredPages(DateTimeOffset cutoff, int maximum = 100)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        using var ctx = backend.CreateContext();
        var keys = ctx.Blobs.AsNoTracking()
            .Where(row => row.BlobKey.StartsWith("prtg_profile_page_") && row.UpdatedAt < cutoffUtc)
            .OrderBy(row => row.UpdatedAt).Select(row => row.BlobKey)
            .Take(Math.Clamp(maximum, 1, 100)).ToArray();
        if (keys.Length == 0) return;
        ctx.Blobs.Where(row => keys.Contains(row.BlobKey) && row.UpdatedAt < cutoffUtc).ExecuteDelete();
    }

    public DateTimeOffset? EarliestNextAttempt(string scope, int pageCount)
    {
        if (pageCount is < 0 or > 150) throw new InvalidDataException("profile-refresh-page-count-invalid");
        var prefix = $"prtg_profile_page_{scope}_";
        using var ctx = backend.CreateContext();
        using var transaction = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
        var keys = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(prefix))
            .OrderBy(row => row.BlobKey).Select(row => row.BlobKey)
            .Take(Math.Clamp(pageCount, 0, 150) + 1).ToArray();
        if (keys.Length != pageCount)
            throw new InvalidDataException("profile-refresh-pages-incomplete");
        var boundedRows = BoundedJsonBlobBatchReader.Read(ctx, keys, MaximumPageBytes);
        var due = new List<DateTimeOffset>();
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            if (key != PageKey(scope, index))
                throw new InvalidDataException("profile-refresh-page-index-invalid");
            if (!boundedRows.TryGetValue(key, out var row) || row.Content is null)
                throw new InvalidDataException("profile-refresh-page-cap-exceeded");
            EnsureBounded(row.Content, row.Length, MaximumPageBytes, "profile-refresh-page-cap-exceeded");
            var page = ParsePage(row.Content);
            if (page.Sensors.Count > 100) throw new InvalidDataException("profile-refresh-page-count-exceeded");
            due.AddRange(page.Sensors.Where(sensor => sensor.NextAttemptAtUtc.HasValue)
                .Select(sensor => sensor.NextAttemptAtUtc!.Value));
        }
        transaction.Commit();
        return due.Count == 0 ? null : due.Min();
    }

    public static string PageKey(string scope, int pageIndex) =>
        $"prtg_profile_page_{scope}_{pageIndex:D4}";

    private static BlobRow AttachBoundedRow(LfDbContext context, string key,
        (string? Content, long Version, int Length, DateTime UpdatedAt) snapshot)
    {
        var content = snapshot.Content ?? throw new InvalidDataException("profile-refresh-bounded-row-missing");
        var row = context.Blobs.Local.FirstOrDefault(candidate => candidate.BlobKey == key);
        if (row is not null)
        {
            var entry = context.Entry(row);
            if (entry.State != EntityState.Unchanged || row.Version != snapshot.Version ||
                row.UpdatedAt != snapshot.UpdatedAt || !StringComparer.Ordinal.Equals(row.Content, content) ||
                entry.Property(candidate => candidate.Version).OriginalValue != snapshot.Version ||
                entry.Property(candidate => candidate.UpdatedAt).OriginalValue != snapshot.UpdatedAt)
                throw new DbUpdateConcurrencyException("profile-refresh-bounded-row-changed");
            return row;
        }
        row = new BlobRow { BlobKey = key, Content = content, Version = snapshot.Version,
            UpdatedAt = snapshot.UpdatedAt };
        context.Blobs.Attach(row);
        return row;
    }

    private static Overview ParseOverview(string? content) => string.IsNullOrWhiteSpace(content)
        ? new() : JsonSerializer.Deserialize<Overview>(content) ?? new();
    private static SensorPage ParsePage(string? content) => string.IsNullOrWhiteSpace(content)
        ? new() : JsonSerializer.Deserialize<SensorPage>(content) ?? new();
    private static string SerializeBounded<T>(T value, int cap, string reason)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > cap) throw new InvalidDataException(reason);
        return json;
    }
    private static void EnsureBounded(string prefix, int length, int cap, string reason)
    {
        if (length > cap || Encoding.UTF8.GetByteCount(prefix) > cap)
            throw new InvalidDataException(reason);
    }
    private static void EnsureLease(Overview value, string owner, long version)
    {
        if (value.LeaseOwner != owner || value.LeaseVersion != version || value.LeaseUntilUtc <= DateTimeOffset.UtcNow)
            throw new OperationCanceledException("profile-refresh-lease-lost");
    }
    private static (int Eligible, int Qualified, int Unavailable, int Failed, int Waiting) Counts(IEnumerable<SensorState> rows)
    {
        var list = rows.ToArray();
        return (list.Count(row => row.Eligible), list.Count(row => row.Eligible && row.Status == "qualified"),
            list.Count(row => row.Status == "unavailable"), list.Count(row => row.Eligible && row.Status == "failed"),
            list.Count(row => row.Eligible && row.Status == "waiting"));
    }
}

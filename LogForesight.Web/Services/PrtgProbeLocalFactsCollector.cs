using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace LogForesight.Web.Services;

/// <summary>Read-only, aggregate local context. Does not copy names, logs, references or credentials.</summary>
public static class PrtgProbeLocalFactsCollector
{
    private const int MaximumPolicyCharacters = 2 * 1024 * 1024;
    private const int MaximumPolicySensors = 15_000;
    private const int MaximumPolicyHosts = 15_000;
    private const int MaximumProfileSample = 100;
    private const int MaximumNetiqReceipts = 10_000;
    private const int MaximumNetiqLineCharacters = 4096;
    private const int LocalSqliteBusyTimeoutMilliseconds = 2000;

    public static async Task<PrtgProbeLocalFacts> CollectAsync(StorageBackend backend,
        SystemSettings settings, IAiProbeService? aiProbe, CancellationToken ct)
    {
        var facts = new PrtgProbeLocalFacts();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            using var db = backend.CreateContext();
            var connection = db.Database.GetDbConnection();
            var sqlite = connection as SqliteConnection;
            db.Database.SetCommandTimeout(sqlite is null ? 5 : LocalSqliteBusyTimeoutMilliseconds / 1000);
            using var progressDeadline = sqlite is not null
                ? await AttachSqliteProgressDeadlineAsync(db.Database, sqlite, token)
                : null;
            var policy = ReadPolicy(db, token);
            token.ThrowIfCancellationRequested();
            var authority = policy.SourceAuthorityFingerprint(settings.PrtgUrl);
            var hasAuthority = authority.Length == 64;
            var guards = PrtgResourceGuardTargets.ParseOverrideObjids(settings.PrtgResourceGuardSensorObjids ?? [])
                .Order().Take(101).ToArray();
            if (guards.Length > 100) throw new InvalidDataException("guard-scope-exceeds-probe-bound");
            var mirrorDevices = await db.PrtgDevices.CountAsync(token);
            var mirrorSensors = await db.PrtgSensors.CountAsync(token);
            var mapDate = await db.PrtgHostMaps.MaxAsync(row => (DateTime?)row.MapDate, token);
            var mapped = mapDate.HasValue ? await db.PrtgHostMaps.Where(row => row.MapDate == mapDate && row.MapStatus == PrtgMapStatus.Ok)
                .Select(row => row.DeviceObjid).Distinct().CountAsync(token) : 0;
            var conflicts = mapDate.HasValue ? await db.PrtgHostMaps.Where(row => row.MapDate == mapDate && row.MapStatus == "conflict")
                .Select(row => row.DeviceObjid).Distinct().CountAsync(token) : 0;
            var guardInMirror = guards.Length == 0 ? 0 : await db.PrtgSensors.CountAsync(row => guards.Contains(row.Objid), token);
            if (policy.SensorIds.Where(id => id > 0).Distinct().Take(MaximumPolicySensors + 1).Count() > MaximumPolicySensors ||
                policy.HostIds.Where(id => id > 0).Distinct().Take(MaximumPolicyHosts + 1).Count() > MaximumPolicyHosts)
                throw new InvalidDataException("policy-scope-exceeds-probe-bound");
            var ids = policy.SensorIds.Where(id => id > 0).Distinct().Order().Take(MaximumProfileSample).ToArray();
            var bindings = ReadBindings(db, ids, token);
            var profiles = ReadProfiles(db, ids, token);
            var identities = ReadIdentities(db, ids, token);
            var now = DateTimeOffset.UtcNow;
            var strategy = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
            var minutes = PrtgFetchStrategy.Profile(strategy).SnapshotIntervalMinutes;
            var strategyContext = ReadCurrentStrategy(db, policy, strategy, minutes, token);
            var expectedAuthority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy, strategy, minutes);
            var qualified = profiles.Count(pair => hasAuthority && settings.PrtgEnabled && strategyContext.Ready &&
                identities.TryGetValue(pair.Key, out var identity) && policy.HostIds.Contains(identity.HostId) &&
                ProfileMatchesCurrentStrategy(pair.Value, strategyContext) &&
                pair.Value.MatchesCurrent(identity, pair.Value.SensorType,
                    strategyContext.StrategyFingerprint, strategyContext.StrategyMinutes,
                    strategyContext.EffectiveFromHourUtc, now) &&
                pair.Value.SourceGeneration == policy.SourceGeneration &&
                bindings.TryGetValue(pair.Key, out var binding) && binding.QualificationProofReference.Length > 0 &&
                binding.BindingRevision == pair.Value.BindingRevision &&
                binding.QualificationProofReference == pair.Value.QualificationProofReference &&
                binding.BindingFingerprint == pair.Value.BindingFingerprint &&
                pair.Value.AuthorityContextFingerprint == binding.AuthorityContextFingerprint &&
                binding.AuthorityContextFingerprint == expectedAuthority &&
                PrtgTrustedSamplingBinding.MatchesProfileAuthority(pair.Value, policy.TimeBasisEvidenceReference));
            var day = DateTime.Today.AddDays(-1);
            token.ThrowIfCancellationRequested();
            var receipts = await ReadLatestReceiptsAsync(db, day, token);
            var parents = await db.DailyRecords.CountAsync(row => row.RecordDate == day, token);
            var eligible = await ReadEligibleParentCountAsync(db, day, token);
            token.ThrowIfCancellationRequested();
            facts = facts with
            {
                Status = "observed", Reason = "local-read-only-counts", AuthorityKind = hasAuthority ? "saved-explicit-authority" : "unknown",
                SourceTimeZoneId = hasAuthority ? policy.SourceTimeZoneId : "unknown",
                RawTimestampTimeZoneId = hasAuthority ? policy.RawTimestampTimeZoneId : "unknown",
                AnalysisTimeZoneId = hasAuthority ? policy.AnalysisTimeZoneId : "unknown",
                SourceCultureName = hasAuthority ? policy.SourceCultureName : "unknown",
                AuthorityFingerprint = hasAuthority ? authority : "unknown",
                PolicyHosts = policy.HostIds.Where(id => id > 0).Distinct().Count(),
                PolicySensors = policy.SensorIds.Where(id => id > 0).Distinct().Count(),
                MirrorDevices = mirrorDevices, MirrorSensors = mirrorSensors,
                MappedDevices = mapped, ConflictingDevices = conflicts,
                GuardOverrideSensors = guards.Length, GuardSensorsInMirror = guardInMirror,
                ReadinessSampledSensors = ids.Length, SavedBindings = bindings.Count, QualifiedProfiles = qualified,
                ReadinessReason = !hasAuthority ? "explicit-time-authority-missing-or-invalid" :
                    ids.Length == 0 ? "formal-scope-empty" : qualified != ids.Length ? "sampled-profile-qualification-incomplete" :
                    policy.SensorIds.Count > ids.Length ? "bounded-profile-sample-not-full-scope-verdict" : "sampled-profiles-current",
                NetiqDay = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                NetiqReceipts = receipts.Count, NetiqSuccess = receipts.Count(row => row.Status == "success"),
                NetiqWaiting = receipts.Count(row => row.Status != "success"), NetiqParentRows = parents, NetiqEligibleParents = eligible,
                NetiqReason = eligible == 0 ? "no-eligible-netiq-parent" : "parent-qualification-is-separate-from-receipt-success"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception)
        { facts = facts with { Reason = "local-facts-unavailable-or-bounded-deadline" }; }

        // Existing probe sends a fixed one-token 'ping', never a production log or prompt.
        if (aiProbe is not null)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var previous = aiProbe.LatestResult;
                var result = await aiProbe.RefreshAsync(deadline.Token);
                facts = facts with { AiProbeStatus = result.Status switch
                    { AiProbeStatus.Ready => "small-request-ready", AiProbeStatus.NotConfigured => "not-configured",
                      AiProbeStatus.NotProbed => "not-probed", _ => "failed" },
                    AiProbeElapsedMilliseconds = result.HttpElapsedMilliseconds,
                    AiProbeTotalTokens = result.TotalTokens,
                    AiProbeEvidenceOrigin = ReferenceEquals(previous, result) ? "cached-small-request" : "current-small-request",
                    AiCostReason = result.TotalTokens.HasValue ? "tokens-observed-price-not-configured" : "token-and-price-cost-not-observed" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            { facts = facts with { AiProbeStatus = "failed-or-deadline", AiProbeElapsedMilliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 3) }; }
        }
        return facts;
    }

    private static PrtgMonitoringPolicy ReadPolicy(
        LogForesight.Core.Persistence.Sql.LfDbContext db, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = EfJsonBlobStore.ReadBoundedInContext(db, PrtgMonitoringPolicyStore.BlobKey,
            MaximumPolicyCharacters);
        ct.ThrowIfCancellationRequested();
        if (row.Version == 0 && row.Prefix is null) return new PrtgMonitoringPolicy();
        if (row.ReportedLength > MaximumPolicyCharacters || row.Prefix is null ||
            Encoding.UTF8.GetByteCount(row.Prefix) > MaximumPolicyCharacters)
            throw new InvalidDataException("policy-exceeds-probe-bound");
        // Match JsonBlobSingleton.Get(): absent/blank is default; explicit JSON null is also default.
        if (string.IsNullOrWhiteSpace(row.Prefix)) return new PrtgMonitoringPolicy();
        return JsonSerializer.Deserialize<PrtgMonitoringPolicy>(row.Prefix, LfJsonOptions.Pretty)
            ?? new PrtgMonitoringPolicy();
    }

    private static IReadOnlyDictionary<long, PrtgTrustedSamplingBinding> ReadBindings(
        LogForesight.Core.Persistence.Sql.LfDbContext db, long[] ids, CancellationToken ct)
    {
        if (ids.Length > MaximumProfileSample) throw new InvalidDataException("binding-sample-exceeds-probe-bound");
        if (ids.Length == 0) return new Dictionary<long, PrtgTrustedSamplingBinding>();
        var keys = ids.ToDictionary(id => PrtgTrustedSamplingBinding.StorePrefix + id.ToString(CultureInfo.InvariantCulture), id => id,
            StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var rows = BoundedJsonBlobBatchReader.Read(db, keys.Keys,
            PrtgTrustedSamplingBinding.MaximumSerializedBytes);
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<long, PrtgTrustedSamplingBinding>();
        foreach (var (blobKey, row) in rows)
        {
            ct.ThrowIfCancellationRequested();
            var id = keys[blobKey];
            if (row.Content is null || row.Length > PrtgTrustedSamplingBinding.MaximumSerializedBytes ||
                Encoding.UTF8.GetByteCount(row.Content) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
                throw new InvalidDataException("PRTG 採樣 binding 超過 8 KiB；拒絕使用截斷值。");
            var binding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(row.Content, LfJsonOptions.Pretty)
                ?? throw new InvalidDataException("PRTG 採樣 binding 無效；拒絕使用舊授權。");
            if (binding.SensorObjid != id) throw new InvalidDataException("PRTG 採樣 binding sensor ID 不符。");
            binding.Validate();
            result.Add(id, binding);
        }
        return result;
    }

    private static IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> ReadProfiles(
        LogForesight.Core.Persistence.Sql.LfDbContext db, long[] ids, CancellationToken ct)
    {
        if (ids.Length > MaximumProfileSample) throw new InvalidDataException("profile-sample-exceeds-probe-bound");
        if (ids.Length == 0) return new Dictionary<long, PrtgTrustedSamplingProfile>();
        var keys = ids.ToDictionary(id => PrtgTrustedSamplingProfile.StorePrefix + id.ToString(CultureInfo.InvariantCulture), id => id,
            StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var rows = BoundedJsonBlobBatchReader.Read(db, keys.Keys,
            PrtgTrustedSamplingProfile.MaximumSerializedBytes);
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<long, PrtgTrustedSamplingProfile>();
        foreach (var (blobKey, row) in rows)
        {
            ct.ThrowIfCancellationRequested();
            var id = keys[blobKey];
            if (row.Content is null || row.Length > PrtgTrustedSamplingProfile.MaximumSerializedBytes ||
                Encoding.UTF8.GetByteCount(row.Content) > PrtgTrustedSamplingProfile.MaximumSerializedBytes)
                continue; // Exclude only this untrusted profile; other bounded saved facts remain observable.
            PrtgTrustedSamplingProfile? profile;
            try { profile = JsonSerializer.Deserialize<PrtgTrustedSamplingProfile>(row.Content, LfJsonOptions.Pretty); }
            catch (JsonException) { continue; }
            if (profile is null || profile.SensorObjid != id)
                continue;
            try { profile.Validate(); }
            catch (InvalidDataException) { continue; }
            result.Add(id, profile);
        }
        return result;
    }

    private static IReadOnlyDictionary<long, PrtgResourceIdentity> ReadIdentities(
        LogForesight.Core.Persistence.Sql.LfDbContext db, long[] ids, CancellationToken ct)
    {
        if (ids.Length > MaximumProfileSample) throw new InvalidDataException("identity-sample-exceeds-probe-bound");
        if (ids.Length == 0) return new Dictionary<long, PrtgResourceIdentity>();
        var keys = ids.ToDictionary(id => PrtgResourceIdentityStore.Prefix + id.ToString(CultureInfo.InvariantCulture), id => id,
            StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var rows = BoundedJsonBlobBatchReader.Read(db, keys.Keys, PrtgResourceIdentityStore.MaxLedgerBytes);
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<long, PrtgResourceIdentity>();
        foreach (var (blobKey, row) in rows)
        {
            ct.ThrowIfCancellationRequested();
            var id = keys[blobKey];
            if (row.Content is null || row.Length > PrtgResourceIdentityStore.MaxLedgerBytes ||
                Encoding.UTF8.GetByteCount(row.Content) > PrtgResourceIdentityStore.MaxLedgerBytes)
                throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用舊涵蓋。");
            PrtgResourceIdentity? identity;
            try { identity = JsonSerializer.Deserialize<PrtgResourceIdentity>(row.Content, LfJsonOptions.Pretty); }
            catch (JsonException ex) { throw new InvalidDataException("PRTG 資源身分紀錄無效；拒絕沿用舊涵蓋。", ex); }
            if (identity is null || identity.SensorId != id || identity.Epoch < 0)
                throw new InvalidDataException("PRTG 資源身分紀錄無效；拒絕沿用舊涵蓋。");
            result.Add(id, identity);
        }
        return result;
    }

    private static PrtgTrustedSamplingStrategyContext ReadCurrentStrategy(
        LogForesight.Core.Persistence.Sql.LfDbContext db, PrtgMonitoringPolicy policy,
        string strategy, int minutes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = EfJsonBlobStore.ReadBoundedInContext(db, PrtgTrustedSamplingStrategyStateStore.BlobKey, 8192);
        ct.ThrowIfCancellationRequested();
        if (row.Version != 0 && (row.ReportedLength > 8192 || row.Prefix is null ||
            Encoding.UTF8.GetByteCount(row.Prefix) > 8192))
            throw new InvalidDataException("PRTG strategy fence 超過 8 KiB 上限。");
        return PrtgTrustedSamplingStrategyStateStore.ReadCurrent(row.Prefix, policy, strategy, minutes);
    }

    private static async Task<IReadOnlyList<NetiqSourceAttempt.Receipt>> ReadLatestReceiptsAsync(
        LogForesight.Core.Persistence.Sql.LfDbContext db, DateTime day, CancellationToken ct)
    {
        var prefix = "netiq_hostday_attempt_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_";
        var matches = db.LogLines.AsNoTracking().Where(row => row.LogKey.StartsWith(prefix));
        var latest = matches.GroupBy(row => row.LogKey)
            .Select(group => new { LogKey = group.Key, Seq = group.Max(row => row.Seq) });
        var rows = await (from row in matches
                          join newest in latest on new { row.LogKey, row.Seq } equals new { newest.LogKey, newest.Seq }
                          orderby row.Seq descending
                          select new { row.LogKey, Length = row.Line.Length, Line = row.Line.Substring(0, MaximumNetiqLineCharacters + 1) })
            .Take(MaximumNetiqReceipts + 1).ToListAsync(ct);
        if (rows.Count > MaximumNetiqReceipts) throw new InvalidDataException("NetIQ attempt ledger exceeds the per-day host limit.");
        var receipts = new List<NetiqSourceAttempt.Receipt>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.Length > MaximumNetiqLineCharacters) throw new InvalidDataException("NetIQ attempt ledger receipt is oversized.");
            NetiqSourceAttempt.Receipt receipt;
            try { receipt = JsonSerializer.Deserialize<NetiqSourceAttempt.Receipt>(row.Line) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new InvalidDataException("NetIQ attempt ledger contains malformed data.", ex); }
            if (receipt.HostId <= 0 || receipt.Status is not ("running" or "success" or "partial" or "retained-incomplete" or "failed" or "stopped") ||
                receipt.Date.Date != day.Date || row.LogKey != prefix + receipt.HostId.ToString(CultureInfo.InvariantCulture) ||
                receipt.AttemptAtUtc.Kind != DateTimeKind.Utc || receipt.AttemptAtUtc > DateTime.UtcNow.AddMinutes(5))
                throw new InvalidDataException("NetIQ attempt ledger contains an invalid or unknown receipt.");
            if (receipt.RunId <= 0) receipt = receipt with { Status = "unknown" };
            receipts.Add(receipt);
        }
        return receipts;
    }

    private static async Task<int> ReadEligibleParentCountAsync(LogForesight.Core.Persistence.Sql.LfDbContext db,
        DateTime day, CancellationToken ct)
    {
        // Query JSON metadata in the provider; never load the potentially large parent payload.
        var sqlite = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        var sqlServer = db.Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer";
        if (!sqlite && !sqlServer) throw new NotSupportedException();
        var sql = sqlite
            ? "SELECT COUNT(*) AS Value FROM lf_daily_records p WHERE p.record_date = {0} AND p.detail_pruned = 0 " +
              "AND json_valid(p.content_json) = 1 AND (SELECT COUNT(*) FROM json_each(CASE WHEN json_valid(p.content_json) THEN p.content_json ELSE '{}' END) j WHERE j.key = 'LogSource' AND j.type = 'integer' AND j.value = 2) = 1 " +
              "AND (SELECT COUNT(*) FROM json_each(CASE WHEN json_valid(p.content_json) THEN p.content_json ELSE '{}' END) j WHERE j.key = 'LogSource') = 1 " +
              "AND (SELECT COUNT(*) FROM json_each(CASE WHEN json_valid(p.content_json) THEN p.content_json ELSE '{}' END) j WHERE j.key = 'LatestNetiqAttemptStatus') <= 1 " +
              "AND NOT EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(p.content_json) THEN p.content_json ELSE '{}' END) j WHERE j.key = 'LatestNetiqAttemptStatus' AND NOT (j.type = 'null' OR (j.type = 'text' AND j.value = 'success')))"
            : "SELECT COUNT(*) AS Value FROM lf_daily_records p WHERE p.record_date = {0} AND p.detail_pruned = 0 AND ISJSON(p.content_json) = 1 " +
              "AND (SELECT COUNT(*) FROM OPENJSON(CASE WHEN ISJSON(p.content_json) = 1 THEN p.content_json ELSE N'{}' END) j WHERE j.[key] = N'LogSource' AND j.[type] = 2 AND j.[value] = N'2') = 1 " +
              "AND (SELECT COUNT(*) FROM OPENJSON(CASE WHEN ISJSON(p.content_json) = 1 THEN p.content_json ELSE N'{}' END) j WHERE j.[key] = N'LogSource') = 1 " +
              "AND (SELECT COUNT(*) FROM OPENJSON(CASE WHEN ISJSON(p.content_json) = 1 THEN p.content_json ELSE N'{}' END) j WHERE j.[key] = N'LatestNetiqAttemptStatus') <= 1 " +
              "AND NOT EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(p.content_json) = 1 THEN p.content_json ELSE N'{}' END) j WHERE j.[key] = N'LatestNetiqAttemptStatus' AND NOT (j.[type] = 0 OR (j.[type] = 1 AND j.[value] = N'success')))";
        // Escape JSON object literals for EF composite formatting, preserving the single parameter.
        sql = sql.Replace("'{}'", "'{{}}'", StringComparison.Ordinal);
        return await db.Database.SqlQueryRaw<int>(sql, day).SingleAsync(ct);
    }

    internal static bool ProfileMatchesCurrentStrategy(PrtgTrustedSamplingProfile profile,
        PrtgTrustedSamplingStrategyContext strategy) => strategy.Ready &&
        profile.StrategyFingerprint == strategy.StrategyFingerprint &&
        profile.StrategyMinutes == strategy.StrategyMinutes &&
        profile.StrategyEffectiveFromHourUtc == strategy.EffectiveFromHourUtc;

    private static async Task<PrtgProbeSqliteProgressDeadline> AttachSqliteProgressDeadlineAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database,
        SqliteConnection connection, CancellationToken token)
    {
        connection.DefaultTimeout = LocalSqliteBusyTimeoutMilliseconds / 1000;
        token.ThrowIfCancellationRequested();
        await database.OpenConnectionAsync(token);
        token.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={LocalSqliteBusyTimeoutMilliseconds}";
        await command.ExecuteNonQueryAsync(token);
        return new PrtgProbeSqliteProgressDeadline(connection, token);
    }
}

/// <summary>
/// Makes SQLite's synchronous VM work interruptible by the collector's caller/deadline token.
/// Keep this attached only to the collector-owned connection and always remove it before disposal.
/// </summary>
internal sealed class PrtgProbeSqliteProgressDeadline : IDisposable
{
    private const int ProgressInstructionInterval = 1000;
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(15);
    private readonly SQLitePCL.sqlite3 _handle;
    private readonly CancellationToken _token;
    private readonly long _deadlineTimestamp;
    private readonly SQLitePCL.delegate_progress _callback;
    private bool _disposed;

    internal PrtgProbeSqliteProgressDeadline(SqliteConnection connection, CancellationToken token,
        TimeSpan? maximumDuration = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var duration = maximumDuration ?? DefaultMaximumDuration;
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _handle = connection.Handle ?? throw new InvalidOperationException("SQLite progress deadline requires an open owned connection.");
        _token = token;
        var durationTicks = checked((long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
        _deadlineTimestamp = checked(Stopwatch.GetTimestamp() + durationTicks);
        _callback = OnProgress;
        SQLitePCL.raw.sqlite3_progress_handler(_handle, ProgressInstructionInterval, _callback, null!);
    }

    private int OnProgress(object? _) =>
        _token.IsCancellationRequested || Stopwatch.GetTimestamp() >= _deadlineTimestamp ? 1 : 0;

    public void Dispose()
    {
        if (_disposed) return;
        SQLitePCL.raw.sqlite3_progress_handler(_handle, 0, null!, null!);
        _disposed = true;
    }
}

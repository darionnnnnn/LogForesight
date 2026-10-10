using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LogForesight.WorkloadAcceptance;

/// <summary>
/// Fail-closed verifier for the durable raw qualification and current profile API snapshots.
/// This proves only that the supplied snapshots satisfy their API contracts; it does not prove
/// native source support, capacity, retention, or whole-round acceptance.
/// </summary>
public static class QualificationCoverage
{
    private const int MaximumSensors = 15_000;
    private const int PageSize = 100;
    private const int MaximumManifestBytes = 512 * 1024;
    private const int MaximumJobBytes = 16 * 1024;
    private const int MaximumJobPageBytes = 128 * 1024;
    private const int MaximumProfilePageBytes = 512 * 1024;
    private const int MaximumJsonDepth = 32;

    public sealed record QualificationCoverageResult(bool Ready, string Reason, int ExpectedSensors,
        int QualifiedSensors, int ReadyProfiles);

    public static QualificationCoverageResult Verify(JsonElement manifest, JsonElement before,
        IReadOnlyList<JsonElement> pages, IReadOnlyList<JsonElement> profilePages, JsonElement after)
    {
        var expectedCount = 0;
        try
        {
            CheckDocument(manifest, MaximumManifestBytes, "manifest");
            var expected = ReadManifest(manifest);
            expectedCount = expected.SensorIds.Count;
            CheckDocument(before, MaximumJobBytes, "job-before");
            CheckDocument(after, MaximumJobBytes, "job-after");

            var beforeJob = ReadEnvelopeData(before, "job-before");
            var afterJob = ReadEnvelopeData(after, "job-after");
            var first = ReadJob(beforeJob, expected);
            var last = ReadJob(afterJob, expected);
            if (!first.Equals(last)) return Reject("job-revision-drift", expectedCount, 0, 0);
            if (!first.Ready) return Reject(first.Reason, expectedCount, 0, 0);

            var expectedPageCount = (expectedCount + PageSize - 1) / PageSize;
            var pageIds = new HashSet<long>();
            var rawRows = new Dictionary<long, RawSensor>();
            var qualifiedCount = 0;
            if (pages is null || pages.Count != expectedPageCount)
                return Reject("qualification-page-count-mismatch", expectedCount, 0, 0);
            for (var pageIndex = 0; pageIndex < expectedPageCount; pageIndex++)
            {
                var pageJson = pages[pageIndex];
                CheckDocument(pageJson, MaximumJobPageBytes, $"qualification-page-{pageIndex}");
                var page = ReadEnvelopeData(pageJson, $"qualification-page-{pageIndex}");
                var offset = pageIndex * PageSize;
                var expectedRows = Math.Min(PageSize, expectedCount - offset);
                RequireString(page, "status", "completed", "qualification-page-status");
                RequireInt(page, "total", expectedCount, "qualification-page-total");
                RequireInt(page, "offset", offset, "qualification-page-offset");
                RequireInt(page, "limit", PageSize, "qualification-page-limit");
                var expectedNext = offset + expectedRows < expectedCount ? offset + expectedRows : (long?)null;
                RequireNullableLong(page, "nextOffset", expectedNext, "qualification-page-next-offset");
                var rows = RequireArray(page, "rows", "qualification-page-rows");
                if (rows.GetArrayLength() != expectedRows)
                    return Reject("qualification-page-row-count-mismatch", expectedCount, qualifiedCount, 0);
                foreach (var row in rows.EnumerateArray())
                {
                    var id = RequirePositiveInt64(row, "sensorObjid", "qualification-sensor-id");
                    if (!expected.SensorIdSet.Contains(id))
                        return Reject("qualification-foreign-sensor", expectedCount, qualifiedCount, 0);
                    if (!pageIds.Add(id)) return Reject("qualification-duplicate-sensor", expectedCount, qualifiedCount, 0);
                    RequireString(row, "status", "qualified", "qualification-sensor-status");
                    RequireBoolean(row, "hasQualificationProof", true, "qualification-proof-missing");
                    var bindingRevision = RequireInt64(row, "bindingRevision", "qualification-binding-revision");
                    if (bindingRevision <= 0)
                        return Reject("qualification-binding-revision-invalid", expectedCount, qualifiedCount, 0);
                    var bindingFingerprint = RequireNonEmptyString(row, "bindingFingerprint", "qualification-binding-fingerprint");
                    rawRows.Add(id, new RawSensor(bindingRevision, bindingFingerprint));
                    qualifiedCount++;
                }
            }
            if (!pageIds.SetEquals(expected.SensorIdSet) || qualifiedCount != expectedCount)
                return Reject("qualification-sensor-set-incomplete", expectedCount, qualifiedCount, 0);

            var profileIds = new HashSet<long>();
            var readyProfiles = 0;
            if (profilePages is null || profilePages.Count != expectedPageCount)
                return Reject("profile-page-count-mismatch", expectedCount, qualifiedCount, 0);
            for (var pageIndex = 0; pageIndex < expectedPageCount; pageIndex++)
            {
                var pageJson = profilePages[pageIndex];
                CheckDocument(pageJson, MaximumProfilePageBytes, $"profile-page-{pageIndex}");
                var page = ReadEnvelopeData(pageJson, $"profile-page-{pageIndex}");
                RequireString(page, "settingsRevision", expected.SettingsRevision, "profile-settings-revision-mismatch");
                RequireString(page, "policyRevision", expected.PolicyRevision, "profile-policy-revision-mismatch");
                RequireString(page, "sourceGeneration", expected.SourceGeneration, "profile-source-generation-mismatch");
                RequireString(page, "authorityContextFingerprint", expected.AuthorityContextFingerprint,
                    "profile-authority-context-mismatch");
                RequireBoolean(page, "prtgEnabled", true, "profile-source-disabled");
                var offset = pageIndex * PageSize;
                var expectedRows = Math.Min(PageSize, expectedCount - offset);
                RequireInt(page, "total", expectedCount, "profile-page-total");
                RequireInt(page, "offset", offset, "profile-page-offset");
                RequireInt(page, "limit", PageSize, "profile-page-limit");
                var expectedNext = offset + expectedRows < expectedCount ? offset + expectedRows : (long?)null;
                RequireNullableLong(page, "nextOffset", expectedNext, "profile-page-next-offset");
                var rows = RequireArray(page, "rows", "profile-page-rows");
                if (rows.GetArrayLength() != expectedRows)
                    return Reject("profile-page-row-count-mismatch", expectedCount, qualifiedCount, readyProfiles);
                foreach (var row in rows.EnumerateArray())
                {
                    var id = RequirePositiveInt64(row, "sensorObjid", "profile-sensor-id");
                    if (!expected.SensorIdSet.Contains(id))
                        return Reject("profile-foreign-sensor", expectedCount, qualifiedCount, readyProfiles);
                    if (!profileIds.Add(id)) return Reject("profile-duplicate-sensor", expectedCount, qualifiedCount, readyProfiles);
                    RequireString(row, "status", "ready", "profile-not-ready");
                    RequireString(row, "bindingStatus", "qualified", "profile-binding-not-qualified");
                    var revision = RequireInt64(row, "bindingRevision", "profile-binding-revision");
                    if (revision <= 0) return Reject("profile-binding-revision-invalid", expectedCount, qualifiedCount, readyProfiles);
                    var fingerprint = RequireNonEmptyString(row, "bindingFingerprint", "profile-binding-fingerprint");
                    RequireNonEmptyString(row, "qualificationProofReference", "profile-qualification-proof-missing");
                    RequirePositiveInt64(row, "currentIdentityEpoch", "profile-identity-epoch");
                    RequireNonEmptyString(row, "currentChannelGeneration", "profile-channel-generation");
                    var missingFacts = RequireArray(row, "missingFacts", "profile-missing-facts");
                    if (missingFacts.GetArrayLength() != 0)
                        return Reject("profile-has-missing-facts", expectedCount, qualifiedCount, readyProfiles);
                    if (!rawRows.TryGetValue(id, out var raw) || raw.BindingRevision != revision ||
                        !string.Equals(raw.BindingFingerprint, fingerprint, StringComparison.Ordinal))
                        return Reject("profile-binding-does-not-match-raw-proof", expectedCount, qualifiedCount, readyProfiles);
                    readyProfiles++;
                }
            }
            if (!profileIds.SetEquals(expected.SensorIdSet) || readyProfiles != expectedCount)
                return Reject("profile-sensor-set-incomplete", expectedCount, qualifiedCount, readyProfiles);

            return new(true, "exact-full-scope-job-and-ready-profile-snapshots", expectedCount,
                qualifiedCount, readyProfiles);
        }
        catch (CoverageException ex)
        {
            return Reject(ex.Reason, expectedCount, 0, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            return Reject("invalid-snapshot-shape", expectedCount, 0, 0);
        }
    }

    private sealed record ExpectedManifest(IReadOnlyList<long> SensorIds, HashSet<long> SensorIdSet,
        string JobId, string ScopeFingerprint, string SettingsRevision, string PolicyRevision,
        string SourceGeneration, string AuthorityContextFingerprint);

    private sealed record RawSensor(long BindingRevision, string BindingFingerprint);

    private sealed record JobSnapshot(bool Ready, string Reason, string JobId, string Status,
        string ScopeFingerprint, string SettingsRevision, string PolicyRevision, string SourceGeneration,
        string AuthorityContextFingerprint, long Version, long Wave, long Selected, long Qualified,
        long Waiting, long Failed, long PageCount, long InitializedPages, long InitializationCursor,
        bool CancelRequested, string StableKey);

    private static ExpectedManifest ReadManifest(JsonElement manifest)
    {
        var idsJson = RequireArray(manifest, "sensorIds", "manifest-sensor-ids");
        var ids = new List<long>(idsJson.GetArrayLength());
        var set = new HashSet<long>();
        foreach (var id in idsJson.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var value) || value <= 0)
                throw new CoverageException("manifest-sensor-id-invalid");
            if (!set.Add(value)) throw new CoverageException("manifest-sensor-id-duplicate");
            ids.Add(value);
        }
        if (ids.Count is < 1 or > MaximumSensors) throw new CoverageException("manifest-sensor-count-out-of-range");
        return new ExpectedManifest(ids, set,
            RequireNonEmptyString(manifest, "jobId", "manifest-job-id"),
            RequireNonEmptyString(manifest, "scopeFingerprint", "manifest-scope-fingerprint"),
            RequireNonEmptyString(manifest, "settingsRevision", "manifest-settings-revision"),
            RequireNonEmptyString(manifest, "policyRevision", "manifest-policy-revision"),
            RequireNonEmptyString(manifest, "sourceGeneration", "manifest-source-generation"),
            RequireNonEmptyString(manifest, "authorityContextFingerprint", "manifest-authority-context"));
    }

    private static JobSnapshot ReadJob(JsonElement job, ExpectedManifest expected)
    {
        var jobId = RequireNonEmptyString(job, "jobId", "job-id-missing");
        var status = RequireNonEmptyString(job, "status", "job-status-missing");
        var scope = RequireNonEmptyString(job, "scopeFingerprint", "job-scope-missing");
        var settings = RequireNonEmptyString(job, "settingsRevision", "job-settings-revision-missing");
        var policy = RequireNonEmptyString(job, "policyRevision", "job-policy-revision-missing");
        var source = RequireNonEmptyString(job, "sourceGeneration", "job-source-generation-missing");
        var authority = RequireNonEmptyString(job, "authorityContextFingerprint", "job-authority-context-missing");
        var version = RequireInt64(job, "version", "job-version-missing");
        var wave = RequireInt64(job, "wave", "job-wave-missing");
        var selected = RequireInt64(job, "selected", "job-selected-missing");
        var qualified = RequireInt64(job, "qualified", "job-qualified-missing");
        var waiting = RequireInt64(job, "waiting", "job-waiting-missing");
        var failed = RequireInt64(job, "failed", "job-failed-missing");
        var pageCount = RequireInt64(job, "pageCount", "job-page-count-missing");
        var initializedPages = RequireInt64(job, "initializedPages", "job-initialized-pages-missing");
        var initializationCursor = RequireInt64(job, "initializationCursor", "job-initialization-cursor-missing");
        var eligible = RequireInt64(job, "eligible", "job-eligible-missing");
        var attempts = RequireInt64(job, "attempts", "job-attempts-missing");
        var totalFailed = RequireInt64(job, "totalFailed", "job-total-failed-missing");
        var maximumAttempts = RequireInt64(job, "maximumAttempts", "job-maximum-attempts-missing");
        var admittedEligible = RequireInt64(job, "admittedEligibleSensorCount", "job-admitted-eligible-missing");
        var initializedEligible = RequireInt64(job, "initializedEligibleSensorCount", "job-initialized-eligible-missing");
        var cursor = RequireInt64(job, "cursor", "job-cursor-missing");
        var durationHours = RequireInt64(job, "durationHours", "job-duration-missing");
        var cancelRequested = RequireBoolean(job, "cancelRequested", false, "job-cancel-requested");
        if (jobId != expected.JobId || scope != expected.ScopeFingerprint || settings != expected.SettingsRevision ||
            policy != expected.PolicyRevision || source != expected.SourceGeneration || authority != expected.AuthorityContextFingerprint)
            throw new CoverageException("job-manifest-mismatch");
        if (version <= 0 || wave <= 0 || selected != expected.SensorIds.Count)
            throw new CoverageException("job-identity-invalid");
        if (eligible < 0 || eligible > selected || admittedEligible < 0 || admittedEligible > selected ||
            initializedEligible < 0 || initializedEligible > selected || attempts < 0 || totalFailed < 0 ||
            maximumAttempts is < 1 or > 3 || durationHours is < 1 or > 720 || cursor < 0 || cursor >= selected)
            throw new CoverageException("job-counter-out-of-range");
        var expectedPages = (expected.SensorIds.Count + PageSize - 1) / PageSize;
        if (status != "completed" || qualified != expected.SensorIds.Count || waiting != 0 || failed != 0 ||
            pageCount != expectedPages || initializedPages != expectedPages || initializationCursor != expectedPages || cancelRequested)
            return new(false, "job-not-fully-completed", jobId, status, scope, settings, policy, source, authority,
                version, wave, selected, qualified, waiting, failed, pageCount, initializedPages, initializationCursor,
                cancelRequested, StableJobKey(job, expected));
        return new(true, "ready", jobId, status, scope, settings, policy, source, authority, version, wave,
            selected, qualified, waiting, failed, pageCount, initializedPages, initializationCursor,
            cancelRequested, StableJobKey(job, expected));
    }

    private static string StableJobKey(JsonElement job, ExpectedManifest expected)
    {
        var fields = new[] { "jobId", "status", "scopeFingerprint", "settingsRevision", "policyRevision",
            "sourceGeneration", "authorityContextFingerprint", "version", "wave", "pageCount", "initializedPages",
            "initializationCursor", "selected", "eligible", "qualified", "waiting", "failed", "attempts",
            "totalFailed", "maximumAttempts", "cancelRequested" };
        var builder = new StringBuilder();
        foreach (var name in fields)
        {
            var value = RequireProperty(job, name, "job-field-missing");
            builder.Append(name).Append('=').Append(value.GetRawText()).Append('\n');
        }
        return builder.ToString();
    }

    private static JsonElement ReadEnvelopeData(JsonElement envelope, string name)
    {
        RequireBoolean(envelope, "success", true, $"{name}-api-failed");
        var data = RequireProperty(envelope, "data", $"{name}-data-missing");
        if (data.ValueKind != JsonValueKind.Object) throw new CoverageException($"{name}-data-invalid");
        return data;
    }

    private static void CheckDocument(JsonElement element, int maximumBytes, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new CoverageException($"{name}-missing");
        var raw = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > maximumBytes) throw new CoverageException($"{name}-over-limit");
        CheckDuplicates(element, name);
    }

    private static void CheckDuplicates(JsonElement element, string name, int depth = 0)
    {
        if (depth > MaximumJsonDepth) throw new CoverageException($"{name}-depth-limit");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CoverageException($"{name}-duplicate-property");
                CheckDuplicates(property.Value, name, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child, name, depth + 1);
    }

    private static JsonElement RequireProperty(JsonElement obj, string name, string reason)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw new CoverageException(reason);
        foreach (var property in obj.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new CoverageException(reason);
    }

    private static JsonElement RequireArray(JsonElement obj, string name, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (value.ValueKind != JsonValueKind.Array) throw new CoverageException(reason);
        return value;
    }

    private static string RequireNonEmptyString(JsonElement obj, string name, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new CoverageException(reason);
        return value.GetString()!;
    }

    private static string RequireString(JsonElement obj, string name, string expected, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (value.ValueKind != JsonValueKind.String || value.GetString() != expected)
            throw new CoverageException(reason);
        return expected;
    }

    private static long RequirePositiveInt64(JsonElement obj, string name, string reason)
    {
        var value = RequireInt64(obj, name, reason);
        if (value <= 0) throw new CoverageException(reason);
        return value;
    }

    private static long RequireInt64(JsonElement obj, string name, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new CoverageException(reason);
        return number;
    }

    private static int RequireInt(JsonElement obj, string name, int expected, string reason)
    {
        var value = RequireInt64(obj, name, reason);
        if (value != expected) throw new CoverageException(reason);
        return expected;
    }

    private static bool RequireBoolean(JsonElement obj, string name, bool expected, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != expected)
            throw new CoverageException(reason);
        return expected;
    }

    private static void RequireNullableLong(JsonElement obj, string name, long? expected, string reason)
    {
        var value = RequireProperty(obj, name, reason);
        if (expected is null)
        {
            if (value.ValueKind != JsonValueKind.Null) throw new CoverageException(reason);
        }
        else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number != expected.Value)
            throw new CoverageException(reason);
    }

    private static QualificationCoverageResult Reject(string reason, int expected, int qualified, int profiles) =>
        new(false, reason, expected, qualified, profiles);

    private sealed class CoverageException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }
}

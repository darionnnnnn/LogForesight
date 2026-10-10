using System.Text.Json;
using LogForesight.WorkloadAcceptance;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgQualificationCoverageTests
{
    [Fact]
    public void ExactFull15000SensorSetAcrossAllPagesIsReady()
    {
        using var fixture = Fixture.Create(15_000);

        var result = Verify(fixture);

        Assert.True(result.Ready, result.Reason);
        Assert.Equal(15_000, result.ExpectedSensors);
        Assert.Equal(15_000, result.QualifiedSensors);
        Assert.Equal(15_000, result.ReadyProfiles);
    }

    [Fact]
    public void LastQualificationPageCannotOmitAnExpectedSensor()
    {
        using var fixture = Fixture.Create(101);
        fixture.Pages[^1] = Json($"{{\"success\":true,\"data\":{{\"status\":\"completed\",\"total\":101,\"offset\":100,\"limit\":100,\"nextOffset\":null,\"rows\":[]}}}}");

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Contains("qualification-page-row-count-mismatch", result.Reason);
    }

    [Fact]
    public void ForeignSensorOnLastProfilePageIsRejected()
    {
        using var fixture = Fixture.Create(101);
        fixture.ProfilePages[^1] = Json(PageJson(101, 100, [ProfileRow(999_999)]));

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("profile-foreign-sensor", result.Reason);
    }

    [Fact]
    public void JobScopeOrRevisionDriftBetweenSnapshotsIsRejected()
    {
        using var fixture = Fixture.Create(3);
        fixture.After = Json(JobJson(3, scopeFingerprint: "changed-scope"));

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("job-manifest-mismatch", result.Reason);
    }

    [Fact]
    public void JobVersionWaveOrCountersDriftingBetweenSnapshotsIsRejected()
    {
        using var fixture = Fixture.Create(3);
        fixture.After = Json(JobJson(3, version: 9));

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("job-revision-drift", result.Reason);
    }

    [Fact]
    public void NonBooleanSourceEnabledAndNonStrictJobCountAreRejected()
    {
        using var fixture = Fixture.Create(3);
        fixture.ProfilePages[0] = Json(PageJson(3, 0, [ProfileRow(1), ProfileRow(2), ProfileRow(3)], prtgEnabled: "true"));

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("profile-source-disabled", result.Reason);

        using var second = Fixture.Create(3);
        second.Before = Json(JobJson(3, selected: "3"));
        result = Verify(second);
        Assert.False(result.Ready);
        Assert.Equal("job-selected-missing", result.Reason);
    }

    [Fact]
    public void MissingProfilePageAndNonCompletedJobAreRejected()
    {
        using var fixture = Fixture.Create(101);
        fixture.ProfilePages.RemoveAt(1);

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("profile-page-count-mismatch", result.Reason);

        using var second = Fixture.Create(3);
        second.Before = Json(JobJson(3, status: "running"));
        second.After = Json(JobJson(3, status: "running"));
        result = Verify(second);
        Assert.False(result.Ready);
        Assert.Equal("job-not-fully-completed", result.Reason);
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedEvenWhenCaseDiffers()
    {
        using var fixture = Fixture.Create(3);
        var raw = fixture.Before.RootElement.GetRawText().Replace("\"success\":true", "\"success\":true,\"Success\":true", StringComparison.Ordinal);
        fixture.Before = Json(raw);

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("job-before-duplicate-property", result.Reason);
    }

    [Fact]
    public void JsonNestingBeyondThirtyTwoLevelsIsRejected()
    {
        using var fixture = Fixture.Create(3);
        var deep = new string('[', 33) + "0" + new string(']', 33);
        var raw = fixture.Before.RootElement.GetRawText().Replace("\"success\":true,", $"\"success\":true,\"extra\":{deep},", StringComparison.Ordinal);
        fixture.Before = Json(raw);

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("job-before-depth-limit", result.Reason);
    }

    [Fact]
    public void AncillaryJobCountersRequireStrictIntegerTypesAndValidRanges()
    {
        using var fixture = Fixture.Create(3);
        var raw = fixture.Before.RootElement.GetRawText().Replace("\"eligible\":3", "\"eligible\":\"3\"", StringComparison.Ordinal);
        fixture.Before = Json(raw);

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("job-eligible-missing", result.Reason);

        using var second = Fixture.Create(3);
        raw = second.Before.RootElement.GetRawText().Replace("\"maximumAttempts\":1", "\"maximumAttempts\":4", StringComparison.Ordinal);
        second.Before = Json(raw);
        result = Verify(second);
        Assert.False(result.Ready);
        Assert.Equal("job-counter-out-of-range", result.Reason);
    }

    [Fact]
    public void ProfileMustMatchRawBindingAndCurrentSourceContext()
    {
        using var fixture = Fixture.Create(3);
        fixture.ProfilePages[0] = Json(PageJson(3, 0,
            [ProfileRow(1, bindingFingerprint: "wrong"), ProfileRow(2), ProfileRow(3)]));

        var result = Verify(fixture);

        Assert.False(result.Ready);
        Assert.Equal("profile-binding-does-not-match-raw-proof", result.Reason);

        using var second = Fixture.Create(3);
        second.ProfilePages[0] = Json(PageJson(3, 0, [ProfileRow(1), ProfileRow(2), ProfileRow(3)],
            sourceGeneration: "old-generation"));
        result = Verify(second);
        Assert.False(result.Ready);
        Assert.Equal("profile-source-generation-mismatch", result.Reason);
    }

    private static QualificationCoverage.QualificationCoverageResult Verify(Fixture fixture) =>
        QualificationCoverage.Verify(fixture.Manifest.RootElement, fixture.Before.RootElement,
            fixture.Pages.Select(page => page.RootElement).ToArray(),
            fixture.ProfilePages.Select(page => page.RootElement).ToArray(), fixture.After.RootElement);

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    private static string ManifestJson(int count) => JsonSerializer.Serialize(new
    {
        sensorIds = Enumerable.Range(1, count).Select(id => (long)id).ToArray(),
        jobId = "job-1", scopeFingerprint = "scope-1", settingsRevision = "settings-1",
        policyRevision = "policy-1", sourceGeneration = "source-1", authorityContextFingerprint = "authority-1"
    });

    private static string JobJson(int count, string status = "completed", long version = 7,
        string scopeFingerprint = "scope-1", object? selected = null) => JsonSerializer.Serialize(new
    {
        success = true,
        data = new
        {
            jobId = "job-1", status, scopeFingerprint, sourceGeneration = "source-1", settingsRevision = "settings-1",
            policyRevision = "policy-1", authorityContextFingerprint = "authority-1", version, wave = 2,
            pageCount = (count + 99) / 100, initializedPages = (count + 99) / 100,
            initializationCursor = (count + 99) / 100, admittedEligibleSensorCount = count,
            initializedEligibleSensorCount = count, cursor = count - 1, selected = selected ?? count,
            eligible = count, qualified = count, waiting = 0, failed = 0, attempts = count, totalFailed = 0,
            maximumAttempts = 1, durationHours = 24, cancelRequested = false
        }
    });

    private static object RawRow(int id) => new
    {
        sensorObjid = id, bindingRevision = 1, bindingFingerprint = $"binding-{id}", status = "qualified",
        reason = "proof-and-authority-current", attempts = 1, waveAttempts = 1, hasQualificationProof = true
    };

    private static object ProfileRow(int id, string? bindingFingerprint = null) => new
    {
        sensorObjid = id, status = "ready", missingFacts = Array.Empty<string>(),
        currentIdentityEpoch = 4, currentChannelGeneration = "channel-generation-1", bindingRevision = 1,
        bindingFingerprint = bindingFingerprint ?? $"binding-{id}", bindingStatus = "qualified",
        qualificationProofReference = $"proof-{id}"
    };

    private static string PageJson(int count, int offset, IReadOnlyList<object> rows,
        object? prtgEnabled = null, string sourceGeneration = "source-1") => JsonSerializer.Serialize(new
    {
        success = true,
        data = new
        {
            total = count, offset, limit = 100, nextOffset = offset + rows.Count < count ? offset + rows.Count : (int?)null,
            settingsRevision = "settings-1", policyRevision = "policy-1", sourceGeneration,
            authorityContextFingerprint = "authority-1", prtgEnabled = prtgEnabled ?? (object)true,
            rows
        }
    });

    private sealed class Fixture : IDisposable
    {
        public required JsonDocument Manifest { get; set; }
        public required JsonDocument Before { get; set; }
        public required JsonDocument After { get; set; }
        public List<JsonDocument> Pages { get; } = [];
        public List<JsonDocument> ProfilePages { get; } = [];

        public static Fixture Create(int count)
        {
            var fixture = new Fixture
            {
                Manifest = Json(ManifestJson(count)), Before = Json(JobJson(count)), After = Json(JobJson(count))
            };
            for (var offset = 0; offset < count; offset += 100)
            {
                var ids = Enumerable.Range(offset + 1, Math.Min(100, count - offset)).ToArray();
                var rawRows = ids.Select(RawRow).ToArray();
                var profileRows = ids.Select(id => ProfileRow(id)).ToArray();
                var next = offset + ids.Length < count ? offset + ids.Length : (int?)null;
                fixture.Pages.Add(Json(JsonSerializer.Serialize(new
                {
                    success = true,
                    data = new { status = "completed", total = count, offset, limit = 100, nextOffset = next, rows = rawRows }
                })));
                fixture.ProfilePages.Add(Json(PageJson(count, offset, profileRows)));
            }
            return fixture;
        }

        public void Dispose()
        {
            Manifest.Dispose(); Before.Dispose(); After.Dispose();
            foreach (var page in Pages) page.Dispose();
            foreach (var page in ProfilePages) page.Dispose();
        }
    }
}

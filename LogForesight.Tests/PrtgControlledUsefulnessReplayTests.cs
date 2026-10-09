using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// Emits a bounded, versioned report for a synthetic technical replay. The independent truth rows
/// are controlled scenarios, not incidents, vendor observations, or operational outcome evidence.
/// </summary>
public sealed class PrtgControlledUsefulnessReplayTests
{
    private const string TruthFixturePath = "LogForesight.Tests/Fixtures/prtg-usefulness-controlled-truth-v1.json";
    private const string FormalManifestPath = "LogForesight.Tests/Fixtures/prtg-round53-formal-cases.json";
    private const string ExpectedFormalManifestSha256 = "9D05B81CE369359FA86F7FF3B4C8F119DC78D20A3681D1DB2C5C474093E4FFC2";
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = false };
    private readonly ITestOutputHelper _output;

    public PrtgControlledUsefulnessReplayTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task WritesBoundedVersionedFourBaselineControlledReplayReport()
    {
        var repoRoot = FindRepositoryRoot();
        var truthPath = Path.Combine(repoRoot, TruthFixturePath.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(repoRoot, FormalManifestPath.Replace('/', Path.DirectorySeparatorChar));
        using var truthJson = JsonDocument.Parse(File.ReadAllBytes(truthPath));
        var truthSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(truthPath)));
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));

        Assert.Equal("LogForesight.ControlledUsefulnessReplay", truthJson.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, truthJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ExpectedFormalManifestSha256, manifestSha256);
        Assert.Equal(ExpectedFormalManifestSha256,
            truthJson.RootElement.GetProperty("formalManifestBinding").GetProperty("sha256").GetString());

        using var manifest = JsonDocument.Parse(manifestBytes);
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        var formalFamilyCounts = manifest.RootElement.GetProperty("families").EnumerateArray()
            .Select(family => new FormalFamilyCount(family.GetProperty("family").GetString()!,
                family.GetProperty("positiveCases").GetArrayLength(),
                family.GetProperty("negativeAndBoundaryCases").GetArrayLength()))
            .ToArray();
        var formalRows = formalFamilyCounts.Sum(item => item.Positive + item.NegativeAndBoundary);
        var formalCaseIds = manifest.RootElement.GetProperty("families").EnumerateArray()
            .SelectMany(family => family.GetProperty("positiveCases").EnumerateArray()
                .Concat(family.GetProperty("negativeAndBoundaryCases").EnumerateArray()))
            .Select(item => item.GetProperty("caseId").GetString()!).ToArray();
        Assert.Equal(222, formalRows);
        Assert.Equal(222, formalCaseIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(formalRows, truthJson.RootElement.GetProperty("formalManifestBinding").GetProperty("caseRows").GetInt32());

        var analysisDay = DateTime.Today.AddDays(-1);
        var asOfUtc = LocalTimeToUtc(analysisDay.AddDays(1));
        var definitions = truthJson.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(item => item.Clone()).ToArray();
        Assert.Equal(definitions.Length, definitions.Select(item => item.GetProperty("id").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(22, definitions.Length);

        var results = new List<ScenarioResult>(definitions.Length);
        foreach (var definition in definitions)
        {
            var result = await ReplayScenarioAsync(definition, analysisDay, asOfUtc);
            results.Add(result);
        }

        var summaries = new Dictionary<string, BaselineSummary>(StringComparer.Ordinal)
        {
            ["netIqOnly"] = Summarize(results, item => item.NetIqOnly),
            ["nativePrtgOnly"] = Summarize(results, item => item.NativePrtgOnly),
            ["simpleUnion"] = Summarize(results, item => item.SimpleUnion),
            ["actualCombinedDailyPipeline"] = Summarize(results, item => item.ActualCombinedDailyPipeline)
        };
        var combinedStrictlyBeatsUnion = StrictlyBeats(summaries["actualCombinedDailyPipeline"], summaries["simpleUnion"]);
        var report = new ReplayReport(
            Schema: "LogForesight.ControlledUsefulnessReplayReport",
            SchemaVersion: 1,
            ReplayLabel: "synthetic-controlled-technical-replay",
            TruthFixture: Path.GetFileName(truthPath),
            TruthFixtureSha256: truthSha256,
            FormalManifest: new FormalManifestBinding(FormalManifestPath, 1, manifestSha256, formalRows,
                formalCaseIds.Distinct(StringComparer.Ordinal).Count(), formalFamilyCounts),
            TimestampSemantics: new TimestampSemantics(
                AnalysisDayLocal: analysisDay.ToString("yyyy-MM-dd"),
                LocalTimeZoneId: TimeZoneInfo.Local.Id,
                AsOfUtc: asOfUtc,
                ActionableByIsPerScenarioSyntheticDeadline: true,
                SourceRetrievalAtMeasured: false,
                DecisionVisibleAtIsMeasuredWallClock: false,
                ReceivedAtMeasured: false,
                DispositionAtMeasured: false,
                AlertVsAdvanceWarningVsActionableLeadAreSeparate: true),
            Denominator: new ReplayDenominator(
                TotalRows: results.Count,
                TruthActionable: results.Count(item => item.Truth.Status == "actionable"),
                TruthBenign: results.Count(item => item.Truth.Status == "benign"),
                TruthUnknown: results.Count(item => item.Truth.Status == "unknown"),
                TruthExcluded: results.Count(item => item.Truth.Status == "excluded"),
                ExecutionFailures: results.Count(item => item.Pipeline.ExecutionStatus == "failed")),
            Baselines: summaries,
            ControlledCombinedStrictlyBeatsSimpleUnion: combinedStrictlyBeatsUnion,
            OperationalOrRealIncidentClaimPermitted: false,
            PublicationDecision: combinedStrictlyBeatsUnion
                ? "controlled scenarios beat the simple union under the stated fixed comparison; this synthetic result still does not establish real-incident usefulness or permit an operational 1+1>2 claim"
                : "no 1+1>2 claim: the controlled combined result does not strictly beat the simple union under the stated fixed comparison",
            Scenarios: results);

        var reportJson = JsonSerializer.Serialize(report, ReportJsonOptions);
        var reportNode = JsonNode.Parse(reportJson) ?? throw new InvalidDataException("Could not parse the controlled replay report.");
        var compactReport = CompactReport(reportNode);
        var expandedReport = ExpandCompactReport(compactReport);
        Assert.Equal(reportNode.ToJsonString(ReportJsonOptions), expandedReport.ToJsonString(ReportJsonOptions));
        var reportBytes = Encoding.UTF8.GetBytes(compactReport.ToJsonString(ReportJsonOptions));
        var outputPath = Environment.GetEnvironmentVariable("LF_PRTG_USEFULNESS_REPORT");
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = Path.Combine(Path.GetTempPath(), "lf-prtg-usefulness-controlled-v1.json");
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(outputPath, reportBytes);
        _output.WriteLine($"Controlled replay report: {Path.GetFullPath(outputPath)} ({reportBytes.Length} UTF-8 bytes; {results.Count} controlled scenarios; formal manifest {formalRows} rows).");

        Assert.True(reportBytes.Length <= 64 * 1024,
            $"Controlled usefulness report exceeded 64 KiB: {reportBytes.Length} bytes; report written to {outputPath}.");
        Assert.All(results.Where(item => item.ExpectationMatched is not null), item =>
            Assert.True(item.ExpectationMatched == true, $"{item.Id}: actual Daily pipeline did not match the fixed scenario expectation; report: {outputPath}"));
        Assert.DoesNotContain(results, item => item.Pipeline.ExecutionStatus == "failed");
    }

    private static async Task<ScenarioResult> ReplayScenarioAsync(JsonElement definition, DateTime analysisDay,
        DateTimeOffset asOfUtc)
    {
        var id = definition.GetProperty("id").GetString()!;
        var family = definition.GetProperty("family").GetString()!;
        var formalCaseId = definition.GetProperty("caseId").ValueKind == JsonValueKind.Null
            ? null : definition.GetProperty("caseId").GetString();
        var caseKind = definition.GetProperty("caseKind").GetString()!;
        var expectedFormalFinding = definition.GetProperty("expectedFormalFinding").ValueKind == JsonValueKind.Null
            ? (bool?)null : definition.GetProperty("expectedFormalFinding").GetBoolean();
        var truth = BuildTruth(definition.GetProperty("truth"), analysisDay);
        var netIqInput = BuildObservation(definition.GetProperty("netIq"), analysisDay, asOfUtc);
        var nativePrtgInput = BuildObservation(definition.GetProperty("nativePrtg"), analysisDay, asOfUtc);
        var netIqOnly = Score(netIqInput, truth, asOfUtc, "netiq-controlled-baseline");
        var nativePrtgOnly = Score(nativePrtgInput, truth, asOfUtc, "pre-recorded synthetic native PRTG observation; not vendor compatibility evidence");
        var simpleUnionInput = Union(netIqInput, nativePrtgInput, asOfUtc);
        var simpleUnion = Score(simpleUnionInput, truth, asOfUtc, "earliest available controlled baseline observation; no correlation logic");

        if (truth.Status == "excluded")
        {
            var excluded = Score(new Observation("excluded", null, "excluded-before-replay", "no trustworthy time"),
                truth, asOfUtc, "controlled row retained in denominator");
            return new ScenarioResult(id, family, formalCaseId, expectedFormalFinding, caseKind,
                "synthetic-device:53001/sensor:53002;case:" + id,
                EvidencePath.NotRunExcluded,
                truth, netIqInput, nativePrtgInput, netIqOnly, nativePrtgOnly, simpleUnion,
                excluded with { DecisionAtUtc = null, VisibleAtUtc = null, RetrievedAtUtc = null,
                    ReceivedAtUtc = null, DispositionAtUtc = null },
                new PipelineResult("excluded", null, null, null, null, "no trustworthy time; retained without pipeline execution"),
                null, "excluded rather than assigned an alert time or failure classification");
        }

        try
        {
            using var fixture = new PrtgFormalRuleCaseFixture();
            fixture.UseAnalysisDay(analysisDay);
            var (host, day) = await SeedScenarioAsync(fixture, id, family, caseKind, formalCaseId, definition);
            SetSyntheticNetIqInput(fixture, host, day, netIqInput);

            var daily = await fixture.RunDailyAsync(host, day);
            var source = PrtgRuleConsumerAcceptanceTests.SourceForFamily(family);
            var registryHit = daily.Registry.For(host.HostId, day).Any(item => item.Source == source);
            var persisted = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
                .ReadRecent(day, 1).SingleOrDefault();
            var persistedPrtgFinding = persisted?.TopIssues.Any(item => item.Source == source) == true;
            var persistedNetIqFinding = persisted?.TopIssues.Any(item =>
                item.Source == "Synthetic NetIQ disk baseline" && item.EventId == 53053) == true;
            var persistedFormalIssue = persisted?.TopIssues.SingleOrDefault(item => item.Source == source);
            var registryFormalIssue = daily.Registry.For(host.HostId, day).FirstOrDefault(item => item.Source == source);
            var evidencePath = CaptureEvidencePath(fixture, host, day, persistedFormalIssue, registryFormalIssue);
            var expectationMatched = expectedFormalFinding is null ||
                (registryHit == expectedFormalFinding.Value && persistedPrtgFinding == expectedFormalFinding.Value);
            var mappingAndIdentityMatched = evidencePath.MappingRowTargetsHost && evidencePath.IdentityActive &&
                !evidencePath.IdentityPendingReconciliation && evidencePath.IdentityDeviceAndHostMatch &&
                !string.IsNullOrWhiteSpace(evidencePath.ResourceFingerprintSha256);
            var currentFindingFingerprintMatched = !persistedPrtgFinding ||
                (evidencePath.SourceGenerationMatchesIdentity && evidencePath.ResourceGenerationMatchesCurrentConsumer);
            var requiresTypedResourceProof =
                (family is "CPUFormal" or "MemoryFormal" or "DiskTwoHourLowWater" or "DiskTrend") &&
                id != "no-native-resource-evidence";
            var typedProofMatched = !requiresTypedResourceProof || evidencePath.PersistedRowsWithTypedProof > 0;
            expectationMatched &= mappingAndIdentityMatched && currentFindingFingerprintMatched && typedProofMatched;
            var combinedStatus = persistedPrtgFinding || persistedNetIqFinding ? "observed" : "no-finding";
            // The combined result is only visible after this Daily decision has completed at the replay cutoff.
            // Keep source observation time separately; an earlier baseline does not make the combined output visible earlier.
            DateTimeOffset? visibleAt = combinedStatus == "observed" ? asOfUtc : null;
            var combinedScore = Score(new Observation(combinedStatus, visibleAt,
                persistedPrtgFinding ? "actual synthetic PrtgDailyPipeline registry plus persisted parent record" :
                    persistedNetIqFinding ? "actual Daily pipeline persisted the controlled NetIQ parent only" :
                    "actual Daily pipeline produced no combined finding for this host day",
                null), truth, asOfUtc, "actual SQLite synthetic PrtgDailyPipeline result") with
            {
                SourceObservedAtUtc = persistedNetIqFinding && netIqInput.Status == "observed"
                    ? netIqInput.FindingAtUtc : null,
                DecisionAtUtc = asOfUtc,
                VisibleAtUtc = visibleAt,
                RetrievedAtUtc = null,
                ReceivedAtUtc = null,
                DispositionAtUtc = null
            };
            return new ScenarioResult(id, family, formalCaseId, expectedFormalFinding, caseKind,
                "synthetic-device:53001/sensor:53002;case:" + id,
                evidencePath,
                truth, netIqInput, nativePrtgInput, netIqOnly, nativePrtgOnly, simpleUnion, combinedScore,
                new PipelineResult("completed", registryHit, persistedPrtgFinding, persistedNetIqFinding,
                    persisted is not null, "fixed host-day; output visibility is represented by the as-of cutoff, not wall-clock runtime"),
                expectationMatched,
                expectationMatched
                    ? "actual Daily registry and persisted formal finding match the fixed result; current host mapping, identity generations, resource fingerprint, and applicable typed proof were captured"
                    : "fixed finding expectation or current mapping, identity generation, or applicable typed proof did not match");
        }
        catch (Exception exception)
        {
            var failed = Score(new Observation("failed", null, "actual Daily pipeline execution failed",
                exception.GetType().Name), truth, asOfUtc, "actual synthetic Daily pipeline execution failure");
            return new ScenarioResult(id, family, formalCaseId, expectedFormalFinding, caseKind,
                "synthetic-device:53001/sensor:53002;case:" + id,
                EvidencePath.NotCapturedFailure,
                truth, netIqInput, nativePrtgInput, netIqOnly, nativePrtgOnly, simpleUnion, failed,
                new PipelineResult("failed", null, null, null, null, exception.GetType().Name), false,
                "actual Daily pipeline failed; only the exception type is retained");
        }
    }

    private static async Task<(WebHost Host, DateTime Day)> SeedScenarioAsync(PrtgFormalRuleCaseFixture fixture,
        string id, string family, string caseKind, string? formalCaseId, JsonElement definition)
    {
        switch (id)
        {
            case "silent-boundary":
            {
                var host = fixture.SeedSilentCase(resourceStatus: "Up", availabilityStatus: "Up");
                fixture.StoreSyntheticHistoricalSilentProof(host, resourceStatus: "Up", availabilityStatus: "Up");
                return (host, fixture.AnalysisDay);
            }
            case "cpu-legitimate-workload":
                return (fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95, 95]), fixture.AnalysisDay);
            case "missing-parent":
            {
                var host = fixture.SeedCoveredStateCase("N7-missing-parent", "Ping", "Ping", "Up", day =>
                [
                    (day.AddHours(1), "Down"),
                    (day.AddHours(1).AddMinutes(35), "Up")
                ], PrtgSensorCategories.Availability, includeParent: false);
                return (host, fixture.AnalysisDay);
            }
            case "no-native-resource-evidence":
                return (fixture.SeedCoveredStateCase("N7-no-resource-sample", "CPU Usage", "SNMP CPU Load", "Up",
                    _ => Array.Empty<(DateTime At, string Status)>(), PrtgSensorCategories.Cpu), fixture.AnalysisDay);
            case "bad-coverage-known-condition":
                return (fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95, 95], goodPhysicalSlotsPerHour: 2),
                    fixture.AnalysisDay);
            case "cross-analysis-utc-day":
                return (fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95, 91], crossesAnalysisMidnight: true),
                    fixture.AnalysisDay);
        }

        if (caseKind == "controlled-excluded-source-time-unknown")
            throw new InvalidOperationException("Excluded source-time rows must not invoke the product pipeline.");
        if (string.IsNullOrWhiteSpace(formalCaseId))
            throw new InvalidDataException($"Scenario {id} has no formal case id or controlled seeder.");

        var manifestCase = PrtgRuleConsumerAcceptanceTests.ReadCase(family, formalCaseId);
        var mutations = manifestCase.GetProperty("mutations");
        return await PrtgRuleConsumerAcceptanceTests.SeedFixedCaseAsync(fixture, family, mutations);
    }

    private static void SetSyntheticNetIqInput(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
        Observation netIqInput)
    {
        var store = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        var record = store.ReadRecent(day, 1).SingleOrDefault();
        if (record is null) return;
        var baselineIssue = record.TopIssues.SingleOrDefault(item =>
            item.Source == "Synthetic NetIQ disk baseline" && item.EventId == 53053);
        if (netIqInput.Status == "observed")
        {
            if (baselineIssue is null)
                throw new InvalidDataException("The controlled NetIQ baseline parent issue was not seeded.");
            baselineIssue.SampleMessages =
            [
                $"Synthetic controlled NetIQ observation. reference={netIqInput.Reference}; observedAtUtc={netIqInput.FindingAtUtc:O}."
            ];
        }
        else if (baselineIssue is not null)
        {
            record.TopIssues.Remove(baselineIssue);
            record.RiskLevel = LogAnalysisService.ComputeRuleBasedRisk(record.TopIssues, record.TrendAlerts, []);
            record.RiskBasis = "Synthetic usefulness replay parent without a NetIQ finding.";
        }
        record.AiAnalyzed = false;
        record.AiPending = false;
        if (store.DeleteDays([day]) != 1) throw new InvalidOperationException("Could not replace the controlled parent record.");
        store.Append(record);
    }

    private static EvidencePath CaptureEvidencePath(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
        LogIssueSignature? persistedFinding, LogIssueSignature? currentRegistryFinding)
    {
        var prtg = fixture.Backend.PrtgStore();
        var identity = prtg.GetResourceIdentity(PrtgFormalRuleCaseFixture.SensorId);
        var mappedToHost = prtg.GetHostMapForDate(day).Any(item =>
            item.DeviceObjid == PrtgFormalRuleCaseFixture.DeviceId && item.HostId == host.HostId &&
            item.MapStatus == PrtgMapStatus.Ok);
        using var context = fixture.Backend.CreateContext();
        var values = context.PrtgValues.Where(item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId)
            .Select(item => new { item.TrustVersion, item.TrustedProof }).ToArray();
        var sourceObservations = persistedFinding?.SourceObservations ?? [];
        var isDeviceAggregate = persistedFinding?.Source == "PRTG:silent";
        var sourceGenerationMatches = persistedFinding is not null &&
            persistedFinding.PrtgSourceGeneration == identity.SourceGeneration &&
            (!isDeviceAggregate || persistedFinding.PrtgSourceGeneration == currentRegistryFinding?.PrtgSourceGeneration);
        // Silent is a device-level finding whose resource generation is the formal consumer's
        // aggregate digest across all qualified device sensors, not any single sensor generation.
        var resourceGenerationMatches = persistedFinding is not null &&
            (persistedFinding.Source == "PRTG:silent"
                ? !string.IsNullOrWhiteSpace(currentRegistryFinding?.PrtgResourceGeneration) &&
                  persistedFinding.PrtgResourceGeneration == currentRegistryFinding.PrtgResourceGeneration
                : persistedFinding.PrtgResourceGeneration == identity.Generation);
        var resourceFingerprintHash = string.IsNullOrWhiteSpace(identity.ResourceFingerprint) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ResourceFingerprint)));
        return new EvidencePath(
            MappingRowTargetsHost: mappedToHost,
            IdentityActive: identity.Active,
            IdentityPendingReconciliation: identity.PendingReconciliation,
            IdentityDeviceAndHostMatch: identity.DeviceId == PrtgFormalRuleCaseFixture.DeviceId && identity.HostId == host.HostId,
            ResourceFingerprintSha256: resourceFingerprintHash,
            PersistedResourceRows: values.Length,
            PersistedRowsWithTypedProof: values.Count(item => item.TrustVersion > 0 &&
                !string.IsNullOrWhiteSpace(item.TrustedProof) && PrtgTrustedSampleProof.Deserialize(item.TrustedProof!) is not null),
            PersistedFormalFinding: persistedFinding is not null,
            SourceGenerationMatchesIdentity: sourceGenerationMatches,
            ResourceGenerationMatchesCurrentConsumer: resourceGenerationMatches,
            RuleAdmissionFingerprintPresent: !string.IsNullOrWhiteSpace(persistedFinding?.PrtgRuleAdmissionFingerprint),
            EventKeySha256: persistedFinding is null ? null : Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(persistedFinding.EventKey))),
            SourceObservationCount: sourceObservations.Count,
            SourceObservationExactResourceKeyCount: sourceObservations.Count(item => !string.IsNullOrWhiteSpace(item.ExactResourceKey)),
            SourceObservationExactHostKeyCount: sourceObservations.Count(item => !string.IsNullOrWhiteSpace(item.ExactHostKey)),
            SourceObservationValidUtcWindowCount: sourceObservations.Count(item => item.HasValidWindow),
            SourceObservationProjectionFingerprintCount: sourceObservations.Count(item => !string.IsNullOrWhiteSpace(item.ProjectionFingerprint)),
            SourceObservationsTruncated: persistedFinding?.SourceObservationsTruncated ?? false,
            SourceObservationResourceScope: sourceObservations.Select(item => item.ResourceScope.ToString()).Distinct().ToArray(),
            SourceObservationReferenceQuality: sourceObservations.Select(item => item.SourceReferenceQuality.ToString()).Distinct().ToArray());
    }

    private static JsonObject CompactReport(JsonNode report)
    {
        var propertyNames = new SortedSet<string>(StringComparer.Ordinal);
        var stringCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        Visit(report);
        var propertyIds = propertyNames.Select((name, index) => (name, id: "p" + index))
            .ToDictionary(item => item.name, item => item.id, StringComparer.Ordinal);
        var stringIds = stringCounts.Where(item => item.Value > 1 && item.Key.Length >= 8)
            .Select((item, index) => (item.Key, id: "s" + index))
            .ToDictionary(item => item.Key, item => item.id, StringComparer.Ordinal);

        var propertyMap = new JsonArray(propertyIds.OrderBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Value, ["name"] = item.Key }).ToArray());
        var stringMap = new JsonArray(stringIds.OrderBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Value, ["value"] = item.Key }).ToArray());
        return new JsonObject
        {
            ["encoding"] = "reversible-property-and-repeated-string-dictionary-v1",
            ["propertyNames"] = propertyMap,
            ["repeatedStrings"] = stringMap,
            ["payload"] = Encode(report)
        };

        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    propertyNames.Add(key);
                    Visit(value);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var value in array) Visit(value);
            }
            else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                stringCounts[text] = stringCounts.GetValueOrDefault(text) + 1;
            }
        }

        JsonNode? Encode(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var encoded = new JsonObject();
                foreach (var (key, value) in obj) encoded[propertyIds[key]] = Encode(value);
                return encoded;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Encode).ToArray());
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && stringIds.TryGetValue(text, out var id))
                return new JsonObject { ["$stringRef"] = id };
            return node?.DeepClone();
        }
    }

    private static JsonNode ExpandCompactReport(JsonObject compact)
    {
        if (compact["encoding"]?.GetValue<string>() != "reversible-property-and-repeated-string-dictionary-v1")
            throw new InvalidDataException("Unsupported controlled report encoding.");
        var propertyNames = compact["propertyNames"]!.AsArray().ToDictionary(
            item => item!["id"]!.GetValue<string>(), item => item["name"]!.GetValue<string>(), StringComparer.Ordinal);
        var repeatedStrings = compact["repeatedStrings"]!.AsArray().ToDictionary(
            item => item!["id"]!.GetValue<string>(), item => item["value"]!.GetValue<string>(), StringComparer.Ordinal);
        return Decode(compact["payload"]) ?? throw new InvalidDataException("The controlled report payload was null.");

        JsonNode? Decode(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj.Count == 1 && obj["$stringRef"] is JsonValue stringRef &&
                    stringRef.TryGetValue<string>(out var id) && repeatedStrings.TryGetValue(id, out var value))
                    return JsonValue.Create(value)!;
                var decoded = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    if (!propertyNames.TryGetValue(key, out var name))
                        throw new InvalidDataException("Controlled report contains an unknown property token.");
                    decoded[name] = Decode(child);
                }
                return decoded;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Decode).ToArray());
            return node?.DeepClone();
        }
    }

    private static TruthResult BuildTruth(JsonElement element, DateTime analysisDay)
    {
        var impact = ReadOffset(element, "impactOffsetMinutes", analysisDay);
        var actionableBy = ReadOffset(element, "actionableByOffsetMinutes", analysisDay);
        var conditionStart = ReadOffset(element, "conditionStartOffsetMinutes", analysisDay);
        return new TruthResult(element.GetProperty("status").GetString()!,
            element.GetProperty("basis").GetString()!, conditionStart, impact, actionableBy);
    }

    private static Observation BuildObservation(JsonElement element, DateTime analysisDay, DateTimeOffset asOfUtc)
    {
        var status = element.GetProperty("status").GetString()!;
        var at = ReadOffset(element, "findingOffsetMinutes", analysisDay);
        if (status == "observed" && at is null)
            throw new InvalidDataException("Observed baseline entries require a controlled observation time.");
        if (at is { } observedAt && observedAt > asOfUtc)
            throw new InvalidDataException("A controlled baseline observation occurs after the replay as-of cutoff.");
        return new Observation(status, at, element.GetProperty("reference").GetString(),
            element.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
    }

    private static DateTimeOffset? ReadOffset(JsonElement element, string property, DateTime analysisDay)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var minutes = value.GetInt32();
        return LocalTimeToUtc(analysisDay.AddMinutes(minutes));
    }

    private static DateTimeOffset LocalTimeToUtc(DateTime localTime) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), TimeZoneInfo.Local));

    private static Observation Union(Observation netIq, Observation nativePrtg, DateTimeOffset asOfUtc)
    {
        var observed = new[] { netIq, nativePrtg }.Where(item => item.Status == "observed")
            .OrderBy(item => item.FindingAtUtc).FirstOrDefault();
        if (observed is not null) return observed with { Reference = "simple-union:" + observed.Reference };
        var uncertain = new[] { netIq, nativePrtg }.FirstOrDefault(item => item.Status == "failed");
        uncertain ??= new[] { netIq, nativePrtg }.FirstOrDefault(item => item.Status == "unknown");
        uncertain ??= new[] { netIq, nativePrtg }.FirstOrDefault(item => item.Status == "excluded");
        if (uncertain is not null) return uncertain with { Reference = "simple-union-incomplete:" + uncertain.Reference };
        return new Observation("no-finding", null, "both controlled baseline windows checked through " + asOfUtc.ToString("O"), null);
    }

    private static TimelineScore Score(Observation observation, TruthResult truth, DateTimeOffset asOfUtc,
        string provenance)
    {
        var visibleAt = observation.Status == "observed" ? observation.FindingAtUtc : null;
        var truthKnown = truth.Status is "actionable" or "benign";
        bool? advanceWarning = null;
        double? leadToImpactMinutes = null;
        bool? actionableLead = null;
        double? leadToActionableByMinutes = null;
        if (truth.Status == "actionable" && truth.ImpactAtUtc is { } impact)
        {
            if (visibleAt is { } alertAt)
            {
                leadToImpactMinutes = (impact - alertAt).TotalMinutes;
                advanceWarning = alertAt < impact;
                if (truth.ActionableByUtc is { } actionBy)
                {
                    leadToActionableByMinutes = (actionBy - alertAt).TotalMinutes;
                    actionableLead = alertAt <= actionBy;
                }
            }
            else if (observation.Status == "no-finding")
            {
                advanceWarning = false;
                actionableLead = truth.ActionableByUtc is not null ? false : null;
            }
        }
        return new TimelineScore(observation.Status, observation.FindingAtUtc,
            observation.Status == "observed" ? observation.FindingAtUtc :
                observation.Status == "no-finding" ? asOfUtc : null,
            visibleAt, RetrievedAtUtc: null, ReceivedAtUtc: null, DispositionAtUtc: null,
            truth.ConditionStartUtc, truth.ImpactAtUtc, truth.ActionableByUtc, advanceWarning, leadToImpactMinutes,
            actionableLead, leadToActionableByMinutes, provenance, observation.Reference, observation.Reason,
            truthKnown);
    }

    private static BaselineSummary Summarize(IReadOnlyList<ScenarioResult> results,
        Func<ScenarioResult, TimelineScore> select)
    {
        var known = results.Where(item => item.Truth.Status is "actionable" or "benign").ToArray();
        var baselineRows = known.Select(item => (Truth: item.Truth.Status, Score: select(item))).ToArray();
        var scored = baselineRows.Where(item => item.Score.Status is "observed" or "no-finding").ToArray();
        var truePositive = scored.Count(item => item.Truth == "actionable" && item.Score.Status == "observed");
        var falseNegative = scored.Count(item => item.Truth == "actionable" && item.Score.Status == "no-finding");
        var falsePositive = scored.Count(item => item.Truth == "benign" && item.Score.Status == "observed");
        var trueNegative = scored.Count(item => item.Truth == "benign" && item.Score.Status == "no-finding");
        var positivePredictions = truePositive + falsePositive;
        var actualPositives = truePositive + falseNegative;
        var correct = truePositive + trueNegative;
        return new BaselineSummary(
            ScenarioDenominator: results.Count,
            TruthKnownActionable: results.Count(item => item.Truth.Status == "actionable"),
            TruthKnownBenign: results.Count(item => item.Truth.Status == "benign"),
            TruthUnknown: results.Count(item => item.Truth.Status == "unknown"),
            TruthExcluded: results.Count(item => item.Truth.Status == "excluded"),
            BaselineUnknownWithinKnownTruth: baselineRows.Count(item => item.Score.Status == "unknown"),
            BaselineExcludedWithinKnownTruth: baselineRows.Count(item => item.Score.Status == "excluded"),
            BaselineFailuresWithinKnownTruth: baselineRows.Count(item => item.Score.Status == "failed"),
            ScoredKnownTruthRows: scored.Length,
            TruePositive: truePositive,
            FalsePositive: falsePositive,
            TrueNegative: trueNegative,
            FalseNegative: falseNegative,
            Precision: positivePredictions == 0 ? null : (double)truePositive / positivePredictions,
            Recall: actualPositives == 0 ? null : (double)truePositive / actualPositives,
            CorrectKnownTruthRows: correct);
    }

    private static bool StrictlyBeats(BaselineSummary combined, BaselineSummary union) =>
        combined.ScenarioDenominator == union.ScenarioDenominator &&
        combined.TruthKnownActionable + combined.TruthKnownBenign == union.TruthKnownActionable + union.TruthKnownBenign &&
        combined.ScoredKnownTruthRows == combined.TruthKnownActionable + combined.TruthKnownBenign &&
        union.ScoredKnownTruthRows == union.TruthKnownActionable + union.TruthKnownBenign &&
        combined.ScoredKnownTruthRows > 0 && union.ScoredKnownTruthRows > 0 &&
        combined.CorrectKnownTruthRows > union.CorrectKnownTruthRows &&
        combined.FalseNegative <= union.FalseNegative && combined.FalsePositive <= union.FalsePositive;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, TruthFixturePath.Replace('/', Path.DirectorySeparatorChar))) &&
                File.Exists(Path.Combine(directory.FullName, FormalManifestPath.Replace('/', Path.DirectorySeparatorChar))))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the controlled usefulness and formal case fixtures.");
    }

    private sealed record Observation(string Status, DateTimeOffset? FindingAtUtc, string? Reference, string? Reason);
    private sealed record TruthResult(string Status, string Basis, DateTimeOffset? ConditionStartUtc,
        DateTimeOffset? ImpactAtUtc, DateTimeOffset? ActionableByUtc);
    private sealed record TimelineScore(string Status, DateTimeOffset? SourceObservedAtUtc, DateTimeOffset? DecisionAtUtc,
        DateTimeOffset? VisibleAtUtc, DateTimeOffset? RetrievedAtUtc, DateTimeOffset? ReceivedAtUtc,
        DateTimeOffset? DispositionAtUtc,
        DateTimeOffset? ConditionStartUtc, DateTimeOffset? ImpactAtUtc, DateTimeOffset? ActionableByUtc, bool? AdvanceWarning,
        double? LeadToImpactMinutes, bool? ActionableLead, double? LeadToActionableByMinutes,
        string Provenance, string? EvidenceReference, string? UncertaintyReason, bool TruthKnown);
    private sealed record PipelineResult(string ExecutionStatus, bool? RegistryFormalFinding,
        bool? PersistedPrtgFinding, bool? PersistedNetIqFinding, bool? ParentRecordPresent, string? FailureOrNote);
    private sealed record EvidencePath(bool MappingRowTargetsHost, bool IdentityActive, bool IdentityPendingReconciliation,
        bool IdentityDeviceAndHostMatch,
        string? ResourceFingerprintSha256, int PersistedResourceRows, int PersistedRowsWithTypedProof,
        bool PersistedFormalFinding, bool SourceGenerationMatchesIdentity, bool ResourceGenerationMatchesCurrentConsumer,
        bool RuleAdmissionFingerprintPresent, string? EventKeySha256, int SourceObservationCount,
        int SourceObservationExactResourceKeyCount, int SourceObservationExactHostKeyCount,
        int SourceObservationValidUtcWindowCount, int SourceObservationProjectionFingerprintCount,
        bool SourceObservationsTruncated, IReadOnlyList<string> SourceObservationResourceScope,
        IReadOnlyList<string> SourceObservationReferenceQuality)
    {
        public static EvidencePath NotRunExcluded { get; } = new(false, false, false, false, null, 0, 0,
            false, false, false, false, null, 0, 0, 0, 0, 0, false, [], []);
        public static EvidencePath NotCapturedFailure { get; } = new(false, false, false, false, null, 0, 0,
            false, false, false, false, null, 0, 0, 0, 0, 0, false, [], []);
    }
    private sealed record ScenarioResult(string Id, string Family, string? FormalCaseId, bool? ExpectedFormalFinding, string CaseKind,
        string ResourceIdentity, EvidencePath EvidencePath, TruthResult Truth, Observation NetIqInput, Observation NativePrtgInput,
        TimelineScore NetIqOnly, TimelineScore NativePrtgOnly, TimelineScore SimpleUnion,
        TimelineScore ActualCombinedDailyPipeline, PipelineResult Pipeline, bool? ExpectationMatched, string ExpectationReason);
    private sealed record FormalFamilyCount(string Family, int Positive, int NegativeAndBoundary);
    private sealed record FormalManifestBinding(string Path, int SchemaVersion, string Sha256, int CaseRows,
        int UniqueCaseRows,
        IReadOnlyList<FormalFamilyCount> FamilyCounts);
    private sealed record TimestampSemantics(string AnalysisDayLocal, string LocalTimeZoneId, DateTimeOffset AsOfUtc,
        bool ActionableByIsPerScenarioSyntheticDeadline, bool SourceRetrievalAtMeasured,
        bool DecisionVisibleAtIsMeasuredWallClock, bool ReceivedAtMeasured, bool DispositionAtMeasured,
        bool AlertVsAdvanceWarningVsActionableLeadAreSeparate);
    private sealed record ReplayDenominator(int TotalRows, int TruthActionable, int TruthBenign,
        int TruthUnknown, int TruthExcluded, int ExecutionFailures);
    private sealed record BaselineSummary(int ScenarioDenominator, int TruthKnownActionable, int TruthKnownBenign,
        int TruthUnknown, int TruthExcluded, int BaselineUnknownWithinKnownTruth,
        int BaselineExcludedWithinKnownTruth, int BaselineFailuresWithinKnownTruth, int ScoredKnownTruthRows,
        int TruePositive, int FalsePositive, int TrueNegative, int FalseNegative,
        double? Precision, double? Recall, int CorrectKnownTruthRows);
    private sealed record ReplayReport(string Schema, int SchemaVersion, string ReplayLabel, string TruthFixture,
        string TruthFixtureSha256,
        FormalManifestBinding FormalManifest, TimestampSemantics TimestampSemantics, ReplayDenominator Denominator,
        IReadOnlyDictionary<string, BaselineSummary> Baselines, bool ControlledCombinedStrictlyBeatsSimpleUnion,
        bool OperationalOrRealIncidentClaimPermitted, string PublicationDecision, IReadOnlyList<ScenarioResult> Scenarios);
}

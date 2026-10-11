using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Consumer coverage exercises fixed manifest witnesses through Daily and downstream AI/mail
/// consumers. The complete fixed manifest remains separately dispatched by its producer suite.
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgRuleConsumerAcceptanceTests
{
    [Fact]
    public void ConsumerManifestReferenceUsesTheFrozen222CaseIdUniverse()
    {
        var cases = ReadFixedCaseUniverse();
        Assert.Equal(222, cases.Count);
        Assert.Equal(8, cases.Select(item => item.Family).Distinct(StringComparer.Ordinal).Count());
        foreach (var family in cases.GroupBy(item => item.Family, StringComparer.Ordinal))
        {
            Assert.Equal(5, family.Count(item => item.IsPositive));
            Assert.InRange(family.Count(item => !item.IsPositive), 20, 25);
        }
        var canonical = string.Join("\n", cases.Select(item => item.CaseId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("54854EE42362487A4D87AF02B994E5B01E60517770FEF0FB9CFA01EB04482381",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    public static IEnumerable<object[]> RepresentativeFixedCases()
    {
        foreach (var testCase in ReadFixedCaseUniverse())
            yield return [testCase.Family, testCase.CaseId,
                testCase.IsPositive || testCase.CaseId == "F53-DiskTrend-N08-duplicate-day" ||
                // These three families use the original-positive mutation solely to reassign
                // the sample window across host midnight; Warning and Silent keep their negatives.
                (testCase.Family is "DiskTwoHourLowWater" or "CPUFormal" or "MemoryFormal") &&
                testCase.HasOriginalPositiveMutation];
    }

    [Theory]
    [MemberData(nameof(RepresentativeFixedCases))]
    public Task FixedManifestCaseCrossesDailyAiAndMailConsumers(string family, string caseId, bool expectedHit) =>
        RunFixedCaseAsync(family, caseId, expectedHit);

    public static IEnumerable<object[]> ResourceAndSilentPositiveCases()
    {
        yield return ["DiskTwoHourLowWater", "F53-DiskTwoHourLowWater-P01-disk-low-water-exact-ten-percent"];
        yield return ["DiskTrend", "F53-DiskTrend-P01-disk-trend-seven-day-low-water-at-boundary"];
        yield return ["CPUFormal", "F53-CPUFormal-P01-cpu-formal-exact-90-percent-two-hours"];
        yield return ["MemoryFormal", "F53-MemoryFormal-P01-memory-formal-exact-90-percent-used-two-hours"];
        yield return ["Silent", "F53-Silent-P01-silent-complete-up-availability-unknown-resource"];
    }

    [Theory]
    [MemberData(nameof(ResourceAndSilentPositiveCases))]
    public Task ResourceAndSilentPositiveConsumerDiagnostic(string family, string caseId) =>
        RunFixedCaseAsync(family, caseId, true);

    [Fact]
    public Task CurrentDaySilentNativeCaptureRemainsDeferredByMailWindow() =>
        RunFixedCaseAsync("Silent", "F53-Silent-P01-silent-complete-up-availability-unknown-resource", true,
            nativeCurrentDaySilent: true);

    [Fact]
    public Task ClosedDaySilentWithQualifiedHighNetIqBaselineReachesActualAiAndMail() =>
        RunFixedCaseAsync("Silent", "F53-Silent-P01-silent-complete-up-availability-unknown-resource", true,
            highRiskSyntheticBaseline: true);

    private async Task RunFixedCaseAsync(string family, string caseId, bool expectedHit,
        bool nativeCurrentDaySilent = false, bool highRiskSyntheticBaseline = false)
    {
        var manifestCase = ReadCase(family, caseId);
        var mutations = manifestCase.GetProperty("mutations");
        using var fixture = new PrtgFormalRuleCaseFixture();
        var (host, day) = await SeedFixedCaseAsync(fixture, family, caseId, mutations, nativeCurrentDaySilent);
        var scenario = family == "Silent"
            ? PrtgFormalRuleCaseManifestTests.NormalizeConsumerSilentScenario(mutations)
            : mutations.TryGetProperty("scenario", out var scenarioValue) ? scenarioValue.GetString()
            : family == "DiskTrend" && mutations.TryGetProperty("parentStatus", out var parentStatusValue)
                ? parentStatusValue.GetString() : null;
        var missingParentScenario = scenario is "missing-parent" or "missing-netiq-parent" or "missing";
        var parentGateScenario = missingParentScenario || scenario is "partial-parent" or "failed-parent" or
            "running-parent" or "unknown-parent" or "partial" or "failed" or "running" or "unknown";
        if (!missingParentScenario)
            SetSyntheticParentNeverAnalyzed(fixture, host, day);
        if (highRiskSyntheticBaseline)
            PromoteSyntheticNetIqBaselineForConsumerTest(fixture, host, day);
        _ = fixture.SeedHumanOwnedBaseline(host);
        var missingParent = missingParentScenario;
        var humanSidecars = parentGateScenario
            ? PrtgFormalRuleCaseManifestTests.CaptureConsumerHumanSidecars(fixture, host, day)
            : fixture.CaptureHumanOwnedData(host);
        if (missingParent)
            PrtgFormalRuleCaseManifestTests.DeleteConsumerParent(fixture, host, day);
        else if (scenario is "partial-parent" or "partial")
            PrtgFormalRuleCaseManifestTests.SetConsumerParentAttemptStatus(fixture, host, day, "partial");
        else if (scenario is "failed-parent" or "running-parent" or "unknown-parent" or "failed" or "running" or "unknown")
            PrtgFormalRuleCaseManifestTests.SetConsumerParentAttemptStatus(fixture, host, day,
                scenario.EndsWith("-parent", StringComparison.Ordinal) ? scenario[..^7] : scenario);

        var resourceFamily = family is "DiskTwoHourLowWater" or "CPUFormal" or "MemoryFormal";
        var hostDayStraddle = resourceFamily &&
            (scenario == "host-day-straddle" ||
             manifestCase.TryGetProperty("originalPositiveMutation", out var originalPositiveMutation) &&
             originalPositiveMutation.ValueKind == JsonValueKind.Object);
        if (hostDayStraddle)
        {
            var priorDay = day.AddDays(-1);
            var priorDaily = await fixture.RunDailyAsync(host, priorDay);
            Assert.DoesNotContain(priorDaily.Registry.For(host.HostId, priorDay), item => item.Source == SourceForFamily(family));
        }
        var daily = await fixture.RunDailyAsync(host, day);
        var source = SourceForFamily(family);
        var findings = daily.Registry.For(host.HostId, day).Where(item => item.Source == source).ToArray();
        if (parentGateScenario)
        {
            // These contracts intentionally have no eligible Daily parent. Never synthesize a
            // replacement parent just to exercise AI or mail; verify the gate and human state.
            Assert.Empty(findings);
            Assert.Equal(humanSidecars, PrtgFormalRuleCaseManifestTests.CaptureConsumerHumanSidecars(fixture, host, day));
            var store = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
            if (missingParent) Assert.Empty(store.ReadRecent(day, 1));
            else
            {
                var blockedParent = Assert.Single(store.ReadRecent(day, 1));
                var expectedAttempt = scenario!.EndsWith("-parent", StringComparison.Ordinal)
                    ? scenario[..^7] : scenario;
                Assert.Equal(expectedAttempt, blockedParent.LatestNetiqAttemptStatus, ignoreCase: true);
                Assert.DoesNotContain(blockedParent.TopIssues, issue => issue.Source == source);
                Assert.False(blockedParent.CanSupplementWithPrtg());
            }
            Assert.Empty(fixture.Backend.WorkOrderStore().GetAllActive());
            return;
        }
        if (expectedHit)
            Assert.True(findings.Length == 1, MissingFindingDiagnostic(fixture, host, day, family, manifestCase, daily));
        var expectedSideEffects = manifestCase.TryGetProperty("expectedFormalSideEffects", out var sideEffects)
            ? sideEffects.EnumerateArray().Select(item => item.GetString()!)
                .OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var actualSideEffects = daily.Registry.For(host.HostId, day)
            .Where(item => item.Source.StartsWith("PRTG:", StringComparison.Ordinal) && item.Source != source)
            .Select(item => item.Source).Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!expectedHit) Assert.Equal(expectedSideEffects, actualSideEffects);
        LogIssueSignature? finding = null;
        if (expectedHit)
        {
            finding = findings.Single();
            var persisted = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1));
            Assert.Contains(persisted.TopIssues, issue => issue.EventKey == finding.EventKey &&
                issue.Source == finding.Source && issue.SampleMessages.SequenceEqual(finding.SampleMessages));
            if (caseId == "F53-DiskTrend-N08-duplicate-day")
            {
                Assert.Single(persisted.TopIssues, issue => issue.Source == "PRTG:disk_free_trend");
                Assert.Single(fixture.Backend.IssueCaseStore().GetOpenForHost(host.HostName));
            }
        }
        else
        {
            Assert.Empty(findings);
            var persisted = Assert.Single(fixture.Backend.RecordStore(new HostKey
                { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1));
            Assert.Contains(persisted.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline" && issue.EventId == 53053);
        }
        Assert.Equal(humanSidecars, fixture.CaptureHumanOwnedData(host));

        var consumerFindings = daily.Registry.For(host.HostId, day)
            .Where(item => expectedHit
                ? item.Source.StartsWith("PRTG:", StringComparison.Ordinal)
                : expectedSideEffects.Contains(item.Source, StringComparer.Ordinal))
            .ToArray();
        var parentRecord = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1));
        if (highRiskSyntheticBaseline)
        {
            Assert.Equal(RiskLevels.High, parentRecord.RiskLevel);
            Assert.Contains(parentRecord.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline" &&
                issue.EventId == 53053 && issue.Severity == IssueSeverity.High && issue.ElevatesDayRisk);
        }
        var aiEligible = parentRecord.AiAnalyzed || parentRecord.RiskLevel != RiskLevels.Low;
        var aiQueueDiagnostic = AiQueueDiagnostic(fixture, host, day, family, manifestCase, parentRecord, consumerFindings);
        if (aiEligible)
        {
            Assert.NotEmpty(consumerFindings);
            var expectedAiDays = hostDayStraddle
                ? new[] { day.AddDays(-1).Date, day.Date }
                : new[] { day.Date };
            var (record, prompt) = await ConsumePendingWithActualHostedService(
                fixture, host, day, aiQueueDiagnostic, expectedAiDays);
            Assert.False(record.AiPending);
            Assert.True(record.AiAnalyzed);
            foreach (var consumerFinding in consumerFindings)
            {
                Assert.Contains(consumerFinding.Source, prompt);
                Assert.NotEmpty(consumerFinding.SampleMessages);
                Assert.Contains(consumerFinding.SampleMessages[0], prompt);
            }
            if (!expectedHit) Assert.DoesNotContain(source, prompt);
            Assert.Equal(humanSidecars, fixture.CaptureHumanOwnedData(host));
        }
        else
        {
            Assert.Equal(RiskLevels.Low, parentRecord.RiskLevel);
            if (expectedHit)
            {
                Assert.NotEmpty(consumerFindings);
                Assert.All(consumerFindings, issue =>
                {
                    Assert.Equal(IssueSeverity.Medium, issue.Severity);
                    Assert.False(issue.ElevatesDayRisk);
                    var matchedRule = KnownIssueCatalog.Rules.Single(rule => rule.Id == issue.RuleId);
                    Assert.Equal(IssueSeverity.Medium, matchedRule.Severity);
                    Assert.False(matchedRule.ElevatesDayRisk);
                });
            }
            Assert.False(parentRecord.AiAnalyzed);
            Assert.False(parentRecord.AiPending);
            var sqlState = ReadSqlAiState(fixture, host, day);
            Assert.False(sqlState.AiAnalyzed);
            Assert.False(sqlState.AiPending);
            var markCount = fixture.Backend.RecordStore().MarkAllForAiRerun();
            Assert.True(markCount == 0, aiQueueDiagnostic + "; actual MarkAllForAiRerun count=" + markCount);
            Assert.NotEmpty(aiQueueDiagnostic);
        }

        var mail = await NotifyWithActualMailService(fixture, "ops@test.local", day);
        if (day.Date == DateTime.Today)
        {
            // The real run-summary window ends yesterday; a current-day PRTG record is not mail-eligible yet.
            // This same-day capture checks the real mail-window boundary; the closed-day synthetic proof case exercises full summary delivery.
            Assert.Empty(mail.Sender.Sent);
            Assert.DoesNotContain($"{host.HostId}|{day:yyyy-MM-dd}", mail.State.SummarySentKeys);
        }
        else
        {
            var message = Assert.Single(mail.Sender.Sent).Message;
            Assert.Equal(new[] { "ops@test.local" }, message.To);
            if (expectedHit)
                Assert.True(message.Body.Contains(finding!.Source, StringComparison.Ordinal),
                    FormalMailFenceDiagnostic(fixture, host, day, family, manifestCase));
            else
            {
                Assert.DoesNotContain(source, message.Body);
                foreach (var effectSource in expectedSideEffects)
                    Assert.Contains(effectSource, message.Body);
            }
            Assert.Contains($"{host.HostId}|{day:yyyy-MM-dd}", mail.State.SummarySentKeys);
        }
        Assert.Equal(humanSidecars, fixture.CaptureHumanOwnedData(host));
    }

    internal static async Task<(WebHost Host, DateTime Day)> SeedFixedCaseAsync(
        PrtgFormalRuleCaseFixture fixture, string family, string caseId, JsonElement mutations,
        bool nativeCurrentDaySilent = false)
    {
        var day = fixture.AnalysisDay;
        var manifestCase = ReadCase(family, caseId);
        var resourceFamily = family is "DiskTwoHourLowWater" or "CPUFormal" or "MemoryFormal";
        var hasOriginalPositiveMutation = manifestCase.TryGetProperty("originalPositiveMutation", out var originalPositiveMutation) &&
            originalPositiveMutation.ValueKind == JsonValueKind.Object;
        var positiveResourceHostDayStraddle = resourceFamily &&
            mutations.TryGetProperty("scenario", out var resourceScenario) &&
            resourceScenario.GetString() == "host-day-straddle";
        if (resourceFamily && (hasOriginalPositiveMutation || positiveResourceHostDayStraddle))
        {
            var positiveMutation = hasOriginalPositiveMutation ? originalPositiveMutation : mutations;
            var seeded = PrtgFormalRuleCaseManifestTests.SeedConsumerHostDayStraddle(fixture, family, positiveMutation);
            return (seeded.Host, seeded.LaterHostDay);
        }
        if (family == "Warning" && caseId == "F53-Warning-P03-warning-cross-midnight-evidence" &&
            manifestCase.TryGetProperty("originalPositiveMutation", out var warningPositiveMutation) &&
            warningPositiveMutation.ValueKind == JsonValueKind.Object)
        {
            // Preserve the frozen 240-minute episode. Only 120 minutes overlap this target day,
            // so it must remain a negative consumer case without the unsupported reclassification tag.
            return (PrtgFormalRuleCaseManifestTests.SeedConsumerStatePositive(
                fixture, family, caseId, warningPositiveMutation), day);
        }
        switch (family)
        {
            case "Down":
            case "Warning":
            case "Flapping":
            {
                if (mutations.TryGetProperty("scenario", out _))
                    return (PrtgFormalRuleCaseManifestTests.SeedConsumerStateScenario(fixture, family, mutations), day);
                return (PrtgFormalRuleCaseManifestTests.SeedConsumerStatePositive(fixture, family, caseId, mutations), day);
            }
            case "Silent":
                if (nativeCurrentDaySilent)
                {
                    var currentSourceAsOf = DateTimeOffset.UtcNow;
                    day = TimeZoneInfo.ConvertTime(currentSourceAsOf, TimeZoneInfo.Local).Date;
                    fixture.UseAnalysisDay(day);
                    var host = fixture.SeedSilentCase();
                    await fixture.CaptureSyntheticSilentSourceAsync(host, currentSourceAsOf);
                    return (host, day);
                }
                return await PrtgFormalRuleCaseManifestTests.SeedConsumerSilentCase(fixture, mutations);
            case "DiskTwoHourLowWater":
            case "CPUFormal":
            case "MemoryFormal":
                return (PrtgFormalRuleCaseManifestTests.SeedConsumerResourceCase(fixture, family, mutations), day);
            case "DiskTrend":
            {
                var validDays = mutations.TryGetProperty("validDays", out var validDayValue)
                    ? validDayValue.GetInt32() : 28;
                var current = mutations.TryGetProperty("currentPercent", out var currentValue)
                    ? currentValue.GetDouble() : 27d;
                var decline = mutations.TryGetProperty("declinePercentPerDay", out var declineValue)
                    ? declineValue.GetDouble() : 1d;
                var dailyValues = mutations.TryGetProperty("dailyValues", out var daily)
                    ? daily.EnumerateArray().Select(value => value.GetDouble()).ToArray() : null;
                var missingDays = mutations.TryGetProperty("missingDayOffsets", out var missing)
                    ? missing.EnumerateArray().Select(value => value.GetInt32()).ToArray() : null;
                var host = fixture.SeedDiskTrendCase(validDays, current, decline, dailyValues, missingDays,
                    "success",
                    trustedHistory: !mutations.TryGetProperty("trustedHistory", out var trusted) || trusted.GetBoolean(),
                    profileEffectiveDaysBefore: mutations.TryGetProperty("profileEffectiveDaysBefore", out var effective)
                        ? effective.GetInt32() : 40,
                    identityDrift: mutations.TryGetProperty("identityDrift", out var drift) ? drift.GetString() : null,
                    hoursPerDay: mutations.TryGetProperty("hoursPerDay", out var hours) ? hours.GetInt32() : 24,
                    outOfRangePercent: mutations.TryGetProperty("outOfRangePercent", out var range) ? range.GetDouble() : null,
                    outOfRangeDayOffset: mutations.TryGetProperty("outOfRangeDayOffset", out var rangeOffset) ? rangeOffset.GetInt32() : null,
                    futureMeasurements: mutations.TryGetProperty("futureMeasurements", out var future) && future.GetBoolean(),
                    physicalSlotsPerHour: mutations.TryGetProperty("physicalSlotsPerHour", out var slots) ? slots.GetInt32() : 4,
                    duplicatePeriodDayOffset: mutations.TryGetProperty("duplicatePeriodDayOffset", out var duplicate) ? duplicate.GetInt32() : null);
                if (mutations.TryGetProperty("ruleEnabled", out var enabled) && !enabled.GetBoolean())
                    fixture.SetDiskTrendRuleEnabled(false);
                return (host, day);
            }
            default:
                throw new InvalidOperationException($"Unsupported fixed consumer family {family}.");
        }
    }

    internal static string SourceForFamily(string family) => family switch
    {
        "Down" => "PRTG:down",
        "Warning" => "PRTG:warning",
        "Flapping" => "PRTG:flapping",
        "Silent" => "PRTG:silent",
        "CPUFormal" => "PRTG:resource_cpu_sustained_pressure",
        "MemoryFormal" => "PRTG:resource_memory_sustained_pressure",
        _ => "PRTG:disk_free_trend"
    };

    private static void SetSyntheticParentNeverAnalyzed(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day)
    {
        using var context = fixture.Backend.CreateContext();
        var row = context.DailyRecords.Single(item => item.HostId == host.HostId && item.RecordDate == day.Date);
        var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)
            ?? throw new InvalidDataException("Synthetic parent record JSON could not be loaded.");
        record.AiAnalyzed = false;
        record.AiPending = false;
        row.AiAnalyzed = false;
        row.AiPending = false;
        row.ContentJson = JsonSerializer.Serialize(record);
        context.SaveChanges();
    }

    private static void PromoteSyntheticNetIqBaselineForConsumerTest(
        PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day)
    {
        var store = fixture.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        var record = Assert.Single(store.ReadRecent(day, 1));
        var baseline = Assert.Single(record.TopIssues.Where(issue =>
            issue.Source == "Synthetic NetIQ disk baseline" && issue.EventId == 53053));
        // This opt-in scenario changes only the known synthetic NetIQ parent issue to an explicitly
        // high/elevating baseline. The fixed Silent manifest and PRTG rule remain untouched.
        baseline.Severity = IssueSeverity.High;
        baseline.ElevatesDayRisk = true;
        Assert.Empty(record.CorrelationAlerts); // This fixed synthetic parent has no correlation findings.
        record.RiskLevel = LogAnalysisService.ComputeRuleBasedRisk(record.TopIssues,
            record.TrendAlerts, []);
        record.RiskBasis = $"rule:{baseline.Source} EventId {baseline.EventId}";
        record.AiAnalyzed = false;
        record.AiPending = false;
        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Equal(1, store.DeleteDays([day]));
        store.Append(record);
        var persisted = Assert.Single(store.ReadRecent(day, 1));
        Assert.Equal(RiskLevels.High, persisted.RiskLevel);
        Assert.False(persisted.AiAnalyzed);
        Assert.False(persisted.AiPending);
    }

    private static string MissingFindingDiagnostic(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
        string family, JsonElement manifestCase,
        (PrtgFindingsRegistry Registry, IReadOnlyList<string> ConsoleLines) daily)
    {
        var source = SourceForFamily(family);
        var findings = daily.Registry.For(host.HostId, day);
        var parent = Assert.Single(fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1));
        var settings = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var authority = family switch
        {
            "CPUFormal" => ResourceDiagnostic(fixture, PrtgResourceFamily.Cpu),
            "MemoryFormal" => ResourceDiagnostic(fixture, PrtgResourceFamily.Memory),
            "DiskTwoHourLowWater" => ResourceDiagnostic(fixture, PrtgResourceFamily.Disk),
            "Silent" => SilentDiagnostic(fixture, host, day, policy, settings.PrtgUrl),
            _ => "not-a-resource-or-silent-family"
        };
        var actual = string.Join("; ", findings.Select(item =>
            $"{item.Source}/{item.RuleId}/{item.EventKey}/reason={string.Join(",", item.PrtgResourceReasonCodes ?? [])}/messages={string.Join(" | ", item.SampleMessages)}"));
        var sideEffects = manifestCase.TryGetProperty("expectedFormalSideEffects", out var expected)
            ? JsonSerializer.Serialize(expected) : "[]";
        return $"{manifestCase.GetProperty("caseId").GetString()} expected source={source}; target day={day:O}; " +
            $"mutations={JsonSerializer.Serialize(manifestCase.GetProperty("mutations"))}; expected side effects={sideEffects}; " +
            $"actual findings=[{actual}]; policy enabled={settings.PrtgEnabled}, urlConfigured={!string.IsNullOrWhiteSpace(settings.PrtgUrl)}, " +
            $"revision={policy.Revision}, core={policy.CoreSystemId}, generation={policy.SourceGeneration}, " +
            $"hosts=[{string.Join(",", policy.HostIds)}], sensors=[{string.Join(",", policy.SensorIds)}], " +
            $"validFrom={policy.ValidFrom:O}, sourceZone={policy.SourceTimeZoneId}, analysisZone={policy.AnalysisTimeZoneId}; " +
            $"parent logSource={parent.LogSource}, attempt={parent.LatestNetiqAttemptStatus}, eligible={parent.CanSupplementWithPrtg()}, " +
            $"risk={parent.RiskLevel}, aiAnalyzed={parent.AiAnalyzed}, aiPending={parent.AiPending}; " +
            $"formal authority/readiness={authority}; Daily output={string.Join(" || ", daily.ConsoleLines)}";
    }

    private static (bool AiAnalyzed, bool AiPending, string RiskLevel) ReadSqlAiState(
        PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day)
    {
        using var context = fixture.Backend.CreateContext();
        var row = context.DailyRecords.Single(item => item.HostId == host.HostId && item.RecordDate == day.Date);
        return (row.AiAnalyzed, row.AiPending, row.RiskLevel);
    }

    private static string AiQueueDiagnostic(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
        string family, JsonElement manifestCase, DailyAnalysisRecord record, IReadOnlyList<LogIssueSignature> findings)
    {
        var sql = ReadSqlAiState(fixture, host, day);
        var ruleFacts = string.Join("; ", findings.Select(finding =>
        {
            var rule = KnownIssueCatalog.Rules.FirstOrDefault(item => item.Id == finding.RuleId);
            return $"{finding.Source}/{finding.RuleId}:findingSeverity={finding.Severity},findingElevates={finding.ElevatesDayRisk}," +
                $"ruleSeverity={rule?.Severity.ToString() ?? "missing"},ruleElevates={rule?.ElevatesDayRisk.ToString() ?? "missing"}";
        }));
        var baseline = string.Join("; ", record.TopIssues.Where(issue =>
                issue.Source == "Synthetic NetIQ disk baseline" && issue.EventId == 53053)
            .Select(issue => $"{issue.Source}/{issue.EventId}:severity={issue.Severity},elevates={issue.ElevatesDayRisk}"));
        var expectedMarkCount = record.AiAnalyzed || record.RiskLevel != RiskLevels.Low ? 1 : 0;
        return $"{manifestCase.GetProperty("caseId").GetString()} family={family}; mutations=" +
            $"{JsonSerializer.Serialize(manifestCase.GetProperty("mutations"))}; parent risk={record.RiskLevel},riskBasis={record.RiskBasis}," +
            $"aiAnalyzed={record.AiAnalyzed},aiPending={record.AiPending}; SQL risk={sql.RiskLevel},aiAnalyzed={sql.AiAnalyzed},aiPending={sql.AiPending}; " +
            $"expected MarkAllForAiRerun count={expectedMarkCount}; findings=[{ruleFacts}]; baseline=[{baseline}]";
    }
    private static string ResourceDiagnostic(PrtgFormalRuleCaseFixture fixture, PrtgResourceFamily family)
    {
        var assessment = fixture.AssessResourcePeriod(family);
        return assessment is null ? $"{family}: no assessment" :
            $"{family}: decision={assessment.Decision}; asOf={assessment.AsOfUtc:O}; " +
            $"evidenceAsOf={assessment.EvidenceAsOfUtc:O}; singleWindowHostDay={assessment.SingleWindowHostDay:O}; " +
            $"rule={assessment.CurrentRule?.Id}/{assessment.CurrentRule?.Enabled}; " +
            $"ruleFingerprint={assessment.CurrentRuleFingerprint}; admission={assessment.RuleAdmissionFingerprint}";
    }

    private static string FormalMailFenceDiagnostic(PrtgFormalRuleCaseFixture fixture, WebHost host,
        DateTime day, string family, JsonElement manifestCase)
    {
        if (family is not ("CPUFormal" or "MemoryFormal"))
            return $"{manifestCase.GetProperty("caseId").GetString()}: non-CPU/memory mail body omitted its expected finding.";

        var record = fixture.Backend.RecordStore(new HostKey
            { HostId = host.HostId, HostName = host.HostName }).ReadRecent(day, 1).SingleOrDefault();
        if (record is null) return $"{manifestCase.GetProperty("caseId").GetString()}: persisted parent is missing.";

        var issue = record.TopIssues.FirstOrDefault(item => item.Source == SourceForFamily(family));
        var manifest = record.PrtgManifest;
        var manifestValid = HostDayWorkflowFingerprint.HasValidPrtgManifest(record);
        long? storedModeVersion;
        long? storedAuthorityRevision;
        using (var context = fixture.Backend.CreateContext())
        {
            storedModeVersion = context.Blobs.AsNoTracking()
                .Where(row => row.BlobKey == PrtgResourcePressureModeStore.BlobKey(host.HostId))
                .Select(row => (long?)row.Version).SingleOrDefault();
            storedAuthorityRevision = context.Blobs.AsNoTracking()
                .Where(row => row.BlobKey == PrtgResourceIdentityStore.AuthorityRevisionKey(host.HostId))
                .Select(row => (long?)row.Version).SingleOrDefault();
        }

        var resourceFamily = family == "CPUFormal" ? PrtgResourceFamily.Cpu : PrtgResourceFamily.Memory;
        var modeSnapshot = new PrtgResourcePressureModeStore(
            fixture.Backend.Blob(PrtgResourcePressureModeStore.BlobKey(host.HostId)))
            .ReadSnapshot(host.HostId, PrtgFormalRuleCaseFixture.SensorId, resourceFamily);
        var policy = new PrtgMonitoringPolicyStore(
            fixture.Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var eventIdentityParts = issue?.EventKey?.Split(':') ?? [];
        var expectedRuleCode = resourceFamily == PrtgResourceFamily.Cpu
            ? PrtgRuleEvaluator.RuleResourceCpuPressure : PrtgRuleEvaluator.RuleResourceMemoryPressure;
        var eventKeyMatchesIssue = issue is not null && eventIdentityParts.Length == 5 &&
            eventIdentityParts[0] == "prtg" && eventIdentityParts[1] == expectedRuleCode &&
            eventIdentityParts[2] == PrtgFormalRuleCaseFixture.SensorId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            eventIdentityParts[3] == issue.PrtgSourceGeneration &&
            eventIdentityParts[4] == issue.PrtgResourceGeneration;
        var identityMatches = issue is not null &&
            fixture.Backend.PrtgStore().GetResourceIdentities([PrtgFormalRuleCaseFixture.SensorId])
                .TryGetValue(PrtgFormalRuleCaseFixture.SensorId, out var identity) &&
            identity.Active && !identity.PendingReconciliation && identity.HostId == host.HostId &&
            identity.SourceGeneration == issue.PrtgSourceGeneration &&
            identity.Generation == issue.PrtgResourceGeneration &&
            identity.ChannelGeneration == issue.PrtgChannelGeneration;
        var profileFresh = issue is not null && PrtgResourceProfileQualification.IsCurrentFormalPressure(
            fixture.Backend, host.HostId, PrtgFormalRuleCaseFixture.SensorId, issue, DateTime.UtcNow);

        var fenceAccepted = false;
        if (issue is not null && record.RecordId > 0)
        {
            using var context = fixture.Backend.CreateContext();
            fenceAccepted = PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(
                context, host.HostId, record.RecordId, issue, null, out _);
        }

        return $"{manifestCase.GetProperty("caseId").GetString()}: persisted mail parent/fence diagnostic; " +
            $"targetIssuePresent={issue is not null}, manifestValid={manifestValid}, " +
            $"manifestParentMatches={manifest?.ParentRecordId == record.RecordId}, " +
            $"modeFenceRequired={manifest?.ResourceModeFenceRequired == true}, " +
            $"manifestModeVersion={manifest?.ResourceModeBlobVersion.ToString() ?? "missing"}, " +
            $"storedModeVersion={storedModeVersion?.ToString() ?? "missing"}, " +
            $"modeVersionMatches={manifest is not null && storedModeVersion == manifest.ResourceModeBlobVersion}, " +
            $"manifestResourceAuthorityRevision={manifest?.ResourceAuthorityRevision.ToString() ?? "missing"}, " +
            $"storedResourceAuthorityRevision={storedAuthorityRevision?.ToString() ?? "missing"}, " +
            $"resourceAuthorityRevisionMatches={manifest is not null && storedAuthorityRevision == manifest.ResourceAuthorityRevision}, " +
            $"manifestPolicyRevisionPresent={!string.IsNullOrWhiteSpace(manifest?.PolicyRevision)}, " +
            $"policyRevisionMatches={manifest is not null && policy.Revision == manifest.PolicyRevision}, " +
            $"currentGrantFormalAndMaintain={modeSnapshot.Grant is { FormalEnabled: true, MaintainAuthorized: true, TrialConsumed: true }}, " +
            $"issueIdentityMatches={eventKeyMatchesIssue && identityMatches}, profileFresh={profileFresh}, " +
            $"currentStartFenceAccepted={fenceAccepted}, aiAnalyzed={record.AiAnalyzed}, aiPending={record.AiPending}.";
    }

    private static string SilentDiagnostic(PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day,
        PrtgMonitoringPolicy policy, string configuredUrl)
    {
        var readiness = new PrtgSilentPresenceFormalConsumer(fixture.Backend).EvaluateWithReadiness(day,
            policy, configuredUrl, new Dictionary<long, long> { [PrtgFormalRuleCaseFixture.DeviceId] = host.HostId },
            KnownIssueCatalog.Rules).Devices[PrtgFormalRuleCaseFixture.DeviceId];
        return $"silent {readiness.State}/{readiness.Reason}; fingerprint={readiness.EvidenceFingerprint}; " +
            $"sourceAsOf=[{string.Join(",", readiness.Findings.Select(item => item.PresenceSourceAsOf?.ToString("O") ?? "null"))}]";
    }

    private static async Task<(DailyAnalysisRecord Record, string Prompt)> ConsumePendingWithActualHostedService(
        PrtgFormalRuleCaseFixture fixture, WebHost host, DateTime day, string diagnostic,
        IReadOnlyCollection<DateTime>? expectedEligibleDays = null)
    {
        var backend = fixture.Backend;
        var recordStore = backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        Assert.Single(recordStore.ReadRecent(day, 1));
        var eligibleDays = expectedEligibleDays ?? [day.Date];
        var expectedEligibleParents = eligibleDays.Select(expectedDay =>
            (HostId: host.HostId, RecordDate: expectedDay.Date)).OrderBy(item => item.RecordDate).ToArray();
        using (var context = backend.CreateContext())
        {
            var actualEligibleParents = context.DailyRecords.AsNoTracking()
                .Where(row => (row.AiAnalyzed || row.RiskLevel != RiskLevels.Low) && !row.DetailPruned)
                .Select(row => new { row.HostId, row.RecordDate })
                .AsEnumerable()
                .Select(row => (HostId: row.HostId, RecordDate: row.RecordDate.Date))
                .OrderBy(item => item.RecordDate).ToArray();
            Assert.Equal(expectedEligibleParents, actualEligibleParents);
        }
        var markCount = backend.RecordStore().MarkAllForAiRerun();
        Assert.True(markCount == expectedEligibleParents.Length,
            diagnostic + $"; expected exact eligible parent universe={expectedEligibleParents.Length}; actual MarkAllForAiRerun count={markCount}");

        var ai = new FakeAiService
        {
            NextContent = """{"risk_level":"中","headline":"PRTG finding reviewed","story":"Formal finding reviewed.","trend_story":"Signal remains relevant.","action":"Review the sensor."}"""
        };
        var backfiller = new DailyRecordBackfiller(backend.CreateContext);
        backfiller.Run(CancellationToken.None);
        var service = new AiAnalysisHostedService(new ScheduleOptionsStore(backend.Blob("schedule_options")),
            new SchedulerRunState(), new AiAnalysisRunState(), backend.RecordStore(), backend,
            new FakeSystemSettingsStore(), new DataVersionStamp(),
            new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
            backfiller, new AvailableWebAi(), aiService: ai);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);
        var updated = Assert.Single(recordStore.ReadRecent(day, 1));
        Assert.Equal(expectedEligibleParents.Length, ai.Prompts.Count);
        foreach (var expectedDay in eligibleDays)
        {
            var dateHeader = $"在 {expectedDay:yyyy-MM-dd}（";
            Assert.Single(ai.Prompts.Where(prompt => prompt.Contains(dateHeader, StringComparison.Ordinal)));
            var processedParent = Assert.Single(recordStore.ReadRecent(expectedDay, 1));
            Assert.True(processedParent.AiAnalyzed);
            Assert.False(processedParent.AiPending);
        }
        var targetHeader = $"在 {day:yyyy-MM-dd}（";
        var targetPrompt = Assert.Single(ai.Prompts.Where(prompt =>
            prompt.Contains(targetHeader, StringComparison.Ordinal)));
        return (updated, targetPrompt);
    }

    private static async Task<(FakeSmtpMailSender Sender, MailNotifyState State)> NotifyWithActualMailService(
        PrtgFormalRuleCaseFixture fixture, string recipient, DateTime day)
    {
        var backend = fixture.Backend;
        var settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.MailEnabled = true;
            value.MailOnRunCompleted = true;
            value.MailMinRiskLevel = RiskLevels.Low;
            // This acceptance scenario explicitly enables low-risk detail visibility as well as
            // low-risk mail delivery; the production default still hides low-risk days.
            value.VisibleDayRiskLevels = RiskLevels.All.ToList();
            value.SmtpServer = "smtp.test.local";
            value.MailFrom = "logforesight@test.local";
            value.MailRecipients = [recipient];
        });
        var hosts = new HostStore(backend.Blob("hosts"));
        var groups = new FakeUserGroupStore();
        var group = groups.Upsert(new UserGroup { GroupName = "consumer-test-admin", Role = UserRole.Admin, Active = true });
        var users = new FakeUserStore();
        users.Upsert(new WebUser { Account = recipient, Email = recipient, Active = true, GroupIds = [group.GroupId] });
        var aggregates = backend.IssueAggregateQuery(hosts);
        var issueHandlings = backend.IssueHandlingStore();
        var issueCases = backend.IssueCaseStore();
        var digest = new MailIssueDigest(aggregates,
            new OccurrenceStatusResolver(hosts, issueHandlings, issueCases, settings), settings,
            new FixedIssueExclusionSource(IssueExclusion.None));
        var batchRuns = new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
        var scheduleOptions = new ScheduleOptionsStore(backend.Blob("schedule_options"));
        var sender = new FakeSmtpMailSender();
        var mailState = new MailNotifyStateStore(backend.Blob("consumer_mail_state"));
        var service = new MailNotificationService(settings, sender, hosts, users, groups,
            new FakeGroupAccessStore(), backend.RecordStore(), backend.RecordHandlingStore(), mailState,
            new ScheduleFreshnessService(batchRuns, scheduleOptions),
            new IssueOwnerStore(backend.Blob("issue_owners")), aggregates, digest,
            new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)), backend);

        await service.NotifyAfterRunAsync();
        return (sender, mailState.Get());
    }

    internal static JsonElement ReadCase(string family, string caseId)
    {
        const string relative = "Fixtures/prtg-round53-formal-cases.json";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (!File.Exists(direct)) direct = Path.Combine(directory.FullName, "LogForesight.Tests", relative);
            if (!File.Exists(direct)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(direct));
            var familyNode = document.RootElement.GetProperty("families").EnumerateArray()
                .Single(item => item.GetProperty("family").GetString() == family);
            return familyNode.GetProperty("positiveCases").EnumerateArray()
                .Concat(familyNode.GetProperty("negativeAndBoundaryCases").EnumerateArray())
                .Single(item => item.GetProperty("caseId").GetString() == caseId).Clone();
        }
        throw new FileNotFoundException("Could not locate the fixed formal PRTG case manifest.", relative);
    }

    private static IReadOnlyList<(string Family, string CaseId, bool IsPositive, bool HasOriginalPositiveMutation)> ReadFixedCaseUniverse()
    {
        const string relative = "Fixtures/prtg-round53-formal-cases.json";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (!File.Exists(direct)) direct = Path.Combine(directory.FullName, "LogForesight.Tests", relative);
            if (!File.Exists(direct)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(direct));
            return document.RootElement.GetProperty("families").EnumerateArray()
                .SelectMany(family =>
                {
                    var name = family.GetProperty("family").GetString()!;
                    return family.GetProperty("positiveCases").EnumerateArray()
                        .Select(item => (name, item.GetProperty("caseId").GetString()!, true, false))
                        .Concat(family.GetProperty("negativeAndBoundaryCases").EnumerateArray()
                            .Select(item => (name, item.GetProperty("caseId").GetString()!, false,
                                item.TryGetProperty("originalPositiveMutation", out var mutation) &&
                                mutation.ValueKind == JsonValueKind.Object)));
                }).ToArray();
        }
        throw new FileNotFoundException("Could not locate the fixed formal PRTG case manifest.", relative);
    }

    private sealed class AvailableWebAi : IWebAiService
    {
        public bool Available => true;
        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class =>
            Task.FromResult<T?>(null);
        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) => Task.FromResult<string?>(null);
    }
}

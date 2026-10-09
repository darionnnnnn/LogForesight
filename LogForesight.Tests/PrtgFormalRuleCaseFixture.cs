using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogForesight.Tests;

/// <summary>
/// SQLite-backed synthetic source fixture for formal PRTG acceptance cases. Its identities and
/// coverage are test evidence only and never assert compatibility with a native PRTG deployment.
/// </summary>
internal sealed class PrtgFormalRuleCaseFixture : IDisposable
{
    private static readonly string SourceGeneration = Guid.Parse("00000000-0000-0000-0000-000000005301").ToString("N");
    internal const long DeviceId = 53001;
    internal const long SensorId = 53002;
    private const long SilentAvailabilitySensorId = 53003;
    private const long SilentSecondAvailabilitySensorId = 53004;
    private const string SourceUrl = "https://192.0.2.53";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-f53-formal-cases-" + Guid.NewGuid().ToString("N"));
    private readonly PrtgFindingsRegistry _registry = new();
    private string? _baselineCaseId;
    private string? _baselineIssueKey;
    private DateTimeOffset? _syntheticIdentityObservedAtUtc;
    private bool _elevateCpuFormalRisk;
    private bool _highRiskNetIqBaseline;

    public StorageBackend Backend { get; }
    public DateTime AnalysisDay { get; private set; } = DateTime.Today.AddDays(-1);

    public PrtgFormalRuleCaseFixture()
    {
        Directory.CreateDirectory(_directory);
        Backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "formal-cases.db")}"
        }, _directory);
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
        {
            SeedVersion = KnownIssueSeed.Version,
            Rules = KnownIssueSeed.CreateRules()
        });
        new SystemSettingsStore(Backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = SourceUrl;
            settings.PrtgAuthMode = PrtgAuthModes.Token;
            settings.PrtgApiTokenEnc = CryptoHelper.Encrypt("synthetic-fixture-token");
            settings.PrtgTimeoutSeconds = 1;
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            settings.PrtgSensorTypeWhitelist = new List<string>(SystemSettings.DefaultPrtgSensorTypeWhitelist);
        });
        new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = "f53-synthetic-policy-v1";
            policy.CoreSystemId = "f53-synthetic-core";
            policy.SourceGeneration = SourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(SourceUrl);
            policy.ValidFrom = new DateTimeOffset(AnalysisDay.AddDays(-32));
            policy.HostIds = [];
            policy.SensorIds = [];
            policy.SourceTimeZoneId = TimeZoneInfo.Local.Id;
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = TimeZoneInfo.Local.Id;
            policy.TimeBasisEvidenceReference = "synthetic-fixture-time-basis";
        });
    }

    public void UseAnalysisDay(DateTime day) => AnalysisDay = day.Date;

    public void UseSilentSourceTimeZone(string timeZoneId)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
            policy.SourceTimeZoneId = timeZoneId);
    }

    public void UseSyntheticIdentityObservedAt(DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Synthetic identity observation must be UTC.", nameof(observedAtUtc));
        _syntheticIdentityObservedAtUtc = observedAtUtc;
    }

    public void UseElevatingCpuFormalRule()
    {
        _elevateCpuFormalRisk = true;
        var rules = KnownIssueSeed.CreateRules();
        var cpuRule = rules.Single(rule => rule.Id == "builtin-prtg-resource-cpu-pressure");
        cpuRule.ElevatesDayRisk = true;
        new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    public void UseHighRiskNetIqBaseline() => _highRiskNetIqBaseline = true;

    public string SeedHumanOwnedBaseline(WebHost host)
    {
        var key = new HostKey { HostId = host.HostId, HostName = host.HostName };
        var records = Backend.RecordStore(key);
        var record = records.ReadRecent(AnalysisDay, 1).Single();
        var issue = record.TopIssues.SingleOrDefault(item => item.Source == "Synthetic NetIQ disk baseline" && item.EventId == 53053)
            ?? throw new InvalidOperationException("Synthetic NetIQ baseline must be present in the parent before human sidecars are seeded.");
        var issueKey = IssueSignatureKey.For(issue);
        new IssueOwnerStore(Backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = issue.Source, EventId = issue.EventId, OwnerUserIds = [53054],
            Note = "Synthetic human owner assignment to preserve."
        });
        var caseId = Guid.NewGuid().ToString("N");
        _baselineCaseId = caseId;
        _baselineIssueKey = issueKey;
        Backend.IssueCaseStore().Save(new IssueCase
        {
            CaseId = caseId, HostName = host.HostName, IssueKey = issueKey, IssueLabel = "Synthetic NetIQ baseline",
            Status = IssueHandlingStatuses.InProgress, HandlerId = 53054, FirstLinkedDate = AnalysisDay,
            LastLinkedDate = AnalysisDay, CreatedAt = DateTime.Now, CreatedByAccount = "synthetic-maintainer",
            UpdatedAt = DateTime.Now
        });
        Backend.IssueHandlingStore().Save(new IssueHandling
        {
            HostName = host.HostName, Date = AnalysisDay, IssueKey = issueKey,
            Status = IssueHandlingStatuses.KnownNoise, ActorId = 53054, ActorAccount = "synthetic-maintainer",
            Note = "Human-owned per-issue disposition to preserve.", UpdatedAt = DateTime.Now
        });
        Backend.RecordHandlingStore().Save(new RecordHandling
        {
            HostName = host.HostName, Date = AnalysisDay, Status = HandlingStatuses.InProgress,
            HandlerId = 53054, Note = "Human-owned day handling to preserve.", UpdatedAt = DateTime.Now
        });
        return CaptureHumanOwnedBaseline(host);
    }

    public string CaptureHumanOwnedBaseline(WebHost host)
    {
        var caseId = _baselineCaseId ?? throw new InvalidOperationException("Human baseline has not been seeded.");
        var issueKey = _baselineIssueKey ?? throw new InvalidOperationException("Human baseline has not been seeded.");
        var record = Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
            .ReadRecent(AnalysisDay, 1).Single();
        var signature = record.TopIssues.Single(issue => issue.Source == "Synthetic NetIQ disk baseline" && issue.EventId == 53053);
        var issueCase = Backend.IssueCaseStore().Get(caseId);
        var issueHandling = Backend.IssueHandlingStore().GetForDay(host.HostName, AnalysisDay)
            .Single(item => item.IssueKey == issueKey);
        var recordHandling = Backend.RecordHandlingStore().Get(host.HostName, AnalysisDay);
        var owner = new IssueOwnerStore(Backend.Blob("issue_owners")).Get("Synthetic NetIQ disk baseline", 53053);
        return JsonSerializer.Serialize(new
        {
            Parent = new { record.LogSource, record.LatestNetiqAttemptStatus, record.RiskLevel, record.RiskBasis },
            Signature = new { signature.LogName, signature.Source, signature.EventId, signature.EntryType,
                signature.Count, signature.SampleMessages },
            Case = issueCase is null ? null : new { issueCase.CaseId, issueCase.IssueKey, issueCase.Status, issueCase.HandlerId },
            IssueHandling = new { issueHandling.Status, issueHandling.ActorId, issueHandling.ActorAccount,
                issueHandling.Note, issueHandling.CaseId },
            RecordHandling = recordHandling is null ? null : new
                { recordHandling.Status, recordHandling.HandlerId, recordHandling.Note },
            Owner = owner is null ? null : new { owner.SourceName, owner.EventId, owner.OwnerUserIds, owner.Note }
        });
    }

    public string CaptureHumanOwnedData(WebHost host)
    {
        using var document = JsonDocument.Parse(CaptureHumanOwnedBaseline(host));
        var root = document.RootElement;
        return JsonSerializer.Serialize(new
        {
            Signature = root.GetProperty("Signature"),
            Case = root.GetProperty("Case"),
            IssueHandling = root.GetProperty("IssueHandling"),
            RecordHandling = root.GetProperty("RecordHandling"),
            Owner = root.GetProperty("Owner")
        });
    }

    public void SetPrtgThreshold(string ruleCode, string? category, int threshold)
    {
        var rules = KnownIssueSeed.CreateRules();
        var rule = rules.Single(item => item.PrtgRuleCode == ruleCode &&
            string.Equals(item.PrtgSensorCategory, category, StringComparison.OrdinalIgnoreCase));
        typeof(KnownIssueRule).GetProperty(nameof(KnownIssueRule.PrtgThreshold))!.SetValue(rule, threshold);
        new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    public WebHost SeedCoveredStateCase(string hostName, string sensorName, string sensorType, string currentStatus,
        Func<DateTime, IReadOnlyList<(DateTime At, string Status)>> transitions, string? category = null,
        string parentStatus = "success", bool includeParent = true)
    {
        var host = new HostStore(Backend.Blob("hosts")).Upsert(new WebHost
        {
            Source = "netiq", HostName = hostName, Active = true, IpAddress = "192.0.2.53"
        });
        var store = Backend.PrtgStore();
        store.UpsertDevices([new PrtgDeviceRow { Objid = DeviceId, Name = hostName, Ip = "192.0.2.53" }], DateTime.Now);
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = SensorId, DeviceObjid = DeviceId, Name = sensorName, SensorType = sensorType,
            Status = currentStatus, Category = category ?? PrtgSensorCategories.Cpu
        }], DateTime.Now);
        store.ReplaceHostMapForDate(AnalysisDay, [new PrtgHostMapRow
        {
            DeviceObjid = DeviceId, MapDate = AnalysisDay, HostId = host.HostId, HostName = host.HostName,
            MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Now
        }]);
        store.ApplyAutoCategories(PrtgSensorTypeCategoryMap.ParseOverrides(
            new SystemSettingsStore(Backend.Blob("system_settings")).Get().PrtgSensorTypeCategoryOverrides).Map);
        var policy = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value =>
        {
            value.HostIds = [host.HostId];
            value.SensorIds = [SensorId];
        });
        var identity = store.BindObservedResource(SensorId, host.HostId, SourceGeneration,
            PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), sensorType,
                "synthetic-formal-case-device-sensor", 0));
        var timedStates = transitions(AnalysisDay).OrderBy(item => item.At).ToArray();
        store.AppendStateChanges(timedStates.Select(item => new PrtgStateChangeRow
        {
            SensorObjid = SensorId, ChangedAt = item.At, Status = item.Status
        }).ToArray());
        new PrtgSensorTimelineStore(Backend.Blob(PrtgSensorTimelineStore.Prefix + SensorId)).Update(evidence =>
        {
            evidence.Bind(SensorId, host.HostId, SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), sensorType,
                    "synthetic-formal-case-device-sensor", 0), identity.Generation, identity.Epoch,
                identity.ChannelGeneration, _syntheticIdentityObservedAtUtc ?? policy.Get().ValidFrom);
            var coverageStart = AnalysisDay.AddDays(-30);
            evidence.Accept(new DateTimeOffset(coverageStart), new DateTimeOffset(AnalysisDay.AddDays(1)),
                timedStates.Select(item => new PrtgTimedState(SensorId, new DateTimeOffset(item.At), item.Status,
                    identity.SourceGeneration, identity.Generation)));
        });
        if (includeParent)
        {
            var baselineIssue = CreateHumanBaselineIssue(_highRiskNetIqBaseline);
            var parentRisk = LogAnalysisService.ComputeRuleBasedRisk([baselineIssue], [], []);
            Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName }).Append(new DailyAnalysisRecord
            {
                LogSource = AnalysisLogSource.Netiq,
                LatestNetiqAttemptStatus = parentStatus,
                Date = AnalysisDay,
                HostId = host.HostId,
                Host = host.HostName,
                RiskLevel = parentRisk,
                RiskBasis = _highRiskNetIqBaseline
                    ? $"rule:{baselineIssue.Source} EventId {baselineIssue.EventId}"
                    : "synthetic NetIQ parent with an existing non-PRTG issue",
                TopIssues = [baselineIssue]
            });
        }
        return host;
    }

    private static LogIssueSignature CreateHumanBaselineIssue(bool highRisk = false) => new()
    {
        LogName = "System", Source = "Synthetic NetIQ disk baseline", EventId = 53053,
        EntryType = System.Diagnostics.EventLogEntryType.Error, Count = 2,
        Category = IssueCategory.Storage, Severity = highRisk ? IssueSeverity.High : IssueSeverity.Low,
        ElevatesDayRisk = highRisk,
        SampleMessages = ["Human-owned NetIQ baseline detail; retain exactly on no-hit."]
    };

    public WebHost SeedFormalResourceCase(PrtgResourceFamily family, IReadOnlyList<double> completedHourValues,
        int goodPhysicalSlotsPerHour = 4, bool crossesAnalysisMidnight = false,
        bool crossesHostMidnight = false,
        bool memoryValuesAreRemainingPercent = false, bool formalRuleEnabled = true,
        string? periodMutation = null, string? profileMutation = null,
        string? evidenceMutation = null, string? authorityMutation = null)
    {
        if (family is not (PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory or PrtgResourceFamily.Disk) ||
            completedHourValues.Count is < 1 or > 2 || goodPhysicalSlotsPerHour is < 1 or > 4)
            throw new ArgumentException("A resource case needs one or two completed-hour values.");

        var (sensorType, category, quantity, channel, caption) = family switch
        {
            PrtgResourceFamily.Cpu => ("SNMP CPU Load", PrtgSensorCategories.Cpu,
                PrtgTrustedQuantitySemantic.CpuLoadPercent, "load", "CPU Usage"),
            PrtgResourceFamily.Memory => ("SNMP Memory", PrtgSensorCategories.Memory,
                memoryValuesAreRemainingPercent ? PrtgTrustedQuantitySemantic.MemoryAvailablePercent : PrtgTrustedQuantitySemantic.MemoryUsedPercent,
                memoryValuesAreRemainingPercent ? "available" : "used",
                memoryValuesAreRemainingPercent ? "Memory Available" : "Memory Used"),
            _ => ("SNMP Disk Free", PrtgSensorCategories.Disk,
                PrtgTrustedQuantitySemantic.DiskFreePercent, "free", "Free Space")
        };
        var host = SeedCoveredStateCase($"F53-{family}-HOST", caption, sensorType, "Up",
            _ => Array.Empty<(DateTime At, string Status)>(), category);
        var store = Backend.PrtgStore();
        var identity = store.GetResourceIdentities([SensorId])[SensorId];
        var channelFingerprint = System.Text.Json.JsonSerializer.Serialize(new
        {
            ChannelIdentifier = channel, ChannelName = caption, Unit = "%", Scale = (double?)1,
            Direction = "direct"
        }) + "|resource-period-v1";
        identity = store.SetObservedChannel(SensorId, SourceGeneration, channelFingerprint, identity.Generation);
        var policyStore = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var analysisTimeZoneId = TimeZoneInfo.Local.Id;
        if (crossesAnalysisMidnight)
        {
            var serviceDayCutoffUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(AnalysisDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
            var serviceOffset = TimeZoneInfo.Local.GetUtcOffset(serviceDayCutoffUtc);
            analysisTimeZoneId = TimeZoneInfo.GetSystemTimeZones()
                .Where(zone => zone.GetUtcOffset(serviceDayCutoffUtc) == serviceOffset.Add(TimeSpan.FromHours(1)))
                .OrderBy(zone => zone.Id, StringComparer.Ordinal).Select(zone => zone.Id).FirstOrDefault()
                ?? throw new InvalidOperationException("No analysis timezone one hour ahead of the service timezone is available for the analysis-midnight fixture.");
        }
        policyStore.Update(policy =>
        {
            policy.ConfirmedBy = "synthetic Maintain fixture";
            policy.AnalysisTimeZoneId = analysisTimeZoneId;
            policy.SourceTimeZoneId = "UTC";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "synthetic-fixture-time-basis";
        });
        var policy = policyStore.Get();
        var settings = new SystemSettingsStore(Backend.Blob("system_settings"));
        var strategyDefinition = PrtgFetchStrategy.Profile(settings.Get().PrtgFetchStrategy);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            Backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(settings.Get().PrtgFetchStrategy),
            strategyDefinition.SnapshotIntervalMinutes, DateTime.UtcNow.AddDays(-32));
        if (!strategy.Ready) throw new InvalidOperationException("Synthetic strategy fixture was not ready.");
        var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(SensorId, identity, sensorType, channel, caption,
            quantity, "%", 1d, "direct", "resource-period-v1", strategy.StrategyFingerprint,
            strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromMinutes(strategy.StrategyMinutes), "seconds", "UTC", "UTC", analysisTimeZoneId,
            DateTimeOffset.UtcNow, "synthetic-source-metadata-reference", "synthetic-physical-sample-reference",
            true, completedHourValues[0], completedHourValues[0], DateTime.UtcNow.ToOADate(), DateTime.UtcNow.ToOADate());
        var profile = PrtgConsumerProfileFixtureClosure.Publish(Backend, sourceProfile);

        var cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(AnalysisDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.AnalysisTimeZoneId);
        var localCutoff = TimeZoneInfo.ConvertTimeFromUtc(cutoffUtc, zone);
        var latestWallHour = localCutoff.Date.AddHours(localCutoff.Hour).AddHours(-1);
        var canIssueMaintainGrant = (family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory) &&
            authorityMutation != "missing-maintain-permission";
        if (canIssueMaintainGrant)
        {
            var baselineValue = family == PrtgResourceFamily.Memory && memoryValuesAreRemainingPercent ? 5d : 95d;
            store.MergeSampledValues(new[] { latestWallHour.AddHours(-1), latestWallHour }
                .Select(hour => TrustedHour(profile, hour, zone, baselineValue, 4)).ToArray());
            var baselineAssessment = new PrtgResourcePeriodConsumer(Backend, settings)
                .EvaluateBatch([SensorId], DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc), DateTime.UtcNow)
                .Assessments.Single(item => item.SensorObjid == SensorId);
            var authorization = new PrtgResourcePressureAuthorizationService(Backend, settings);
            var trial = authorization.IssueSuccessfulTrial(baselineAssessment, maintainAuthorized: true, DateTime.UtcNow);
            if (!authorization.SetFormalMode(baselineAssessment, trial.TrialResultId, enabled: true,
                    maintainAuthorized: true, DateTime.UtcNow))
                throw new InvalidOperationException("Synthetic valid baseline did not authorize the current resource profile.");
            using var context = Backend.CreateContext();
            var seededBaselineRows = context.PrtgValues.Where(row => row.SensorObjid == SensorId).ToList();
            context.PrtgValues.RemoveRange(seededBaselineRows);
            context.SaveChanges();
            new PrtgResourcePressureHintStore(Backend.Blob(
                PrtgResourcePressureHintStore.BlobKey(host.HostId))).Update(document =>
            {
                document.Items ??= [];
                document.Items.RemoveAll(hint => hint.SensorObjid == SensorId && hint.Family == family);
            });
        }
        var hours = periodMutation switch
        {
            "open-latest-hour" => new[] { latestWallHour.AddHours(-1), latestWallHour.AddHours(1) },
            "missing-latest-hour" => new[] { latestWallHour.AddHours(-1) },
            "gap-between-hours" => new[] { latestWallHour.AddHours(-2), latestWallHour },
            null when completedHourValues.Count == 1 => new[] { latestWallHour },
            null when crossesAnalysisMidnight || crossesHostMidnight =>
                new[] { AnalysisDay.Date.AddHours(23), AnalysisDay.Date.AddDays(1) },
            null => new[] { latestWallHour.AddHours(-1), latestWallHour },
            _ => throw new ArgumentOutOfRangeException(nameof(periodMutation), periodMutation, "Unknown hourly window mutation.")
        };
        store.MergeSampledValues(hours
            .Select((wallHour, index) => TrustedHour(profile, wallHour, zone, completedHourValues[index], goodPhysicalSlotsPerHour)).ToArray());

        if (profileMutation is not null) MutateResourceProfile(profileMutation, family, completedHourValues);
        if (evidenceMutation is not null)
            MutateResourceEvidence(evidenceMutation);
        if (authorityMutation is not null)
            MutateResourceAuthority(authorityMutation, profile, family, completedHourValues);
        if (!formalRuleEnabled) SetFormalResourceRuleEnabled(family, enabled: false);
        return host;
    }

    private void MutateResourceEvidence(string mutation)
    {
        using var context = Backend.CreateContext();
        var rows = context.PrtgValues.Where(row => row.SensorObjid == SensorId).OrderBy(row => row.PeriodStart).ToList();
        if (rows.Count == 0) throw new InvalidOperationException($"No synthetic resource rows exist for {mutation}.");
        foreach (var row in rows)
        {
            if (mutation == "unproved-mean")
            {
                row.TrustVersion = 0;
                row.TrustedProof = null;
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.TrustedProof))
                throw new InvalidOperationException($"{mutation} requires a trusted proof.");
            var proof = PrtgTrustedSampleProof.Deserialize(row.TrustedProof);
            proof = mutation switch
            {
                "duplicate-physical-slot" => proof with
                { Slots = proof.Slots.Select((slot, index) => index == 1 ? slot with { Slot = 0 } : slot).ToArray() },
                "summary-proof-disagrees" => proof,
                "future-samples" or "future-sample" => FutureReceivedProof(row, proof),
                "insufficient-slot-window" => proof with { Slots = proof.Slots.Take(1).ToArray() },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown resource evidence mutation.")
            };
            row.TrustedProof = PrtgTrustedSampleProof.Serialize(proof);
            if (mutation == "summary-proof-disagrees") row.AvgValue += 1;
            if (mutation == "insufficient-slot-window") row.Coverage = 25;
        }
        context.SaveChanges();
        using var verificationContext = Backend.CreateContext();
        var persisted = verificationContext.PrtgValues.Where(row => row.SensorObjid == SensorId).ToArray();
        if (mutation == "unproved-mean" && persisted.Any(row => row.TrustVersion != 0 || row.TrustedProof is not null))
            throw new InvalidOperationException("Unproved-mean mutation was not persisted.");
        if (mutation == "duplicate-physical-slot" && persisted.Any(row =>
                PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).IsStructurallyValid()))
            throw new InvalidOperationException("Duplicate-slot mutation did not invalidate the persisted proof.");
        if (mutation == "summary-proof-disagrees" && persisted.All(row =>
                PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots.Average(slot => slot.Value) == row.AvgValue))
            throw new InvalidOperationException("Summary/proof disagreement mutation was not persisted.");
        if (mutation is "future-samples" or "future-sample")
        {
            var cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(AnalysisDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
            if (persisted.SelectMany(row => PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).Slots)
                .All(slot => slot.ReceivedAt <= cutoffUtc))
                throw new InvalidOperationException("Future-received-sample mutation did not cross the analysis cutoff.");
        }
        if (mutation == "insufficient-slot-window" && persisted.Any(row => row.Coverage != 25))
            throw new InvalidOperationException("Insufficient-slot-window mutation coverage was not persisted.");
    }

    private static PrtgTrustedSampleProof FutureReceivedProof(PrtgValueRow row, PrtgTrustedSampleProof proof)
    {
        var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(proof.AnalysisTimeZoneId);
        var hourEnd = DateTime.SpecifyKind(row.PeriodStart.AddHours(1).AddSeconds(1), DateTimeKind.Unspecified);
        var receivedAfterHourEndUtc = TimeZoneInfo.ConvertTimeToUtc(hourEnd, analysisZone);
        var latestSlot = proof.Slots.Max(slot => slot.Slot);
        var futureProof = proof with
        {
            Slots = proof.Slots.Select(slot => slot.Slot == latestSlot
                ? slot with { ReceivedAt = receivedAfterHourEndUtc }
                : slot).ToArray()
        };
        if (!futureProof.IsStructurallyValid())
            throw new InvalidOperationException("Future-sample mutation must retain a structurally valid slot proof.");
        return futureProof;
    }

    private void MutateResourceAuthority(string mutation, PrtgTrustedSamplingProfile profile,
        PrtgResourceFamily family, IReadOnlyList<double> completedHourValues)
    {
        var policyStore = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var store = Backend.PrtgStore();
        switch (mutation)
        {
            case "channel-generation-drift":
            case "source-generation-drift":
            case "resource-epoch-drift":
            case "resource-generation-drift":
                MutateResourceEvidenceAuthority(mutation);
                break;
            case "live-source-generation-mismatch":
            case "live-resource-generation-mismatch":
            case "live-channel-generation-mismatch":
            case "live-epoch-mismatch":
                MutateLiveResourceIdentity(mutation);
                break;
            case "wrong-host-scope":
                policyStore.Update(policy => policy.HostIds = []);
                break;
            case "paused-sensor":
                var category = family switch
                {
                    PrtgResourceFamily.Cpu => PrtgSensorCategories.Cpu,
                    PrtgResourceFamily.Memory => PrtgSensorCategories.Memory,
                    _ => PrtgSensorCategories.Disk
                };
                store.UpsertSensors([new PrtgSensorRow { Objid = SensorId, DeviceObjid = DeviceId,
                    Name = profile.PrimaryChannelCaption, SensorType = profile.SensorType, Category = category,
                    Status = "Up", Paused = true }], DateTime.Now);
                break;
            case "formal-source-policy-unready":
                policyStore.Update(policy => policy.CoreSystemId = string.Empty);
                break;
            case "trial-profile-drift":
                MutateResourceProfile("trial-profile-drift", family, completedHourValues);
                break;
            case "unverified-time-basis":
                policyStore.Update(policy => policy.TimeBasisEvidenceReference = null);
                break;
            case "missing-maintain-permission":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown resource authority mutation.");
        }
    }

    private void MutateLiveResourceIdentity(string mutation)
    {
        var store = Backend.PrtgStore();
        var identity = store.GetResourceIdentity(SensorId);
        switch (mutation)
        {
            case "live-source-generation-mismatch":
                var sourceGeneration = Guid.Parse("00000000-0000-0000-0000-000000005399").ToString("N");
                new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
                    .Update(policy => policy.SourceGeneration = sourceGeneration);
                store.BindObservedResource(SensorId, identity.HostId, sourceGeneration, identity.ResourceFingerprint);
                break;
            case "live-resource-generation-mismatch":
                store.BindObservedResource(SensorId, identity.HostId, identity.SourceGeneration,
                    identity.ResourceFingerprint + "|live-profile-drift");
                break;
            case "live-channel-generation-mismatch":
                store.SetObservedChannel(SensorId, identity.SourceGeneration,
                    identity.ChannelFingerprint + "|live-profile-drift", identity.Generation);
                break;
            case "live-epoch-mismatch":
                identity.Epoch++;
                Backend.Blob(PrtgResourceIdentityStore.Prefix + SensorId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
                    .Mutate(_ => (JsonSerializer.Serialize(identity), true));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation,
                    "Unknown live resource identity mutation.");
        }
    }

    private void MutateResourceEvidenceAuthority(string mutation)
    {
        using var context = Backend.CreateContext();
        var rows = context.PrtgValues.Where(row => row.SensorObjid == SensorId).ToList();
        foreach (var row in rows)
        {
            var proof = PrtgTrustedSampleProof.Deserialize(row.TrustedProof
                ?? throw new InvalidOperationException($"{mutation} requires trusted physical evidence."));
            var altered = mutation switch
            {
                "source-generation-drift" => proof with { SourceGeneration = proof.SourceGeneration + "-stale" },
                "resource-generation-drift" => proof with { ResourceGeneration = proof.ResourceGeneration + "-stale" },
                "channel-generation-drift" => proof with { ChannelGeneration = proof.ChannelGeneration + "-stale" },
                "resource-epoch-drift" => proof with { ResourceEpoch = proof.ResourceEpoch + "-stale" },
                "trial-profile-drift" => proof with
                {
                    SemanticVersion = new PrtgTrustedSamplingProfileStore(Backend)
                        .GetMany([SensorId])[SensorId].SemanticVersion
                },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation,
                    "Unknown resource evidence authority mutation.")
            };
            if (!row.AvgValue.HasValue)
                throw new InvalidDataException($"{mutation} requires a persisted aggregate value before recontextualizing its proof.");
            row.TrustedProof = Recontextualize(altered, row.AvgValue.Value);
        }
        context.SaveChanges();

        using var verificationContext = Backend.CreateContext();
        var persisted = verificationContext.PrtgValues.Where(row => row.SensorObjid == SensorId).ToArray();
        if (persisted.Length == 0 || persisted.Any(row =>
                !PrtgTrustedSampleProof.Deserialize(row.TrustedProof!).IsStructurallyValid()))
            throw new InvalidOperationException($"{mutation} must preserve valid physical proof structure.");

        string Recontextualize(PrtgTrustedSampleProof proof, double value)
        {
            var slots = mutation == "trial-profile-drift"
                ? proof.Slots.Select(slot => slot with
                {
                    PhysicalIdHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(slot.PhysicalIdHash + "|profile|" + proof.SemanticVersion)))[..32]
                }).ToArray()
                : proof.Slots;
            var first = slots.OrderBy(slot => slot.Slot).First();
            var sample = new PrtgTrustedSample(SensorId, value, proof.SourceGeneration,
                proof.ResourceGeneration, proof.ChannelGeneration, proof.ResourceEpoch,
                proof.SemanticVersion, proof.StrategyVersion, proof.StrategyMinutes,
                proof.StrategyEffectiveFromHour, first.MeasuredAt, first.ReceivedAt,
                proof.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good,
                "synthetic-authority-mutation", proof.RawTimestampTimeZoneId, proof.AnalysisTimeZoneId);
            return PrtgTrustedSampleProof.Serialize(PrtgTrustedSampleProof.From(sample, slots));
        }
    }

    private void MutateResourceProfile(string mutation, PrtgResourceFamily family,
        IReadOnlyList<double> completedHourValues)
    {
        var key = PrtgTrustedSamplingProfile.StorePrefix + SensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var profileStore = new PrtgTrustedSamplingProfileStore(Backend);
        var current = profileStore.GetMany([SensorId])[SensorId];
        var changedProfile = mutation switch
        {
            "wrong-quantity-semantic" => WithDigest(current with
            {
                Quantity = family == PrtgResourceFamily.Disk
                    ? PrtgTrustedQuantitySemantic.CpuLoadPercent
                    : PrtgTrustedQuantitySemantic.DiskFreePercent,
                MetadataDigest = ""
            }),
            "trial-profile-drift" => WithDigest(current with
            { SemanticVersion = current.SemanticVersion + "-trial-drift", MetadataDigest = "" }),
            "wrong-unit" => WithDigest(current with { Unit = "MB", MetadataDigest = "" }),
            "wrong-scale" => WithDigest(current with { Scale = 100d, MetadataDigest = "" }),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown resource profile mutation.")
        };
        // These are adversarial persistence fixtures: retain a valid serialized digest while
        // deliberately disagreeing with the saved explicit binding. The production publisher
        // must reject those mutations, so exercise the consumer's persistence guard directly.
        // The fixed scale=100 case remains unchanged and is not normalized away.
        Backend.Blob(key).Mutate(_ => (JsonSerializer.Serialize(changedProfile), true));

        var persistedJson = JsonNode.Parse(Backend.Blob(key).Read() ?? "null");
        var verified = mutation switch
        {
            "wrong-quantity-semantic" => persistedJson?["Quantity"]?.GetValue<int>() ==
                (int)changedProfile.Quantity,
            "wrong-unit" => persistedJson?["Unit"]?.GetValue<string>() == "MB",
            "wrong-scale" => persistedJson?["Scale"]?.GetValue<double>() == 100d,
            "trial-profile-drift" => persistedJson?["SemanticVersion"]?.GetValue<string>() == changedProfile.SemanticVersion,
            _ => false
        };
        if (!verified) throw new InvalidOperationException($"Persisted profile did not retain '{mutation}'.");

        if (mutation == "trial-profile-drift")
        {
            // Replace the prior semantic proof with two fresh, valid observations under the new
            // profile. Do not reissue or rewrite the successful-trial grant: its old profile
            // fingerprint must fail to authorize the changed semantic context.
            using var context = Backend.CreateContext();
            var oldRows = context.PrtgValues.Where(row => row.SensorObjid == SensorId).ToList();
            context.PrtgValues.RemoveRange(oldRows);
            context.SaveChanges();
            var cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(AnalysisDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(changedProfile.AnalysisTimeZoneId);
            var localCutoff = TimeZoneInfo.ConvertTimeFromUtc(cutoffUtc, zone);
            var latestWallHour = localCutoff.Date.AddHours(localCutoff.Hour).AddHours(-1);
            var hours = completedHourValues.Count == 1
                ? new[] { latestWallHour }
                : new[] { latestWallHour.AddHours(-1), latestWallHour };
            Backend.PrtgStore().MergeSampledValues(hours.Select((hour, index) =>
                TrustedHour(changedProfile, hour, zone, completedHourValues[index], 4)).ToArray());
        }



        static PrtgTrustedSamplingProfile WithDigest(PrtgTrustedSamplingProfile candidate)
        {
            var digestMethod = typeof(PrtgTrustedSamplingProfile).GetMethod(
                "ComputeDigest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Trusted profile digest method is unavailable to the synthetic fixture.");
            var digest = (string?)digestMethod.Invoke(candidate, null)
                ?? throw new InvalidOperationException("Trusted profile digest could not be recomputed.");
            return candidate with { MetadataDigest = digest };
        }
    }

    public PrtgResourcePeriodAssessment? AssessResourcePeriod(PrtgResourceFamily family,
        DateTime? evidenceCutoffUtc = null)
    {
        var settings = new SystemSettingsStore(Backend.Blob("system_settings"));
        var cutoffUtc = evidenceCutoffUtc ?? TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(AnalysisDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        return new PrtgResourcePeriodConsumer(Backend, settings)
            .EvaluateBatch([SensorId], cutoffUtc, DateTime.UtcNow).Assessments
            .SingleOrDefault(item => item.SensorObjid == SensorId && item.Family == family);
    }

    public void SetFormalResourceRuleEnabled(PrtgResourceFamily family, bool enabled)
    {
        var code = family switch
        {
            PrtgResourceFamily.Cpu => PrtgRuleEvaluator.RuleResourceCpuPressure,
            PrtgResourceFamily.Memory => PrtgRuleEvaluator.RuleResourceMemoryPressure,
            PrtgResourceFamily.Disk => PrtgRuleEvaluator.RuleResourceDiskPressure,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        var rules = KnownIssueSeed.CreateRules().Select(rule => rule.PrtgRuleCode == code
            ? rule.CloneForSeedOverwrite(enabled) : rule).ToList();
        new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    public WebHost SeedSilentCase(string resourceStatus = "Unknown", string availabilityStatus = "Up",
        int availabilitySensorCount = 1, DateTime? unknownSince = null)
    {
        var host = SeedCoveredStateCase("F53-Silent-HOST", "Synthetic hardware availability", "Synthetic Hardware",
            resourceStatus, day => [(unknownSince ?? day, resourceStatus)], PrtgSensorCategories.Hardware);
        var store = Backend.PrtgStore();
        store.UpsertSensors([new PrtgSensorRow
        {
            Objid = SilentAvailabilitySensorId, DeviceObjid = DeviceId, Name = "Ping availability",
            SensorType = "Ping", Status = availabilityStatus, Category = PrtgSensorCategories.Availability
        }], DateTime.Now);
        if (availabilitySensorCount > 1)
        {
            store.UpsertSensors([new PrtgSensorRow
            {
                Objid = SilentSecondAvailabilitySensorId, DeviceObjid = DeviceId, Name = "HTTPS availability",
                SensorType = "HTTP", Status = availabilityStatus, Category = PrtgSensorCategories.Availability
            }], DateTime.Now);
        }
        var policyStore = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy => policy.SensorIds = policy.SensorIds
            .Concat(availabilitySensorCount > 1
                ? [SilentAvailabilitySensorId, SilentSecondAvailabilitySensorId] : [SilentAvailabilitySensorId])
            .Distinct().ToList());
        var policy = policyStore.Get();
        var fingerprint = PrtgTimelineResourceIdentity.BuildResourceFingerprint(
            DeviceId.ToString(), "Ping", "synthetic-formal-case-device-sensor", 0);
        var identity = store.BindObservedResource(SilentAvailabilitySensorId, host.HostId, SourceGeneration, fingerprint);
        new PrtgSensorTimelineStore(Backend.Blob(PrtgSensorTimelineStore.Prefix + SilentAvailabilitySensorId)).Update(evidence =>
        {
            evidence.Bind(SilentAvailabilitySensorId, host.HostId, SourceGeneration, fingerprint,
                identity.Generation, identity.Epoch, identity.ChannelGeneration, policy.ValidFrom);
            var from = AnalysisDay.AddDays(-30);
            evidence.Accept(new DateTimeOffset(from), new DateTimeOffset(AnalysisDay.AddDays(1)),
                [new PrtgTimedState(SilentAvailabilitySensorId, new DateTimeOffset(AnalysisDay), availabilityStatus,
                    identity.SourceGeneration, identity.Generation)]);
        });
        if (availabilitySensorCount > 1)
        {
            var secondFingerprint = PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                DeviceId.ToString(), "HTTP", "synthetic-formal-case-device-sensor", 0);
            var secondIdentity = store.BindObservedResource(SilentSecondAvailabilitySensorId, host.HostId,
                SourceGeneration, secondFingerprint);
            new PrtgSensorTimelineStore(Backend.Blob(PrtgSensorTimelineStore.Prefix + SilentSecondAvailabilitySensorId)).Update(evidence =>
            {
                evidence.Bind(SilentSecondAvailabilitySensorId, host.HostId, SourceGeneration, secondFingerprint,
                    secondIdentity.Generation, secondIdentity.Epoch, secondIdentity.ChannelGeneration, policy.ValidFrom);
                var from = AnalysisDay.AddDays(-30);
                evidence.Accept(new DateTimeOffset(from), new DateTimeOffset(AnalysisDay.AddDays(1)),
                    [new PrtgTimedState(SilentSecondAvailabilitySensorId, new DateTimeOffset(AnalysisDay), availabilityStatus,
                        secondIdentity.SourceGeneration, secondIdentity.Generation)]);
            });
        }
        return host;
    }

    /// <summary>
    /// Writes an explicitly synthetic historical proof for testing the bounded consumer of a closed day.
    /// It exercises the real snapshot store and downstream validation, but does not claim a native PRTG read.
    /// The source timestamps and timeline must all belong to AnalysisDay; native capture freshness remains tested separately.
    /// </summary>
    public void StoreSyntheticHistoricalSilentProof(WebHost host, string resourceStatus = "Unknown",
        string availabilityStatus = "Up")
    {
        var hostDay = AnalysisDay.Date;
        if (hostDay >= DateTime.Today)
            throw new InvalidOperationException("Historical silent proof fixture only accepts a closed analysis day.");
        var policy = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
        var localSourceTime = DateTime.SpecifyKind(hostDay.AddHours(23), DateTimeKind.Unspecified);
        var sourceAsOf = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localSourceTime, sourceZone));
        var sourceDay = TimeZoneInfo.ConvertTime(sourceAsOf, sourceZone).Date;
        var sensors = new[]
        {
            new PrtgSilentSensorSnapshot(SensorId, DeviceId, "Synthetic Hardware", PrtgSensorCategories.Hardware,
                resourceStatus, false),
            new PrtgSilentSensorSnapshot(SilentAvailabilitySensorId, DeviceId, "Ping", PrtgSensorCategories.Availability,
                availabilityStatus, false)
        }.OrderBy(sensor => sensor.SensorObjid).ToArray();
        var canonicalInventory = string.Join("\n", sensors.Select(sensor =>
            $"{sensor.SensorObjid}|{sensor.DeviceObjid}|{sensor.SensorType}|{sensor.Category}|{sensor.SourceStatus}|{sensor.Paused}"));
        var inventoryFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalInventory)));
        var mappingFingerprint = PrtgSilentPresenceMappingFingerprint.Compute(DeviceId, host.HostId);
        var snapshot = new PrtgSilentDeviceSnapshot(DeviceId, host.HostId, SourceGeneration, policy.Revision,
            Backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), mappingFingerprint, sourceDay,
            sourceAsOf, sourceAsOf, sourceAsOf, false, sensors.Length, inventoryFingerprint, sensors)
        { SourceAuthorityFingerprint = policy.SourceAuthorityFingerprint(SourceUrl) };
        new PrtgSilentPresenceSnapshotStore(Backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, sourceDay))).Save(snapshot);
    }
    public WebHost SeedDiskTrendCase(int validDays, double currentPercent, double declinePercentPerDay,
        IReadOnlyList<double>? dailyValues = null, IReadOnlyCollection<int>? missingDayOffsets = null,
        string parentStatus = "success", bool includeParent = true, bool trustedHistory = true,
        int profileEffectiveDaysBefore = 40, string? identityDrift = null, int hoursPerDay = 24,
        double? outOfRangePercent = null, int? outOfRangeDayOffset = null, bool futureMeasurements = false,
        int physicalSlotsPerHour = 4, int? duplicatePeriodDayOffset = null)
    {
        if (validDays < 1 || validDays > 35 || currentPercent is < 0 or > 100 || !double.IsFinite(declinePercentPerDay) ||
            (dailyValues is not null && (dailyValues.Count != validDays || dailyValues.Any(value => !double.IsFinite(value) || value is < 0 or > 100))) ||
            (missingDayOffsets is not null && missingDayOffsets.Any(offset => offset < 0 || offset >= validDays)) ||
            profileEffectiveDaysBefore < 0 || hoursPerDay is < 1 or > 24 || physicalSlotsPerHour is < 1 or > 4 ||
            (duplicatePeriodDayOffset is not null && (duplicatePeriodDayOffset < 0 || duplicatePeriodDayOffset >= validDays)) ||
            (outOfRangePercent.HasValue && (!double.IsFinite(outOfRangePercent.Value) || outOfRangePercent.Value is >= 0 and <= 100 ||
                outOfRangeDayOffset is null || outOfRangeDayOffset < 0 || outOfRangeDayOffset >= validDays)))
            throw new ArgumentOutOfRangeException(nameof(validDays), "Synthetic disk trend input is outside the bounded fixture contract.");
        var sensorType = "SNMP Disk Free";
        var host = SeedCoveredStateCase("F53-DiskTrend-HOST", "Disk C: free", sensorType, "Up",
            _ => Array.Empty<(DateTime At, string Status)>(), PrtgSensorCategories.Disk, parentStatus, includeParent);
        var store = Backend.PrtgStore();
        var historyDays = Math.Max(validDays, 1);
        var firstDay = AnalysisDay.AddDays(-(historyDays - 1));
        var now = DateTime.Now;
        // Seed trusted history under a profile effective before every stored sample, then apply
        // the requested too-new profile only after MergeSampledValues has validated each proof.
        var seedProfileDaysBefore = profileEffectiveDaysBefore < historyDays
            ? historyDays + 5 : profileEffectiveDaysBefore;
        var profile = GetDiskTrendProfile(store, host, sensorType, seedProfileDaysBefore);
        var rows = new List<PrtgValueRow>(historyDays * 24);
        for (var offset = 0; offset < historyDays; offset++)
        {
            if (missingDayOffsets?.Contains(offset) == true) continue;
            var hostDay = firstDay.AddDays(offset).Date;
            store.ReplaceHostMapForDate(hostDay, [new PrtgHostMapRow
            {
                DeviceObjid = DeviceId, MapDate = hostDay, HostId = host.HostId, HostName = host.HostName,
                MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            }]);
            var dailyValue = dailyValues is null
                ? currentPercent + (historyDays - 1 - offset) * declinePercentPerDay
                : dailyValues[offset];
            if (outOfRangeDayOffset == offset && outOfRangePercent.HasValue)
                dailyValue = outOfRangePercent.Value;
            for (var hour = 0; hour < hoursPerDay; hour++)
            {
                var localWallHour = DateTime.SpecifyKind(hostDay.AddDays(futureMeasurements ? 10 : 0).AddHours(hour), DateTimeKind.Unspecified);
                var analysisWallHour = TimeZoneInfo.ConvertTimeToUtc(localWallHour, TimeZoneInfo.Local);
                var row = PrtgResourceFixture.TrustedDiskHour(SensorId, analysisWallHour, profile, dailyValue);
                if (!trustedHistory)
                {
                    row.TrustedProof = null;
                    row.TrustVersion = 0;
                }
                if (physicalSlotsPerHour < 4 && row.TrustedProof is not null)
                {
                    var proof = PrtgTrustedSampleProof.Deserialize(row.TrustedProof);
                    row.TrustedProof = PrtgTrustedSampleProof.Serialize(proof with
                        { Slots = proof.Slots.Take(physicalSlotsPerHour).ToArray() });
                    row.Coverage = physicalSlotsPerHour * 25;
                }
                rows.Add(row);
                if (duplicatePeriodDayOffset == offset && hour == 0) rows.Add(row);
            }
        }
        store.MergeSampledValues(rows);
        if (profileEffectiveDaysBefore < historyDays)
        {
            // Model a new trusted-profile authorization after the historical rows were ingested.
            // The strategy store otherwise keeps its first activation hour for the same fingerprint.
            var strategyBlob = Backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey);
            strategyBlob.Mutate(raw =>
            {
                var state = JsonNode.Parse(raw ?? throw new InvalidOperationException(
                    "The historical strategy fence must exist before advancing its activation hour."))!.AsObject();
                var fingerprintKey = state.Select(pair => pair.Key)
                    .Single(key => string.Equals(key, "Fingerprint", StringComparison.OrdinalIgnoreCase));
                state[fingerprintKey] = "stale-synthetic-history-fence";
                return (state.ToJsonString(), true);
            });
            _ = GetDiskTrendProfile(store, host, sensorType, profileEffectiveDaysBefore);
        }
        var identity = store.GetResourceIdentity(SensorId);
        var channelFingerprint = System.Text.Json.JsonSerializer.Serialize(new
        { ChannelIdentifier = "free", ChannelName = "Free Space", Unit = "%", Scale = (double?)1, Direction = "descending-danger" }) +
            "|" + PrtgDiskAssessmentService.ParserSemanticVersion;
        new PrtgDiskSemanticEvidenceStore(Backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).ConfirmManually(
            new PrtgDiskSemanticContext(SensorId, DeviceId, host.HostId, sensorType, "free", "Free Space", "%", 1,
                "descending-danger"), 53, "Synthetic typed evidence for the dedicated formal trend consumer.",
            DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration,
            identity.Generation, identity.ChannelGeneration, identity.Epoch);
        new PrtgDiskVerificationResultStore(Backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(
            new PrtgDiskVerificationResult(SensorId, DeviceId, host.HostId, sensorType, "Verified",
                "Synthetic typed values match the seeded trusted samples.", "free", "Free Space", "%", 1,
                "descending-danger", 3, true, DateTime.UtcNow, AnalysisDay,
                PrtgDiskAssessmentService.ParserSemanticVersion, SourceGeneration: identity.SourceGeneration,
                ResourceGeneration: identity.Generation, ChannelGeneration: identity.ChannelGeneration,
                IdentityEpoch: identity.Epoch));
        var policy = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var revision = Backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        new PrtgSensorTimelineStore(Backend.Blob(PrtgSensorTimelineStore.Prefix + SensorId)).Update(evidence =>
        {
            evidence.Bind(SensorId, host.HostId, identity.SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), sensorType,
                    "synthetic-formal-case-device-sensor", 0), identity.Generation, identity.Epoch,
                identity.ChannelGeneration, policy.ValidFrom);
            evidence.MappingRevision = revision;
            evidence.DiskSemanticValidFrom = policy.ValidFrom;
            evidence.DiskSemanticCheckedAt = DateTimeOffset.UtcNow;
            evidence.DiskSemanticFingerprint = channelFingerprint;
            evidence.Accept(evidence.ValidFrom, DateTimeOffset.Now,
                [new(SensorId, evidence.ValidFrom, "Up", evidence.SourceGeneration, evidence.ResourceGeneration)]);
        });
        ApplyDiskTrendIdentityDrift(identityDrift, store, host);
        SetDiskTrendRuleEnabled(true);
        return host;
    }

    private void ApplyDiskTrendIdentityDrift(string? drift, EfPrtgStore store, WebHost host)
    {
        if (string.IsNullOrWhiteSpace(drift)) return;
        var identity = store.GetResourceIdentity(SensorId);
        switch (drift)
        {
            case "channel":
                store.SetObservedChannel(SensorId, identity.SourceGeneration, "synthetic-drifted-channel");
                break;
            case "source":
                new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
                    policy.SourceGeneration = Guid.Parse("00000000-0000-0000-0000-000000005399").ToString("N"));
                break;
            case "resource":
                store.BindObservedResource(SensorId, host.HostId, identity.SourceGeneration,
                    "synthetic-drifted-resource-identity");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift), drift, "Unknown disk identity mutation.");
        }
    }

    public PrtgDiskAssessmentRow AssessDiskTrendPreview(WebHost host)
        => AssessDiskTrend(host, PrtgDiskDecisionMode.Preview);

    public PrtgDiskAssessmentRow AssessDiskTrendFormal(WebHost host)
        => AssessDiskTrend(host, PrtgDiskDecisionMode.Formal);

    private PrtgDiskAssessmentRow AssessDiskTrend(WebHost host, PrtgDiskDecisionMode mode)
    {
        var settings = new SystemSettingsStore(Backend.Blob("system_settings"));
        var service = new PrtgDiskAssessmentService(Backend.PrtgStore(),
            new HostStore(Backend.Blob("hosts")), settings,
            new PrtgDiskSemanticEvidenceStore(Backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)),
            new PrtgDiskVerificationResultStore(Backend.Blob(PrtgDiskVerificationResultStore.BlobKey)));
        var loadedRules = new KnownIssueRuleStore(Backend.Blob("rules")).Load();
        var rule = (loadedRules.Success ? loadedRules.Content!.Rules : KnownIssueSeed.CreateRules())
            .Single(item => item.Id == "builtin-prtg-disk-free-trend");
        var batch = service.Assess(DateOnly.FromDateTime(AnalysisDay), rule,
            mode, selectedHostIds: [host.HostId], selectedSensorObjids: [SensorId]);
        return batch.Rows.Single(row => row.SensorObjid == SensorId);
    }

    private PrtgTrustedSamplingProfile GetDiskTrendProfile(EfPrtgStore store, WebHost host, string sensorType,
        int effectiveDaysBefore)
    {
        var effectiveWallHour = DateTime.SpecifyKind(
            AnalysisDay.Date.AddDays(-effectiveDaysBefore), DateTimeKind.Unspecified);
        var effectiveHour = TimeZoneInfo.ConvertTimeToUtc(effectiveWallHour, TimeZoneInfo.Local);
        return PrtgResourceFixture.ConfigureDiskTrustedProfile(store,
            Backend.Blob(PrtgMonitoringPolicyStore.BlobKey),
            Backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey), SensorId, DeviceId, host.HostId,
            sensorType, effectiveHour, creationReference: "synthetic-formal-case-device-sensor",
            sourceGeneration: SourceGeneration, channelCaption: "Free Space", preserveIdentity: true);
    }

    public void SetDiskTrendRuleEnabled(bool enabled)
    {
        var rules = KnownIssueSeed.CreateRules().Select(rule => rule.Id == "builtin-prtg-disk-free-trend"
            ? rule.CloneForSeedOverwrite(enabled) : rule).ToList();
        new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }

    public async Task<PrtgSilentDeviceSnapshot> CaptureSyntheticSilentSourceAsync(WebHost host,
        DateTimeOffset sourceAsOf, DateTimeOffset? deviceStatusAsOf = null,
        string resourceStatus = "Unknown", string availabilityStatus = "Up", int availabilitySensorCount = 1)
    {
        var policy = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
        var sourceDay = TimeZoneInfo.ConvertTime(sourceAsOf, sourceZone).Date;
        using var client = new PrtgClient(SourceUrl, "synthetic-fixture-token", 5, false,
            new SilentSourceHandler(sourceAsOf, deviceStatusAsOf, resourceStatus, availabilityStatus,
                availabilitySensorCount), PrtgAuthModes.Token, "", "", "");
        var store = new PrtgSilentPresenceSnapshotStore(Backend.Blob(
            PrtgSilentPresenceSnapshotStore.BlobKey(DeviceId, sourceDay)));
        return await new PrtgSilentPresenceCapture(Backend).CaptureDeviceAsync(client, store, policy,
            SourceUrl, DeviceId, host.HostId, sourceDay,
            PrtgSilentPresenceMappingFingerprint.Compute(DeviceId, host.HostId),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
    }

    private static PrtgValueRow TrustedHour(PrtgTrustedSamplingProfile profile, DateTime wallHour,
        TimeZoneInfo analysisZone, double value, int goodPhysicalSlots = 4)
    {
        wallHour = DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified);
        var instantHour = TimeZoneInfo.ConvertTimeToUtc(wallHour, analysisZone);
        var slots = Enumerable.Range(0, goodPhysicalSlots).Select(slot =>
        {
            var measuredAt = instantHour.AddMinutes(slot * profile.StrategyMinutes);
            var material = $"{profile.SensorObjid}|{wallHour:O}|{slot}";
            var physicalId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(material)))[..32];
            return new PrtgTrustedSampleSlot(slot, physicalId, measuredAt, measuredAt.AddSeconds(10), value);
        }).ToArray();
        var sample = new PrtgTrustedSample(profile.SensorObjid, value, profile.SourceGeneration,
            profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, slots[0].MeasuredAt, slots[0].ReceivedAt,
            profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "synthetic-physical-hour",
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new PrtgValueRow
        {
            SensorObjid = profile.SensorObjid, PeriodStart = wallHour,
            AvgValue = value, MinValue = value, MaxValue = value,
            Coverage = goodPhysicalSlots * 100.0 / (60 / profile.StrategyMinutes),
            Quality = PrtgDataQuality.Sampled, TrustVersion = 1,
            TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
        };
    }

    public async Task<(PrtgFindingsRegistry Registry, IReadOnlyList<string> ConsoleLines)> RunDailyAsync(
        WebHost host, DateTime day, bool captureWorkflowReceipt = false)
    {
        var hostStore = new HostStore(Backend.Blob("hosts"));
        var coordinator = new IssueCaseCoordinator(Backend.IssueCaseStore(), Backend.IssueHandlingStore(),
            Backend.RecordHandlingStore(), Backend.RecordStore(), hostStore,
            new IssueOwnerStore(Backend.Blob("issue_owners")));
        var dispatch = NightlyDispatchFakes.Create(Backend.IssueCaseStore(), Backend.IssueHandlingStore(),
            Backend.RecordHandlingStore(), hostStore, new IssueOwnerStore(Backend.Blob("issue_owners")), coordinator);
        var recorder = new BatchRunRecorder(new BatchRunStore(Backend.LogStore("batch_runs"),
            Backend.LogStore("batch_run_logs")), "f53-formal-case-fixture", Array.Empty<string>());
        var console = new CapturingConsole();
        HostDayWorkflowService? workflow = null;
        if (captureWorkflowReceipt)
        {
            var parentRecord = Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
                .ReadRecent(day, 1).Single();
            workflow = new HostDayWorkflowService(new HostDayWorkflowStore(Backend));
            workflow.ParentSucceeded(host.HostId, host.HostName, day, "synthetic-formal-case-parent",
                parentRecord.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(parentRecord),
                prtgEnabled: true, aiEnabled: false, parentRecordId: parentRecord.RecordId);
        }
        var context = new AnalysisRunContext(new RunRequest(), new AppSettings(), new RetentionOptions(), console,
            CancellationToken.None, new EventLogService(), coordinator, Backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), false, null, _registry, dispatch, workflow, captureWorkflowReceipt);
        await PrtgDailyPipeline.RunAsync(context, Backend, hostStore, [day], Task.CompletedTask,
            hostIds: [host.HostId], guard: null, structureSyncGate: new CompletedStructureSyncGate());
        return (_registry, console.Lines);
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class CapturingConsole : IRunConsole
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class CompletedStructureSyncGate : IPrtgStructureSyncGate
    {
        public bool IsRunning => true;
        public Task<bool> WaitUntilIdleAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class SilentSourceHandler(DateTimeOffset sourceAsOf, DateTimeOffset? deviceStatusAsOf,
        string resourceStatus, string availabilityStatus, int availabilitySensorCount) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var devices = request.RequestUri!.Query.Contains("content=devices", StringComparison.Ordinal);
            string payload;
            if (devices)
                payload = JsonSerializer.Serialize(new { treesize = 1, devices = new[] { new { objid = DeviceId, paused = false } } });
            else
            {
                var sensors = new List<object>
                {
                    new { objid = SensorId, parentid = DeviceId, type = "Synthetic Hardware", status = resourceStatus,
                        status_raw = resourceStatus == "Up" ? "3" : "0", paused = false },
                    new { objid = SilentAvailabilitySensorId, parentid = DeviceId, type = "Ping", status = availabilityStatus,
                        status_raw = availabilityStatus == "Up" ? "3" : "0", paused = false }
                };
                if (availabilitySensorCount > 1)
                    sensors.Add(new { objid = SilentSecondAvailabilitySensorId, parentid = DeviceId, type = "HTTP",
                        status = availabilityStatus, status_raw = availabilityStatus == "Up" ? "3" : "0", paused = false });
                payload = JsonSerializer.Serialize(new { treesize = sensors.Count, sensors });
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            response.Headers.Date = devices ? deviceStatusAsOf ?? sourceAsOf : sourceAsOf;
            return Task.FromResult(response);
        }
    }
}

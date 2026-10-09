using System.Diagnostics;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

public sealed class SourceEvidenceTests
{
    [Fact]
    public void Sentinel_provenance_keeps_UTC_host_and_original_message_fingerprint_but_no_unproven_reference()
    {
        var originalMessage = new string('x', 3000);
        var fields = new Dictionary<string, string>
        {
            [SentinelFieldMap.Timestamp] = "2026-10-06T01:02:03Z",
            [SentinelFieldMap.HostIp] = "192.0.2.10",
            [SentinelFieldMap.LogName] = "System",
            [SentinelFieldMap.Source] = "disk",
            [SentinelFieldMap.EventId] = "153",
            [SentinelFieldMap.Severity] = "4",
            [SentinelFieldMap.Message] = originalMessage
        };

        var mapped = SentinelEventMapper.Map(new SentinelEvent(fields));
        Assert.NotNull(mapped);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero), mapped!.SourceEvidence!.EventTimeUtc);
        Assert.Equal(SourceResourceScope.Host, mapped.SourceEvidence.ResourceScope);
        Assert.Equal("host-ip:192.0.2.10", mapped.SourceEvidence.ExactHostKey);
        Assert.Null(mapped.SourceEvidence.SourceReference);
        Assert.Equal(SourceReferenceQuality.Unknown, mapped.SourceEvidence.SourceReferenceQuality);
        Assert.Equal(SourceEvidence.Fingerprint(fields.OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => new[] { p.Key, p.Value }).ToArray()), mapped.SourceEvidence.ProjectionFingerprint);

        var signature = new LogIssueSignature
        {
            LogName = mapped.LogName, Source = mapped.Source, EventId = mapped.EventId,
            EntryType = mapped.EntryType, RuleId = "test-rule"
        };
        var selected = RiskyEventSelector.SelectSourceEvents(new() { signature }, new() { mapped });
        Assert.Single(selected);
        Assert.True(selected[0].Message.Length < originalMessage.Length);
        Assert.Equal(mapped.SourceEvidence.ProjectionFingerprint, selected[0].SourceEvidence!.ProjectionFingerprint);
        Assert.Null(selected[0].SourceEvidence.SourceReference);

        var retained = RiskyEventSelector.Select(new() { signature }, new() { mapped }, hostId: 4, DateTime.Today);
        Assert.Single(retained);
        Assert.True(retained[0].Message.Length < originalMessage.Length);
        Assert.Equal(mapped.SourceEvidence.ProjectionFingerprint, retained[0].SourceEvidence!.ProjectionFingerprint);
    }

    [Fact]
    public void Aggregate_and_storage_shaper_keep_bounded_observations_and_visible_truncation()
    {
        var logs = Enumerable.Range(0, 100).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(i), LogName = "System", Source = "disk",
            EventId = 153, EntryType = EventLogEntryType.Error,
            SourceEvidence = new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.LocalEventRecord,
                ResourceScope = SourceResourceScope.Host,
                ExactResourceKey = "host:node-a", ExactHostKey = "host:node-a",
                EventTimeUtc = DateTimeOffset.UtcNow.AddMinutes(i),
                SourceReference = $"event-record:node-a:System:{i}",
                SourceReferenceQuality = SourceReferenceQuality.ExactNative,
                ProjectionFingerprint = SourceEvidence.Fingerprint(i.ToString())
            }
        }).ToList();

        var signature = Assert.Single(LogAggregator.Aggregate(logs));
        Assert.True(signature.SourceObservations.Count <= 64);
        Assert.True(signature.SourceObservationsTruncated);
        var json = SourceEvidence.SerializeObservations(signature.SourceObservations);
        Assert.NotNull(json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= SourceEvidence.MaximumSerializedBytes);
        Assert.Equal(signature.SourceObservations.Count, SourceEvidence.DeserializeObservations(json).Count);

        var lowRisk = new DailyAnalysisRecord
        {
            RiskLevel = RiskLevels.Low,
            TopIssues = new() { signature }
        };
        var stored = RecordStorageShaper.ForStorage(lowRisk).TopIssues.Single();
        Assert.Equal(signature.SourceObservations.Count, stored.SourceObservations.Count);
        Assert.True(stored.SourceObservationsTruncated);
    }

    [Fact]
    public void Exact_volume_match_requires_same_host_resource_relation_UTC_overlap_and_both_native_refs()
    {
        var start = new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var eventProof = ExactVolume("host-id:7", "volume:C:", start, end, "event-record:machine:System:100",
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.LocalEventRecord);
        var prtgProof = ExactVolume("host-id:7", "volume:C:", start.AddMinutes(15), end.AddMinutes(15),
            "prtg:device:70:sensor:700:epoch:2", SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg);

        Assert.Equal("exact-resource-window-source-references", SourceEvidenceMatcher.Match(eventProof, prtgProof).ReasonCode);
        Assert.True(SourceEvidenceMatcher.Match(eventProof, prtgProof).SameResourceAndWindow);
        Assert.Equal("resource-identity-mismatch", SourceEvidenceMatcher.Match(eventProof,
            ExactVolume("host-id:7", "volume:D:", start, end, "prtg:device:70:sensor:700", SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg)).ReasonCode);
        Assert.Equal("host-identity-mismatch", SourceEvidenceMatcher.Match(eventProof,
            ExactVolume("host-id:8", "volume:C:", start, end, "prtg:device:80:sensor:800", SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg)).ReasonCode);
        Assert.Equal("utc-window-unavailable-or-no-overlap", SourceEvidenceMatcher.Match(eventProof,
            ExactVolume("host-id:7", "volume:C:", end.AddHours(1), end.AddHours(2), "prtg:device:70:sensor:700",
                SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg)).ReasonCode);
        Assert.Equal("signal-pair-unverified", SourceEvidenceMatcher.Match(eventProof,
            ExactVolume("host-id:7", "volume:C:", start, end, "prtg:device:70:sensor:700",
                SourceEvidenceRelation.CapacityToDiskSensor, SourceEvidenceKind.Prtg)).ReasonCode);

        var unknownReference = ExactVolume("host-id:7", "volume:C:", start, end, null,
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg);
        unknownReference.SourceReferenceQuality = SourceReferenceQuality.Unknown;
        Assert.Equal("source-reference-unavailable", SourceEvidenceMatcher.Match(eventProof, unknownReference).ReasonCode);

        var noWindow = ExactVolume("host-id:7", "volume:C:", start, end, "prtg:device:70:sensor:700",
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg);
        noWindow.WindowStartUtc = null;
        noWindow.WindowEndUtc = null;
        Assert.Equal("utc-window-unavailable-or-no-overlap", SourceEvidenceMatcher.Match(eventProof, noWindow).ReasonCode);

        var instantProof = new SourceEvidence
        {
            SourceKind = SourceEvidenceKind.LocalEventRecord,
            RelationContract = SourceEvidenceRelation.DiskIoToHardware,
            ResourceScope = SourceResourceScope.Volume,
            ExactHostKey = "host-id:7", ExactResourceKey = "volume:C:",
            EventTimeUtc = start.AddMinutes(30),
            SourceReference = "event-record:machine:System:101", SourceReferenceQuality = SourceReferenceQuality.ExactNative
        };
        Assert.True(SourceEvidenceMatcher.Match(instantProof, prtgProof).SameResourceAndWindow);
        instantProof.EventTimeUtc = prtgProof.WindowEndUtc; // the actual half-open PRTG interval ends fifteen minutes after the event fixture's interval.
        Assert.Equal("utc-window-unavailable-or-no-overlap", SourceEvidenceMatcher.Match(instantProof, prtgProof).ReasonCode);

        var wrongSourceKind = ExactVolume("host-id:7", "volume:C:", start, end, "other:ref",
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Unknown);
        Assert.Equal("signal-pair-unverified", SourceEvidenceMatcher.Match(eventProof, wrongSourceKind).ReasonCode);
    }

    [Fact]
    public void Projection_fingerprint_never_satisfies_source_reference_requirement()
    {
        var start = new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        var left = ExactVolume("host-id:7", "volume:C:", start, start.AddHours(1), null,
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.LocalEventRecord);
        var right = ExactVolume("host-id:7", "volume:C:", start, start.AddHours(1), null,
            SourceEvidenceRelation.DiskIoToHardware, SourceEvidenceKind.Prtg);
        left.ProjectionFingerprint = SourceEvidence.Fingerprint("same-fields");
        right.ProjectionFingerprint = left.ProjectionFingerprint;
        Assert.Equal("source-reference-unavailable", SourceEvidenceMatcher.Match(left, right).ReasonCode);
    }

    [Fact]
    public void Sql_content_json_and_normalized_lightweight_issue_round_trip_the_same_bounded_provenance()
    {
        using var fixture = new EfSqliteFixture();
        var store = new EfAnalysisRecordStore(fixture.NewContext, "source-evidence-sql");
        var date = new DateTime(2026, 10, 6);
        var observations = Enumerable.Range(0, 8).Select(i => new SourceEvidence
        {
            SourceKind = SourceEvidenceKind.LocalEventRecord,
            ResourceScope = SourceResourceScope.Host,
            ExactHostKey = "host-id:77", ExactResourceKey = "host-id:77",
            EventTimeUtc = new DateTimeOffset(2026, 10, 6, 1, i, 0, TimeSpan.Zero),
            SourceReference = $"event-record:node:System:{i}",
            SourceReferenceQuality = SourceReferenceQuality.ExactNative,
            ProjectionFingerprint = SourceEvidence.Fingerprint(i.ToString())
        }).ToList();
        var record = new DailyAnalysisRecord
        {
            HostId = 77, Host = "node", Date = date, RiskLevel = RiskLevels.Low,
            TopIssues = new()
            {
                new LogIssueSignature
                {
                    LogName = "System", Source = "disk", EventId = 153,
                    EntryType = EventLogEntryType.Error, Count = observations.Count,
                    SourceObservations = observations
                }
            }
        };

        store.Append(record);

        var detailed = store.GetOne(new[] { new HostKey { HostId = 77, HostName = "node" } }, date)!;
        var lightweight = store.QueryWorkflowRecoveryPage(date, date, afterRecordId: 0, take: 10).Records.Single();
        var expected = SourceEvidence.SerializeObservations(observations);
        Assert.Equal(expected, SourceEvidence.SerializeObservations(detailed.TopIssues.Single().SourceObservations));
        Assert.Equal(expected, SourceEvidence.SerializeObservations(lightweight.TopIssues.Single().SourceObservations));
    }

    [Fact]
    public void Risky_event_sql_retains_provenance_when_message_is_truncated()
    {
        using var fixture = new EfSqliteFixture();
        var store = new EfRiskyEventStore(fixture.NewContext);
        var date = new DateTime(2026, 10, 6);
        var evidence = new SourceEvidence
        {
            SourceKind = SourceEvidenceKind.LocalEventRecord,
            ResourceScope = SourceResourceScope.Host,
            ExactHostKey = "host-id:77", ExactResourceKey = "host-id:77",
            EventTimeUtc = new DateTimeOffset(2026, 10, 6, 1, 2, 3, TimeSpan.Zero),
            SourceReference = "event-record:node:System:42",
            SourceReferenceQuality = SourceReferenceQuality.ExactNative,
            ProjectionFingerprint = SourceEvidence.Fingerprint(new string('m', 2600))
        };
        store.ReplaceDay(77, date, new()
        {
            new RiskyEvent
            {
                HostId = 77, Date = date, LogName = "System", Source = "disk", EventId = 153,
                EntryType = EventLogEntryType.Error, EventTime = date.AddHours(1),
                Message = new string('m', 2000), SourceEvidence = evidence
            }
        });

        var loaded = Assert.Single(store.Query(77, date, "disk", 153, 5));
        Assert.Equal(evidence.SourceReference, loaded.SourceEvidence!.SourceReference);
        Assert.Equal(evidence.ProjectionFingerprint, loaded.SourceEvidence.ProjectionFingerprint);
    }

    private static SourceEvidence ExactVolume(string host, string volume, DateTimeOffset start,
        DateTimeOffset end, string? sourceReference, SourceEvidenceRelation relation,
        SourceEvidenceKind kind = SourceEvidenceKind.Unknown) => new()
    {
        SourceKind = kind,
        RelationContract = relation,
        ResourceScope = SourceResourceScope.Volume,
        ExactHostKey = host,
        ExactResourceKey = volume,
        WindowStartUtc = start,
        WindowEndUtc = end,
        SourceReference = sourceReference,
        SourceReferenceQuality = sourceReference == null ? SourceReferenceQuality.Unknown : SourceReferenceQuality.ExactNative
    };
}

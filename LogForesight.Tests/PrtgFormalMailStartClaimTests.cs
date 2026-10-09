using LogForesight.Core.Analysis;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Actual MailNotificationService recipient-start fencing against SQLite and Fake SMTP.</summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgFormalMailStartClaimTests
{
    [Fact]
    public async Task ModeOffBeforeUrgentStartRevokesOnlyTheFormalFindingAndKeepsBaselineRecord()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true, highRiskNetIqBaseline: true);
        fixture.DisableMode();

        await fixture.Service.NotifyAfterRunAsync();

        var intent = Assert.Single(fixture.MailState.Get().UrgentOutbox.Values);
        Assert.Empty(intent.FormalStartFenceRefs);
        Assert.Contains(intent.FormalIssueStates, pair => pair.Value == "revoked");
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "revoked");
        Assert.Empty(fixture.MailState.Get().FormalMailStartClaims);
        Assert.Contains(fixture.ReloadParent().TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline");
        Assert.Single(fixture.Sender.Attempts); // the non-pressure baseline remains eligible for delivery.
    }

    [Fact]
    public async Task ModeOffAfterDurableUrgentStartMayFinishAndPreservesAcceptedResult()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true);
        fixture.Sender.OnSend = _ => fixture.DisableMode();

        await fixture.Service.NotifyAfterRunAsync();

        Assert.Single(fixture.Sender.Attempts);
        var state = fixture.MailState.Get();
        var intent = Assert.Single(state.UrgentOutbox.Values);
        var reference = Assert.Single(intent.FormalStartFenceRefs).Value;
        Assert.Equal(fixture.Host.HostId, reference.HostId);
        Assert.Equal(PrtgFormalMailClaimStore.BlobKey(fixture.Host.HostId, fixture.ReloadParent().Date,
            "urgent", reference.FenceKey, Fixture.Recipient), reference.ShardKey);
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "smtp-accepted" && claim.SmtpAcceptedAtUtc.HasValue);
        Assert.Contains(intent.FormalIssueStates, pair => pair.Value == "smtp-accepted");
        Assert.Empty(state.FormalMailStartClaims);
        Assert.False(fixture.ModeStore.ReadHostSnapshot(fixture.Host.HostId).Grants.Single().FormalEnabled);
    }

    [Fact]
    public async Task ReenabledModeCannotReviveAnUrgentRetryWithItsOldParentFence()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true, highRiskNetIqBaseline: true);
        fixture.Sender.ThrowOnSend = new InvalidOperationException("synthetic ambiguous SMTP result");

        await fixture.Service.NotifyAfterRunAsync();

        var firstIntent = Assert.Single(fixture.MailState.Get().UrgentOutbox.Values);
        var originalReference = Assert.Single(firstIntent.FormalStartFenceRefs).Value;
        Assert.Contains(firstIntent.FormalIssueStates, pair => pair.Value == "failed-or-unknown");
        fixture.Sender.ThrowOnSend = null;
        fixture.DisableMode();
        fixture.ReenableSameGrant();

        await fixture.Service.RetryPendingUrgentAsync();

        var retriedIntent = Assert.Single(fixture.MailState.Get().UrgentOutbox.Values);
        Assert.Equal(originalReference, Assert.Single(retriedIntent.FormalStartFenceRefs).Value);
        Assert.Contains(retriedIntent.FormalIssueStates, pair => pair.Value == "revoked");
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "revoked");
        Assert.Equal(2, fixture.Sender.Attempts.Count); // retry may carry baseline content, never the revoked pressure finding.
    }

    [Fact]
    public async Task OldQueuedIntentWithMissingExactParentRemainsVisibleAndDoesNotSendNewAlert()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true);
        await fixture.Service.NotifyAfterRunAsync();
        var attemptsBeforeOldRetry = fixture.Sender.Attempts.Count;
        var oldIntentKey = "old-retry|parent:999999";
        var oldDate = fixture.ReloadParent().Date.AddDays(-30);
        var settingsRevision = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().Revision;
        fixture.MailState.Update(state => state.UrgentOutbox[oldIntentKey] = new MailUrgentIntent
        {
            Key = oldIntentKey, HostId = fixture.Host.HostId, ParentRecordId = 999999, RecordDate = oldDate,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30), UpdatedAtUtc = DateTime.UtcNow.AddDays(-29),
            SettingsRevision = settingsRevision, Status = "pending", ProblemKeys = ["prtg:resource_cpu_sustained_pressure:53002"]
        });

        await fixture.Service.RetryPendingUrgentAsync();

        var oldIntent = fixture.MailState.Get().UrgentOutbox[oldIntentKey];
        Assert.Equal("parent-record-missing", oldIntent.WaitingReasonCode);
        Assert.NotNull(oldIntent.WaitingSinceUtc);
        Assert.Equal(attemptsBeforeOldRetry, fixture.Sender.Attempts.Count);
    }

    [Fact]
    public async Task ExistingOldUrgentIntentRetriesSameParentAndFenceAfterUnknownSmtpOutcome()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, analysisDay: DateTime.Today.AddDays(-30), elevateCpuFormalRisk: true);
        var parent = fixture.ReloadParent();
        var exactParent = fixture.Backend.RecordStore().LookupByRecordId(parent.RecordId);
        Assert.True(exactParent.Record is not null && HostDayWorkflowFingerprint.HasValidPrtgManifest(exactParent.Record));
        Assert.Equal(parent.Date.Kind, exactParent.Record!.Date.Kind);
        var issue = Assert.Single(parent.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue));
        var issueKey = IssueSignatureKey.For(issue) + "|" + issue.EventKey + "|" + issue.PrtgChannelGeneration;
        var originalKey = $"queued-legacy-urgent|parent:{parent.RecordId}";
        var settingsRevision = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get().Revision;
        fixture.MailState.Update(state => state.UrgentOutbox[originalKey] = new MailUrgentIntent
        {
            Key = originalKey, HostId = parent.HostId, ParentRecordId = parent.RecordId, RecordDate = parent.Date,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30), UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            SettingsRevision = settingsRevision, Status = "pending", ProblemKeys = parent.TopIssues.Select(item => item.EventKey).ToList(),
            Recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["formal-mail@test.local"] = "pending" }
        });
        fixture.Sender.ThrowOnSend = new InvalidOperationException("synthetic unknown SMTP result");

        await fixture.Service.NotifyAfterRunAsync();

        var afterUnknown = fixture.MailState.Get().UrgentOutbox[originalKey];
        var originalReference = Assert.Single(afterUnknown.FormalStartFenceRefs).Value;
        Assert.StartsWith($"{parent.RecordId}|mode:", originalReference.FenceKey, StringComparison.Ordinal);
        Assert.Equal(PrtgFormalMailClaimStore.BlobKey(parent.HostId, parent.Date, "urgent",
            originalReference.FenceKey, "formal-mail@test.local"), originalReference.ShardKey);
        Assert.Contains(afterUnknown.FormalIssueStates, pair => pair.Value == "failed-or-unknown");
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "failed-or-unknown");
        Assert.Equal(1, fixture.Sender.Attempts.Count);

        fixture.Sender.ThrowOnSend = null;
        await fixture.Service.RetryPendingUrgentAsync();

        var accepted = fixture.MailState.Get().UrgentOutbox[originalKey];
        Assert.Equal(originalKey, accepted.Key);
        Assert.Equal(parent.RecordId, accepted.ParentRecordId);
        Assert.Equal(originalReference, Assert.Single(accepted.FormalStartFenceRefs).Value);
        Assert.Equal("smtp-accepted", accepted.Status);
        Assert.Contains(accepted.FormalIssueStates, pair => pair.Value == "smtp-accepted");
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "smtp-accepted");
        Assert.Equal(2, fixture.Sender.Attempts.Count);
    }

    [Fact]
    public async Task LegacyUrgentFenceIsPreservedAndFailsClosedWithoutShardRecapture()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true);
        using (var context = fixture.Backend.CreateContext())
            context.Database.ExecuteSqlRaw("CREATE TRIGGER reject_formal_mail_start BEFORE INSERT ON lf_blobs WHEN NEW.blob_key LIKE 'prtg_formal_mail_claims_v3_%' BEGIN SELECT RAISE(ABORT, 'claim rejected'); END;");
        try
        {
            await fixture.Service.NotifyAfterRunAsync();
        }
        finally
        {
            using var cleanup = fixture.Backend.CreateContext();
            cleanup.Database.ExecuteSqlRaw("DROP TRIGGER IF EXISTS reject_formal_mail_start;");
        }
        Assert.Empty(fixture.Sender.Attempts);
        var parent = fixture.ReloadParent();
        var issue = Assert.Single(parent.TopIssues.Where(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue));
        var issueKey = IssueSignatureKey.For(issue) + "|" + issue.EventKey + "|" + issue.PrtgChannelGeneration;
        var legacyFence = TestFence(fixture.Host.HostId) with
        {
            ParentRecordId = parent.RecordId,
            ResourceModeBlobVersion = parent.PrtgManifest!.ResourceModeBlobVersion
        };
        fixture.MailState.Update(state =>
            Assert.Single(state.UrgentOutbox.Values).FormalStartFences[issueKey] = legacyFence);

        await fixture.Service.NotifyAfterRunAsync();

        var intent = Assert.Single(fixture.MailState.Get().UrgentOutbox.Values);
        Assert.Equal(legacyFence, intent.FormalStartFences[issueKey]);
        Assert.Empty(intent.FormalStartFenceRefs);
        Assert.Contains(intent.FormalIssueStates, pair => pair.Value == "revoked");
        Assert.Empty(fixture.MailState.Get().FormalMailStartClaims);
        Assert.Null(fixture.ReadShardIfPresent());
        Assert.Empty(fixture.Sender.Attempts);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("daily-digest")]
    public async Task SummaryAndDigestCountTheStoredBaselineAfterPressureRevocation(string lane)
    {
        using var fixture = await Fixture.CreateAsync(urgent: false, summary: lane == "summary", dailyDigest: lane == "daily-digest",
            elevateCpuFormalRisk: true, highRiskNetIqBaseline: true);
        fixture.DisableMode();

        if (lane == "summary") await fixture.Service.NotifyAfterRunAsync();
        else await fixture.Service.CheckAndSendDailyWeeklyAsync(
            fixture.ReloadParent().Date.Date.AddDays(1).AddHours(12));

        var message = Assert.Single(fixture.Sender.Attempts);
        Assert.Equal(RiskLevels.High, fixture.ReloadParent().PrtgBaselineRiskLevel);
        Assert.Contains(lane == "summary" ? "1 台主機達 高 風險以上" : "1 個主機日達 高 風險以上",
            message.Body, StringComparison.Ordinal);
        Assert.Contains("Synthetic NetIQ disk baseline/53053", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_cpu_sustained_pressure", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.ReadShard().Claims.Values, claim => claim.Status == "revoked");
    }

    [Fact]
    public async Task StatsOnlyRecipientDoesNotCreateAFormalStartClaimForHiddenFinding()
    {
        using var fixture = await Fixture.CreateAsync(urgent: false, summary: true, statsOnly: true, elevateCpuFormalRisk: true, highRiskNetIqBaseline: true);
        fixture.DisableMode();

        await fixture.Service.NotifyAfterRunAsync();

        Assert.Single(fixture.Sender.Attempts);
        Assert.Empty(fixture.MailState.Get().FormalMailStartClaims);
        Assert.Null(fixture.ReadShardIfPresent());
        Assert.DoesNotContain("resource_cpu_sustained_pressure", fixture.Sender.Attempts[0].Body,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatsOnlyRecipientKeepsAValidFormalFindingInRiskCountsWithoutCreatingAClaim()
    {
        using var fixture = await Fixture.CreateAsync(urgent: false, summary: true, statsOnly: true, elevateCpuFormalRisk: true);
        await fixture.Service.NotifyAfterRunAsync();

        var message = Assert.Single(fixture.Sender.Attempts);
        Assert.Contains("1 台主機達 高 風險以上", message.Body, StringComparison.Ordinal);
        Assert.Null(fixture.ReadShardIfPresent());
        Assert.Empty(fixture.MailState.Get().FormalMailStartClaims);
    }

    [Fact]
    public async Task RevokedCpuFilteringKeepsRiskFromSurvivingDiskFinding()
    {
        using var fixture = await Fixture.CreateAsync(urgent: false, summary: true, elevateCpuFormalRisk: true);
        var record = fixture.ReloadParent();
        record.PrtgBaselineRiskLevel = RiskLevels.Low;
        record.PrtgBaselineRiskBasis = "netiq-baseline";
        record.TopIssues.Add(new LogIssueSignature
        {
            LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:disk_free_trend", EventId = 0,
            Category = IssueCategory.Storage, Severity = IssueSeverity.High, ElevatesDayRisk = true,
            EventKey = "prtg:disk_free_trend:53002"
        });
        var filter = typeof(MailNotificationService).GetMethod("FilterFormalIssues",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var filtered = (DailyAnalysisRecord)filter.Invoke(null,
            [record, new HashSet<string>(StringComparer.Ordinal), suppressed])!;

        Assert.DoesNotContain(filtered.TopIssues, PrtgResourceFormalDeliveryFence.IsTargetPressureIssue);
        Assert.Contains(filtered.TopIssues, issue => issue.Source == "PRTG:disk_free_trend");
        Assert.Equal(RiskLevels.High, filtered.RiskLevel);
        Assert.Equal("prtg:disk_free_trend", filtered.RiskBasis);
    }

    [Fact]
    public async Task NetIqOnlyMailBypassesTheFormalClaimBlobBound()
    {
        using var fixture = await Fixture.CreateAsync(urgent: false, summary: true, highRiskNetIqBaseline: true);
        fixture.RemoveFormalPressureFromParent();
        var netIqOnlyParent = fixture.ReloadParent();
        Assert.Equal(RiskLevels.High, netIqOnlyParent.RiskLevel);
        var netIqBaseline = Assert.Single(netIqOnlyParent.TopIssues.Where(issue => issue.Source == "Synthetic NetIQ disk baseline"));
        Assert.False(netIqBaseline.Suppressed);
        Assert.Equal("success", netIqOnlyParent.LatestNetiqAttemptStatus);
        Assert.DoesNotContain(netIqOnlyParent.TopIssues, PrtgResourceFormalDeliveryFence.IsTargetPressureIssue);
        Assert.DoesNotContain($"{netIqOnlyParent.HostId}|{netIqOnlyParent.Date:yyyy-MM-dd}",
            fixture.MailState.Get().SummarySentKeys);
        Assert.Equal(UserRole.Admin, fixture.RecipientGroupRole);
        Assert.True(fixture.RecipientIsActive);
        var oversizedLegacyFence = TestFence(fixture.Host.HostId) with
            { RuleFingerprint = new string('x', MailNotifyStateStore.MaxMutationCharacters) };
        fixture.MailState.Update(state => state.FormalMailStartClaims["legacy-oversized"] =
            Claim("sending-result-unknown", DateTime.UtcNow, oversizedLegacyFence));

        await fixture.Service.NotifyAfterRunAsync();

        Assert.Single(fixture.Sender.Attempts);
        Assert.Equal(new string('x', MailNotifyStateStore.MaxMutationCharacters),
            fixture.MailState.Get().FormalMailStartClaims["legacy-oversized"].Fence.RuleFingerprint);
    }

    [Fact]
    public void FormalClaimRetentionPrunesOnlyOldTerminalClaimsAndPreservesUnknownFence()
    {
        var now = DateTime.UtcNow;
        var fence = TestFence(1);
        var shard = new PrtgFormalMailClaimShard { HostId = 1 };
        foreach (var (key, status, ageDays) in new[]
                 {
                     ("old-accepted", "smtp-accepted", 91), ("old-revoked", "revoked", 91),
                     ("recent-accepted", "smtp-accepted", 89), ("old-unknown", "sending-result-unknown", 365),
                     ("old-failed", "failed-or-unknown", 365)
                 })
        {
            var fenceKey = "fence-" + key;
            shard.Fences[fenceKey] = new PrtgFormalMailFenceEntry { Fence = fence, CreatedAtUtc = now.AddDays(-ageDays) };
            shard.Claims[key] = new MailFormalStartClaim { FenceKey = fenceKey, Status = status,
                UpdatedAtUtc = now.AddDays(-ageDays) };
        }

        PrtgFormalMailClaimStore.PruneTerminalEntries(shard, now);

        Assert.DoesNotContain("old-accepted", shard.Claims.Keys);
        Assert.DoesNotContain("old-revoked", shard.Claims.Keys);
        Assert.Contains("recent-accepted", shard.Claims.Keys);
        Assert.Contains("old-unknown", shard.Claims.Keys);
        Assert.Contains("old-failed", shard.Claims.Keys);
        Assert.Contains("fence-old-unknown", shard.Fences.Keys);
        Assert.Contains("fence-old-failed", shard.Fences.Keys);
        Assert.DoesNotContain("fence-old-accepted", shard.Fences.Keys);
    }

    [Fact]
    public void FormalClaimRetentionHonorsConfiguredRecordRetentionBeyondNinetyDays()
    {
        var now = DateTime.UtcNow;
        var fence = TestFence(1);
        var shard = new PrtgFormalMailClaimShard { HostId = 1 };
        shard.Fences["fence"] = new PrtgFormalMailFenceEntry { Fence = fence, CreatedAtUtc = now.AddDays(-120) };
        shard.Claims["claim"] = new MailFormalStartClaim { FenceKey = "fence", Status = "smtp-accepted",
            UpdatedAtUtc = now.AddDays(-120) };

        PrtgFormalMailClaimStore.PruneTerminalEntries(shard, now, TimeSpan.FromDays(365));

        Assert.Contains("claim", shard.Claims.Keys);
        Assert.Contains("fence", shard.Fences.Keys);
        PrtgFormalMailClaimStore.PruneTerminalEntries(shard, now.AddDays(250), TimeSpan.FromDays(365));
        Assert.DoesNotContain("claim", shard.Claims.Keys);
        Assert.DoesNotContain("fence", shard.Fences.Keys);
    }

    [Fact]
    public void HostShardLimitFailsClosedWithoutEvictingExistingUnknownClaims()
    {
        using var fixture = Fixture.CreateForStateOnly();
        var hostId = 55L;
        var fence = TestFence(hostId);
        using (var context = fixture.Backend.CreateContext())
        {
            using var transaction = context.Database.BeginTransaction();
            var shards = new PrtgFormalMailClaimStore.MutationSession(context);
            for (var index = 0; index < PrtgFormalMailClaimStore.MaxClaimsPerHost; index++)
            {
                var fenceKey = $"{index + 1}|mode:1|issue-{index:D3}";
                shards.SetFence(hostId, fenceKey, fence with { ParentRecordId = index + 1,
                    ResourceModeBlobVersion = 1 }, DateTime.UtcNow);
                shards.SetClaim(hostId, $"claim-{index:D3}", new MailFormalStartClaim
                { FenceKey = fenceKey, Status = "sending-result-unknown", UpdatedAtUtc = DateTime.UtcNow });
            }
            shards.Persist();
            context.SaveChanges();
            transaction.Commit();
        }

        Assert.Throws<InvalidDataException>(() =>
        {
            using var context = fixture.Backend.CreateContext();
            using var transaction = context.Database.BeginTransaction();
            var shards = new PrtgFormalMailClaimStore.MutationSession(context);
            shards.SetFence(hostId, "9999|mode:1|overflow-issue",
                fence with { ParentRecordId = 9999, ResourceModeBlobVersion = 1 }, DateTime.UtcNow);
            shards.SetClaim(hostId, "overflow-claim", new MailFormalStartClaim
            { FenceKey = "9999|mode:1|overflow-issue", Status = "sending-result-unknown", UpdatedAtUtc = DateTime.UtcNow });
            shards.Persist();
            context.SaveChanges();
            transaction.Commit();
        });

        using var verify = fixture.Backend.CreateContext();
        var stored = System.Text.Json.JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(
            verify.Blobs.Single(row => row.BlobKey == PrtgFormalMailClaimStore.BlobKey(hostId)).Content, LfJsonOptions.Pretty)!;
        Assert.Equal(PrtgFormalMailClaimStore.MaxClaimsPerHost, stored.Claims.Count);
        Assert.Equal(PrtgFormalMailClaimStore.MaxClaimsPerHost, stored.Fences.Count);
        Assert.All(stored.Claims.Values, claim => Assert.Equal("sending-result-unknown", claim.Status));
    }

    [Fact]
    public void ClaimShardKeyIsStableAndPartitionsByDayAndLane()
    {
        var first = PrtgFormalMailClaimStore.BlobKey(12, new DateTime(2026, 10, 8), "urgent", "fence-a");

        Assert.Equal(first, PrtgFormalMailClaimStore.BlobKey(12, new DateTime(2026, 10, 8), "urgent", "fence-a"));
        Assert.StartsWith("prtg_formal_mail_claims_v3_12_20261008_urgent_", first, StringComparison.Ordinal);
        Assert.NotEqual(first, PrtgFormalMailClaimStore.BlobKey(12, new DateTime(2026, 10, 9), "urgent", "fence-a"));
        Assert.NotEqual(first, PrtgFormalMailClaimStore.BlobKey(12, new DateTime(2026, 10, 8), "summary", "fence-a"));
    }

    [Fact]
    public void RecipientScopedClaimShardKeeps257RecipientsBelowPerShardBound()
    {
        var shards = Enumerable.Range(0, 257)
            .Select(index => PrtgFormalMailClaimStore.BlobKey(12, new DateTime(2026, 10, 8), "urgent",
                "same-issue-fence", $"recipient-{index:D3}@example.test"))
            .GroupBy(key => key, StringComparer.Ordinal)
            .Select(group => group.Count()).ToArray();

        Assert.True(shards.Length > 1);
        Assert.All(shards, size => Assert.InRange(size, 1, PrtgFormalMailClaimStore.MaxClaimsPerHost));
    }

    [Fact]
    public void HostShardsDistributeMoreThanThreeThousandClaimsWithoutFleetSingletonBound()
    {
        using var fixture = Fixture.CreateForStateOnly();
        const int hosts = 3001;
        var recordDate = DateTime.UtcNow.Date.AddDays(-1);
        using (var context = fixture.Backend.CreateContext())
        using (var transaction = context.Database.BeginTransaction())
        {
            var shards = new PrtgFormalMailClaimStore.MutationSession(context);
            for (var hostId = 1; hostId <= hosts; hostId++)
            {
                var fenceKey = "1|mode:1|issue";
                var recipient = $"recipient-{hostId:D4}@example.test";
                var shardKey = PrtgFormalMailClaimStore.BlobKey(hostId, recordDate, "urgent", fenceKey, recipient);
                shards.SetFence(hostId, shardKey, fenceKey,
                    TestFence(hostId) with { ParentRecordId = 1, ResourceModeBlobVersion = 1 }, DateTime.UtcNow);
                shards.SetClaim(hostId, shardKey, "urgent|" + recipient + "|" + fenceKey, new MailFormalStartClaim
                { FenceKey = fenceKey, Status = "sending-result-unknown", UpdatedAtUtc = DateTime.UtcNow });
            }
            shards.Persist();
            context.SaveChanges();
            transaction.Commit();
        }

        using var verify = fixture.Backend.CreateContext();
        Assert.Equal(hosts, verify.Blobs.Count(row => row.BlobKey.StartsWith("prtg_formal_mail_claims_v3_")));
    }

    [Fact]
    public void SerializedOutputChecksUtf8BytesAsWellAsCharacterCount()
    {
        Assert.Throws<InvalidDataException>(() => MailNotifyStateStore.EnsureBoundedSerializedContent(
            new string('界', MailNotifyStateStore.MaxMutationCharacters / 2 + 1)));
        MailNotifyStateStore.EnsureBoundedSerializedContent(new string('a', MailNotifyStateStore.MaxMutationCharacters));
        Assert.Throws<InvalidDataException>(() => PrtgFormalMailClaimStore.EnsureBoundedShard(
            new string('界', PrtgFormalMailClaimStore.MaxShardCharacters / 2 + 1)));
        PrtgFormalMailClaimStore.EnsureBoundedShard(new string('a', PrtgFormalMailClaimStore.MaxShardCharacters));
    }

    private static MailFormalStartClaim Claim(string status, DateTime updatedAtUtc,
        PrtgResourceFormalDeliveryFence fence) => new()
    { Status = status, UpdatedAtUtc = updatedAtUtc, Fence = fence };

    private static PrtgResourceFormalDeliveryFence TestFence(long hostId = 1) => new(
        hostId, 2, 3, 4, PrtgResourceFamily.Cpu, "source", "resource", "channel", "rule", "fingerprint",
        "admission", "issue");

    [Fact]
    public async Task FailedSerializableClaimRollsBackBeforeFakeSmtpStarts()
    {
        using var fixture = await Fixture.CreateAsync(urgent: true, elevateCpuFormalRisk: true);
        using (var context = fixture.Backend.CreateContext())
            context.Database.ExecuteSqlRaw("CREATE TRIGGER reject_formal_mail_start BEFORE INSERT ON lf_blobs WHEN NEW.blob_key LIKE 'prtg_formal_mail_claims_v3_%' BEGIN SELECT RAISE(ABORT, 'claim rejected'); END;");
        try
        {
            await fixture.Service.NotifyAfterRunAsync();

            Assert.Empty(fixture.Sender.Attempts);
            var state = fixture.MailState.Get();
            Assert.Empty(state.FormalMailStartClaims);
            Assert.Empty(Assert.Single(state.UrgentOutbox.Values).FormalStartFenceRefs);
            Assert.Null(fixture.ReadShardIfPresent());
        }
        finally
        {
            using var cleanup = fixture.Backend.CreateContext();
            cleanup.Database.ExecuteSqlRaw("DROP TRIGGER IF EXISTS reject_formal_mail_start;");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string Recipient = "formal-mail@test.local";
        private readonly PrtgFormalRuleCaseFixture _source;
        private readonly SystemSettingsStore _settings;
        private readonly FakeUserStore _users;
        private readonly FakeUserGroupStore _groups;
        private readonly FakeGroupAccessStore _groupAccess;
        private readonly FakeHandlingStore _handlings;
        private readonly ScheduleFreshnessService _freshness;

        public StorageBackend Backend => _source.Backend;
        public WebHost Host { get; }
        public FakeSmtpMailSender Sender { get; } = new();
        public MailNotifyStateStore MailState { get; }
        public PrtgResourcePressureModeStore ModeStore { get; }
        public MailNotificationService Service { get; }
        public UserRole RecipientGroupRole => _groups.GetAll().Single().Role;
        public bool RecipientIsActive => _users.FindByAccount(Recipient)?.Active == true;

        private Fixture(PrtgFormalRuleCaseFixture source, WebHost host, bool urgent, bool summary, bool dailyDigest,
            bool statsOnly = false)
        {
            _source = source;
            Host = host;
            _settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            _settings.Update(settings =>
            {
                settings.MailEnabled = true;
                settings.MailOnRunCompleted = summary;
                settings.MailUrgentEnabled = urgent;
                settings.MailDailyEnabled = dailyDigest;
                settings.MailDailyTime = "00:00";
                settings.MailWeeklyEnabled = false;
                settings.MailDigestSkipEmpty = false;
                settings.MailMinRiskLevel = RiskLevels.High;
                settings.MailRecipients = [Recipient];
                settings.MailFrom = "logforesight@test.local";
                settings.SmtpServer = "smtp.test.local";
                settings.PrtgEnabled = true;
                settings.PrtgUrl = "https://192.0.2.53";
            });

            _groups = new FakeUserGroupStore();
            var group = _groups.Upsert(new UserGroup { GroupName = "formal-mail-admins",
                Role = statsOnly ? UserRole.User : UserRole.Admin, Active = true });
            _users = new FakeUserStore();
            _users.Upsert(new WebUser { Account = Recipient, Email = Recipient, Active = true, GroupIds = [group.GroupId] });
            _groupAccess = new FakeGroupAccessStore();
            _handlings = new FakeHandlingStore();
            _freshness = new ScheduleFreshnessService(
                new BatchRunStore(Backend.LogStore("batch_runs"), Backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(Backend.Blob("schedule_options")));
            MailState = new MailNotifyStateStore(Backend.Blob("mail_notify_state"));
            ModeStore = new PrtgResourcePressureModeStore(Backend.Blob(PrtgResourcePressureModeStore.BlobKey(Host.HostId)));
            var hosts = new HostStore(Backend.Blob("hosts"));
            var aggregates = Backend.IssueAggregateQuery(hosts);
            var digest = new MailIssueDigest(aggregates,
                new OccurrenceStatusResolver(hosts, Backend.IssueHandlingStore(), Backend.IssueCaseStore(), _settings),
                _settings, new FixedIssueExclusionSource(IssueExclusion.None));
            Service = new MailNotificationService(_settings, Sender, hosts, _users,
                _groups, _groupAccess, Backend.RecordStore(), _handlings, MailState, _freshness,
                issueOwners: new IssueOwnerStore(Backend.Blob("issue_owners")),
                issueAggregates: aggregates, issueDigest: digest,
                prtgMonitoring: new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)),
                prtgBackend: Backend);
        }

        public static async Task<Fixture> CreateAsync(bool urgent, bool summary = false, bool dailyDigest = false,
            bool statsOnly = false, DateTime? analysisDay = null, bool elevateCpuFormalRisk = false,
            bool highRiskNetIqBaseline = false)
        {
            var source = new PrtgFormalRuleCaseFixture();
            if (elevateCpuFormalRisk) source.UseElevatingCpuFormalRule();
            if (highRiskNetIqBaseline) source.UseHighRiskNetIqBaseline();
            if (analysisDay.HasValue)
            {
                source.UseAnalysisDay(analysisDay.Value);
                var observedLocal = DateTime.SpecifyKind(analysisDay.Value.Date.AddDays(-31), DateTimeKind.Unspecified);
                var observedUtc = TimeZoneInfo.ConvertTimeToUtc(observedLocal, TimeZoneInfo.Local);
                source.UseSyntheticIdentityObservedAt(new DateTimeOffset(observedUtc));
            }
            var host = source.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [99d, 99d]);
            await source.RunDailyAsync(host, source.AnalysisDay);
            var parent = source.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName })
                .ReadRecent(source.AnalysisDay, 1).Single();
            Assert.Contains(parent.TopIssues, PrtgResourceFormalDeliveryFence.IsTargetPressureIssue);
            Assert.Contains(parent.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline");
            if (elevateCpuFormalRisk || highRiskNetIqBaseline)
                Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(parent));
            if (elevateCpuFormalRisk) Assert.Equal(RiskLevels.High, parent.RiskLevel);
            if (highRiskNetIqBaseline)
            {
                Assert.Equal(RiskLevels.High, parent.PrtgBaselineRiskLevel);
                Assert.Contains(parent.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline" &&
                    issue.Severity == IssueSeverity.High && issue.ElevatesDayRisk);
            }
            else
            {
                Assert.Equal(RiskLevels.Low, parent.PrtgBaselineRiskLevel);
                Assert.Contains(parent.TopIssues, issue => issue.Source == "Synthetic NetIQ disk baseline" &&
                    issue.Severity == IssueSeverity.Low && !issue.ElevatesDayRisk);
            }
            if (elevateCpuFormalRisk)
                Assert.Contains(parent.TopIssues, issue => PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(issue) &&
                    issue.Severity == IssueSeverity.High && issue.ElevatesDayRisk);
            return new Fixture(source, host, urgent, summary, dailyDigest, statsOnly);
        }

        public static Fixture CreateForStateOnly()
        {
            var source = new PrtgFormalRuleCaseFixture();
            var host = source.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [99d, 99d]);
            return new Fixture(source, host, urgent: false, summary: false, dailyDigest: false);
        }

        public DailyAnalysisRecord ReloadParent() => Backend.RecordStore(
            new HostKey { HostId = Host.HostId, HostName = Host.HostName }).ReadRecent(_source.AnalysisDay, 1).Single();

        public PrtgFormalMailClaimShard ReadShard() => ReadShardIfPresent()
            ?? throw new InvalidDataException("Expected the per-host formal mail shard to exist.");

        public PrtgFormalMailClaimShard? ReadShardIfPresent()
        {
            using var context = Backend.CreateContext();
            var prefix = $"prtg_formal_mail_claims_v3_{Host.HostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_";
            var raw = context.Blobs.Where(row => row.BlobKey.StartsWith(prefix) ||
                    row.BlobKey == PrtgFormalMailClaimStore.BlobKey(Host.HostId))
                .OrderByDescending(row => row.UpdatedAt).Select(row => row.Content).FirstOrDefault();
            return raw is null ? null : System.Text.Json.JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(raw, LfJsonOptions.Pretty);
        }

        public void RemoveFormalPressureFromParent()
        {
            var record = ReloadParent();
            record.TopIssues = record.TopIssues.Where(issue =>
                !PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(issue)).ToList();
            record.PrtgManifest = null;
            record.RiskLevel = LogAnalysisService.ComputeRuleBasedRisk(record.TopIssues, record.TrendAlerts, []);
            using var context = Backend.CreateContext();
            var row = context.DailyRecords.Single(candidate => candidate.RecordId == record.RecordId);
            row.ContentJson = System.Text.Json.JsonSerializer.Serialize(record);
            row.RiskLevel = record.RiskLevel;
            context.SaveChanges();
        }

        public void DisableMode() => ModeStore.DisableFormalMode(Host.HostId, PrtgFormalRuleCaseFixture.SensorId, DateTime.UtcNow);

        public void ReenableSameGrant()
        {
            var grant = ModeStore.ReadHostSnapshot(Host.HostId).Grants.Single();
            ModeStore.Update(document =>
            {
                var index = document.Grants.FindIndex(candidate => candidate.SensorObjid == grant.SensorObjid && candidate.Family == grant.Family);
                document.Grants[index] = grant with { FormalEnabled = true, UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1) };
            });
        }

        public void Dispose() => _source.Dispose();
    }
}

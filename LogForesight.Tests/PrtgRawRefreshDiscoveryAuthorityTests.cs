using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgRawRefreshDiscoveryAuthorityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-raw-refresh-discovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RefreshNoticeIgnoresDateFormatRevisionButRejectsRawAuthorityAToBToA()
    {
        var settings = Settings();
        var policy = Policy("policy-r1");
        var identity = Identity();
        var context = Context(policy, settings);
        var binding = PrtgTrustedSamplingBinding.Create(11, "3", "CPU Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds", "UTC", "UTC",
            policy.TimeBasisEvidenceReference, settings.Revision, policy.Revision, context,
            policy.SourceGeneration, identity.Generation, identity.Epoch, identity.ChannelGeneration, 1)
            .WithQualification("qualification-proof-r1", 90, DateTime.UtcNow.ToOADate(), "source-version-r1", DateTimeOffset.UtcNow);
        identity.ChannelFingerprint = binding.BindingFingerprint;
        var now = DateTimeOffset.UtcNow;
        var notice = new PrtgTrustedSamplingProofRefreshRequest(11, binding.BindingRevision,
            binding.BindingFingerprint, binding.QualificationProofReference, identity.Epoch,
            identity.Generation, identity.SourceGeneration, identity.ChannelGeneration, context,
            settings.Revision, policy.Revision, now, now);

        policy.Revision = "policy-date-format-only";
        policy.MessageDateFormat = "dd/MM/yyyy";
        policy.MessageTimeAuthorityRevision++;
        Assert.Equal(context, Context(policy, settings));
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.IsCurrentProofRefreshNotice(
            notice, binding, identity, settings, policy));
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.HasCurrentRawProof(11,
            binding, identity, context, settings, policy));

        policy.RawTimestampTimeZoneId = "America/New_York";
        policy.RawAuthorityRevision++;
        policy.RawTimestampTimeZoneId = "UTC";
        policy.RawAuthorityRevision++;
        var returnedAuthority = Context(policy, settings);
        Assert.NotEqual(context, returnedAuthority);
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.IsCurrentProofRefreshNotice(
            notice, binding, identity, settings, policy));
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.HasCurrentRawProof(11,
            binding, identity, returnedAuthority, settings, policy));
    }

    [Fact]
    public void ChannelDiscoveryDeduplicatesByRawContextAndRequiresAValidContextForNewJobs()
    {
        Directory.CreateDirectory(root);
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
        var store = new PrtgChannelDiscoveryJobStore(backend);
        var now = DateTimeOffset.UtcNow;
        var initial = Job("policy-r1", new string('A', 64), now);
        var accepted = store.Queue(initial, now);
        var sameRawContextAfterPolicyRevision = store.Queue(Job("policy-date-format-only",
            initial.RawAuthorityContextFingerprint, now), now);

        Assert.Equal(accepted.JobId, sameRawContextAfterPolicyRevision.JobId);
        var changedRawContext = store.Queue(Job("policy-r2", new string('B', 64), now), now);
        Assert.NotEqual(accepted.JobId, changedRawContext.JobId);
        Assert.Throws<ArgumentException>(() => store.Queue(Job("policy-legacy", "", now), now));
    }

    private PrtgChannelDiscoveryJobStore.Job Job(string policyRevision, string rawContext, DateTimeOffset now) => new()
    {
        JobId = Guid.NewGuid().ToString("N"), RequesterUserId = 4, SensorObjid = 11,
        Status = "queued", SettingsRevision = "settings-r1", PolicyRevision = policyRevision,
        RawAuthorityContextFingerprint = rawContext, SourceGeneration = "source-r1",
        HostSnapshotVersion = 7, ResourceGeneration = "resource-r1", IdentityEpoch = 2,
        ChannelGeneration = "channel-r1", IdentityChannelFingerprint = "channel-fp-r1",
        BindingRevision = 0, BindingFingerprint = "", QueuedAtUtc = now,
        DeadlineUtc = now + PrtgTrustedSamplingChannelDiscoveryHostedService.JobDeadline
    };

    private static SystemSettings Settings() => new()
    {
        Revision = "settings-r1", PrtgEnabled = true, PrtgUrl = "https://source.example",
        PrtgFetchStrategy = PrtgFetchStrategy.Conservative
    };

    private static PrtgMonitoringPolicy Policy(string revision) => new()
    {
        Revision = revision, CoreSystemId = "core-r1", SourceGeneration = "source-r1",
        EndpointHint = EfPrtgObservationStore.SourceHintFor("https://source.example"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), HostIds = [1], SensorIds = [11],
        SourceTimeZoneId = "UTC", SourceCultureName = "en-US", RawTimestampTimeZoneId = "UTC",
        AnalysisTimeZoneId = "UTC", TimeBasisEvidenceReference = "time-basis-r1", RawAuthorityRevision = 3
    };

    private static PrtgResourceIdentity Identity() => new()
    {
        SensorId = 11, Epoch = 2, Generation = "resource-r1", SourceGeneration = "source-r1",
        DeviceId = 22, HostId = 1, ChannelGeneration = "channel-r1", Active = true,
        PendingReconciliation = false
    };

    private static string Context(PrtgMonitoringPolicy policy, SystemSettings settings) =>
        PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

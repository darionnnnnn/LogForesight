using LogForesight.Core.Configuration;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgRawAuthorityRevisionTests : IDisposable
{
    private const string Url = "https://fixture.example";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-raw-authority-revision-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly EfJsonBlobStore _policyBlob;
    private readonly PrtgMonitoringPolicyStore _policyStore;
    private readonly SystemSettingsStore _settingsStore;

    public PrtgRawAuthorityRevisionTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);
        _policyBlob = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey);
        _policyStore = new PrtgMonitoringPolicyStore(_policyBlob);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        _settingsStore.Update(settings =>
        {
            settings.PrtgUrl = Url;
            settings.PrtgEnabled = false;
        });
    }

    [Fact]
    public void RevisionZeroRetainsLegacyHashesAndMessageOrScopeChangesLeaveRawAuthorityStable()
    {
        Seed(BasePolicy());
        var initial = _policyStore.Get();
        var oldRaw = initial.SourceAuthorityFingerprint(Url);
        var oldStrategy = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            initial.SourceGeneration, initial.RawTimestampTimeZoneId, initial.SourceTimeZoneId,
            initial.AnalysisTimeZoneId, "conservative", 15);
        var oldContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(initial, "conservative", 15);

        Assert.Equal(0, initial.RawAuthorityRevision);
        Assert.Equal(LegacySourceAuthorityFingerprint(initial), oldRaw);
        Assert.Equal(LegacyStrategyFingerprint(initial.SourceGeneration, initial.RawTimestampTimeZoneId,
            initial.SourceTimeZoneId, initial.AnalysisTimeZoneId, "conservative", 15), oldStrategy);
        Assert.Equal(LegacyAuthorityContextFingerprint(initial, "conservative", 15), oldContext);

        var messageFingerprint = initial.MessageTimeFingerprint(Url);
        var settingsRevision = _settingsStore.Get().Revision;
        _policyStore.UpdateWithResourceEpochs(initial.Revision, [], current =>
        {
            current.MessageDateFormat = PrtgMessageDateTimeParser.Dmy24;
            current.MessageTimeAuthorityRevision++;
            current.Revision = "revision-message-format";
        }, expectedSettingsRevision: settingsRevision);
        var afterMessageChange = _policyStore.Get();

        Assert.Equal(0, afterMessageChange.RawAuthorityRevision);
        Assert.Equal(oldRaw, afterMessageChange.SourceAuthorityFingerprint(Url));
        Assert.Equal(oldContext, PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            afterMessageChange, "conservative", 15));
        Assert.NotEqual(messageFingerprint, afterMessageChange.MessageTimeFingerprint(Url));

        _policyStore.UpdateWithResourceEpochs(afterMessageChange.Revision, [], current =>
        {
            current.HostIds.Add(2);
            current.SensorIds.Add(200);
            current.Revision = "revision-scope-change";
        }, expectedSettingsRevision: settingsRevision);
        var afterScopeChange = _policyStore.Get();

        Assert.Equal(0, afterScopeChange.RawAuthorityRevision);
        Assert.Equal(oldRaw, afterScopeChange.SourceAuthorityFingerprint(Url));
        Assert.Equal(oldStrategy, PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            afterScopeChange.SourceGeneration, afterScopeChange.RawTimestampTimeZoneId,
            afterScopeChange.SourceTimeZoneId, afterScopeChange.AnalysisTimeZoneId,
            "conservative", 15));
        Assert.Equal(oldContext, PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            afterScopeChange, "conservative", 15));
    }

    [Fact]
    public void RawTimeBasisAtoBtoADoesNotRestoreAnOldSensorProofFingerprint()
    {
        Seed(BasePolicy());
        var initial = _policyStore.Get();
        var oldRaw = initial.SourceAuthorityFingerprint(Url);
        var oldStrategy = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            initial.SourceGeneration, initial.RawTimestampTimeZoneId, initial.SourceTimeZoneId,
            initial.AnalysisTimeZoneId, "conservative", 15);
        var oldContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(initial, "conservative", 15);
        var messageFingerprint = initial.MessageTimeFingerprint(Url);

        SaveTimeBasis("Pacific Standard Time", "Asia/Taipei", "basis-b");
        var afterB = _policyStore.Get();
        Assert.Equal(1, afterB.RawAuthorityRevision);
        Assert.NotEqual(oldRaw, afterB.SourceAuthorityFingerprint(Url));
        Assert.NotEqual(oldContext, PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            afterB, "conservative", 15));

        SaveTimeBasis("UTC", "UTC", "basis-a");
        var afterA = _policyStore.Get();
        Assert.Equal(2, afterA.RawAuthorityRevision);
        Assert.Equal("UTC", afterA.RawTimestampTimeZoneId);
        Assert.Equal("UTC", afterA.AnalysisTimeZoneId);
        Assert.NotEqual(oldRaw, afterA.SourceAuthorityFingerprint(Url));
        Assert.NotEqual(oldStrategy, PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            afterA.SourceGeneration, afterA.RawTimestampTimeZoneId, afterA.SourceTimeZoneId,
            afterA.AnalysisTimeZoneId, "conservative", 15, afterA.RawAuthorityRevision));
        Assert.NotEqual(oldContext, PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            afterA, "conservative", 15));
        Assert.Equal(messageFingerprint, afterA.MessageTimeFingerprint(Url));
    }

    [Fact]
    public void RawAuthorityRevisionOverflowRollsBackPolicyAndEvidenceMutation()
    {
        Seed(BasePolicy(long.MaxValue));
        var semanticBlob = _backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey);
        var evidence = new PrtgDiskSemanticEvidence(100, 10, 1, "disk", "free", "Free", "%", 1,
            "descending-danger", PrtgDiskSemanticEvidenceSource.Manual, 1, "owned", DateTime.UtcNow,
            "fixture-v1", "source-generation-a", "resource-a", "channel-a", 1);
        semanticBlob.Mutate(_ => (JsonSerializer.Serialize(new Dictionary<long, PrtgDiskSemanticEvidence>
            { [100] = evidence }, LfJsonOptions.Pretty), true));
        var semanticBefore = semanticBlob.Read();
        var semanticVersion = semanticBlob.ReadVersion();
        var before = _policyBlob.Read()!;
        var version = _policyBlob.ReadVersion();
        var settingsRevision = _settingsStore.Get().Revision;

        Assert.Throws<InvalidOperationException>(() => _policyStore.UpdateTimeBasisWithRawEvidenceInvalidation(
            _policyStore.Get().Revision, settingsRevision, [100], current =>
            {
                current.RawTimestampTimeZoneId = "Pacific Standard Time";
                current.AnalysisTimeZoneId = "Asia/Taipei";
                current.TimeBasisEvidenceReference = "basis-overflow";
                current.Revision = "revision-overflow";
            }));

        Assert.Equal(version, _policyBlob.ReadVersion());
        Assert.Equal(before, _policyBlob.Read());
        Assert.Equal(long.MaxValue, _policyStore.Get().RawAuthorityRevision);
        Assert.Equal(semanticBefore, semanticBlob.Read());
        Assert.Equal(semanticVersion, semanticBlob.ReadVersion());
    }

    private void SaveTimeBasis(string rawZone, string analysisZone, string evidence)
    {
        var current = _policyStore.Get();
        var settingsRevision = _settingsStore.Get().Revision;
        _policyStore.UpdateTimeBasisWithRawEvidenceInvalidation(current.Revision, settingsRevision, [100], policy =>
        {
            policy.RawTimestampTimeZoneId = rawZone;
            policy.AnalysisTimeZoneId = analysisZone;
            policy.TimeBasisEvidenceReference = evidence;
            policy.Revision = "revision-" + Guid.NewGuid().ToString("N");
        });
    }

    private void Seed(PrtgMonitoringPolicy policy) =>
        _policyBlob.Mutate(_ => (JsonSerializer.Serialize(policy, LfJsonOptions.Pretty), true));

    private static PrtgMonitoringPolicy BasePolicy(long rawAuthorityRevision = 0) => new()
    {
        Revision = "revision-initial",
        CoreSystemId = "fixture-core",
        SourceGeneration = "source-generation-a",
        EndpointHint = EfPrtgObservationStore.SourceHintFor(Url),
        ValidFrom = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero),
        HostIds = [1],
        SensorIds = [100],
        SourceTimeZoneId = "UTC",
        SourceCultureName = "en-US",
        MessageDateFormat = PrtgMessageDateTimeParser.Ymd24,
        MessageTimeAuthorityRevision = 1,
        RawAuthorityRevision = rawAuthorityRevision,
        RawTimestampTimeZoneId = "UTC",
        AnalysisTimeZoneId = "UTC",
        TimeBasisEvidenceReference = "basis-a"
    };

    private static string LegacySourceAuthorityFingerprint(PrtgMonitoringPolicy policy)
    {
        var components = new[] { "prtg.table.json:devices+sensors:v1", policy.SourceGeneration,
            policy.EndpointHint, policy.SourceTimeZoneId, policy.SourceCultureName, policy.RawTimestampTimeZoneId,
            policy.AnalysisTimeZoneId, policy.TimeBasisEvidenceReference };
        var value = string.Concat(components.Select(component =>
            component.Length.ToString(CultureInfo.InvariantCulture) + ":" + component));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string LegacyStrategyFingerprint(string sourceGeneration, string rawTimeZone,
        string sourceTimeZone, string analysisTimeZone, string strategyName, int strategyMinutes)
    {
        var facts = string.Join("\u001f", sourceGeneration, rawTimeZone, sourceTimeZone,
            analysisTimeZone, strategyName, strategyMinutes.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)));
    }

    private static string LegacyAuthorityContextFingerprint(PrtgMonitoringPolicy policy,
        string strategyName, int strategyMinutes)
    {
        var strategy = LegacyStrategyFingerprint(policy.SourceGeneration, policy.RawTimestampTimeZoneId,
            policy.SourceTimeZoneId, policy.AnalysisTimeZoneId, strategyName, strategyMinutes);
        var facts = string.Join("\u001f", strategy, policy.EndpointHint, policy.TimeBasisEvidenceReference,
            policy.SourceCultureName, policy.ValidFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

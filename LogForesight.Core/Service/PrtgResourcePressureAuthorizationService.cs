using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// Issues persisted opaque same-profile trial results and activates formal CPU/memory only when
/// the current server assessment, rules version, Maintain permission, and token all match.
/// </summary>
public sealed class PrtgResourcePressureAuthorizationService(StorageBackend backend,
    ISystemSettingsStore? settings = null)
{
    private static readonly TimeSpan TrialLifetime = TimeSpan.FromHours(24);

    public PrtgResourceTrialGrant IssueSuccessfulTrial(PrtgResourcePeriodAssessment assessment,
        bool maintainAuthorized, DateTime nowUtc)
    {
        ValidateServerAssessment(assessment, maintainAuthorized, nowUtc);
        if (!EnsureCurrentAuthority(assessment, nowUtc))
            throw new InvalidOperationException("資源來源身分或 profile 已在試算後變更；請重新試算。");
        if (assessment.Family is not (PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory) ||
            assessment.Decision.Kind == PrtgResourceDecisionKind.Insufficient)
            throw new InvalidOperationException("尚未取得可用的 CPU／記憶體來源證據；不能建立正式模式試算結果。");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = nowUtc.Add(TrialLifetime);
        var hash = HashToken(token);
        var store = ModeStore(assessment.HostId);
        if (!store.UpsertTrial(ToGrant(assessment, hash, maintainAuthorized, false, nowUtc, expires)))
            throw new InvalidOperationException("試算時間早於已保存的試算；請使用目前的伺服器時間重試。");
        if (!EnsureCurrentAuthority(assessment, DateTime.UtcNow))
        {
            store.RemoveTrial(assessment.HostId, assessment.SensorObjid, assessment.Family, hash);
            store.DisableIfActivationMatches(assessment.HostId, assessment.SensorObjid, assessment.Family,
                hash, nowUtc);
            throw new InvalidOperationException("資源來源身分在試算保存時已變更；請重新試算。");
        }
        return new(token, assessment.HostId, assessment.SensorObjid, assessment.Family,
            expires.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            assessment.ProfileFingerprint, PrtgResourcePressureEvaluator.RulesVersion);
    }

    public bool SetFormalMode(PrtgResourcePeriodAssessment currentAssessment, string trialResultId,
        bool enabled, bool maintainAuthorized, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentAssessment);
        if (!maintainAuthorized) throw new UnauthorizedAccessException("正式資源模式需要 Maintain 權限。");
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("操作時間必須是 UTC。", nameof(nowUtc));
        if (!enabled) return DisableFormalMode(currentAssessment.HostId, currentAssessment.SensorObjid,
            maintainAuthorized, nowUtc);

        ValidateServerAssessment(currentAssessment, maintainAuthorized, nowUtc);
        if (!EnsureCurrentAuthority(currentAssessment, nowUtc)) return false;
        if (currentAssessment.Family is not (PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory) ||
            currentAssessment.Decision.Kind == PrtgResourceDecisionKind.Insufficient ||
            string.IsNullOrWhiteSpace(trialResultId) || trialResultId.Length > 128)
            return false;

        var store = ModeStore(currentAssessment.HostId);
        var expected = ToGrant(currentAssessment, HashToken(trialResultId), true, false, nowUtc,
            nowUtc.Add(TrialLifetime));
        if (!store.TryConsumeTrial(expected, expected.TrialResultHash, nowUtc)) return false;
        if (!EnsureCurrentAuthority(currentAssessment, DateTime.UtcNow))
        {
            store.DisableIfActivationMatches(currentAssessment.HostId, currentAssessment.SensorObjid,
                currentAssessment.Family, expected.TrialResultHash, nowUtc);
            return false;
        }
        return true;
    }

    /// <summary>Maintain-only off switch that does not depend on a working profile or fresh evidence.</summary>
    public bool DisableFormalMode(long hostId, long sensorObjid, bool maintainAuthorized, DateTime nowUtc)
    {
        if (!maintainAuthorized) throw new UnauthorizedAccessException("正式資源模式需要 Maintain 權限。");
        if (hostId <= 0 || sensorObjid <= 0 || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("關閉正式模式的資源識別或時間無效。");
        return new PrtgResourcePressureModeStore(backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(hostId))).DisableFormalMode(hostId, sensorObjid, nowUtc);
    }

    internal PrtgResourcePolicyAuthorization? GetCurrentAuthorization(
        PrtgResourcePeriodAssessment assessment, DateTime nowUtc)
        => GetCurrentAuthorizationSnapshot(assessment, nowUtc).Authorization;

    internal (PrtgResourcePolicyAuthorization? Authorization, PrtgResourcePressureModeSnapshot Mode)
        GetCurrentAuthorizationSnapshot(PrtgResourcePeriodAssessment assessment, DateTime nowUtc)
    {
        if (assessment.Family is not (PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory))
            return (null, new(null, null, 0));
        var mode = ModeStore(assessment.HostId).ReadSnapshot(assessment.HostId,
            assessment.SensorObjid, assessment.Family);
        var grant = mode.Grant;
        if (grant is null || !grant.FormalEnabled || !grant.MaintainAuthorized ||
            grant.ProfileFingerprint != assessment.ProfileFingerprint ||
            grant.RulesVersion != PrtgResourcePressureEvaluator.RulesVersion || !SameIdentity(grant, assessment) ||
            assessment.CurrentRule is null || assessment.CurrentRule.Id != grant.RuleId ||
            assessment.CurrentRuleFingerprint != grant.RuleFingerprint ||
            !EnsureCurrentRule(assessment))
            return (null, mode);
        return (new(PrtgResourceDecisionMode.FormalRisk, maintainAuthorized: true,
            serverPersistedTrial: true, assessment.ProfileFingerprint, grant.RulesVersion), mode);
    }

    private static void ValidateServerAssessment(PrtgResourcePeriodAssessment assessment,
        bool maintainAuthorized, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!maintainAuthorized) throw new UnauthorizedAccessException("正式資源模式需要 Maintain 權限。");
        if (nowUtc.Kind != DateTimeKind.Utc || assessment.AsOfUtc.Kind != DateTimeKind.Utc ||
            assessment.AsOfUtc > nowUtc || assessment.HostId <= 0 || assessment.DeviceObjid <= 0 ||
            assessment.SensorObjid <= 0 || assessment.Decision.ProfileFingerprint.Length != 64 ||
            assessment.Decision.ProfileFingerprint.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("試算不是目前伺服器端的可信資源判定。");
    }

    private bool EnsureCurrentAuthority(PrtgResourcePeriodAssessment assessment, DateTime nowUtc)
    {
        if (settings is null) throw new InvalidOperationException("正式資源授權需要目前的來源設定。");
        if (assessment.CurrentRule is null || !EnsureCurrentRule(assessment)) return false;
        var liveSettings = settings.Get();
        if (!liveSettings.PrtgEnabled) return false;
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!policy.Ready(liveSettings.PrtgUrl) || !policy.SensorIds.Contains(assessment.SensorObjid) ||
            !policy.HostIds.Contains(assessment.HostId) || policy.SourceGeneration != assessment.SourceGeneration)
            return false;

        var store = backend.PrtgStore();
        if (!store.GetResourceIdentities([assessment.SensorObjid]).TryGetValue(assessment.SensorObjid, out var identity) ||
            !identity.Active || identity.PendingReconciliation || identity.SensorId != assessment.SensorObjid ||
            identity.HostId != assessment.HostId || identity.DeviceId != assessment.DeviceObjid ||
            identity.SourceGeneration != assessment.SourceGeneration || identity.Generation != assessment.ResourceGeneration ||
            identity.ChannelGeneration != assessment.ChannelGeneration ||
            identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) != assessment.ResourceEpoch)
            return false;
        var metadata = store.GetResourcePressureSensorMetadata([assessment.SensorObjid])
            .FirstOrDefault(item => item.SensorObjid == assessment.SensorObjid);
        if (metadata is null || metadata.Paused || metadata.DevicePaused || metadata.DeviceObjid != identity.DeviceId ||
            assessment.Family != (metadata.Category?.ToLowerInvariant() switch
            {
                PrtgSensorCategories.Cpu => PrtgResourceFamily.Cpu,
                PrtgSensorCategories.Memory => PrtgResourceFamily.Memory,
                PrtgSensorCategories.Disk => PrtgResourceFamily.Disk,
                _ => (PrtgResourceFamily?)null
            })) return false;

        var profile = new PrtgTrustedSamplingProfileStore(backend).GetMany([assessment.SensorObjid])
            .GetValueOrDefault(assessment.SensorObjid);
        if (profile is null || profile.SourceGeneration != assessment.SourceGeneration ||
            profile.ResourceGeneration != assessment.ResourceGeneration ||
            profile.ChannelGeneration != assessment.ChannelGeneration ||
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) != assessment.ResourceEpoch ||
            profile.SemanticVersion != assessment.SemanticVersion || profile.StrategyFingerprint != assessment.StrategyVersion)
            return false;
        var strategyDefinition = PrtgFetchStrategy.Profile(liveSettings.PrtgFetchStrategy);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(liveSettings.PrtgFetchStrategy),
            strategyDefinition.SnapshotIntervalMinutes, nowUtc);
        if (!strategy.Ready || strategy.StrategyFingerprint != profile.StrategyFingerprint ||
            strategy.EffectiveFromHourUtc != profile.StrategyEffectiveFromHourUtc) return false;
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy,
            assessment.SensorObjid, metadata.SensorType, strategy, nowUtc,
            assessment.EvidenceAsOfUtc, nowUtc);
        if (!resolution.Ready) return false;
        var fingerprint = PrtgResourcePressureEvaluator.GetProfileFingerprint(assessment.Family,
            BuildCurrentContext(profile));
        return fingerprint == assessment.ProfileFingerprint && assessment.AsOfUtc <= nowUtc;
    }

    private bool EnsureCurrentRule(PrtgResourcePeriodAssessment assessment)
    {
        var current = PrtgResourceCurrentRuleCatalog.Load(backend).For(assessment.Family);
        return current is not null && assessment.CurrentRule is not null &&
               current.Rule.Id == assessment.CurrentRule.Id &&
               current.Fingerprint == assessment.CurrentRuleFingerprint;
    }

    private static PrtgResourceCurrentContext BuildCurrentContext(PrtgTrustedSamplingProfile profile)
    {
        var stableContract = System.Text.Json.JsonSerializer.Serialize(new
        {
            profile.SensorType, profile.PrimaryChannelId, profile.PrimaryChannelCaption, profile.Quantity,
            profile.Unit, profile.Scale, profile.Direction, profile.SemanticVersion, profile.StrategyFingerprint,
            profile.StrategyMinutes, profile.StrategyEffectiveFromHourUtc, profile.ConfirmedScanInterval,
            profile.IntervalRawUnit, profile.RawTimestampTimeZoneId, profile.SourceApiTimeZoneId,
            profile.AnalysisTimeZoneId
        });
        var stableFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableContract)));
        return new(profile.SensorObjid, profile.SourceGeneration, profile.ResourceGeneration,
            profile.ChannelGeneration, profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, profile.ConfirmedScanInterval,
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId, stableFingerprint);
    }

    private static bool SameIdentity(PrtgResourcePressureModeGrant grant, PrtgResourcePeriodAssessment assessment) =>
        grant.SourceGeneration == assessment.SourceGeneration &&
        grant.ResourceGeneration == assessment.ResourceGeneration &&
        grant.ChannelGeneration == assessment.ChannelGeneration && grant.ResourceEpoch == assessment.ResourceEpoch &&
        grant.SemanticVersion == assessment.SemanticVersion && grant.StrategyVersion == assessment.StrategyVersion &&
        assessment.CurrentRule is not null && grant.RuleId == assessment.CurrentRule.Id &&
        grant.RuleFingerprint == assessment.CurrentRuleFingerprint;

    private static PrtgResourcePressureModeGrant ToGrant(PrtgResourcePeriodAssessment assessment,
        string tokenHash, bool maintainAuthorized, bool enabled, DateTime nowUtc, DateTime expiresUtc) => new(
        assessment.HostId, assessment.SensorObjid, assessment.Family, assessment.ProfileFingerprint,
        PrtgResourcePressureEvaluator.RulesVersion, assessment.SourceGeneration, assessment.ResourceGeneration,
        assessment.ChannelGeneration, assessment.ResourceEpoch, assessment.SemanticVersion,
        assessment.StrategyVersion,
        assessment.CurrentRule?.Id ?? throw new InvalidOperationException("正式授權缺少目前啟用規則。"),
        assessment.CurrentRuleFingerprint,
        tokenHash, maintainAuthorized, enabled, nowUtc, expiresUtc,
        TrialConsumed: false);

    private PrtgResourcePressureModeStore ModeStore(long hostId) =>
        new(backend.Blob(PrtgResourcePressureModeStore.BlobKey(hostId)));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

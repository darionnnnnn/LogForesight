using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Rechecks a device-level silent finding against the durable native point and live identities.</summary>
public static class PrtgSilentDeviceProofRevalidator
{
    public static bool IsCurrent(StorageBackend backend, long hostId, DateTime hostDay,
        LogIssueSignature issue, KnownIssueRule? capturedRule, string configuredUrl)
    {
        var parts = issue.EventKey.Split(':');
        if (hostId <= 0 || hostDay != hostDay.Date || issue.RuleId is null ||
            parts.Length != 5 || parts[0] != "prtg" ||
            parts[1] != PrtgRuleEvaluator.RuleSilent || !long.TryParse(parts[2], out var deviceId) || deviceId <= 0 ||
            string.IsNullOrWhiteSpace(issue.PrtgSourceGeneration) || string.IsNullOrWhiteSpace(issue.PrtgResourceGeneration) ||
            issue.PrtgPresenceSourceDay is null || issue.PrtgPresenceSourceAsOf is null ||
            issue.PrtgPresenceDeviceStatusAsOf is null || string.IsNullOrWhiteSpace(issue.PrtgPresenceSourceAuthorityFingerprint) ||
            string.IsNullOrWhiteSpace(issue.PrtgPresenceMappingFingerprint) || string.IsNullOrWhiteSpace(issue.PrtgPresenceInventoryFingerprint))
            return false;

        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!settings.PrtgEnabled || !policy.Ready(configuredUrl) || !policy.HostIds.Contains(hostId) ||
            issue.PrtgSourceGeneration != policy.SourceGeneration || !policy.SourceAuthorityFingerprint(configuredUrl)
                .Equals(issue.PrtgPresenceSourceAuthorityFingerprint, StringComparison.Ordinal)) return false;

        var rulesStore = new KnownIssueRuleStore(backend.Blob("rules"));
        if (!rulesStore.Exists) return false;
        var currentRule = rulesStore.Load().Content?.Rules.FirstOrDefault(rule => rule.Id == issue.RuleId && rule.Enabled &&
            string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgRuleCode == PrtgRuleEvaluator.RuleSilent && rule.PrtgThreshold == PrtgRuleCatalog.DefaultSilentThreshold &&
            rule.PrtgSensorCategory is null);
        if (currentRule is null || capturedRule is null ||
            JsonSerializer.Serialize(currentRule) != JsonSerializer.Serialize(capturedRule)) return false;

        // The host map is evaluated at the parent record day. No sensor-level scope is borrowed.
        var mapRows = backend.PrtgStore().GetLatestHostMapWithDate(PrtgTriggeredValueFetcher.HostMapLookbackDays, hostDay).Rows;
        var deviceRows = mapRows.Where(row => row.DeviceObjid == deviceId).ToArray();
        if (deviceRows.Length != 1 || deviceRows[0].MapStatus != PrtgMapStatus.Ok || deviceRows[0].HostId != hostId)
            return false;

        var actual = new PrtgSilentPresenceFormalConsumer(backend).Evaluate(hostDay, policy, configuredUrl,
            new Dictionary<long, long> { [deviceId] = hostId }, [currentRule]);
        var finding = actual.SingleOrDefault(candidate => candidate.DeviceObjid == deviceId && candidate.SensorObjid is null &&
            candidate.RuleCode == PrtgRuleEvaluator.RuleSilent);
        if (finding is null) return false;
        var recomputed = PrtgFindingMapper.ToSignature(finding, hostDay);
        return recomputed.EventKey == issue.EventKey &&
            recomputed.PrtgSourceGeneration == issue.PrtgSourceGeneration &&
            recomputed.PrtgResourceGeneration == issue.PrtgResourceGeneration &&
            recomputed.PrtgPresenceSourceDay == issue.PrtgPresenceSourceDay &&
            recomputed.PrtgPresenceSourceAsOf == issue.PrtgPresenceSourceAsOf &&
            recomputed.PrtgPresenceDeviceStatusAsOf == issue.PrtgPresenceDeviceStatusAsOf &&
            recomputed.PrtgPresenceSourceAuthorityFingerprint == issue.PrtgPresenceSourceAuthorityFingerprint &&
            recomputed.PrtgPresenceMappingFingerprint == issue.PrtgPresenceMappingFingerprint &&
            recomputed.PrtgPresenceInventoryFingerprint == issue.PrtgPresenceInventoryFingerprint;
    }
}

using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>預覽與正式管線共用的主機／試點與規則分類資格。</summary>
public static class PrtgFormalEligibility
{
    public static bool HostAllowed(WebHost? host, SystemSettings settings, PrtgMonitoringPolicy policy) =>
        settings.PrtgEnabled && policy.Ready(settings.PrtgUrl) && host is { Active: true, MergedInto: null, Source: "netiq" } &&
        policy.HostIds.Contains(host.HostId);
    public static bool RuleCategoryMatches(KnownIssueRule rule, string? category) => rule.PrtgSensorCategory == null ||
        string.Equals(rule.PrtgSensorCategory, category, StringComparison.OrdinalIgnoreCase);
}

namespace LogForesight.Web.Services;

/// <summary>Measured source evidence required before admitting a durable raw qualification wave.</summary>
public sealed record PrtgQualificationCapacityPilot(
    string SourceFingerprint, string ScopeFingerprint, string RequestShapeFingerprint,
    string SourceApiVersion, string ApplicationRuntimeFingerprint, DateTimeOffset ObservedAtUtc, int SensorCount,
    int SensorTableGets, int ChannelTableGets, int PrimaryPropertyGets,
    int ClosingSensorTableGets, int HistoricXmlGets, double ObservedElapsedSeconds,
    double MaximumRequestSeconds, bool RawIdentityAndTimeValidated);

public sealed record PrtgQualificationCapacityDecision(bool Admitted, string Status, string Reason,
    long WorstCaseTableRequests, long WorstCaseHistoricRequests,
    double RequiredSeconds, double AvailableSeconds, int MaxAttempts);

/// <summary>
/// Deliberately pessimistic estimate. Raw proof is one bounded XML request per attempt; a complete
/// profile authority attempt is four table/property calls plus that raw request. The shared database
/// coordinator reserves one historic token/minute while this job is active, leaving four/minute to
/// every other historic caller in this LF database. 25% of each measured/contracted capacity is held;
/// a 120-second startup drain covers active rolling tokens and pending source reservations.
/// </summary>
public static class PrtgQualificationCapacityEvaluator
{
    public const int QualificationHistoricRequestsPerMinute = 1;
    public const int OtherHistoricRequestsPerMinute = 4;
    public const double RequiredHeadroomFraction = 0.25;
    public const int ExpectedSensorTableGets = 1;
    public const int ExpectedChannelTableGets = 1;
    public const int ExpectedPrimaryPropertyGets = 1;
    public const int ExpectedClosingSensorTableGets = 1;
    public const int ExpectedHistoricXmlGets = 1;
    public const int MaximumPilotSensors = 5;
    public const int InitialWindowDrainSeconds = 120;
    public const int PerSensorProbeDeadlineSeconds = 30;
    public static readonly TimeSpan MaximumPilotAge = TimeSpan.FromDays(7);

    public static PrtgQualificationCapacityDecision Evaluate(int eligibleSensors, int durationHours,
        int maxAttempts, PrtgQualificationCapacityPilot? pilot, string expectedSourceFingerprint,
        string expectedScopeFingerprint, string expectedRequestShapeFingerprint, DateTimeOffset nowUtc,
        double reservedTableRequestsPerSecond)
    {
        if (eligibleSensors is < 0 or > 15_000 || durationHours is < 1 or > 720 || maxAttempts is < 1 or > 3 ||
            nowUtc.Offset != TimeSpan.Zero || !double.IsFinite(reservedTableRequestsPerSecond) ||
            reservedTableRequestsPerSecond <= 0)
            return Block("waiting-capacity", "capacity-input-invalid", 0, 0, 0, 0, maxAttempts);
        if (eligibleSensors == 0)
            return new(true, "admitted", "no-binding-raw-qualification-requests-required", 0, 0, 0,
                durationHours * 3600d, maxAttempts);
        if (pilot is null || !ValidPilot(pilot, expectedSourceFingerprint, expectedScopeFingerprint,
                expectedRequestShapeFingerprint, PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), nowUtc))
            return Block("waiting-capacity", "qualification-capacity-pilot-missing-or-stale",
                (long)eligibleSensors * maxAttempts * 4, (long)eligibleSensors * maxAttempts,
                0, durationHours * 3600d, maxAttempts);

        var attempts = (long)eligibleSensors * maxAttempts;
        var historic = attempts * ExpectedHistoricXmlGets;
        var table = attempts * (ExpectedSensorTableGets + ExpectedChannelTableGets +
            ExpectedPrimaryPropertyGets + ExpectedClosingSensorTableGets);
        var headroom = 1 - RequiredHeadroomFraction;
        var historicSeconds = historic / (QualificationHistoricRequestsPerMinute / 60d * headroom);
        var tableSeconds = table / (reservedTableRequestsPerSecond * headroom);
        // The pilot measures source work without the formal purpose lane's waits. Account
        // for both even when source latency could overlap part of the rate spacing. Every
        // attempt must also fit its own finite deadline with the same 25% headroom.
        var perSensorSeconds = pilot.MaximumRequestSeconds + table / (attempts * reservedTableRequestsPerSecond);
        var measuredSeconds = attempts * perSensorSeconds / headroom;
        // The qualification token wait completes before the worker starts its 30-second probe.
        // These stages are sequential, so do not overlap the historic lane with table/probe cost.
        var required = InitialWindowDrainSeconds + historicSeconds + Math.Max(tableSeconds, measuredSeconds);
        var available = durationHours * 3600d;
        if (perSensorSeconds > PerSensorProbeDeadlineSeconds * headroom)
            return Block("waiting-capacity", "qualification-probe-deadline-headroom-insufficient",
                table, historic, required, available, maxAttempts);
        var admitted = required <= available;
        return new PrtgQualificationCapacityDecision(admitted, admitted ? "admitted" : "waiting-capacity",
            admitted ? "bounded-measured-capacity-with-headroom" : "worst-case-cost-exceeds-deadline",
            table, historic, required, available, maxAttempts);
    }

    public static string RequestShapeFingerprint() =>
        "sensor:objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince;" +
        "channels:objid,name,lastvalue_raw,unit;property:primarychannel;" +
        "sensor-close:objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince;" +
        "historicdata.xml:avg=0,usecaption=1,id-preserving-xml;" +
            "requests=4table+1historic;maxChannels=100;maxBytes=512KiB;perSensorDeadline=30s";

    public static bool ValidPilot(PrtgQualificationCapacityPilot pilot, string sourceFingerprint,
        string scopeFingerprint, string requestShapeFingerprint, string expectedApplicationRuntimeFingerprint,
        DateTimeOffset nowUtc)
    {
        if (pilot.SensorCount is < 1 or > MaximumPilotSensors ||
            pilot.SourceFingerprint != sourceFingerprint || pilot.ScopeFingerprint != scopeFingerprint ||
            pilot.RequestShapeFingerprint != requestShapeFingerprint || string.IsNullOrWhiteSpace(pilot.SourceApiVersion) ||
            pilot.ApplicationRuntimeFingerprint != expectedApplicationRuntimeFingerprint ||
            !pilot.RawIdentityAndTimeValidated || pilot.ObservedAtUtc.Offset != TimeSpan.Zero ||
            pilot.ObservedAtUtc > nowUtc || nowUtc - pilot.ObservedAtUtc > MaximumPilotAge ||
            pilot.SensorTableGets != pilot.SensorCount * ExpectedSensorTableGets ||
            pilot.ChannelTableGets != pilot.SensorCount * ExpectedChannelTableGets ||
            pilot.PrimaryPropertyGets != pilot.SensorCount * ExpectedPrimaryPropertyGets ||
            pilot.ClosingSensorTableGets != pilot.SensorCount * ExpectedClosingSensorTableGets ||
            pilot.HistoricXmlGets != pilot.SensorCount * ExpectedHistoricXmlGets ||
            !double.IsFinite(pilot.ObservedElapsedSeconds) || pilot.ObservedElapsedSeconds <= 0 ||
            !double.IsFinite(pilot.MaximumRequestSeconds) || pilot.MaximumRequestSeconds <= 0 ||
            pilot.MaximumRequestSeconds > 30 || pilot.ObservedElapsedSeconds > pilot.SensorCount * 30)
            return false;
        return true;
    }

    private static PrtgQualificationCapacityDecision Block(string status, string reason, long table,
        long historic, double required, double available, int retries) =>
        new(false, status, reason, table, historic, required, available, retries);
}

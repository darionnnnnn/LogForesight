using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ClosedDayCrossHostDayFindingRequiresBothTrustedHours(int missingHourIndex)
    {
        SeedDiskHistory(with28Days: true, descending: false);
        SeedTypedSemanticEvidence();
        EnableResourceConsumerSettings();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var laterHostDayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var first = laterHostDayStartUtc.AddHours(-1);
        var second = laterHostDayStartUtc;
        using (var db = _backend.CreateContext())
        {
            var rows = db.PrtgValues.Where(row => row.SensorObjid == SensorId &&
                (row.PeriodStart == first || row.PeriodStart == second)).ToArray();
            db.PrtgValues.RemoveRange(rows);
            db.SaveChanges();
        }
        var presentHour = missingHourIndex == 0 ? second : first;
        store.MergeSampledValues([PrtgResourceFixture.TrustedDiskHour(SensorId, presentHour, profile, 5)]);

        var result = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
            .EvaluateClosedHostDayBatch([SensorId], _completedDay, DateTime.UtcNow,
                evaluationHostIds: [HostId]);

        Assert.Contains(SensorId, result.ClosedDay!.RejectedSensorObjids);
        Assert.DoesNotContain(result.QualifiedFormalFindings, finding =>
            finding.ReasonCodes.Contains("disk-two-hour-low-water", StringComparer.Ordinal));
        Assert.DoesNotContain(result.FormalFindings, finding =>
            finding.ResourceReasonCodes?.Contains("disk-two-hour-low-water", StringComparer.Ordinal) == true);
    }

    [Fact]
    public void CrossHostDayAssessmentUsesHostDateOfTheLaterHourWhenAnalysisZoneDiffers()
    {
        var hostDay = new DateTime(2026, 9, 30);
        var laterHourStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(hostDay, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var firstAnalysisWall = DateTime.SpecifyKind(laterHourStartUtc.AddHours(-1), DateTimeKind.Unspecified);
        var secondAnalysisWall = DateTime.SpecifyKind(laterHourStartUtc, DateTimeKind.Unspecified);

        var assessment = Assessment(TimeZoneInfo.Utc.Id, firstAnalysisWall, secondAnalysisWall);

        Assert.Equal(hostDay, assessment.SingleWindowHostDay);
    }

    [Theory]
    [InlineData(2026, 11, 1, 1, 2)]
    [InlineData(2026, 3, 8, 2, 3)]
    public void CrossHostDayAssessmentRejectsAmbiguousOrInvalidAnalysisHours(
        int year, int month, int day, int firstHour, int secondHour)
    {
        var zone = EasternTimeZone();
        var first = new DateTime(year, month, day, firstHour, 0, 0, DateTimeKind.Unspecified);
        var second = new DateTime(year, month, day, secondHour, 0, 0, DateTimeKind.Unspecified);

        var assessment = Assessment(zone.Id, first, second);

        Assert.Null(assessment.SingleWindowHostDay);
    }

    [Fact]
    public void CrossHostDayAssessmentRejectsNonconsecutiveUtcHours()
    {
        var first = new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Unspecified);
        var second = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);

        var assessment = Assessment(TimeZoneInfo.Utc.Id, first, second);

        Assert.Null(assessment.SingleWindowHostDay);
    }

    private static PrtgResourcePeriodAssessment Assessment(string analysisTimeZoneId, DateTime firstWallHour,
        DateTime secondWallHour)
    {
        var context = new PrtgResourceCurrentContext(1, "source", "resource", "channel", "1", "semantic",
            "strategy", 15, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15),
            "UTC", analysisTimeZoneId);
        var hours = new[]
        {
            new PrtgResourceReadyHour(firstWallHour, 5, 100, 4, 4),
            new PrtgResourceReadyHour(secondWallHour, 5, 100, 4, 4)
        };
        var now = DateTime.UtcNow;
        var decision = new PrtgResourcePressureDecision(PrtgResourceDecisionKind.Hit,
            PrtgResourceDecisionMode.FormalRisk, "resource_disk_sustained_pressure", "profile", "disk-low-water",
            PrtgResourceFamily.Disk, now, hours, 100, 5, 5, "medium", false);
        var input = new PrtgResourceReadinessInput(PrtgResourceFamily.Disk, context,
            new PrtgResourceMeasurementDefinition(PrtgResourceSemantic.DiskRemaining, "%", 1, true), [], now);
        return new PrtgResourcePeriodAssessment(1, 2, 3, PrtgResourceFamily.Disk, input, decision, now,
            "source", "resource", "channel", "1", "semantic", "strategy", null, "", "");
    }

    private static TimeZoneInfo EasternTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
    }
}

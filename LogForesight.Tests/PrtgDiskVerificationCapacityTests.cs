using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskVerificationCapacityTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    [Theory]
    [InlineData("free capacity")]
    [InlineData("available capacity")]
    public void ManualConfirmation_AcceptsCoreAvailableCapacityChannelNames(string channelName)
    {
        var service = CreateService();
        _fixtureBlobResults.Save(Candidate());

        var evidence = service.ConfirmManually(
            new(1, "channel-1", channelName, "%", 1, "descending-danger", "verified capacity channel"), 42);

        Assert.Equal(channelName, evidence.MainChannelName);
    }

    [Theory]
    [InlineData("free capacity used")]
    [InlineData("available capacity total")]
    [InlineData("free capacity (main)")]
    public void ManualConfirmation_RejectsNamesOutsideCoreExactChannelAllowList(string channelName)
    {
        var service = CreateService();
        _fixtureBlobResults.Save(Candidate());

        Assert.Throws<ArgumentException>(() => service.ConfirmManually(
            new(1, "channel-1", channelName, "%", 1, "descending-danger", "verified channel"), 42));
    }

    private PrtgDiskVerificationResultStore _fixtureBlobResults = null!;

    private PrtgDiskVerificationService CreateService()
    {
        var settings = new SystemSettingsStore(_fixture.Blob("system_settings"));
        _fixtureBlobResults = new PrtgDiskVerificationResultStore(_fixture.Blob(PrtgDiskVerificationResultStore.BlobKey));
        var evidence = new PrtgDiskSemanticEvidenceStore(_fixture.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "disk-host", Active = true });
        using (var db = _fixture.NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 2 });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 1, DeviceObjid = 2, Category = PrtgSensorCategories.Disk, SensorType = "snmpdiskfree"
            });
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = 2, MapDate = DateTime.Today, HostId = 1, MapStatus = PrtgMapStatus.Ok
            });
            db.SaveChanges();
        }

        return new PrtgDiskVerificationService(settings, new EfPrtgStore(_fixture.NewContext), hosts,
            new PrtgProbeRunState(), evidence,
            _fixtureBlobResults, new PrtgBackfillRunState(), new PrtgStructureSyncRunState(),
            new SchedulerRunState(), new KnownIssueRuleStore(_fixture.Blob("rules")));
    }

    private static PrtgDiskVerificationResult Candidate() => new(
        1, 2, 1, "snmpdiskfree", "NeedsManualReview", "metadata incomplete", null, null, null, null, null,
        1, true, DateTime.UtcNow, DateTime.Today.AddDays(-1), PrtgDiskVerificationService.ParserSemanticVersion);

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }
}

using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskVerificationResultStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private PrtgDiskVerificationResultStore Store() => new(new EfJsonBlobStore(_fx.NewContext, PrtgDiskVerificationResultStore.BlobKey));

    [Fact]
    public void TypedResultPersistsAcrossStoreInstancesWithoutRawPayload()
    {
        var result = new PrtgDiskVerificationResult(11, 22, 5_000_000_000, "SNMP Disk Free", "NeedsManualReview",
            "單位未能確認。", "301", "Free Space", null, 1, "descending-danger", 3, true,
            DateTime.UtcNow, DateTime.Today.AddDays(-1), "disk-semantic-v1");
        Store().Save(result);

        var loaded = Store().Get(11);
        var json = _fx.Blob(PrtgDiskVerificationResultStore.BlobKey).Read();
        Assert.Equal(result, loaded);
        Assert.DoesNotContain("value_raw", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecentResultsAreBounded()
    {
        var store = Store();
        for (var i = 1; i <= 60; i++)
            store.Save(new(i, 22, 3, "disk", "NeedsManualReview", "typed", null, null, null, null, null, 0, null,
                DateTime.UtcNow.AddMinutes(i), DateTime.Today.AddDays(-1), "disk-semantic-v1"));

        Assert.Equal(20, Store().GetRecent().Count);
        Assert.Equal(50, Store().GetRecent(500).Count);
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }
}

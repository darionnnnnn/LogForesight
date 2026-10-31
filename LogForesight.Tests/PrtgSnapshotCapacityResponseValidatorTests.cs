using System.Text;
using System.Text.Json;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotCapacityResponseValidatorTests
{
    [Fact]
    public void Full_batch100_uses_count101_exact_id_set_and_bounded_relative_url()
    {
        var ids = Enumerable.Range(1001, 100).Select(id => (long)id).ToArray();
        var url = PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl(ids);
        var rows = Rows(ids);

        Assert.EndsWith("&count=101", url);
        Assert.True(Encoding.UTF8.GetByteCount(url) <= PrtgSnapshotTargetResolver.MaximumRelativeUrlBytes);
        PrtgSnapshotCapacityResponseValidator.Validate(rows, ids);
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            Rows(ids.Append(2001)), ids));
        // 真子集（PRTG 刪了一顆）收下；缺的由呼叫端警告。
        PrtgSnapshotCapacityResponseValidator.Validate(Rows(ids.Take(99)), ids);
    }

    [Fact]
    public void Snapshot_contract_accepts_proper_subset_rejects_outside_and_empty()
    {
        long[] requested = [1, 2, 3];
        var subset = PrtgSnapshotResponseContract.Validate(Rows(new long[] { 1, 2 }), requested);
        Assert.True(subset.Compatible);
        Assert.True(subset.MinimumFieldsObserved);
        Assert.True(PrtgSnapshotResponseContract.Validate(Rows(requested), requested).Compatible);
        Assert.Equal("sensor-outside-requested-scope",
            PrtgSnapshotResponseContract.Validate(Rows(new long[] { 1, 2, 4 }), requested).Reason);
        Assert.False(PrtgSnapshotResponseContract.Validate(Rows(Array.Empty<long>()), requested).Compatible);
    }

    [Fact]
    public void Batch_url_requires_sorted_unique_ids_and_response_rejects_duplicate_keys()
    {
        Assert.Throws<ArgumentException>(() => PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl([0]));
        Assert.Throws<ArgumentException>(() => PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl([-1]));
        Assert.Throws<ArgumentException>(() => PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl([2, 1]));
        Assert.Throws<ArgumentException>(() => PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl([1, 1]));
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            "{\"sensors\":[{\"objid\":1,\"objid\":1,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", [1]));
    }

    [Fact]
    public void Response_json_depth_is_bounded_to_32()
    {
        var nested = new string('[', 33) + "0" + new string(']', 33);
        var json = "{\"sensors\":[{\"objid\":1,\"status\":\"Up\",\"lastcheck\":\"now\",\"nested\":" + nested + "}]}";
        Assert.Equal("malformed-json", Assert.Throws<InvalidDataException>(() =>
            PrtgSnapshotCapacityResponseValidator.Validate(json, [1])).Message);
    }

    private static string Rows(IEnumerable<long> ids) =>
        "{\"sensors\":[" + string.Join(',', ids.Select(id =>
            "{\"objid\":" + id + ",\"status\":\"Up\",\"lastcheck\":\"now\"}")) + "]}";
}

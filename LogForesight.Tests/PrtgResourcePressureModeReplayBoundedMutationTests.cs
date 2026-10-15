using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureModeReplayBoundedMutationTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    [Fact]
    public void ProgressAndHostCursorRoundTripThroughBoundedMutationPaths()
    {
        var progressBlob = _fixture.Blob(PrtgResourcePressureModeReplayProgressStore.BlobKey(1));
        var progress = new PrtgResourcePressureModeReplayProgressStore(progressBlob);
        progress.SetNextJobOffset(3);
        Assert.Equal(3, progress.Get().NextJobOffset);

        var cursorBlob = _fixture.Blob("prtg_resource_pressure_replay_host_cursor");
        var cursor = new PrtgResourcePressureModeReplayHostCursorStore(cursorBlob);
        cursor.Set("host-after-1");
        Assert.Equal("host-after-1", cursor.Get());
        Assert.Equal("host-after-1", JsonSerializer.Deserialize<PrtgResourcePressureModeReplayHostCursor>(
            cursorBlob.Read(), LfJsonOptions.Pretty)?.AfterKey);
    }

    [Fact]
    public void ProgressMutationRejectsPhysicalNulAndPreservesBytesAndVersion()
    {
        const string key = "prtg_resource_pressure_replay_progress_1";
        var blob = _fixture.Blob(key);
        var invalid = JsonSerializer.Serialize(new PrtgResourcePressureModeReplayProgressDocument()) + "\0hidden";
        blob.Mutate(_ => ("{}", true));
        WritePhysicalText(key, invalid);
        var expected = Encoding.UTF8.GetBytes(invalid);
        var version = blob.ReadVersion();
        var store = new PrtgResourcePressureModeReplayProgressStore(blob);

        Assert.Throws<InvalidDataException>(() => store.SetNextJobOffset(1));

        Assert.Equal(version, blob.ReadVersion());
        Assert.Equal(expected, ReadPhysicalBytes(key));
    }

    [Fact]
    public void HostCursorMutationRejectsPhysicalNulAndPreservesBytesAndVersion()
    {
        const string key = "prtg_resource_pressure_replay_host_cursor";
        var blob = _fixture.Blob(key);
        var invalid = JsonSerializer.Serialize(new PrtgResourcePressureModeReplayHostCursor("host-1"),
            LfJsonOptions.Pretty) + "\0hidden";
        blob.Mutate(_ => ("{}", true));
        WritePhysicalText(key, invalid);
        var expected = Encoding.UTF8.GetBytes(invalid);
        var version = blob.ReadVersion();
        var store = new PrtgResourcePressureModeReplayHostCursorStore(blob);

        Assert.Throws<InvalidDataException>(() => store.Set("host-2"));

        Assert.Equal(version, blob.ReadVersion());
        Assert.Equal(expected, ReadPhysicalBytes(key));
    }

    private void WritePhysicalText(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var context = _fixture.NewContext();
        context.Database.ExecuteSqlInterpolated(
            $"UPDATE lf_blobs SET content=CAST({bytes} AS TEXT) WHERE blob_key={key}");
        Assert.Equal(bytes, ReadPhysicalBytes(key));
    }

    private byte[] ReadPhysicalBytes(string key)
    {
        using var context = _fixture.NewContext();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CAST(content AS BLOB) FROM lf_blobs WHERE blob_key=$key";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        var wasClosed = command.Connection!.State == System.Data.ConnectionState.Closed;
        if (wasClosed) command.Connection.Open();
        try { return (byte[])command.ExecuteScalar()!; }
        finally { if (wasClosed) command.Connection.Close(); }
    }

    public void Dispose() => _fixture.Dispose();
}

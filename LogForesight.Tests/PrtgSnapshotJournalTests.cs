using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-journal-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    public PrtgSnapshotJournalTests() => _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);

    [Fact]
    public void 來源與資料庫不同_拒絕重播且不修改檔案()
    {
        var journal = new PrtgSnapshotJournal(_backend);
        var now = DateTime.Today;
        journal.Save("source-a", new[] { new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12) },
            Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        var original = File.ReadAllBytes(journal.FilePath);
        Assert.Throws<InvalidDataException>(() => journal.Load("source-b", now));
        _backend.Blob("prtg_snapshot_database_id").Mutate(_ => ("\"different-database\"", 0));
        Assert.Throws<InvalidDataException>(() => new PrtgSnapshotJournal(_backend).Load("source-a", now));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    [Fact]
    public void 滿額與超過安全重播期_保留原檔()
    {
        var now = DateTime.Today;
        var journal = new PrtgSnapshotJournal(_backend);
        var row = new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12);
        journal.Save("source", new[] { row }, Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        var original = File.ReadAllBytes(journal.FilePath);
        Assert.Throws<InvalidDataException>(() => journal.Save("source",
            Enumerable.Repeat(row, PrtgSnapshotJournal.MaxRows + 1).ToArray(), Array.Empty<PrtgSnapshotJournal.Batch>(), now));
        Assert.Throws<InvalidDataException>(() => journal.Load("source", now.AddDays(31)));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    [Fact]
    public void 未完成暫存檔不影響上一份已提交樣本()
    {
        var journal = new PrtgSnapshotJournal(_backend);
        var now = DateTime.Today;
        journal.Save("source", new[] { new PrtgSnapshotAccumulator.CheckpointRow(now, 1, 12, 1, 12, 12) },
            Array.Empty<PrtgSnapshotJournal.Batch>(), now);
        File.WriteAllText(journal.FilePath + ".tmp", "incomplete");
        var state = journal.Load("source", now)!;
        Assert.Equal(12, Assert.Single(state.Accumulator).Sum);
    }

    [Fact]
    public void 合法JSON樣本遭修改與舊版無checksum_拒絕重播且保留原檔()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Save("source", [new(now, 1, 12, 1, 12, 12)], [], now);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal.FilePath))!;
        node["Accumulator"]![0]!["Sum"] = 13;
        var changed = node.ToJsonString(); File.WriteAllText(journal.FilePath, changed);
        Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => journal.Load("source", now)).Message);
        Assert.Equal(changed, File.ReadAllText(journal.FilePath));
        node["Version"] = 1; changed = node.ToJsonString(); File.WriteAllText(journal.FilePath, changed);
        Assert.Contains("舊版", Assert.Throws<InvalidDataException>(() => journal.Load("source", now)).Message);
        Assert.Equal(changed, File.ReadAllText(journal.FilePath));
    }

    [Fact]
    public void 過期寫入者不能覆蓋已保存樣本_持有工作者時第二個不能接管()
    {
        var now = DateTime.Today;
        using var first = new PrtgSnapshotJournal(_backend);
        first.Save("source", [new(now, 1, 12, 1, 12, 12)], [], now);
        using var second = new PrtgSnapshotJournal(_backend);
        var stale = second.Load("source", now)!;
        first.Save("source", [..stale.Accumulator, new(now, 2, 20, 1, 20, 20)], [], now);
        Assert.Throws<InvalidDataException>(() => second.Save("source", [..stale.Accumulator, new(now, 3, 30, 1, 30, 30)], [], now));
        Assert.Contains(first.Load("source", now)!.Accumulator, r => r.SensorObjid == 2);
        first.AcquireOwnership();
        Assert.Throws<IOException>(() => second.AcquireOwnership());
        first.Dispose();
        second.AcquireOwnership();
        second.Load("source", now);
        second.Save("source", [new(now, 2, 20, 1, 20, 20)], [], now);
    }

    [Fact]
    public void 同URL換Core或資源世代_拒絕舊待寫且不破壞原檔()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => p.SourceGeneration = "source-a");
        var journal = new PrtgSnapshotJournal(_backend); var now = DateTime.Today;
        journal.Save(PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"),
            [new(now, 1, 12, 1, 12, 12)], [], now);
        var original = File.ReadAllBytes(journal.FilePath);
        _backend.Blob("prtg_resource_generation_revision").Mutate(_ => ("1", 0));
        Assert.Throws<InvalidDataException>(() => journal.Load(PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"), now));
        policy.Update(p => p.SourceGeneration = "source-b");
        Assert.Throws<InvalidDataException>(() => journal.Load(PrtgSnapshotJournal.Binding(_backend, "https://fixture.example"), now));
        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

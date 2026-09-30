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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

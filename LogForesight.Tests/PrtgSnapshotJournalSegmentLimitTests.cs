using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 快照 journal 的單段上限：整點結算一個 delta 同時帶 N 個移除鍵與一個 N 列 batch（約 2N 列），
/// 各組成要**分別**比對單段上限，不能合計（壓縮時整份狀態另由 SnapshotChunks 切成每段 ≤ 單段上限）。
/// 合計比對會讓 5 萬顆以上的快照目標在整點直接停止採集。
/// </summary>
public sealed class PrtgSnapshotJournalSegmentLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-journal-limit-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgSnapshotJournalSegmentLimitTests() =>
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PrtgSnapshotAccumulator.CheckpointRow[] Rows(DateTime hour, int count) =>
        Enumerable.Range(1, count).Select(i => new PrtgSnapshotAccumulator.CheckpointRow(hour, i, i, 1, i, i)).ToArray();

    private static PrtgSnapshotJournal.Batch Batch(DateTime hour, int count) =>
        new(Guid.NewGuid().ToString("N"), Enumerable.Range(1, count).Select(i => new PrtgValueRow
        {
            SensorObjid = i,
            PeriodStart = hour,
            AvgValue = i,
            MinValue = i,
            MaxValue = i,
            Coverage = 10,
            Quality = PrtgDataQuality.Sampled,
            CreatedAt = hour
        }).ToArray());

    /// <summary>一小時的完整週期：累積 N 顆 → 整點結算（移除 N 鍵＋寫入 N 列 batch）→ 回傳 batch id</summary>
    private static string SettleHour(PrtgSnapshotJournal journal, DateTime hour, int sensors, DateTime now)
    {
        var rows = Rows(hour, sensors);
        journal.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(rows, []), null, null, now);
        var batch = Batch(hour, sensors);
        journal.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta([],
            rows.Select(r => new PrtgSnapshotAccumulator.CheckpointKey(r.Hour, r.SensorObjid)).ToArray()),
            [batch], null, now);
        return batch.Id;
    }

    [Fact]
    public void 六萬顆整點結算不擲例外且可重新載入()
    {
        var now = DateTime.Today.AddHours(2);
        using (var journal = new PrtgSnapshotJournal(_backend))
        {
            journal.Load("source", now);
            journal.EnableIncremental("source", now);
            SettleHour(journal, DateTime.Today.AddHours(1), 60_000, now);
        }
        using var reader = new PrtgSnapshotJournal(_backend);
        reader.Load("source", now);
    }

    [Fact]
    public void 單一組成超過單段上限仍擲例外()
    {
        var now = DateTime.Today.AddHours(2);
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Load("source", now);
        journal.EnableIncremental("source", now);
        Assert.Throws<InvalidDataException>(() => journal.AppendDelta("source",
            new PrtgSnapshotAccumulator.CheckpointDelta(Rows(DateTime.Today.AddHours(1), 120_000), []), null, null, now));
    }

    [Fact]
    public void 六萬顆連續兩個整點結算不超過整體上限()
    {
        var now = DateTime.Today.AddHours(3);
        using (var journal = new PrtgSnapshotJournal(_backend))
        {
            journal.Load("source", now);
            journal.EnableIncremental("source", now);
            var first = SettleHour(journal, DateTime.Today.AddHours(1), 60_000, now);
            journal.AppendAck("source", first, now);
            SettleHour(journal, DateTime.Today.AddHours(2), 60_000, now);
        }
        using var reader = new PrtgSnapshotJournal(_backend);
        reader.Load("source", now);
    }

    [Fact]
    public void 狀態超過單段上限時壓縮切段後仍可載入()
    {
        var now = DateTime.Today.AddHours(3);
        using (var journal = new PrtgSnapshotJournal(_backend))
        {
            journal.Load("source", now);
            journal.EnableIncremental("source", now);
            // 待寫 60,000 列（未 ack）＋下一小時累積 60,000 列＝120,000：超過單段 100,000、未超過整體 200,000
            SettleHour(journal, DateTime.Today.AddHours(1), 60_000, now);
            // 讓下一筆 delta 觸發壓縮，把整份狀態寫成新 generation 的起始 snapshot
            typeof(PrtgSnapshotJournal).GetField("_segmentCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(journal, 127);
            journal.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                Rows(DateTime.Today.AddHours(2), 60_000), []), null, null, now);
        }
        using var reader = new PrtgSnapshotJournal(_backend);
        reader.Load("source", now);
    }
}

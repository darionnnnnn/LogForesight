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
    public void 空V2checkpoint可轉換binding並以新binding重新載入()
    {
        var now = DateTime.Today;
        using var seed = new PrtgSnapshotJournal(_backend);
        seed.Save("source-a", [], [], now);
        var prior = seed.Load("source-a", now)!;
        var original = File.ReadAllBytes(seed.FilePath);
        using var migration = new PrtgSnapshotJournal(_backend);
        Assert.Equal("source-a", migration.Load("source-b", now)!.SourceEndpoint);

        migration.EnableIncremental("source-b", now);

        Assert.False(File.Exists(seed.FilePath));
        Assert.True(File.Exists(seed.ManifestFilePath));
        using var recovered = new PrtgSnapshotJournal(_backend);
        var state = recovered.Load("source-b", now)!;
        Assert.Equal(2, state.Version);
        Assert.Equal("source-b", state.SourceEndpoint);
        Assert.Equal(prior.DatabaseId, state.DatabaseId);
        Assert.Empty(state.Accumulator);
        Assert.Empty(state.Pending);

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(recovered.ManifestFilePath));
        var generation = manifest.RootElement.GetProperty("Generation").GetString()!;
        var segmentPath = Path.Combine(Path.GetDirectoryName(recovered.FilePath)!, generation, "segments", "00000000000000000001.json");
        using var segment = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(segmentPath));
        Assert.Equal("source-b", segment.RootElement.GetProperty("SourceEndpoint").GetString());
        Assert.Equal(prior.DatabaseId, segment.RootElement.GetProperty("DatabaseId").GetString());
        var snapshot = segment.RootElement.GetProperty("Snapshot");
        Assert.Equal("source-b", snapshot.GetProperty("SourceEndpoint").GetString());
        Assert.Equal(prior.DatabaseId, snapshot.GetProperty("DatabaseId").GetString());
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(segmentPath))));
    }

    [Fact]
    public void 非空V2checkpoint不能轉換binding且保留原檔()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Save("source-a", [new(now, 1, 12, 1, 12, 12)], [], now);
        journal.Load("source-a", now);
        var original = File.ReadAllBytes(journal.FilePath);
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original));

        Assert.Throws<InvalidDataException>(() => journal.EnableIncremental("source-b", now));

        Assert.Equal(original, File.ReadAllBytes(journal.FilePath));
        Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(journal.FilePath))));
        Assert.False(File.Exists(journal.ManifestFilePath));
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

    [Fact]
    public void 增量append_1與10筆既有backlog寫入成本相同且舊segment不變()
    {
        static (long Bytes, int FilesRead, int FilesWritten, bool Stable, bool QuotaAccurate) Run(string root, int backlog)
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
            var now = DateTime.Today;
            using (var seed = new PrtgSnapshotJournal(backend))
            {
                seed.Load("source", now);
                seed.EnableIncremental("source", now);
                for (var i = 0; i < backlog; i++)
                    seed.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                        Enumerable.Range(1, 1_000).Select(offset =>
                            new PrtgSnapshotAccumulator.CheckpointRow(now, i * 1_000L + offset, offset, 1, offset, offset)).ToArray(), []), null, null, now);
            }

            using var writer = new PrtgSnapshotJournal(backend);
            writer.Load("source", now);
            var segmentDir = Directory.GetDirectories(Path.GetDirectoryName(writer.FilePath)!, "checkpoint.g.*")
                .Select(path => Path.Combine(path, "segments")).Single();
            var oldHashes = Directory.GetFiles(segmentDir).ToDictionary(path => Path.GetFileName(path)!,
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            var readBase = writer.BytesRead;
            var writeBase = writer.BytesWritten;
            var filesReadBase = writer.FilesRead;
            var filesWrittenBase = writer.FilesWritten;
            var added = Enumerable.Range(1, 1_000).Select(offset =>
                new PrtgSnapshotAccumulator.CheckpointRow(now, 100_000 + offset, offset, 1, offset, offset)).ToArray();
            writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(added, []), null, null, now);
            var batchId = Guid.NewGuid().ToString("N");
            var batchRows = Enumerable.Range(1, 1_000).Select(offset => new PrtgValueRow
            {
                SensorObjid = 100_000 + offset,
                PeriodStart = now,
                AvgValue = offset,
                MinValue = offset,
                MaxValue = offset,
                Coverage = 10,
                Quality = PrtgDataQuality.Sampled,
                CreatedAt = now
            }).ToArray();
            writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta([],
                added.Select(row => new PrtgSnapshotAccumulator.CheckpointKey(row.Hour, row.SensorObjid)).ToArray()),
                [new(batchId, batchRows)], null, now);
            writer.AppendAck("source", batchId, now);
            var stable = oldHashes.All(pair =>
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(segmentDir, pair.Key!))));
                return actual == pair.Value;
            });
            var physicalBytes = Directory.GetFiles(Path.GetDirectoryName(writer.FilePath)!, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            return (writer.BytesWritten - writeBase, writer.FilesRead - filesReadBase,
                writer.FilesWritten - filesWrittenBase, stable && writer.BytesRead - readBase < 2048,
                physicalBytes == writer.SavedBytes && physicalBytes <= PrtgSnapshotJournal.MaxBytes);
        }

        var one = Run(Path.Combine(_root, "one"), 1);
        var ten = Run(Path.Combine(_root, "ten"), 10);
        Assert.InRange(Math.Abs(one.Bytes - ten.Bytes), 0, 8); // manifest sequence文字位數差只增加固定幾個bytes。
        Assert.Equal(6, one.FilesWritten);
        Assert.Equal(6, ten.FilesWritten);
        Assert.Equal(3, one.FilesRead);
        Assert.Equal(3, ten.FilesRead);
        Assert.True(one.Stable);
        Assert.True(ten.Stable);
        Assert.True(one.QuotaAccurate);
        Assert.True(ten.QuotaAccurate);
    }

    [Fact]
    public void 接近64MiB時把舊generation與segment檔計入配額且超大segment讀取前拒絕()
    {
        var now = DateTime.Today;
        using var journal = new PrtgSnapshotJournal(_backend);
        journal.Load("source", now);
        journal.EnableIncremental("source", now);
        journal.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
            [new(now, 1, 10, 1, 10, 10)], []), null, null, now);
        var originalManifest = File.ReadAllBytes(journal.ManifestFilePath);

        // 模擬 generation 切換中仍須保留的舊代檔案；此非 JSON segment 也必須納入真實目錄容量。
        var oldGeneration = Path.Combine(Path.GetDirectoryName(journal.FilePath)!, "checkpoint.g." + Guid.NewGuid().ToString("N"));
        var oldSegments = Path.Combine(oldGeneration, "segments");
        Directory.CreateDirectory(oldSegments);
        var retainedFile = Path.Combine(oldSegments, "retained-old-generation.tmp");
        var before = Directory.GetFiles(Path.GetDirectoryName(journal.FilePath)!, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        using (var stream = new FileStream(retainedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(PrtgSnapshotJournal.MaxBytes - before + 1);
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(journal.FilePath)!, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) > PrtgSnapshotJournal.MaxBytes);

        // 讓下一筆 delta 走 rotation/compaction，容量檢查需在切換 manifest 前拒絕。
        typeof(PrtgSnapshotJournal).GetField("_segmentCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(journal, 127);
        Assert.Throws<InvalidDataException>(() => journal.AppendDelta("source",
            new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 2, 20, 1, 20, 20)], []), null, null, now));
        Assert.Equal(originalManifest, File.ReadAllBytes(journal.ManifestFilePath));
        Assert.True(File.Exists(retainedFile));
        Assert.Equal(PrtgSnapshotJournal.MaxBytes - before + 1, new FileInfo(retainedFile).Length);

        // committed segment 自身超大時，ReadFile 應先檢查長度，不得先配置整個檔案。
        File.Delete(retainedFile);
        Directory.Delete(oldGeneration, recursive: true);
        // Generation 名稱來自 manifest；從其已驗證內容取得實際 segment 路徑。
        using var manifestDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(journal.ManifestFilePath));
        var generation = manifestDoc.RootElement.GetProperty("Generation").GetString()!;
        var segment = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(journal.FilePath)!, generation, "segments"), "*.json");
        var committedSegment = segment[0];
        using (var stream = new FileStream(committedSegment, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(PrtgSnapshotJournal.MaxBytes + 1);
        using var reader = new PrtgSnapshotJournal(_backend);
        Assert.Throws<InvalidDataException>(() => reader.Load("source", now));
        Assert.Equal(new FileInfo(journal.ManifestFilePath).Length, reader.BytesRead);
        Assert.True(File.Exists(committedSegment));
    }

    [Fact]
    public void V2遷移中斷_切換前保留V2切換後可恢復新generation()
    {
        var now = DateTime.Today;
        var row = new PrtgSnapshotAccumulator.CheckpointRow(now, 8, 40, 2, 10, 30, 50);
        using (var seed = new PrtgSnapshotJournal(_backend)) seed.Save("source", [row], [], now);
        var original = File.ReadAllBytes(new PrtgSnapshotJournal(_backend).FilePath);

        using (var beforeSwitch = new PrtgSnapshotJournal(_backend))
        {
            beforeSwitch.Load("source", now);
            beforeSwitch.FaultPoint = point => { if (point == "before-generation-switch") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => beforeSwitch.EnableIncremental("source", now));
        }
        Assert.Equal(original, File.ReadAllBytes(new PrtgSnapshotJournal(_backend).FilePath));
        Assert.Equal(40, Assert.Single(new PrtgSnapshotJournal(_backend).Load("source", now)!.Accumulator).Sum);

        using (var afterSwitch = new PrtgSnapshotJournal(_backend))
        {
            afterSwitch.Load("source", now);
            afterSwitch.FaultPoint = point => { if (point == "after-generation-switch") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => afterSwitch.EnableIncremental("source", now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(40, Assert.Single(recovered.Load("source", now)!.Accumulator).Sum);
        Assert.True(File.Exists(recovered.ManifestFilePath));
        Assert.False(File.Exists(recovered.FilePath));
    }

    [Fact]
    public void Segment發佈前後中斷_只重播manifest已提交的prefix()
    {
        var now = DateTime.Today;
        using (var setup = new PrtgSnapshotJournal(_backend))
        {
            setup.Load("source", now);
            setup.EnableIncremental("source", now);
        }
        using (var torn = new PrtgSnapshotJournal(_backend))
        {
            torn.Load("source", now);
            torn.FaultPoint = point => { if (point == "after-segment-flush") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => torn.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 1, 10, 1, 10, 10)], []), null, null, now));
        }
        using (var prefix = new PrtgSnapshotJournal(_backend))
            Assert.Empty(prefix.Load("source", now)!.Accumulator);

        using (var published = new PrtgSnapshotJournal(_backend))
        {
            published.Load("source", now);
            published.FaultPoint = point => { if (point == "after-append-manifest") throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => published.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 2, 20, 1, 20, 20)], []), null, null, now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(20, Assert.Single(recovered.Load("source", now)!.Accumulator).Sum);
    }

    [Fact]
    public void V3已提交中段checksum毀損或sequence遺失_拒絕且保留其餘檔案()
    {
        var now = DateTime.Today;
        using (var writer = new PrtgSnapshotJournal(_backend))
        {
            writer.Load("source", now);
            writer.EnableIncremental("source", now);
            for (var i = 1; i <= 3; i++)
                writer.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                    [new(now, i, i, 1, i, i)], []), null, null, now);
        }
        var generation = Directory.GetDirectories(Path.GetDirectoryName(new PrtgSnapshotJournal(_backend).FilePath)!, "checkpoint.g.*").Single();
        var segmentDir = Path.Combine(generation, "segments");
        var middle = Path.Combine(segmentDir, "00000000000000000002.json");
        var remaining = File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json"));
        var damaged = File.ReadAllBytes(middle);
        damaged[damaged.Length / 2] ^= 1;
        File.WriteAllBytes(middle, damaged);
        using (var corrupt = new PrtgSnapshotJournal(_backend))
            Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => corrupt.Load("source", now)).Message);
        Assert.Equal(damaged, File.ReadAllBytes(middle));
        Assert.Equal(remaining, File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json")));

        File.Delete(middle);
        using var missing = new PrtgSnapshotJournal(_backend);
        Assert.Contains("遺失", Assert.Throws<InvalidDataException>(() => missing.Load("source", now)).Message);
        Assert.Equal(remaining, File.ReadAllBytes(Path.Combine(segmentDir, "00000000000000000003.json")));
    }

    [Theory]
    [InlineData("before-generation-switch")]
    [InlineData("after-generation-switch")]
    public void Rotation切換中斷_完整恢復原generation或新generation(string interruptionPoint)
    {
        var now = DateTime.Today;
        using (var seed = new PrtgSnapshotJournal(_backend))
        {
            seed.Load("source", now);
            seed.EnableIncremental("source", now);
            for (var i = 1; i <= 126; i++)
                seed.AppendDelta("source", new PrtgSnapshotAccumulator.CheckpointDelta(
                    [new(now, i, i, 1, i, i)], []), null, null, now);
        }
        using (var writer = new PrtgSnapshotJournal(_backend))
        {
            writer.Load("source", now);
            writer.FaultPoint = point => { if (point == interruptionPoint) throw new IOException("simulated interruption"); };
            Assert.Throws<IOException>(() => writer.AppendDelta("source",
                new PrtgSnapshotAccumulator.CheckpointDelta([new(now, 500, 500, 1, 500, 500)], []), null, null, now));
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(126, recovered.Load("source", now)!.Accumulator.Count);
    }

    [Fact]
    public void V2snapshot超過100000列_遷移拆成多個有界segments()
    {
        var now = DateTime.Today;
        var rows = Enumerable.Range(1, 100_001)
            .Select(id => new PrtgSnapshotAccumulator.CheckpointRow(now, id, id, 1, id, id, 100)).ToArray();
        using (var journal = new PrtgSnapshotJournal(_backend)) journal.Save("source", rows, [], now);
        using (var journal = new PrtgSnapshotJournal(_backend))
        {
            var state = journal.Load("source", now)!;
            journal.EnableIncremental("source", now);
            Assert.Equal(100_001, state.Accumulator.Count);
        }
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(100_001, recovered.Load("source", now)!.Accumulator.Count);
        var generation = Directory.GetDirectories(Path.GetDirectoryName(recovered.FilePath)!, "checkpoint.g.*").Single();
        var segments = Directory.GetFiles(Path.Combine(generation, "segments"), "*.json");
        Assert.Equal(2, segments.Length);
        Assert.All(segments, path =>
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(json["Snapshot"]!["Accumulator"]!.AsArray().Count <= 100_000);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}

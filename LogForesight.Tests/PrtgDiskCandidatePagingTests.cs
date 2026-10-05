using System.Data.Common;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiskCandidatePagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CandidateCommandInterceptor _interceptor = new();
    private readonly DateTime _completedDay = new(2026, 9, 24);
    private readonly DateOnly _completedDate = new(2026, 9, 24);

    public PrtgDiskCandidatePagingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options);

    public void Dispose() => _connection.Dispose();

    private sealed class CandidateCommandInterceptor : DbCommandInterceptor
    {
        public int TotalCommands { get; private set; }
        public int CandidateCommands { get; private set; }
        public int CandidateCountQueries { get; private set; }
        public int ValuesCommands { get; private set; }
        public int MapsCommands { get; private set; }
        public List<string> ExecutedSql { get; } = new();

        public void Reset()
        {
            TotalCommands = 0;
            CandidateCommands = 0;
            ValuesCommands = 0;
            MapsCommands = 0;
            ExecutedSql.Clear();
        }

        private void Record(string sql)
        {
            TotalCommands++;
            ExecutedSql.Add(sql);

            if (sql.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase))
            {
                CandidateCommands++;
                if (sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
                    CandidateCountQueries++;
            }
            else if (sql.Contains("lf_prtg_values", StringComparison.OrdinalIgnoreCase))
            {
                ValuesCommands++;
            }
            else if (sql.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase))
            {
                MapsCommands++;
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }
    }

    [Fact]
    public void 驗收1_候選查詢命令數回歸_至少201個selected跨頁每頁候選查詢不大於2()
    {
        // 建立測試資料：
        // 205 個合法 disk sensors (mapped to active host 1)
        // 5 個 non-disk sensors
        // 5 個 mapped to inactive host 2
        // 5 個 mapped to unselected host 3
        // 2 個 conflict mapped sensors
        using (var db = NewContext())
        {
            for (var i = 1; i <= 205; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1000 + i });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = 2000 + i,
                    DeviceObjid = 1000 + i,
                    Category = PrtgSensorCategories.Disk,
                    SensorType = "disk",
                    Name = $"valid-disk-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 1000 + i,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Ok
                });
            }

            // Non-disk
            for (var i = 1; i <= 5; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 2000 + i });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = 3000 + i,
                    DeviceObjid = 2000 + i,
                    Category = "CPU",
                    SensorType = "cpu",
                    Name = $"cpu-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 2000 + i,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Ok
                });
            }

            // Inactive host 2
            for (var i = 1; i <= 5; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 3000 + i });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = 4000 + i,
                    DeviceObjid = 3000 + i,
                    Category = PrtgSensorCategories.Disk,
                    SensorType = "disk",
                    Name = $"inactive-host-disk-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 3000 + i,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 2,
                    MapStatus = PrtgMapStatus.Ok
                });
            }

            // Unselected host 3
            for (var i = 1; i <= 5; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 4000 + i });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = 5000 + i,
                    DeviceObjid = 4000 + i,
                    Category = PrtgSensorCategories.Disk,
                    SensorType = "disk",
                    Name = $"host3-disk-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 4000 + i,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 3,
                    MapStatus = PrtgMapStatus.Ok
                });
            }

            // Conflict maps (newest map is conflict)
            for (var i = 1; i <= 2; i++)
            {
                db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 5000 + i });
                db.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = 6000 + i,
                    DeviceObjid = 5000 + i,
                    Category = PrtgSensorCategories.Disk,
                    SensorType = "disk",
                    Name = $"conflict-disk-{i}"
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 5000 + i,
                    MapDate = _completedDay.AddDays(-2),
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Ok
                });
                db.PrtgHostMaps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 5000 + i,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Conflict
                });
            }

            db.SaveChanges();
        }

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "active-1", Active = true });
        hosts.Upsert(new WebHost { HostId = 2, HostName = "inactive-2", Active = false });
        hosts.Upsert(new WebHost { HostId = 3, HostName = "active-3", Active = true });

        var store = new EfPrtgStore(NewContext);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext, PrtgDiskVerificationResultStore.BlobKey));
        var service = new PrtgDiskAssessmentService(store, hosts, new FakeSystemSettingsStore(), evidenceStore, verificationStore);

        // 構造至少 201 個 selected IDs：含不存在、重複、非 disk、不同 host、conflict
        var selectedIds = new List<long>();
        for (var i = 1; i <= 205; i++) selectedIds.Add(2000 + i); // 205 valid
        selectedIds.Add(2001); // duplicate
        selectedIds.Add(2002); // duplicate
        selectedIds.Add(999991); // non-existent
        selectedIds.Add(999992); // non-existent
        selectedIds.Add(-1); // invalid
        selectedIds.Add(0); // invalid
        for (var i = 1; i <= 5; i++) selectedIds.Add(3000 + i); // non-disk
        for (var i = 1; i <= 5; i++) selectedIds.Add(4000 + i); // inactive host
        for (var i = 1; i <= 5; i++) selectedIds.Add(5000 + i); // unselected host
        for (var i = 1; i <= 2; i++) selectedIds.Add(6000 + i); // conflict
        Assert.True(selectedIds.Count >= 220); // > 201 selected IDs

        // 跨頁測試：100 / 100 / 餘數 5（限 host 1）
        var selectedHostScope = new long[] { 1 };

        // Page 1: offset 0, limit 100
        _interceptor.Reset();
        var page1 = service.Assess(_completedDate, null, limit: 100, offset: 0,
            selectedHostIds: selectedHostScope, selectedSensorObjids: selectedIds);

        Assert.Equal(205, page1.CandidateCount);
        Assert.Equal(100, page1.Rows.Count);
        Assert.True(page1.HasMore);
        Assert.InRange(_interceptor.CandidateCommands, 1, 2); // 候選查詢 <= 2 (1 count + 1 page)
        Assert.Equal(2001L, page1.Rows[0].SensorObjid);
        Assert.Equal(2100L, page1.Rows[99].SensorObjid);
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 100,
            selectedHostIds: selectedHostScope, selectedSensorObjids: selectedIds.Append(777777).ToArray(),
            candidateSnapshot: page1.CandidateSnapshot));
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 100,
            selectedHostIds: Array.Empty<long>(), selectedSensorObjids: selectedIds,
            candidateSnapshot: page1.CandidateSnapshot));

        // 每日主機映射替換不會增加 ScopeRevisionBlob；後頁須用有界目前映射核對快照日期與主機。
        using (var db = NewContext())
        {
            var map = db.PrtgHostMaps.Single(x => x.DeviceObjid == 1101);
            map.HostId = 2;
            db.SaveChanges();
        }
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null,
            limit: 100, offset: 100, selectedHostIds: selectedHostScope, selectedSensorObjids: selectedIds,
            candidateSnapshot: page1.CandidateSnapshot));
        using (var db = NewContext())
        {
            var map = db.PrtgHostMaps.Single(x => x.DeviceObjid == 1101);
            map.HostId = 1;
            db.SaveChanges();
        }

        // Page 2: offset 100, limit 100
        _interceptor.Reset();
        var page2 = service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 100,
            selectedHostIds: selectedHostScope, selectedSensorObjids: selectedIds,
            candidateSnapshot: page1.CandidateSnapshot);

        Assert.Equal(205, page2.CandidateCount);
        Assert.Equal(100, page2.Rows.Count);
        Assert.True(page2.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands); // 候選集合沿用首頁快照，不再打候選 SQL
        Assert.Equal(2101L, page2.Rows[0].SensorObjid);
        Assert.Equal(2200L, page2.Rows[99].SensorObjid);

        // Page 3: offset 200, limit 100
        _interceptor.Reset();
        var page3 = service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 200,
            selectedHostIds: selectedHostScope, selectedSensorObjids: selectedIds,
            candidateSnapshot: page2.CandidateSnapshot);

        Assert.Equal(205, page3.CandidateCount);
        Assert.Equal(5, page3.Rows.Count);
        Assert.False(page3.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands); // 候選集合沿用首頁快照，不再打候選 SQL
        Assert.Equal(2201L, page3.Rows[0].SensorObjid);
        Assert.Equal(2205L, page3.Rows[4].SensorObjid);

        // 驗證全域穩定排序與無重複、漏列
        var allPagedIds = page1.Rows.Concat(page2.Rows).Concat(page3.Rows).Select(r => r.SensorObjid).ToList();
        Assert.Equal(205, allPagedIds.Count);
        Assert.Equal(allPagedIds.Distinct().Count(), allPagedIds.Count);
        for (var i = 0; i < 205; i++)
        {
            Assert.Equal(2001 + i, allPagedIds[i]);
        }

        // 205 個候選的完整作業只計一次總數；舊的逐頁 Assess 路徑會重複 COUNT。
        Assert.Equal(1, _interceptor.CandidateCountQueries);
    }

    [Fact]
    public void HostMapDataRevision_Replace空清單與Prune同交易遞增且失敗回滾()
    {
        var store = new EfPrtgStore(NewContext);
        var mapDate = DateTime.Today.AddDays(-2);
        var scopeRevision = new EfJsonBlobStore(NewContext, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        Assert.Equal(0, store.ReadHostMapDataRevision());

        store.ReplaceHostMapForDate(mapDate, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 7001, HostId = 71, HostName = "host-71", MapStatus = PrtgMapStatus.Ok }
        });
        Assert.Equal(1, store.ReadHostMapDataRevision());
        store.ReplaceHostMapForDate(mapDate, Array.Empty<PrtgHostMapRow>());
        Assert.Equal(2, store.ReadHostMapDataRevision());
        Assert.Equal(scopeRevision, new EfJsonBlobStore(NewContext, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion());

        store.ReplaceHostMapForDate(mapDate, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 7001, HostId = 71, HostName = "host-71", MapStatus = PrtgMapStatus.Ok }
        });
        var revisionBeforeFailure = store.ReadHostMapDataRevision();
        using (var db = NewContext())
            db.Database.ExecuteSqlRaw($"CREATE TRIGGER reject_host_map_revision BEFORE UPDATE ON lf_blobs WHEN OLD.blob_key = '{EfPrtgStore.HostMapDataRevisionBlobKey}' BEGIN SELECT RAISE(ABORT, 'test revision failure'); END;");

        Assert.ThrowsAny<Exception>(() => store.ReplaceHostMapForDate(mapDate, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 7001, HostId = 72, HostName = "host-72", MapStatus = PrtgMapStatus.Ok }
        }));
        using (var db = NewContext())
            Assert.Equal(71, db.PrtgHostMaps.Single(m => m.DeviceObjid == 7001).HostId);
        Assert.Equal(revisionBeforeFailure, store.ReadHostMapDataRevision());

        using (var db = NewContext())
        {
            db.Database.ExecuteSqlRaw("DROP TRIGGER reject_host_map_revision;");
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = 7002, MapDate = DateTime.Today.AddDays(-100), HostId = 72,
                HostName = "expired-host", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.Today.AddDays(-100)
            });
            db.SaveChanges();
            db.Database.ExecuteSqlRaw($"CREATE TRIGGER reject_host_map_revision BEFORE UPDATE ON lf_blobs WHEN OLD.blob_key = '{EfPrtgStore.HostMapDataRevisionBlobKey}' BEGIN SELECT RAISE(ABORT, 'test revision failure'); END;");
        }

        Assert.ThrowsAny<Exception>(() => store.Prune(retentionDays: 90, maxRows: 10, batchSize: 5));
        using (var db = NewContext())
        {
            Assert.Contains(db.PrtgHostMaps, m => m.DeviceObjid == 7002);
            db.Database.ExecuteSqlRaw("DROP TRIGGER reject_host_map_revision;");
        }
        Assert.Equal(revisionBeforeFailure, store.ReadHostMapDataRevision());
        Assert.Equal(1, store.Prune(retentionDays: 90, maxRows: 10, batchSize: 5));
        Assert.Equal(revisionBeforeFailure + 1, store.ReadHostMapDataRevision());
        Assert.Equal(scopeRevision, new EfJsonBlobStore(NewContext, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion());
    }

    [Fact]
    public void 驗收2_固定一萬五千SensorIDs分頁複雜度測試()
    {
        // 固定 15000 sensor IDs（分佈於 3000 devices / 1 host）
        const int deviceCount = 3000;
        const int sensorsPerDevice = 5;
        const int totalSensors = deviceCount * sensorsPerDevice; // 15000

        using (var db = NewContext())
        {
            var devices = new List<PrtgDeviceRow>(deviceCount);
            var maps = new List<PrtgHostMapRow>(deviceCount);
            for (var d = 1; d <= deviceCount; d++)
            {
                devices.Add(new PrtgDeviceRow { Objid = 10000 + d, Name = $"dev-{d}" });
                maps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = 10000 + d,
                    MapDate = _completedDay.AddDays(-1),
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Ok
                });
            }

            // 適度批次 seed
            foreach (var chunk in devices.Chunk(1000))
            {
                db.PrtgDevices.AddRange(chunk);
                db.SaveChanges();
            }
            foreach (var chunk in maps.Chunk(1000))
            {
                db.PrtgHostMaps.AddRange(chunk);
                db.SaveChanges();
            }

            var sensors = new List<PrtgSensorRow>(totalSensors);
            var sensorId = 1L;
            for (var d = 1; d <= deviceCount; d++)
            {
                for (var s = 0; s < sensorsPerDevice; s++)
                {
                    sensors.Add(new PrtgSensorRow
                    {
                        Objid = sensorId++,
                        DeviceObjid = 10000 + d,
                        Category = PrtgSensorCategories.Disk,
                        SensorType = "disk",
                        Name = $"disk-{sensorId - 1}"
                    });
                }
            }
            foreach (var chunk in sensors.Chunk(1000))
            {
                db.PrtgSensors.AddRange(chunk);
                db.SaveChanges();
            }
        }

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "scale-host", Active = true });

        var store = new EfPrtgStore(NewContext);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext, PrtgDiskVerificationResultStore.BlobKey));
        var service = new PrtgDiskAssessmentService(store, hosts, new FakeSystemSettingsStore(), evidenceStore, verificationStore);

        var all15000Ids = Enumerable.Range(1, totalSensors).Select(x => (long)x).ToArray();

        // 1. 第一頁 (offset 0, limit 100)
        _interceptor.Reset();
        var firstPage = service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 0,
            selectedSensorObjids: all15000Ids, candidateMappingRevision: 1L,
            candidatePolicyVersion: "policy-v1");

        Assert.Equal(totalSensors, firstPage.CandidateCount);
        Assert.Equal(100, firstPage.Rows.Count);
        Assert.True(firstPage.Rows.Count <= 100);
        Assert.True(firstPage.HasMore);
        Assert.InRange(_interceptor.CandidateCommands, 1, 2);
        Assert.Equal(1L, firstPage.Rows[0].SensorObjid);
        Assert.Equal(100L, firstPage.Rows[99].SensorObjid);

        // 2. 中間頁 (offset 7450, limit 100)
        _interceptor.Reset();
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 7450,
            selectedSensorObjids: all15000Ids, candidateSnapshot: firstPage.CandidateSnapshot,
            candidateMappingRevision: 2L, candidatePolicyVersion: "policy-v1"));
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 7450,
            selectedSensorObjids: all15000Ids, candidateSnapshot: firstPage.CandidateSnapshot,
            candidateMappingRevision: 1L, candidatePolicyVersion: "policy-v2"));
        Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate.AddDays(-1), null,
            limit: 100, offset: 7450, selectedSensorObjids: all15000Ids, candidateSnapshot: firstPage.CandidateSnapshot,
            candidateMappingRevision: 1L, candidatePolicyVersion: "policy-v1"));
        var midPage = service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 7450,
            selectedSensorObjids: all15000Ids, candidateSnapshot: firstPage.CandidateSnapshot,
            candidateMappingRevision: 1L, candidatePolicyVersion: "policy-v1");

        Assert.Equal(totalSensors, midPage.CandidateCount);
        Assert.Equal(100, midPage.Rows.Count);
        Assert.True(midPage.Rows.Count <= 100);
        Assert.True(midPage.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands);
        Assert.Equal(7451L, midPage.Rows[0].SensorObjid);
        Assert.Equal(7550L, midPage.Rows[99].SensorObjid);

        // 3. 末頁 (offset 14950, limit 100)
        _interceptor.Reset();
        var lastPage = service.AssessCandidatePage(_completedDate, null, limit: 100, offset: 14950,
            selectedSensorObjids: all15000Ids, candidateSnapshot: midPage.CandidateSnapshot,
            candidateMappingRevision: 1L, candidatePolicyVersion: "policy-v1");

        Assert.Equal(totalSensors, lastPage.CandidateCount);
        Assert.Equal(50, lastPage.Rows.Count);
        Assert.True(lastPage.Rows.Count <= 100);
        Assert.False(lastPage.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands);
        Assert.Equal(14951L, lastPage.Rows[0].SensorObjid);
        Assert.Equal(15000L, lastPage.Rows[49].SensorObjid);
        Assert.Equal(1, _interceptor.CandidateCountQueries);

        // 第 15,001 顆加入後，新範圍先 COUNT，超上限立即拒收，不讀回完整候選列。
        using (var db = NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 13001, Name = "device-3001" });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 15001,
                DeviceObjid = 13001,
                Category = PrtgSensorCategories.Disk,
                SensorType = "disk",
                Name = "disk-15001"
            });
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = 13001,
                MapDate = _completedDay.AddDays(-1),
                HostId = 1,
                MapStatus = PrtgMapStatus.Ok
            });
            db.SaveChanges();
        }
        var all15001Ids = Enumerable.Range(1, totalSensors + 1).Select(x => (long)x).ToArray();
        _interceptor.Reset();
        var overCap = Assert.Throws<InvalidOperationException>(() => service.AssessCandidatePage(_completedDate, null,
            limit: 100, offset: 0, selectedSensorObjids: all15001Ids,
            candidateMappingRevision: 1L, candidatePolicyVersion: "policy-v1"));
        Assert.Contains("15000", overCap.Message);
        Assert.Equal(1, _interceptor.CandidateCommands); // 只有COUNT，拒收前未取回候選列
        Assert.Equal(2, _interceptor.CandidateCountQueries); // 原15,000範圍一次，超限範圍一次
    }

    [Fact]
    public void 候選快照超長Unit在SQL投影截限後明確拒收()
    {
        using (var db = NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 7001 });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 8001,
                DeviceObjid = 7001,
                Category = PrtgSensorCategories.Disk,
                SensorType = "disk",
                Unit = new string('u', 129)
            });
            db.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                DeviceObjid = 7001,
                MapDate = _completedDay.AddDays(-1),
                HostId = 1,
                MapStatus = PrtgMapStatus.Ok
            });
            db.SaveChanges();
        }

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "unit-host", Active = true });
        var store = new EfPrtgStore(NewContext);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext, PrtgDiskVerificationResultStore.BlobKey));
        var service = new PrtgDiskAssessmentService(store, hosts, new FakeSystemSettingsStore(), evidenceStore, verificationStore);

        _interceptor.Reset();
        var rejected = Assert.Throws<InvalidOperationException>(() => service.Assess(_completedDate, null,
            selectedSensorObjids: new[] { 8001L }));
        Assert.Contains("metadata", rejected.Message);
        Assert.Equal(2, _interceptor.CandidateCommands); // COUNT加有SQL子字串上限的候選投影
        Assert.Equal(1, _interceptor.CandidateCountQueries);
        Assert.Contains("substr", string.Join("\n", _interceptor.ExecutedSql), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 驗收3_SQLServerProvider_一萬五千IDs候選查詢ToQueryString可翻譯且參數有界()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var ids = Enumerable.Range(1, 15000).Select(x => (long)x).ToArray();
        var query = EfPrtgStore.BuildLatestMappedReadinessSensorsQuery(
            ctx, new[] { 7L }, _completedDay, _completedDay.AddDays(-30), ids);

        var sql = query.ToQueryString();

        Assert.Contains("OPENJSON", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("category", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lf_prtg_sensors", sql, StringComparison.OrdinalIgnoreCase);

        // 驗證參數形式有界，未展開成 15000 個 @p 參數
        Assert.DoesNotContain("@p100", sql);
        Assert.DoesNotContain("@p1000", sql);
        Assert.DoesNotContain("@p14999", sql);
    }

    [Fact]
    public void 驗收3_SQLServerProvider_未指定sensorIDs時亦可正常翻譯()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var query = EfPrtgStore.BuildLatestMappedReadinessSensorsQuery(
            ctx, new[] { 7L }, _completedDay, _completedDay.AddDays(-30), null);

        var sql = query.ToQueryString();

        Assert.Contains("category", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lf_prtg_sensors", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENJSON(@__filterSensorIds", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 驗收4_語意邊界與反例判定全數保留()
    {
        using (var db = NewContext())
        {
            // Sensor 101: 正常
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 11 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 101, DeviceObjid = 11, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 11, MapDate = _completedDay.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Ok });

            // Sensor 102: 最新為 Conflict (Day -1)，不可回退 Day -2 Ok
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 12 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 102, DeviceObjid = 12, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 12, MapDate = _completedDay.AddDays(-2), HostId = 1, MapStatus = PrtgMapStatus.Ok });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 12, MapDate = _completedDay.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Conflict });

            // Sensor 103: 未來 Map (Day +1)，目標完成日看不到
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 13 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 103, DeviceObjid = 13, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 13, MapDate = _completedDay.AddDays(1), HostId = 1, MapStatus = PrtgMapStatus.Ok });

            // Sensor 104: 過期 Map (Day -35，超過 30 天 lookback)
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 14 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 104, DeviceObjid = 14, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 14, MapDate = _completedDay.AddDays(-35), HostId = 1, MapStatus = PrtgMapStatus.Ok });

            // Sensor 105: 停用主機 (Host 2 Active=false)
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 15 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 105, DeviceObjid = 15, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 15, MapDate = _completedDay.AddDays(-1), HostId = 2, MapStatus = PrtgMapStatus.Ok });

            // Sensor 106: 已合併主機 (Host 3 MergedInto=1)
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 16 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 106, DeviceObjid = 16, Category = PrtgSensorCategories.Disk, SensorType = "disk" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 16, MapDate = _completedDay.AddDays(-1), HostId = 3, MapStatus = PrtgMapStatus.Ok });

            // Sensor 107: 非 disk
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 17 });
            db.PrtgSensors.Add(new PrtgSensorRow { Objid = 107, DeviceObjid = 17, Category = "Memory", SensorType = "mem" });
            db.PrtgHostMaps.Add(new PrtgHostMapRow { DeviceObjid = 17, MapDate = _completedDay.AddDays(-1), HostId = 1, MapStatus = PrtgMapStatus.Ok });

            db.SaveChanges();
        }

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "active-host", Active = true });
        hosts.Upsert(new WebHost { HostId = 2, HostName = "inactive-host", Active = false });
        hosts.Upsert(new WebHost { HostId = 3, HostName = "merged-host", Active = true, MergedInto = 1 });

        var store = new EfPrtgStore(NewContext);
        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));
        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext, PrtgDiskVerificationResultStore.BlobKey));
        var service = new PrtgDiskAssessmentService(store, hosts, new FakeSystemSettingsStore(), evidenceStore, verificationStore);

        // 1. null selectedSensorObjids: 正常回傳母體內唯一合法 candidate (101)
        var nullBatch = service.Assess(_completedDate, null);
        Assert.Equal(1, nullBatch.CandidateCount);
        Assert.Equal(101L, Assert.Single(nullBatch.Rows).SensorObjid);

        // 2. empty selected: 不回退全站，total 0, rows 0, hasMore false
        _interceptor.Reset();
        var emptyBatch = service.Assess(_completedDate, null, selectedSensorObjids: Array.Empty<long>());
        Assert.Equal(0, emptyBatch.CandidateCount);
        Assert.Empty(emptyBatch.Rows);
        Assert.False(emptyBatch.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands); // 甚至無須發出 SQL

        // 3. 全無效 ID (0, -1, 負數): total 0, rows 0, hasMore false
        _interceptor.Reset();
        var invalidBatch = service.Assess(_completedDate, null, selectedSensorObjids: new long[] { 0, -1, -99 });
        Assert.Equal(0, invalidBatch.CandidateCount);
        Assert.Empty(invalidBatch.Rows);
        Assert.False(invalidBatch.HasMore);
        Assert.Equal(0, _interceptor.CandidateCommands);

        // 4. 重複正 ID 去重: [101, 101, 101] -> total 1, rows 1
        var dupBatch = service.Assess(_completedDate, null, selectedSensorObjids: new long[] { 101, 101, 101 });
        Assert.Equal(1, dupBatch.CandidateCount);
        Assert.Equal(101L, Assert.Single(dupBatch.Rows).SensorObjid);

        // 5. 反例排除：即使明確指定 102..107，全數排除
        var excludedBatch = service.Assess(_completedDate, null,
            selectedSensorObjids: new long[] { 102, 103, 104, 105, 106, 107, 999 });
        Assert.Equal(0, excludedBatch.CandidateCount);
        Assert.Empty(excludedBatch.Rows);

        // 6. 歷史 as-of 反例：目標日為 Day -5，Day -2 的 mapping 對 Day -5 是未來
        var asOfEarlier = DateOnly.FromDateTime(_completedDay.AddDays(-5));
        var earlierBatch = service.Assess(asOfEarlier, null, selectedSensorObjids: new long[] { 101 });
        Assert.Equal(0, earlierBatch.CandidateCount); // 101 的 map 在 Day -1，對 Day -5 尚未發生
    }

    [Fact]
    public void 驗收4_正式Assess與decision返回與小fixture舊語意一致()
    {
        const long deviceId = 701;
        const long sensorId = 801;
        var today = DateTime.Today;
        var completedDay = DateOnly.FromDateTime(today.AddDays(-1));
        var dayDate = completedDay.ToDateTime(TimeOnly.MinValue);

        using (var db = NewContext())
        {
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = deviceId });
            db.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = sensorId,
                DeviceObjid = deviceId,
                Category = PrtgSensorCategories.Disk,
                SensorType = "SNMP Disk Free",
                Name = "Disk C:"
            });

            var values = new List<PrtgValueRow>();
            var maps = new List<PrtgHostMapRow>();
            for (var offset = -27; offset <= 0; offset++)
            {
                var date = dayDate.AddDays(offset);
                maps.Add(new PrtgHostMapRow
                {
                    DeviceObjid = deviceId,
                    MapDate = date,
                    HostId = 1,
                    MapStatus = PrtgMapStatus.Ok
                });
                for (var hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow
                    {
                        SensorObjid = sensorId,
                        PeriodStart = date.AddHours(hour),
                        AvgValue = 75,
                        MinValue = 75,
                        MaxValue = 75,
                        Coverage = 100,
                        Quality = PrtgDataQuality.Ok,
                        CreatedAt = today
                    });
                }
            }
            db.PrtgHostMaps.AddRange(maps);
            db.PrtgValues.AddRange(values);
            db.SaveChanges();
        }

        var evidenceStore = new PrtgDiskSemanticEvidenceStore(new EfJsonBlobStore(NewContext, PrtgDiskSemanticEvidenceStore.BlobKey));
        evidenceStore.ConfirmManually(
            new PrtgDiskSemanticContext(sensorId, deviceId, 1, "SNMP Disk Free", "free", "Free", "%", 1, "descending-danger"),
            9, "Manually confirmed percent free channel.", DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);

        var verificationStore = new PrtgDiskVerificationResultStore(new EfJsonBlobStore(NewContext, PrtgDiskVerificationResultStore.BlobKey));
        verificationStore.Save(new PrtgDiskVerificationResult(
            sensorId, deviceId, 1, "SNMP Disk Free", "Verified", "Typed channel values matched.",
            "free", "Free", "%", 1, "descending-danger", 1, true,
            DateTime.UtcNow, today.AddDays(-1), PrtgDiskAssessmentService.ParserSemanticVersion));

        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostId = 1, HostName = "active-svr", Active = true });

        var store = new EfPrtgStore(NewContext);
        var service = new PrtgDiskAssessmentService(store, hosts, new FakeSystemSettingsStore(), evidenceStore, verificationStore);

        // Assess with selectedSensorObjids
        var batch = service.Assess(completedDay, null, PrtgDiskDecisionMode.Preview,
            selectedSensorObjids: new long[] { sensorId });

        Assert.Equal(1, batch.CandidateCount);
        var row = Assert.Single(batch.Rows);
        Assert.Equal(sensorId, row.SensorObjid);
        Assert.Equal(PrtgValueReadinessStatus.Ready, row.Readiness.Status);
        Assert.True(row.Readiness.SemanticReady);
        Assert.NotNull(row.EvidenceValidity);
        Assert.True(row.EvidenceValidity!.IsValid);
        Assert.Equal(PrtgDiskDecisionExclusion.RuleMissing, row.Decision.Exclusion);
    }
}

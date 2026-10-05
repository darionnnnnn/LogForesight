using System.Data.Common;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgReadinessSensorDetailsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ReadCommandCapture _capture = new();

    public PrtgReadinessSensorDetailsTests()
    {
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection)
        .AddInterceptors(_capture)
        .Options);

    private EfPrtgStore CreateStore() => new(NewContext);

    private void Seed(params PrtgSensorRow[] sensors)
    {
        var store = CreateStore();
        store.UpsertDevices([new PrtgDeviceRow { Objid = 10, Name = "device-10" }], DateTime.Now);
        store.UpsertSensors(sensors, DateTime.Now);
        _capture.Reset();
    }

    [Fact]
    public void 超過100個ID在查詢前拒絕()
    {
        var store = CreateStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetReadinessSensorDetailsByIds(
            Enumerable.Range(1, 101).Select(x => (long)x).ToArray()));
        Assert.Empty(_capture.Commands);
    }

    [Fact]
    public void ById明細單次SQL讀回唯一集合_不執行候選計數或映射查詢()
    {
        Seed(
            new PrtgSensorRow { Objid = 1, DeviceObjid = 10, Name = "磁碟一", SensorType = "disk", Category = "disk", Unit = "%" },
            new PrtgSensorRow { Objid = 2, DeviceObjid = 10, Name = "磁碟二", SensorType = "disk", Category = "disk", Unit = "%" });

        var rows = CreateStore().GetReadinessSensorDetailsByIds([2, 1, 2]);

        Assert.Equal(new long[] { 1, 2 }, rows.Select(row => row.Objid));
        Assert.All(rows, row => Assert.Equal(10, row.DeviceObjid));
        var command = Assert.Single(_capture.Commands);
        Assert.Contains("SELECT", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COUNT", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lf_prtg_host_map", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("substr", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 缺少sensor或裝置join時拒絕不完整結果()
    {
        Seed(
            new PrtgSensorRow { Objid = 1, DeviceObjid = 10, Name = "valid", SensorType = "disk" },
            new PrtgSensorRow { Objid = 2, DeviceObjid = 99, Name = "orphan", SensorType = "disk" });

        Assert.Throws<InvalidOperationException>(() => CreateStore().GetReadinessSensorDetailsByIds([1, 404]));
        Assert.Throws<InvalidOperationException>(() => CreateStore().GetReadinessSensorDetailsByIds([2]));
    }

    [Fact]
    public void Unicode欄位保留且超長名稱只讀有限前綴後明確拒絕()
    {
        Seed(new PrtgSensorRow
        {
            Objid = 1, DeviceObjid = 10, Name = new string('界', 255), SensorType = new string('型', 128),
            Category = new string('分', 64), Unit = new string('單', 64)
        });
        var details = Assert.Single(CreateStore().GetReadinessSensorDetailsByIds([1]));
        Assert.Equal(255, details.Name.Length);
        Assert.Equal(128, details.SensorType.Length);
        Assert.Equal(64, details.Category!.Length);
        Assert.Equal(64, details.Unit!.Length);

        using (var context = NewContext())
        {
            context.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2, DeviceObjid = 10, Name = new string('界', 256), SensorType = "disk", SyncedAt = DateTime.Now,
                CreatedAt = DateTime.Now
            });
            context.SaveChanges();
        }
        _capture.Reset();
        Assert.Throws<InvalidOperationException>(() => CreateStore().GetReadinessSensorDetailsByIds([2]));
        Assert.Single(_capture.Commands);
        Assert.Contains("substr", _capture.Commands[0], StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class ReadCommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public void Reset() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }
    }
}

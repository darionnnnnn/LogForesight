using System.Data;
using System.Data.Common;
using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgStorageEnvironmentProbeTests
{
    [Fact]
    public async Task SQLite_實際讀取引擎頁面上限與資料根目錄磁碟且不輸出路徑()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-storage-facts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "private-database-name.db");
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={databasePath}" }, root);
            using var db = backend.CreateContext();

            await db.Database.GetDbConnection().OpenAsync();
            var facts = await PrtgStorageEnvironmentProbe.CollectAsync(db, root);
            var json = JsonSerializer.Serialize(facts);

            Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
            Assert.Equal("measured", facts.Status);
            Assert.Equal("Sqlite", facts.Provider);
            Assert.Matches(@"^\d+(?:\.\d+){1,3}$", facts.EngineVersion);
            Assert.InRange(facts.QueriesAttempted, 1, PrtgStorageEnvironmentProbe.MaximumMetadataQueries);
            Assert.Equal(facts.QueriesAttempted, facts.QueriesSucceeded);
            Assert.Equal("measured", facts.FileStatus);
            Assert.Equal("measured", facts.LocalFileStatus);
            Assert.True(facts.FileCapacities.Count > 0);
            Assert.True(facts.FileCapacities.Count <= PrtgStorageEnvironmentProbe.MaximumRowsPerKind);
            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-database-name", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SQLite檔案metadata_相對路徑與UNC均拒收()
    {
        Assert.False(PrtgStorageEnvironmentProbe.IsLocalFixedPath("relative\\database.db"));
        Assert.False(PrtgStorageEnvironmentProbe.IsLocalFixedPath("\\\\remote-host\\share\\database.db"));
        Assert.False(PrtgStorageEnvironmentProbe.IsLocalFixedPath("//remote-host/share/database.db"));
    }

    [Fact]
    public async Task 未支援provider_保留明確unknown且不開啟連線()
    {
        using var connection = new ProbeConnection(ProbeConnectionMode.Denied);
        var facts = await PrtgStorageEnvironmentProbe.CollectConnectionAsync(
            "Microsoft.EntityFrameworkCore.InMemory", connection, null, CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.Equal("unknown", facts.Status);
        Assert.Equal("unknown", facts.Provider);
        Assert.Equal(0, facts.QueriesAttempted);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task SQLmetadata權限拒絕_只回unknown且不序列化provider錯誤細節()
    {
        using var connection = new ProbeConnection(ProbeConnectionMode.Denied);
        await connection.OpenAsync();
        var facts = await PrtgStorageEnvironmentProbe.CollectConnectionAsync(
            "Microsoft.EntityFrameworkCore.SqlServer", connection, null, CancellationToken.None, TimeSpan.FromSeconds(1));
        var json = JsonSerializer.Serialize(facts);

        Assert.Equal("unknown", facts.Status);
        Assert.Equal("unknown", facts.FileStatus);
        Assert.Equal("unknown", facts.VolumeStatus);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.True(facts.QueriesAttempted > 0);
        Assert.DoesNotContain("secret-server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission denied", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SQL基本metadata保留Express與檔案事實_選用logDMV拒絕時仍可用()
    {
        using var connection = new SqlMetadataConnection();
        var facts = await PrtgStorageEnvironmentProbe.CollectConnectionAsync(
            "Microsoft.EntityFrameworkCore.SqlServer", connection, null, CancellationToken.None, TimeSpan.FromSeconds(1));

        Assert.Equal("partial", facts.Status);
        Assert.Equal("16.0.1000.6", facts.EngineVersion);
        Assert.Equal("Express", facts.Edition);
        Assert.Equal("express", facts.EngineEdition);
        Assert.Equal("measured", facts.FileStatus);
        Assert.Equal(3, facts.QueriesAttempted);
        Assert.Equal(2, facts.QueriesSucceeded);
        Assert.Equal("unknown", facts.LogStatus);
        Assert.Null(facts.DatabaseLogAggregate);
        Assert.Equal(1024L * 1024, facts.FileCapacities[0].AllocatedBytes);
        Assert.Equal(256L * 1024, facts.FileCapacities[0].UsedBytes);
        Assert.Equal(2L * 1024 * 1024, facts.FileCapacities[0].MaximumBytes);
    }

    [Fact]
    public async Task 整體期限到期_回timeout與unknown而不洩漏連線內容()
    {
        using var connection = new ProbeConnection(ProbeConnectionMode.HoldUntilCancelled);
        var facts = await PrtgStorageEnvironmentProbe.CollectConnectionAsync(
            "Microsoft.EntityFrameworkCore.SqlServer", connection, null, CancellationToken.None, TimeSpan.FromMilliseconds(20));

        Assert.Equal("timeout", facts.Status);
        Assert.True(facts.TimedOut);
        Assert.Equal("unknown", facts.FileStatus);
        Assert.Equal("unknown", facts.VolumeStatus);
        Assert.InRange(facts.QueriesAttempted, 0, PrtgStorageEnvironmentProbe.MaximumMetadataQueries);
    }

    [Fact]
    public async Task 外部取消_回cancelled且不當成timeout()
    {
        using var connection = new ProbeConnection(ProbeConnectionMode.HoldUntilCancelled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var facts = await PrtgStorageEnvironmentProbe.CollectConnectionAsync(
            "Microsoft.EntityFrameworkCore.SqlServer", connection, null, cancellation.Token, TimeSpan.FromSeconds(1));

        Assert.Equal("cancelled", facts.Status);
        Assert.True(facts.Cancelled);
        Assert.False(facts.TimedOut);
        Assert.Equal("unknown", facts.FileStatus);
    }

    [Fact]
    public async Task 超量或不安全metadata_限制列數並將無效欄位降為unknown()
    {
        var untrusted = new PrtgStorageEnvironmentFacts
        {
            Status = "provider exception at secret-server",
            Provider = "Sqlite",
            EngineVersion = "secret-server 3.46.1",
            QueriesAttempted = int.MaxValue,
            QueriesSucceeded = int.MaxValue,
            DatabaseLogAggregate = new PrtgStorageLogAggregate(-5, -6),
            FileCapacities = Enumerable.Range(0, 40)
                .Select(_ => new PrtgStorageCapacityRow("C:\\private\\file.db", -1, -2, -3, "unbounded path", -4, 101))
                .ToArray(),
            VolumeCapacities = Enumerable.Range(0, 40)
                .Select(_ => new PrtgStorageVolumeRow("\\\\secret-server\\share", -1, -1))
                .ToArray()
        };
        var evidence = await PrtgCompatibilityProbe.ExecuteCoreAsync(
            (_, _) => throw new InvalidOperationException("No PRTG request is expected without samples."),
            new TestConsole(), Array.Empty<PrtgProbeRunner.SensorTypeSample>(),
            new PrtgProbeEvidenceContext { StorageEnvironment = untrusted });
        var json = PrtgCompatibilityProbe.SerializeEvidence(evidence);

        Assert.Equal("unknown", evidence.StorageEnvironment.Status);
        Assert.Equal("unknown", evidence.StorageEnvironment.EngineVersion);
        Assert.Equal(PrtgStorageEnvironmentProbe.MaximumMetadataQueries, evidence.StorageEnvironment.QueriesAttempted);
        Assert.Equal(PrtgStorageEnvironmentProbe.MaximumRowsPerKind, evidence.StorageEnvironment.FileCapacities.Count);
        Assert.Equal(PrtgStorageEnvironmentProbe.MaximumRowsPerKind, evidence.StorageEnvironment.VolumeCapacities.Count);
        Assert.True(evidence.StorageEnvironment.FileRowsTruncated);
        Assert.True(evidence.StorageEnvironment.VolumeRowsTruncated);
        Assert.Equal("unknown", evidence.StorageEnvironment.LogStatus);
        Assert.Null(evidence.StorageEnvironment.DatabaseLogAggregate!.AllocatedBytes);
        Assert.Null(evidence.StorageEnvironment.DatabaseLogAggregate.UsedBytes);
        Assert.All(evidence.StorageEnvironment.FileCapacities,
            row => Assert.Equal("unknown", row.Role));
        Assert.DoesNotContain("secret-server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= PrtgCompatibilityProbe.MaxJsonSizeBytes);
    }

    private sealed class TestConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private enum ProbeConnectionMode { Denied, HoldUntilCancelled }

    private sealed class ProbeConnection(ProbeConnectionMode mode) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public override string ConnectionString { get; set; } = "Server=secret-server;Password=secret";
        public override string Database => "private-database";
        public override string DataSource => "secret-server";
        public override string ServerVersion => "unknown";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            if (mode == ProbeConnectionMode.HoldUntilCancelled)
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new FakePermissionException();
        private sealed class FakePermissionException : DbException
        {
            public FakePermissionException() : base("permission denied at secret-server") { }
        }
    }

    private sealed class SqlMetadataConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        public override string ConnectionString { get; set; } = "Server=secret-server;Password=secret";
        public override string Database => "private-database";
        public override string DataSource => "secret-server";
        public override string ServerVersion => "16.0.1000.6";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new SqlMetadataCommand(this);
    }

    private sealed class SqlMetadataCommand(SqlMetadataConnection connection) : DbCommand
    {
        private string _commandText = string.Empty;
        public override string CommandText { get => _commandText; set => _commandText = value; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection DbConnection { get => connection; set => throw new NotSupportedException(); }
        protected override DbParameterCollection DbParameterCollection => null!;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (CommandText.Contains("sys.dm_db_log_space_usage", StringComparison.OrdinalIgnoreCase))
                throw new FakePermissionException();
            var table = new DataTable();
            if (CommandText.Contains("sys.database_files", StringComparison.OrdinalIgnoreCase) &&
                CommandText.Contains("SERVERPROPERTY", StringComparison.OrdinalIgnoreCase))
            {
                table.Columns.Add("version", typeof(string));
                table.Columns.Add("edition", typeof(string));
                table.Columns.Add("engine_edition", typeof(int));
                table.Columns.Add("type_desc", typeof(string));
                table.Columns.Add("size", typeof(long));
                table.Columns.Add("max_size", typeof(long));
                table.Columns.Add("growth", typeof(long));
                table.Columns.Add("is_percent_growth", typeof(bool));
                table.Columns.Add("used_pages", typeof(long));
                table.Rows.Add("16.0.1000.6", "Express Edition", 4, "ROWS", 128L, 256L, 64L, false, 32L);
            }
            else if (CommandText.Contains("sys.dm_os_volume_stats", StringComparison.OrdinalIgnoreCase))
            {
                table.Columns.Add("type_desc", typeof(string));
                table.Columns.Add("total_bytes", typeof(long));
                table.Columns.Add("available_bytes", typeof(long));
            }
            else
            {
                throw new InvalidOperationException("Unexpected SQL metadata query.");
            }
            return table.CreateDataReader();
        }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteDbDataReader(behavior));
        }
        private sealed class FakePermissionException() : DbException("permission denied at secret-server");
    }
}

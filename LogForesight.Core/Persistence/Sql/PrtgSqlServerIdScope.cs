using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// Uses connection-local ID tables for large SQL Server readiness scopes. SQLite and small scopes keep
/// their existing parameterized LINQ membership queries. The callback runs inside the retry strategy so
/// a retry recreates its temporary inputs before rerunning every dependent query.
/// </summary>
internal static class PrtgSqlServerIdScope
{
    private const int InlineIdThreshold = 256;
    private const int MaximumIdCount = 15000;
    private const string HostTable = "#PrtgReadinessHostIds";
    private const string SensorTable = "#PrtgReadinessSensorIds";

    public static TResult Execute<TResult>(Func<LfDbContext> contextFactory,
        IReadOnlyCollection<long> hostIds, IReadOnlyCollection<long>? sensorIds,
        Func<LfDbContext, IReadOnlyCollection<long>, IReadOnlyCollection<long>?, IQueryable<long>?, IQueryable<long>?, TResult> action)
    {
        using var probe = contextFactory();
        var isSqlServer = string.Equals(probe.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal);
        if (!isSqlServer)
            return action(probe, hostIds, sensorIds, null, null);

        var hosts = Canonicalize(hostIds, nameof(hostIds));
        var sensors = sensorIds is null ? null : Canonicalize(sensorIds, nameof(sensorIds));
        var useHostTable = hosts.Length >= InlineIdThreshold;
        var useSensorTable = sensors is { Length: >= InlineIdThreshold };
        if (!useHostTable && !useSensorTable)
            return action(probe, hosts, sensors, null, null);

        var strategy = probe.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            using var context = contextFactory();
            var scope = new Scope(context, useHostTable ? hosts : null, useSensorTable ? sensors : null);
            Exception? operationError = null;
            try
            {
                return action(context, hosts, sensors, scope.HostIds, scope.SensorIds);
            }
            catch (Exception ex)
            {
                operationError = ex;
                throw;
            }
            finally
            {
                try { scope.Dispose(); }
                catch when (operationError is not null) { }
            }
        });
    }

    private static long[] Canonicalize(IReadOnlyCollection<long> ids, string parameterName)
    {
        if (ids.Count > MaximumIdCount || ids.Any(id => id <= 0))
            throw new ArgumentOutOfRangeException(parameterName, $"ID 範圍必須為正數且不超過 {MaximumIdCount} 筆。");
        return ids.Distinct().ToArray();
    }

    private sealed class Scope : IDisposable
    {
        private readonly LfDbContext _context;
        private readonly DbConnection _connection;
        private readonly bool _openedHere;
        private readonly List<string> _tables = [];
        private bool _disposed;

        public Scope(LfDbContext context, IReadOnlyCollection<long>? hostIds, IReadOnlyCollection<long>? sensorIds)
        {
            _context = context;
            _connection = context.Database.GetDbConnection();
            _openedHere = _connection.State != ConnectionState.Open;
            if (_openedHere) context.Database.OpenConnection();
            try
            {
                if (hostIds is not null) CreateTable(HostTable, "IX_PrtgReadinessHostIds", hostIds);
                if (sensorIds is not null) CreateTable(SensorTable, "IX_PrtgReadinessSensorIds", sensorIds);
            }
            catch
            {
                try { Dispose(); }
                catch { /* Preserve the table creation/insertion failure. */ }
                throw;
            }
        }

        public IQueryable<long>? HostIds => _tables.Contains(HostTable)
            ? _context.Database.SqlQueryRaw<long>($"SELECT [Id] AS [Value] FROM {HostTable}")
            : null;

        public IQueryable<long>? SensorIds => _tables.Contains(SensorTable)
            ? _context.Database.SqlQueryRaw<long>($"SELECT [Id] AS [Value] FROM {SensorTable}")
            : null;

        private void CreateTable(string tableName, string indexName, IReadOnlyCollection<long> ids)
        {
            using var command = _connection.CreateCommand();
            command.CommandTimeout = _context.Database.GetCommandTimeout() ?? command.CommandTimeout;
            command.CommandText = $"CREATE TABLE {tableName} ([Id] bigint NOT NULL);";
            command.ExecuteNonQuery();
            _tables.Add(tableName);

            command.CommandText = $"CREATE UNIQUE CLUSTERED INDEX {indexName} ON {tableName} ([Id]); " +
                $"INSERT INTO {tableName} ([Id]) SELECT [Id] FROM OPENJSON(@idsJson) WITH ([Id] bigint '$');";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@idsJson";
            parameter.DbType = DbType.String;
            parameter.Size = -1;
            parameter.Value = JsonSerializer.Serialize(ids);
            command.Parameters.Add(parameter);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Exception? cleanupError = null;
            foreach (var tableName in _tables.AsEnumerable().Reverse())
            {
                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandTimeout = _context.Database.GetCommandTimeout() ?? command.CommandTimeout;
                    command.CommandText = $"DROP TABLE {tableName};";
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    cleanupError ??= ex;
                }
            }

            if (_openedHere)
            {
                try { _context.Database.CloseConnection(); }
                catch (Exception ex) { cleanupError ??= ex; }
            }
            if (cleanupError is not null) throw new InvalidOperationException("清理 readiness 臨時 ID 表失敗。", cleanupError);
        }
    }
}

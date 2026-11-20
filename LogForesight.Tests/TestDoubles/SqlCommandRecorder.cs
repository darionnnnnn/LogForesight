using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LogForesight.Tests;

/// <summary>
/// 共用的 SQL 指令記錄器：記下每道讀取指令（語句＋參數）與讀取呼叫次數。
/// 規模類測試用它回答「一次操作發幾道查詢、讀回多少列」——這兩個數字與機器快慢無關，
/// 可以在小資料集上斷言「成本不隨主機數成長」（第 77 輪批次 0 的 T1）。
/// 查詢計畫守門（<see cref="QueryPlanGuardTests"/>）以同一份記錄重放 EXPLAIN。
/// </summary>
internal sealed class SqlCommandRecorder : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<(string Sql, List<(string Name, object? Value)> Parameters)> _commands = new();
    private long _readCalls;

    public List<(string Sql, List<(string Name, object? Value)> Parameters)> Commands
    {
        get { lock (_gate) return _commands.ToList(); }
    }

    /// <summary>
    /// 自上次 <see cref="Clear"/> 起的讀取呼叫次數（EF 的 <c>ReadCount</c>）。讀到底的指令會多一次回 false 的呼叫，
    /// 所以它是「讀回列數＋讀到底的指令數」；比較兩個主機數下的成長時，這個固定差值不影響結論。
    /// </summary>
    public long ReadCalls
    {
        get { lock (_gate) return _readCalls; }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        lock (_gate) _readCalls += eventData.ReadCount;
        return base.DataReaderDisposing(command, eventData, result);
    }

    /// <summary>
    /// 是否一併記錄不回傳資料列的指令（<c>ExecuteDelete</c>／<c>ExecuteUpdate</c>、INSERT 等）。預設 false，
    /// 避免影響既有以「讀取指令數」為依據的測試；要看這類指令的查詢計畫時才開。
    /// </summary>
    public bool IncludeNonQueries { get; set; }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        if (IncludeNonQueries) Record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (IncludeNonQueries) Record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _commands.Clear();
            _readCalls = 0;
        }
    }

    public List<(string Sql, List<(string Name, object? Value)> Parameters)> Selects()
    {
        lock (_gate)
            return _commands.Where(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>清空後執行 <paramref name="action"/>，回傳這段期間的讀取指令數與讀取呼叫次數。</summary>
    public (int Commands, long ReadCalls) Measure(Action action)
    {
        Clear();
        action();
        lock (_gate) return (_commands.Count, _readCalls);
    }

    private void Record(DbCommand command)
    {
        lock (_gate)
            _commands.Add((command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToList()));
    }
}

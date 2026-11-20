using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 暫時性錯誤（短退避重試有機會成功）一律依資料庫錯誤碼判定，不看訊息文字（第 79 輪批次 D7）。
/// SQLite 看主結果碼 5（BUSY）、6（LOCKED）；SQL Server 看 <c>SqlException.Number</c>（死結 1205、鎖逾時 1222、命令逾時 -2、連線中斷等）。
/// 兩側各一例：錯誤碼符合但訊息毫無關鍵字要判為暫時性；訊息塞滿關鍵字但錯誤碼不符不得判為暫時性。
/// <c>SqlException</c> 沒有公開建構子，測試以反射呼叫其內部建構子並塞入含錯誤碼的 <c>SqlError</c>（見 <see cref="NewSqlException"/>）。
/// </summary>
public sealed class TransientErrorCodeTests
{
    private const string KeywordMessage = "database is locked busy deadlock timeout user-function";

    // ── SQLite ──

    [Theory]
    [InlineData(5)]     // SQLITE_BUSY
    [InlineData(6)]     // SQLITE_LOCKED
    public void SQLite_忙碌或鎖定的錯誤碼判為暫時性_訊息不含關鍵字也成立(int code)
    {
        Assert.True(EfJsonBlobStore.IsTransient(new SqliteException("x", code)));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new SqliteException("x", code))));
    }

    [Theory]
    [InlineData(5, 261)]    // SQLITE_BUSY_RECOVERY：延伸碼低 8 位元是主碼
    [InlineData(6, 262)]    // SQLITE_LOCKED_SHAREDCACHE 的延伸碼形狀
    public void SQLite_延伸碼的主碼是忙碌或鎖定仍判為暫時性(int primary, int extended)
    {
        Assert.True(EfJsonBlobStore.IsTransient(new SqliteException("x", primary, extended)));
    }

    [Theory]
    [InlineData(1)]     // SQLITE_ERROR
    [InlineData(4)]     // SQLITE_ABORT：與 5、6 相鄰的另一側
    [InlineData(7)]     // SQLITE_NOMEM：與 5、6 相鄰的另一側
    [InlineData(11)]    // SQLITE_CORRUPT
    public void SQLite_訊息含關鍵字但錯誤碼不屬暫時性_不判為暫時性(int code)
    {
        Assert.False(EfJsonBlobStore.IsTransient(new SqliteException(KeywordMessage, code)));
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException(KeywordMessage, new SqliteException(KeywordMessage, code))));
    }

    [Fact]
    public void 只有訊息文字沒有資料庫錯誤碼的例外不判為暫時性()
    {
        Assert.False(EfJsonBlobStore.IsTransient(new InvalidOperationException(KeywordMessage)));
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException(KeywordMessage, new Exception(KeywordMessage))));
    }

    // ── SQL Server ──

    [Theory]
    [InlineData(1205)]  // 死結
    [InlineData(1222)]  // 鎖等待逾時
    [InlineData(-2)]    // 命令逾時
    [InlineData(-1)]    // 連線層錯誤
    [InlineData(10054)] // 連線被對方重設
    [InlineData(40613)] // Azure SQL 資料庫暫時無法使用
    public void SQLServer_死結逾時連線中斷的錯誤碼判為暫時性_訊息不含關鍵字也成立(int number)
    {
        Assert.True(EfJsonBlobStore.IsTransient(NewSqlException(number, "x")));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", NewSqlException(number, "x"))));
    }

    [Theory]
    [InlineData(547)]   // 外鍵衝突
    [InlineData(8152)]  // 字串或二進位資料會被截斷
    [InlineData(1204)]  // 與 1205、1222 相鄰但不是暫時性
    [InlineData(1223)]
    [InlineData(0)]
    public void SQLServer_訊息含關鍵字但錯誤碼不屬暫時性_不判為暫時性(int number)
    {
        Assert.False(EfJsonBlobStore.IsTransient(NewSqlException(number, KeywordMessage)));
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException(KeywordMessage, NewSqlException(number, KeywordMessage))));
    }

    [Fact]
    public void 建構出來的SqlException其Number與Message與輸入一致_測試替身本身可信()
    {
        var ex = NewSqlException(1205, "hello");
        Assert.Equal(1205, ex.Number);
        Assert.Contains("hello", ex.Message);
    }

    /// <summary>
    /// 以反射建構帶指定 <see cref="SqlException.Number"/> 與訊息的 <see cref="SqlException"/>：
    /// 先建 <see cref="SqlError"/>（內部建構子，參數除第一個 int 錯誤碼外多半用預設值），放進內部的 <see cref="SqlErrorCollection"/>，
    /// 再呼叫 <see cref="SqlException"/> 的非公開建構子。內部建構子的參數形狀隨 Microsoft.Data.SqlClient 版本而異，所以依型別補預設值。
    /// </summary>
    private static SqlException NewSqlException(int number, string message)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var errorCtor = typeof(SqlError).GetConstructors(All).OrderByDescending(c => c.GetParameters().Length).First();
        var errorArgs = errorCtor.GetParameters().Select((p, i) =>
            i == 0 ? number
            : p.ParameterType == typeof(string) ? message
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
            : null).ToArray();
        var error = (SqlError)errorCtor.Invoke(errorArgs);

        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), All, null, null, null)!;
        typeof(SqlErrorCollection).GetMethod("Add", All)!.Invoke(collection, new object[] { error });

        var exCtor = typeof(SqlException).GetConstructors(All)
            .Single(c => c.GetParameters().Select(p => p.ParameterType)
                .SequenceEqual(new[] { typeof(string), typeof(SqlErrorCollection), typeof(Exception), typeof(Guid) }));
        return (SqlException)exCtor.Invoke(new object?[] { message, collection, null, Guid.NewGuid() });
    }
}

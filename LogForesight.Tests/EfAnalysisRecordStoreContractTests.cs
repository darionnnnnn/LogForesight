using LogForesight;
using LogForesight.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// SQL 後端（EF Core）跑與 JSONL **完全相同**的一組合約測試（docs/DB-PLAN.md 一致性機制 #3）。
///
/// 用 SQLite in-memory：LINQ 保持 provider 中立，SQLite 上通過即代表 store 邏輯正確；
/// SqlServer 專屬行為（migration、連線）在真實環境以 log 驗證。SQLite in-memory 的資料庫
/// 生命週期綁在**開啟中的連線**，所以整個測試實例共用一條連線，每個 context 在其上建立。
/// </summary>
public class EfAnalysisRecordStoreContractTests : AnalysisRecordStoreContractTests
{
    private readonly SqliteConnection _connection;

    public EfAnalysisRecordStoreContractTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options);

    protected override IAnalysisRecordStore CreateStore() =>
        new EfAnalysisRecordStore(NewContext, "sqlite-in-memory");

    public override void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}

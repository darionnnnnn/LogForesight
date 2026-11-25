using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦單成員的主機名鍵篩選（<see cref="EfIssueCaseStore.BuildMemberHostQuery"/>）：
/// SQL Server 只驗 EF 的 <c>ToQueryString</c> 翻譯，未連線實際 SQL Server；SQLite 的結果由合約測試與服務測試負責。
/// </summary>
public class WorkOrderMemberHostQueryTranslationTests
{
    [Fact]
    public void SqlServer翻譯_主機名鍵走host_name_key的IN清單_與工作單條件及狀態條件並存()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True").Options);

        var sql = EfIssueCaseStore.BuildMemberHostQuery(ctx, 7, WorkOrderQueries.StatusActive, new DateTime(2026, 1, 1), new[] { "SRV-A", "SRV-B" }).ToQueryString();

        Assert.Contains("[host_name_key] IN (", sql);
        Assert.Contains("[work_order_id] = ", sql);
        Assert.Contains("[closed_at] IS NULL", sql);
    }

    [Fact]
    public void SqlServer翻譯_不分大小寫靠正規化的host_name_key欄_不對主機名欄套函式()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True").Options);

        var sql = EfIssueCaseStore.BuildMemberHostQuery(ctx, 7, WorkOrderQueries.StatusAll, new DateTime(2026, 1, 1), new[] { "SRV-A" }).ToQueryString();

        // 比對的是 host_name_key（寫入時已轉大寫），不是 UPPER/LOWER(host_name)：兩個 provider 行為一致、索引可用
        Assert.DoesNotContain("UPPER(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOWER(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[host_name] IN", sql);
    }
}

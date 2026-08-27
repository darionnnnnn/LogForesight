using System.Collections.Concurrent;
using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動入庫的上界（回饋三十四輪 A4）：原文長度與單次存檔的列數。
/// 一台吵雜的 DC 一天可達數萬則，原文完全不截斷、整批一次 SaveChanges 時，
/// 記憶體與資料庫體積都沒有上界。
/// </summary>
public class PermissionChangeAppendBoundsTests
{
    private static ConcurrentDictionary<string, byte> Claims() => new(StringComparer.Ordinal);

    private static EventLogEntryData PermEvent(DateTime at, string message) => new()
    {
        EventId = 4756,
        Source = "Microsoft-Windows-Security-Auditing",
        LogName = "Security",
        TimeGenerated = at,
        EntryType = EventLogEntryType.Information,
        Message = message
    };

    private static string MessageOfLength(int length)
    {
        const string head = "已將成員新增到安全性通用群組。\r\n群組名稱:\tEnterprise Admins\r\n成員名稱:\tCONTOSO\\AdminUser\r\n詳細資料:\t";
        return head + new string('詳', Math.Max(0, length - head.Length));
    }

    [Fact]
    public void 原文未超過上限時逐字保留()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);
        var message = MessageOfLength(8000);

        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC01", WebHost.OsWindows, new List<EventLogEntryData> { PermEvent(at, message) }, at.Date);

        var record = Assert.Single(store.Query(null, null, 1000));
        Assert.Equal(message, record.RawText);
    }

    [Fact]
    public void 原文超過上限時截斷且看得出被截斷()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);
        var message = MessageOfLength(20000);

        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC01", WebHost.OsWindows, new List<EventLogEntryData> { PermEvent(at, message) }, at.Date);

        var record = Assert.Single(store.Query(null, null, 1000));
        Assert.NotNull(record.RawText);
        Assert.True(record.RawText!.Length < message.Length, "超長原文必須被截斷");
        Assert.EndsWith("...", record.RawText);
        // 截斷不能砍掉可讀的開頭
        Assert.StartsWith("已將成員新增到安全性通用群組。", record.RawText);
    }

    [Fact]
    public void 超過一批的寫入全數入庫且建立時間一致()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 0, 0, 0);

        // 1200 筆 > 2 批（每批 500）；時間各不相同以免被去重折疊
        var records = Enumerable.Range(0, 1200).Select(i => new PermissionChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            HostName = "SRV-DC01",
            DetectedAt = at.AddSeconds(i),
            Target = "Enterprise Admins",
            ChangeType = "成員新增",
            AlertText = $"第 {i} 筆",
            Source = PermissionChangeSources.Netiq,
            EventId = 4756
        }).ToList();

        store.AppendChanges(records);

        Assert.Equal(1200, store.Query(null, null, 5000).Count);

        // 分批只是寫入策略：同一次偵測的列必須共用同一個 created_at（保留期清理看它）
        using var ctx = fixture.NewContext();
        Assert.Single(ctx.PermissionChanges.Select(r => r.CreatedAt).Distinct().ToList());
    }

    [Fact]
    public void 不足一批的寫入正常入庫()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 0, 0, 0);

        store.AppendChanges(Enumerable.Range(0, 3).Select(i => new PermissionChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            HostName = "SRV-DC01",
            DetectedAt = at.AddSeconds(i),
            Target = "Enterprise Admins",
            ChangeType = "成員新增",
            AlertText = $"第 {i} 筆",
            Source = PermissionChangeSources.Netiq,
            EventId = 4756
        }).ToList());

        Assert.Equal(3, store.Query(null, null, 1000).Count);
    }
}

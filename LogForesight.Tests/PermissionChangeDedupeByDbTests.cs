using System.Collections.Concurrent;
using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動去重改由資料庫逐主機日現查（回饋三十四輪 A2）。
/// 舊做法是開跑時把整個查詢窗的去重鍵全載進記憶體，正式環境每日近十萬筆、回望天數一拉大
/// 就是千萬筆鍵、數 GB 且整趟不釋放。這裡驗證換掉載入來源後，去重的**結果**不變：
/// 同一主機日重跑不重複、不同主機不互相誤判、不同日不互相誤判。
/// </summary>
public class PermissionChangeDedupeByDbTests
{
    private static ConcurrentDictionary<string, byte> Claims() => new(StringComparer.Ordinal);

    private static List<EventLogEntryData> MemberAddEvent(DateTime at, string member) => new()
    {
        new EventLogEntryData
        {
            EventId = 4756,
            Source = "Microsoft-Windows-Security-Auditing",
            LogName = "Security",
            TimeGenerated = at,
            EntryType = EventLogEntryType.Information,
            Message = $"已將成員新增到安全性通用群組。\r\n群組名稱:\tEnterprise Admins\r\n成員名稱:\t{member}"
        }
    };

    private static int CountFor(PermissionChangeStore store, string hostName) =>
        store.Query(null, null, 1000)
            .Count(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void 同一主機日跨執行重跑不產生重複紀錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);
        var events = MemberAddEvent(at, "CONTOSO\\AdminUser");

        // 兩次「執行」各自有全新的佔位集合（跨執行＝行程內的佔位不會留下來）
        HostDayPostProcessor.RecordPermissionChanges(store, Claims(), "SRV-DC01", WebHost.OsWindows, events, at.Date);
        HostDayPostProcessor.RecordPermissionChanges(store, Claims(), "SRV-DC01", WebHost.OsWindows, events, at.Date);

        Assert.Equal(1, CountFor(store, "SRV-DC01"));
    }

    [Fact]
    public void 同一次執行內同主機日重入只處理一次()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);
        var events = MemberAddEvent(at, "CONTOSO\\AdminUser");
        var claims = Claims();

        HostDayPostProcessor.RecordPermissionChanges(store, claims, "SRV-DC01", WebHost.OsWindows, events, at.Date);
        HostDayPostProcessor.RecordPermissionChanges(store, claims, "SRV-DC01", WebHost.OsWindows, events, at.Date);

        Assert.Equal(1, CountFor(store, "SRV-DC01"));
    }

    [Fact]
    public void 不同主機同時間同內容各自寫入不被誤判為重複()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);
        var events = MemberAddEvent(at, "CONTOSO\\AdminUser");

        HostDayPostProcessor.RecordPermissionChanges(store, Claims(), "SRV-DC01", WebHost.OsWindows, events, at.Date);
        HostDayPostProcessor.RecordPermissionChanges(store, Claims(), "SRV-DC02", WebHost.OsWindows, events, at.Date);

        Assert.Equal(1, CountFor(store, "SRV-DC01"));
        Assert.Equal(1, CountFor(store, "SRV-DC02"));
    }

    [Fact]
    public void 同主機不同日的相同內容各自寫入()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var day1 = new DateTime(2026, 8, 19, 10, 30, 0);
        var day2 = day1.AddDays(1);

        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC01", WebHost.OsWindows, MemberAddEvent(day1, "CONTOSO\\AdminUser"), day1.Date);
        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC01", WebHost.OsWindows, MemberAddEvent(day2, "CONTOSO\\AdminUser"), day2.Date);

        Assert.Equal(2, CountFor(store, "SRV-DC01"));
    }

    [Fact]
    public void 去重鍵查詢只回傳指定主機與指定區間內的列()
    {
        using var fixture = new EfSqliteFixture();
        var store = new PermissionChangeStore(fixture.NewContext);
        var at = new DateTime(2026, 8, 19, 10, 30, 0);

        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC01", WebHost.OsWindows, MemberAddEvent(at, "CONTOSO\\AdminUser"), at.Date);
        HostDayPostProcessor.RecordPermissionChanges(
            store, Claims(), "SRV-DC02", WebHost.OsWindows, MemberAddEvent(at, "CONTOSO\\AdminUser"), at.Date);

        // 命中：同主機、時間落在區間內
        Assert.Single(store.GetDedupeKeysForHost("SRV-DC01", at.AddMinutes(-5), at.AddMinutes(5)));
        // 區間外不回傳
        Assert.Empty(store.GetDedupeKeysForHost("SRV-DC01", at.AddDays(1), at.AddDays(2)));
        // 主機不同不回傳（另一台的列不會混進來）
        Assert.Single(store.GetDedupeKeysForHost("SRV-DC02", at.AddMinutes(-5), at.AddMinutes(5)));
        // 沒有這台主機
        Assert.Empty(store.GetDedupeKeysForHost("SRV-NOPE", at.AddMinutes(-5), at.AddMinutes(5)));
    }
}

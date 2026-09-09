using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 「同步結構與對應」服務（docs/PRTG-SPEC.md §5a）：前置檢查、與取數執行的互斥、
/// 上次結果的持久化。這些都在背景工作的入口，跑錯的症狀是「按了沒反應」或
/// 「兩邊同時寫鏡像」，不會有例外。
/// </summary>
public class PrtgStructureSyncServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settingsStore;

    public PrtgStructureSyncServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PrtgStructureSyncStatusStore StatusStore() =>
        new(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));

    private PrtgStructureSyncService Create(SchedulerRunState? schedulerState = null) =>
        new(_settingsStore, _backend, new PrtgStructureSyncRunState(),
            schedulerState ?? new SchedulerRunState(),
            new HostStore(_backend.Blob("hosts")), StatusStore());

    private void EnablePrtg()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
        });
    }

    [Fact]
    public void TryStart_PRTG未啟用時拒絕()
    {
        var service = Create();

        Assert.False(service.TryStart(out var error));
        Assert.Contains("PRTG 未啟用", error);
    }

    [Fact]
    public void TryStart_未設定連線位址時拒絕()
    {
        _settingsStore.Update(s => s.PrtgEnabled = true);
        var service = Create();

        Assert.False(service.TryStart(out var error));
        Assert.Contains("連線位址", error);
    }

    [Fact]
    public void TryStart_未設定認證時拒絕()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
        });
        var service = Create();

        Assert.False(service.TryStart(out var error));
        Assert.Contains("認證", error);
    }

    /// <summary>
    /// 取數執行進行中不得啟動同步：那一趟自己就會同步結構與對應，
    /// 兩邊同時寫同一批鏡像表沒有意義，而且會讓對應算在寫到一半的鏡像上。
    /// </summary>
    [Fact]
    public void TryStart_取數執行進行中時拒絕()
    {
        EnablePrtg();
        var schedulerState = new SchedulerRunState();
        Assert.True(schedulerState.TryBeginRun("manual:tester", out _));

        var service = Create(schedulerState);

        Assert.False(service.TryStart(out var error));
        Assert.Contains("取數執行進行中", error);
    }

    [Fact]
    public void TryStart_取數執行結束後可以啟動()
    {
        EnablePrtg();
        var schedulerState = new SchedulerRunState();
        Assert.True(schedulerState.TryBeginRun("manual:tester", out _));
        schedulerState.EndRun(new RunOutcome(true, null, "manual:tester", DateTime.Now));

        var service = Create(schedulerState);

        Assert.True(service.TryStart(out var error));
        Assert.Null(error);
    }

    /// <summary>
    /// 上次結果要持久化：站台重啟後畫面仍要說得出上次同步是什麼時候、對應成果如何。
    /// **從未執行過與執行過但零筆是兩件事**，靠 CompletedAt 有沒有被寫過分辨。
    /// </summary>
    [Fact]
    public void 上次結果持久化_重建服務仍讀得到()
    {
        // 從未執行過
        Assert.Null(StatusStore().GetOrNull());
        var freshStatus = Create().GetStatus();
        Assert.Null(freshStatus.LastCompletedAt);

        // 寫入一筆「執行過但零筆」
        StatusStore().Update(x =>
        {
            x.CompletedAt = new DateTime(2026, 9, 9, 10, 0, 0);
            x.Success = true;
            x.Devices = 0;
            x.Sensors = 0;
            x.MapDate = new DateTime(2026, 9, 9);
            x.MapOk = 0;
            x.MapSkippedNoIp = 2;
            x.MapSkippedExcluded = 1;
            x.MapSkippedManualSibling = 3;
        });

        // 重建服務（模擬站台重啟）後仍讀得到
        var status = Create().GetStatus();

        Assert.Equal(new DateTime(2026, 9, 9, 10, 0, 0), status.LastCompletedAt);
        Assert.True(status.LastSuccess);
        Assert.Equal(0, status.LastDevices);
        Assert.Equal(0, status.LastMapOk);
        // 三種略過加總成一個數字給畫面
        Assert.Equal(6, status.LastMapSkipped);
    }

    [Fact]
    public void 等待閘門_同步未執行時立即返回()
    {
        var service = Create();

        Assert.False(service.IsRunning);
        // 沒有執行中就不該等待，逾時保護不該被觸發
        var task = service.WaitUntilIdleAsync(CancellationToken.None);
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task 等待閘門_取消會穿透()
    {
        EnablePrtg();
        var service = Create();
        Assert.True(service.TryStart(out _));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WaitUntilIdleAsync(cts.Token));

        service.Cancel();
    }

    /// <summary>
    /// 重算入口在 PRTG 未啟用時零成本返回（docs/PRTG-SPEC.md §4）——
    /// 沒有鏡像資料，重算只會把空的對應寫一次，反而抹掉既有結果。
    /// </summary>
    [Fact]
    public void 重算入口_PRTG未啟用時不做事()
    {
        var prtgStore = _backend.PrtgStore();
        var day = DateTime.Today;
        prtgStore.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 5001, HostId = 9, MapStatus = PrtgMapStatus.Ok, Ip = "10.5.0.1" }
        });

        // PRTG 未啟用（預設）
        var refresher = new PrtgHostMapRefresher(_settingsStore, _backend);
        Assert.Null(refresher.TryRefreshToday());

        // 既有對應原封不動——若真的重算了，鏡像是空的會把它洗掉
        var row = Assert.Single(prtgStore.GetHostMapForDate(day));
        Assert.Equal(9, row.HostId);
    }

    [Fact]
    public void 重算入口_PRTG啟用時會重算()
    {
        EnablePrtg();
        var prtgStore = _backend.PrtgStore();
        var day = DateTime.Today;
        prtgStore.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 5002, HostId = 9, MapStatus = PrtgMapStatus.Ok, Ip = "10.5.0.2" }
        });

        Assert.Null(new PrtgHostMapRefresher(_settingsStore, _backend).TryRefreshToday());

        // 鏡像是空的，重算後該日對應應該被清空
        Assert.Empty(prtgStore.GetHostMapForDate(day));
    }
}

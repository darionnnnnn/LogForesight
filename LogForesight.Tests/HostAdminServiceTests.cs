using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機維護的輸入驗證與合併守則（docs/WEB-SPEC.md §9.8）。
/// </summary>
public class HostAdminServiceTests : IDisposable
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _groups = new();
    private readonly RecordingAuditService _audit = new();
    private readonly EfSqliteFixture _fx = new();
    private readonly string _snapshotDir = Path.Combine(Path.GetTempPath(), "lf-host-snapshot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _snapshotService?.Dispose();
        _fx.Dispose();
        if (Directory.Exists(_snapshotDir)) Directory.Delete(_snapshotDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>記錄「重算今天的 PRTG 對應」被叫了幾次，供斷言觸發條件（docs/PRTG-SPEC.md §4）。</summary>
    private sealed class CountingMapRefresher : IPrtgHostMapRefresher
    {
        public int Calls { get; private set; }

        /// <summary>設定後由 <see cref="TryRefreshToday"/> 回傳，用來驗警告有沒有被帶進回應。</summary>
        public string? WarningToReturn { get; set; }

        public string? TryRefreshToday()
        {
            Calls++;
            return WarningToReturn;
        }
    }

    private readonly CountingMapRefresher _mapRefresher = new();
    private readonly StorageBackend _backend;
    private readonly CountingSnapshotService _snapshotService;

    public HostAdminServiceTests()
    {
        Directory.CreateDirectory(_snapshotDir);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_snapshotDir, "snapshot.db")}" }, _snapshotDir);
        _snapshotService = new CountingSnapshotService(_backend, _hosts);
    }

    private sealed class CountingSnapshotService : PrtgSnapshotHostedService
    {
        public int Calls { get; private set; }

        public CountingSnapshotService(StorageBackend backend, IHostStore hostStore)
            : base(
                new FakeSystemSettingsStore(),
                backend,
                new SchedulerRunState(),
                new PrtgStructureSyncService(new FakeSystemSettingsStore(), backend, new PrtgStructureSyncRunState(), new SchedulerRunState(), hostStore, new PrtgStructureSyncStatusStore(backend.Blob("sync_status")), new PrtgBackfillRunState(), new FakeSentinelStore(), new DataVersionStamp(), new FakeHostApplicationLifetime()),
                new PrtgBackfillService(new FakeSystemSettingsStore(), backend, new PrtgBackfillRunState(), new PrtgProbeRunState(), hostStore, new SchedulerRunState(), new PrtgStructureSyncRunState(), new FakeSentinelStore(), null!),
                hostStore,
                new FakeSentinelStore(),
                new PrtgProbeRunState(),
                new FakeHostApplicationLifetime())
        {
        }

        public override void RequestScopeRefresh()
        {
            Calls++;
            base.RequestScopeRefresh();
        }
    }

    private HostAdminService Create() => new(
        _hosts,
        _groups,
        new FakeUserStore(),
        new FakeNetiqServerCatalog("SENTINEL-A"),
        new FakeNetiqHostServiceForAdmin(),
        _audit,
        new UserDisplayNameService(new FakeSystemSettingsStore()),
        new EfPrtgStore(_fx.NewContext),
        _mapRefresher,
        new FakeSystemSettingsStore(), TestPermissionStamps.Shared,
        _snapshotService);

    private HostAdminService CreateWithPrtg(EfPrtgStore prtgStore) => new(
        _hosts,
        _groups,
        new FakeUserStore(),
        new FakeNetiqServerCatalog("SENTINEL-A"),
        new FakeNetiqHostServiceForAdmin(),
        _audit,
        new UserDisplayNameService(new FakeSystemSettingsStore()),
        prtgStore,
        _mapRefresher,
        new FakeSystemSettingsStore(), TestPermissionStamps.Shared,
        _snapshotService);

    // ── 輸入驗證 ─────────────────────────────────────────────────────────────
    //
    // 這一條路徑與 NetiqHostService 寫的是同一份資料。驗證只掛在其中一條的話，
    // 從編輯表單就能繞過去存進不合格的值——而不合格的 IP／Sentinel 的後果是
    // 這台主機永遠查無資料，且完全沒有跡象。

    [Fact]
    public void SaveHost_IP格式不合法_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", IpAddress = "10.1" }));

        Assert.Contains("10.1", ex.Message);
    }

    [Fact]
    public void SaveHost_Sentinel不在名單中_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", NetiqServer = "SENTINEL-X" }));

        Assert.Contains("SENTINEL-A", ex.Message);
    }

    [Fact]
    public void SaveHost_IP與Sentinel皆留空_允許()
    {
        var result = Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", RoleDesc = "檔案伺服器" });

        Assert.Equal("SRV-01", result.HostName);
        Assert.Null(result.IpAddress);
        Assert.Null(result.NetiqServer);
    }

    // ── 合併與解除 ───────────────────────────────────────────────────────────

    [Fact]
    public void MergeHost_來源已併入其他主機_擋下()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var c = _hosts.Upsert(new WebHost { HostName = "C" });
        service.MergeHost(a.HostId, b.HostId);

        Assert.Throws<DomainException>(() => service.MergeHost(a.HostId, c.HostId));
    }

    /// <summary>
    /// 目標本身是墓碑會形成 A→B→C 的鏈。查詢的別名展開認得整條鏈（歷史不會掉），
    /// 但鏈對使用者是純粹的困惑——併入一台已停用的主機，畫面上看不出資料最後去了哪。
    /// </summary>
    [Fact]
    public void MergeHost_目標本身是墓碑_擋下並指向最終主機()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var c = _hosts.Upsert(new WebHost { HostName = "C" });
        service.MergeHost(b.HostId, c.HostId);

        var ex = Assert.Throws<DomainException>(() => service.MergeHost(a.HostId, b.HostId));

        Assert.Contains("最終", ex.Message);
    }

    [Fact]
    public void MergeHost_來源與目標相同_擋下()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().MergeHost(host.HostId, host.HostId));
    }

    [Fact]
    public void UnmergeHost_恢復啟用並清除墓碑標記()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        service.MergeHost(a.HostId, b.HostId);

        service.UnmergeHost(a.HostId);

        var after = _hosts.Get(a.HostId)!;
        Assert.Null(after.MergedInto);
        Assert.True(after.Active);
    }

    [Fact]
    public void UnmergeHost_未曾合併_擋下()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().UnmergeHost(host.HostId));
    }

    // ── 未回報篩選（docs/archive/HISTORY.md 定案 9：新主機寬限期）───────────

    [Fact]
    public void GetHosts_剛匯入未滿寬限期的主機_不算未回報()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.1", Active = true, CreatedAt = DateTime.Now.AddHours(-1)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetHosts_建立超過寬限期仍未回報_算未回報()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.2", Active = true, CreatedAt = DateTime.Now.AddHours(-25)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Single(result.Items);
    }

    [Fact]
    public void GetHosts_已回報過的主機_寬限期不適用_沿用原本兩天判定()
    {
        // 剛建立（1 小時前）但曾經回報過、且那次回報已是 3 天前——寬限期只管「從未回報過」的主機
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.3", Active = true,
            CreatedAt = DateTime.Now.AddHours(-1), LastReportAt = DateTime.Now.AddDays(-3)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Single(result.Items);
    }

    // ── 表格排序（表頭點擊排序，取代原本獨立的排序下拉）─────────────────

    [Fact]
    public void GetHosts_依IP升冪排序()
    {
        _hosts.Upsert(new WebHost { HostName = "C", IpAddress = "10.0.0.30" });
        _hosts.Upsert(new WebHost { HostName = "A", IpAddress = "10.0.0.10" });
        _hosts.Upsert(new WebHost { HostName = "B", IpAddress = "10.0.0.20" });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "ip", Dir = "asc" });

        Assert.Equal(new[] { "A", "B", "C" }, result.Items.Select(h => h.HostName));
    }

    [Fact]
    public void GetHosts_Dir為desc時整體反轉()
    {
        _hosts.Upsert(new WebHost { HostName = "A" });
        _hosts.Upsert(new WebHost { HostName = "B" });
        _hosts.Upsert(new WebHost { HostName = "C" });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "name", Dir = "desc" });

        Assert.Equal(new[] { "C", "B", "A" }, result.Items.Select(h => h.HostName));
    }

    [Fact]
    public void GetHosts_依最近回報排序_null視為最舊()
    {
        _hosts.Upsert(new WebHost { HostName = "無回報" });
        _hosts.Upsert(new WebHost { HostName = "有回報", LastReportAt = DateTime.Now });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "lastReport", Dir = "desc" });

        Assert.Equal(new[] { "有回報", "無回報" }, result.Items.Select(h => h.HostName));
    }

    // ── 批次改群組（docs/archive/FEEDBACK-5-PLAN.md §8）─────────────────────────────

    [Fact]
    public void SetGroupsBatch_加入模式_與既有群組取聯集()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var deptB = _groups.Upsert(new HostGroup { GroupName = "部門B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", GroupIds = new List<long> { deptA.GroupId } });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        var result = Create().SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptB.GroupId }, "add");

        Assert.Equal(2, result.UpdatedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { deptA.GroupId, deptB.GroupId }, _hosts.Get(a.HostId)!.GroupIds);
        Assert.Equal(new[] { deptB.GroupId }, _hosts.Get(b.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_取代模式_改為僅勾選的群組()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var deptB = _groups.Upsert(new HostGroup { GroupName = "部門B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", GroupIds = new List<long> { deptA.GroupId } });

        Create().SetGroupsBatch(new[] { a.HostId }, new[] { deptB.GroupId }, "replace");

        Assert.Equal(new[] { deptB.GroupId }, _hosts.Get(a.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_已併入其他主機的主機_略過並回報()
    {
        var service = Create();
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        service.MergeHost(a.HostId, b.HostId);

        var result = service.SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptA.GroupId }, "add");

        Assert.Equal(1, result.UpdatedCount);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("A", skipped.HostName);
        Assert.Equal(new[] { deptA.GroupId }, _hosts.Get(b.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_群組不存在_擋下()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(new[] { a.HostId }, new long[] { 999 }, "add"));
    }

    [Fact]
    public void SetGroupsBatch_模式不合法_擋下()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(new[] { a.HostId }, Array.Empty<long>(), "remove"));
    }

    [Fact]
    public void SetGroupsBatch_未勾選任何主機_擋下()
    {
        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(Array.Empty<long>(), Array.Empty<long>(), "add"));
    }

    [Fact]
    public void SetGroupsBatch_寫入一筆彙總稽核紀錄()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        Create().SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptA.GroupId }, "add");

        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("部門A", entry.Summary);
        Assert.Contains("2 台", entry.Summary);
    }

    // ── 分級（回饋十九輪批次G）─────────────────────────────────────────────

    [Fact]
    public void SaveHost_分級不合法_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", Tier = "gold" }));

        Assert.Contains("gold", ex.Message);
    }

    [Fact]
    public void SaveHost_未指定分級_預設一般()
    {
        var result = Create().SaveHost(new SaveHostRequest { HostName = "SRV-01" });

        Assert.Equal("standard", result.Tier);
        Assert.Equal("一般", result.TierText);
    }

    [Fact]
    public void SaveHost_可設定為核心分級()
    {
        var result = Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", Tier = "core" });

        Assert.Equal("core", result.Tier);
        Assert.Equal("核心", result.TierText);
    }

    /// <summary>
    /// 迴歸測試：Upsert 對「已存在」主機走逐欄複製分支，這一分支曾漏抄 Tier——
    /// 新主機因為走 Add 分支（直接用傳入物件）不會踩到，只有編輯既有主機才會現形，
    /// 這正是原本的 bug 在單元測試裡沒被抓到、要靠瀏覽器對既有主機實測才發現的原因。
    /// </summary>
    [Fact]
    public void SaveHost_編輯既有主機的分級_不會被靜默丟棄()
    {
        var service = Create();
        service.SaveHost(new SaveHostRequest { HostName = "SRV-01", Tier = "standard" });

        var result = service.SaveHost(new SaveHostRequest { HostName = "SRV-01", Tier = "core" });

        Assert.Equal("core", result.Tier);
        Assert.Equal("core", _hosts.FindByName("SRV-01")!.Tier);
    }

    [Fact]
    public void SetTierBatch_套用到選取的主機()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        var result = Create().SetTierBatch(new[] { a.HostId, b.HostId }, "core");

        Assert.Equal(2, result.UpdatedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal("core", _hosts.Get(a.HostId)!.Tier);
        Assert.Equal("core", _hosts.Get(b.HostId)!.Tier);
    }

    [Fact]
    public void SetTierBatch_已併入其他主機的主機_略過並回報()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        service.MergeHost(a.HostId, b.HostId);

        var result = service.SetTierBatch(new[] { a.HostId, b.HostId }, "test");

        Assert.Equal(1, result.UpdatedCount);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("A", skipped.HostName);
        Assert.Equal("test", _hosts.Get(b.HostId)!.Tier);
    }

    [Fact]
    public void SetTierBatch_分級不合法_擋下()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().SetTierBatch(new[] { a.HostId }, "gold"));
    }

    [Fact]
    public void SetTierBatch_未勾選任何主機_擋下()
    {
        Assert.Throws<DomainException>(() => Create().SetTierBatch(Array.Empty<long>(), "core"));
    }

    [Fact]
    public void SetTierBatch_寫入一筆彙總稽核紀錄()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        Create().SetTierBatch(new[] { a.HostId, b.HostId }, "core");

        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("核心", entry.Summary);
        Assert.Contains("2 台", entry.Summary);
    }

    // ── PRTG 對應篩選 ────────────────────────────────────────────────────────

    [Fact]
    public void GetHosts_PRTG篩選mapped_只回有ok對應的主機_雙面斷言()
    {
        using var fx = new EfSqliteFixture();
        var prtgStore = new EfPrtgStore(fx.NewContext);
        var now = DateTime.Now;

        var a = _hosts.Upsert(new WebHost { HostName = "SRV-OK", Active = true });
        var b = _hosts.Upsert(new WebHost { HostName = "SRV-CONFLICT", Active = true });
        var c = _hosts.Upsert(new WebHost { HostName = "SRV-NOMAP", Active = true });

        prtgStore.ReplaceHostMapForDate(now, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = a.HostId, HostName = a.HostName, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1002, HostId = b.HostId, HostName = b.HostName, MapStatus = PrtgMapStatus.Conflict },
            new() { DeviceObjid = 1003, HostId = null, HostName = null, MapStatus = PrtgMapStatus.Unmatched }
        });

        var service = CreateWithPrtg(prtgStore);
        var result = service.GetHosts(new HostSearchRequest { PrtgMap = "mapped" });
        var ids = result.Items.Select(h => h.HostId).ToList();

        Assert.Contains(a.HostId, ids);
        Assert.DoesNotContain(b.HostId, ids);
        Assert.DoesNotContain(c.HostId, ids);
    }

    [Fact]
    public void GetHosts_PRTG篩選unmatched_只回無ok對應的主機_雙面斷言()
    {
        using var fx = new EfSqliteFixture();
        var prtgStore = new EfPrtgStore(fx.NewContext);
        var now = DateTime.Now;

        var a = _hosts.Upsert(new WebHost { HostName = "SRV-OK", Active = true });
        var b = _hosts.Upsert(new WebHost { HostName = "SRV-CONFLICT", Active = true });
        var c = _hosts.Upsert(new WebHost { HostName = "SRV-NOMAP", Active = true });

        prtgStore.ReplaceHostMapForDate(now, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = a.HostId, HostName = a.HostName, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1002, HostId = b.HostId, HostName = b.HostName, MapStatus = PrtgMapStatus.Conflict },
            new() { DeviceObjid = 1003, HostId = null, HostName = null, MapStatus = PrtgMapStatus.Unmatched }
        });

        var service = CreateWithPrtg(prtgStore);
        var result = service.GetHosts(new HostSearchRequest { PrtgMap = "unmatched" });
        var ids = result.Items.Select(h => h.HostId).ToList();

        Assert.DoesNotContain(a.HostId, ids);
        Assert.Contains(b.HostId, ids);
        Assert.Contains(c.HostId, ids);
    }

    [Fact]
    public void GetHosts_PRTG篩選傳無效值_當作沒篩回全部主機()
    {
        using var fx = new EfSqliteFixture();
        var prtgStore = new EfPrtgStore(fx.NewContext);
        var now = DateTime.Now;

        var a = _hosts.Upsert(new WebHost { HostName = "SRV-OK", Active = true });
        var b = _hosts.Upsert(new WebHost { HostName = "SRV-CONFLICT", Active = true });

        prtgStore.ReplaceHostMapForDate(now, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = a.HostId, HostName = a.HostName, MapStatus = PrtgMapStatus.Ok }
        });

        var service = CreateWithPrtg(prtgStore);
        var result = service.GetHosts(new HostSearchRequest { PrtgMap = "invalid_filter_value" });

        Assert.Equal(2, result.Total);
        var ids = result.Items.Select(h => h.HostId).ToList();
        Assert.Contains(a.HostId, ids);
        Assert.Contains(b.HostId, ids);
    }

    [Fact]
    public void GetHosts_無任何PRTG對應資料時_PRTG篩選mapped回空清單()
    {
        using var fx = new EfSqliteFixture();
        var prtgStore = new EfPrtgStore(fx.NewContext);

        var a = _hosts.Upsert(new WebHost { HostName = "SRV-A", Active = true });
        var b = _hosts.Upsert(new WebHost { HostName = "SRV-B", Active = true });

        // prtgStore 完全沒有任何對應資料
        var service = CreateWithPrtg(prtgStore);
        var result = service.GetHosts(new HostSearchRequest { PrtgMap = "mapped" });

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    // ── PRTG 對應的重算觸發（docs/PRTG-SPEC.md §4）──────────────────────────
    //
    // 對應的另一半來自主機主檔：IP 或啟用狀態一改，今天的對應就過期了。
    // 不重算的話要等到隔天夜間批次才對得上，而畫面上完全沒有跡象。

    [Fact]
    public void 新增有IP的主機會重算今日對應()
    {
        var service = Create();

        service.SaveHost(new SaveHostRequest
        {
            HostName = "new-host",
            IpAddress = "10.20.30.40",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });

        Assert.Equal(1, _mapRefresher.Calls);
    }

    [Fact]
    public void 只改角色描述不重算()
    {
        var service = Create();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "desc-host",
            IpAddress = "10.20.30.41",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });
        var callsAfterCreate = _mapRefresher.Calls;

        service.SaveHost(new SaveHostRequest
        {
            HostName = "desc-host",
            IpAddress = "10.20.30.41",
            NetiqServer = "SENTINEL-A",
            RoleDesc = "改了描述",
            Os = "windows",
            Tier = "standard",
            Active = true
        });

        Assert.Equal(callsAfterCreate, _mapRefresher.Calls);
    }

    [Fact]
    public void 改IP會重算今日對應()
    {
        var service = Create();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "ip-host",
            IpAddress = "10.20.30.42",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });
        var callsAfterCreate = _mapRefresher.Calls;

        service.SaveHost(new SaveHostRequest
        {
            HostName = "ip-host",
            IpAddress = "10.20.30.99",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });

        Assert.Equal(callsAfterCreate + 1, _mapRefresher.Calls);
    }

    [Fact]
    public void 停用主機會重算今日對應()
    {
        // 已停用的主機不參與對應，停用後那筆對應要跟著消失
        var service = Create();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "off-host",
            IpAddress = "10.20.30.43",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });
        var callsAfterCreate = _mapRefresher.Calls;

        service.SaveHost(new SaveHostRequest
        {
            HostName = "off-host",
            IpAddress = "10.20.30.43",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = false
        });

        Assert.Equal(callsAfterCreate + 1, _mapRefresher.Calls);
    }

    [Fact]
    public void 合併與解除合併都會重算今日對應()
    {
        // 已合併（有墓碑）的主機不參與對應，解除後又恢復資格——兩個方向都要重算
        var service = Create();
        var a = service.SaveHost(new SaveHostRequest
        {
            HostName = "merge-a", IpAddress = "10.30.1.1", NetiqServer = "SENTINEL-A",
            Os = "windows", Tier = "standard", Active = true
        });
        var b = service.SaveHost(new SaveHostRequest
        {
            HostName = "merge-b", IpAddress = "10.30.1.2", NetiqServer = "SENTINEL-A",
            Os = "windows", Tier = "standard", Active = true
        });

        var beforeMerge = _mapRefresher.Calls;
        service.MergeHost(a.HostId, b.HostId);
        Assert.Equal(beforeMerge + 1, _mapRefresher.Calls);

        var beforeUnmerge = _mapRefresher.Calls;
        service.UnmergeHost(a.HostId);
        Assert.Equal(beforeUnmerge + 1, _mapRefresher.Calls);
    }

    /// <summary>
    /// 重算失敗只是「對應要等下次夜間批次才跟上」，主機本身已經存好了——
    /// 但這件事要說出來，否則使用者改完 IP 看到 PRTG 區塊還是舊的，會以為存檔沒生效。
    /// </summary>
    [Fact]
    public void 重算失敗時警告帶進主機儲存的回應()
    {
        _mapRefresher.WarningToReturn = "重算今日 PRTG 對應失敗: 資料庫忙碌";
        var service = Create();

        var dto = service.SaveHost(new SaveHostRequest
        {
            HostName = "warn-host",
            IpAddress = "10.40.1.1",
            NetiqServer = "SENTINEL-A",
            Os = "windows",
            Tier = "standard",
            Active = true
        });

        Assert.Equal("重算今日 PRTG 對應失敗: 資料庫忙碌", dto.RemapWarning);
    }

    [Fact]
    public void 不需要重算時回應不帶警告()
    {
        _mapRefresher.WarningToReturn = "不該出現";
        var service = Create();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "nowarn-host", IpAddress = "10.40.1.2", NetiqServer = "SENTINEL-A",
            Os = "windows", Tier = "standard", Active = true
        });

        // 第二次只改描述——不重算，也就不該冒出警告
        var dto = service.SaveHost(new SaveHostRequest
        {
            HostName = "nowarn-host", IpAddress = "10.40.1.2", NetiqServer = "SENTINEL-A",
            RoleDesc = "只改描述", Os = "windows", Tier = "standard", Active = true
        });

        Assert.Null(dto.RemapWarning);
    }

    // ── 未回報主機的 PRTG 現況提示 ─────────────────────────────────────────────

    /// <summary>
    /// 三台未回報（Ping Down＋traffic Up／Ping Up＋traffic Down／無 ok 對應）＋一台正常回報
    /// （對應到 Ping Down 的 device，用來證明非未回報主機不算提示）。
    /// </summary>
    internal static (WebHost Down, WebHost Up, WebHost NoMap, WebHost Normal) SeedSilentPrtgScenario(
        IHostStore hosts, EfPrtgStore seedStore, DateTime syncedAt)
    {
        var now = DateTime.Now;
        var down = hosts.Upsert(new WebHost { HostName = "SILENT-DOWN", Active = true, CreatedAt = now.AddDays(-30), LastReportAt = now.AddDays(-5) });
        var up = hosts.Upsert(new WebHost { HostName = "SILENT-UP", Active = true, CreatedAt = now.AddDays(-30), LastReportAt = now.AddDays(-5) });
        var noMap = hosts.Upsert(new WebHost { HostName = "SILENT-NOMAP", Active = true, CreatedAt = now.AddDays(-30), LastReportAt = now.AddDays(-5) });
        var normal = hosts.Upsert(new WebHost { HostName = "NORMAL", Active = true, CreatedAt = now.AddDays(-30), LastReportAt = now.AddHours(-1) });

        seedStore.ReplaceHostMapForDate(now.Date, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 2001, HostId = down.HostId, HostName = down.HostName, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 2002, HostId = up.HostId, HostName = up.HostName, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 2003, HostId = noMap.HostId, HostName = noMap.HostName, MapStatus = PrtgMapStatus.Conflict },
            new() { DeviceObjid = 2004, HostId = normal.HostId, HostName = normal.HostName, MapStatus = PrtgMapStatus.Ok }
        });

        seedStore.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1, DeviceObjid = 2001, Name = "Ping", SensorType = "ping", Status = "Down", Category = PrtgSensorCategories.Availability },
            new() { Objid = 2, DeviceObjid = 2001, Name = "Traffic", SensorType = "snmptraffic", Status = "Up", Category = PrtgSensorCategories.Traffic },
            new() { Objid = 3, DeviceObjid = 2002, Name = "Ping", SensorType = "ping", Status = "Up", Category = PrtgSensorCategories.Availability },
            new() { Objid = 4, DeviceObjid = 2002, Name = "Traffic", SensorType = "snmptraffic", Status = "Down", Category = PrtgSensorCategories.Traffic },
            new() { Objid = 5, DeviceObjid = 2004, Name = "Ping", SensorType = "ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, syncedAt);

        // 「最後結構同步時間」取裝置表：結構同步必定同時寫裝置
        seedStore.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 2001, Name = "D1" }, new() { Objid = 2002, Name = "D2" },
            new() { Objid = 2003, Name = "D3" }, new() { Objid = 2004, Name = "D4" }
        }, syncedAt);

        return (down, up, noMap, normal);
    }

    private HostAdminService CreateWithPrtgAndSettings(EfPrtgStore prtgStore, FakeSystemSettingsStore settings) => new(
        _hosts,
        _groups,
        new FakeUserStore(),
        new FakeNetiqServerCatalog("SENTINEL-A"),
        new FakeNetiqHostServiceForAdmin(),
        _audit,
        new UserDisplayNameService(new FakeSystemSettingsStore()),
        prtgStore,
        _mapRefresher,
        settings, TestPermissionStamps.Shared,
        _snapshotService);

    [Fact]
    public void GetHosts_未回報主機PRTG提示_down_up_nomap與正常主機null_sensor只查一次()
    {
        var (down, up, noMap, normal) = SeedSilentPrtgScenario(_hosts, new EfPrtgStore(_fx.NewContext), DateTime.Now);
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => s.PrtgEnabled = true);
        // 門檻 0：每次量測都記一筆，拿操作次數當呼叫計數（EfPrtgStore 是 sealed，無法做替身）
        var monitor = new SqlPerformanceMonitor(thresholdMs: 0);

        var result = CreateWithPrtgAndSettings(new EfPrtgStore(_fx.NewContext, monitor), settings)
            .GetHosts(new HostSearchRequest { PageSize = 50 });
        var byId = result.Items.ToDictionary(h => h.HostId);

        Assert.Equal(PrtgPresenceHint.Down, byId[down.HostId].PrtgHint);
        Assert.Equal(PrtgPresenceHint.Up, byId[up.HostId].PrtgHint);
        Assert.Equal(PrtgPresenceHint.NoMap, byId[noMap.HostId].PrtgHint);
        Assert.Null(byId[normal.HostId].PrtgHint);
        Assert.False(byId[down.HostId].PrtgHintStale);

        var calls = monitor.Snapshot().TopSlowOperations
            .Single(o => o.Operation == "prtg:GetSensorStatesForDevices");
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public void GetHosts_PRTG同步時間3天前_提示標為過時()
    {
        var (down, _, noMap, _) = SeedSilentPrtgScenario(_hosts, new EfPrtgStore(_fx.NewContext), DateTime.Now.AddDays(-3));
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => s.PrtgEnabled = true);

        var byId = CreateWithPrtgAndSettings(new EfPrtgStore(_fx.NewContext), settings)
            .GetHosts(new HostSearchRequest { PageSize = 50 }).Items.ToDictionary(h => h.HostId);

        Assert.Equal(PrtgPresenceHint.Down, byId[down.HostId].PrtgHint);
        Assert.True(byId[down.HostId].PrtgHintStale);
        Assert.True(byId[noMap.HostId].PrtgHintStale);
    }

    [Fact]
    public void GetHosts_PRTG未啟用_提示全部為null()
    {
        SeedSilentPrtgScenario(_hosts, new EfPrtgStore(_fx.NewContext), DateTime.Now);
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => s.PrtgEnabled = false);

        var items = CreateWithPrtgAndSettings(new EfPrtgStore(_fx.NewContext), settings)
            .GetHosts(new HostSearchRequest { PageSize = 50 }).Items;

        Assert.Equal(4, items.Count);
        Assert.All(items, h => Assert.Null(h.PrtgHint));
        Assert.All(items, h => Assert.False(h.PrtgHintStale));
    }

    [Fact]
    public void GetHosts_一台主機多個device_sensor合併判定()
    {
        var now = DateTime.Now;
        var seed = new EfPrtgStore(_fx.NewContext);
        var host = _hosts.Upsert(new WebHost { HostName = "MULTI", Active = true, CreatedAt = now.AddDays(-30), LastReportAt = now.AddDays(-5) });
        seed.ReplaceHostMapForDate(now.Date, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 3001, HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 3002, HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok }
        });
        seed.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 11, DeviceObjid = 3001, Name = "Traffic", SensorType = "snmptraffic", Status = "Up", Category = PrtgSensorCategories.Traffic },
            new() { Objid = 12, DeviceObjid = 3002, Name = "Ping", SensorType = "ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        var settings = new FakeSystemSettingsStore();
        settings.Update(s => s.PrtgEnabled = true);

        var dto = Assert.Single(CreateWithPrtgAndSettings(new EfPrtgStore(_fx.NewContext), settings)
            .GetHosts(new HostSearchRequest()).Items);

        Assert.Equal(PrtgPresenceHint.Down, dto.PrtgHint);
    }

    [Fact]
    public void ComputeSilentPrtgHints_對應表3台本頁只1台未回報_只查該台對應且提示不變()
    {
        // 對應表當日有 3 台主機的列（SILENT-DOWN／SILENT-UP ok、SILENT-NOMAP conflict、NORMAL ok 共 4 列）
        var (down, up, noMap, normal) = SeedSilentPrtgScenario(_hosts, new EfPrtgStore(_fx.NewContext), DateTime.Now);
        var monitor = new SqlPerformanceMonitor(thresholdMs: 0);
        var store = new EfPrtgStore(_fx.NewContext, monitor);

        // 本頁只有 down（未回報）與 normal（正常回報）
        var (hints, stale) = HostAdminService.ComputeSilentPrtgHints(store, new[] { down, normal }, DateTime.Now);

        Assert.Single(hints);
        Assert.Equal(PrtgPresenceHint.Down, hints[down.HostId]);
        Assert.False(stale);

        // 走多台版、不走整表版
        var ops = monitor.Snapshot().TopSlowOperations;
        Assert.Equal(1, ops.Single(o => o.Operation == "prtg:GetLatestHostMapForHosts").Count);
        Assert.DoesNotContain(ops, o => o.Operation == "prtg:GetLatestHostMapWithDate");

        // 多台版只回傳被要求的主機列；空集合不查庫
        var rows = store.GetLatestHostMapForHosts(new[] { down.HostId });
        Assert.All(rows, r => Assert.Equal(down.HostId, r.HostId));
        Assert.Single(rows);
        Assert.Equal(2, store.GetLatestHostMapForHosts(new[] { up.HostId, noMap.HostId }).Count);
        Assert.Empty(store.GetLatestHostMapForHosts(Array.Empty<long>()));
    }
}

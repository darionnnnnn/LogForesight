using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

public class PrtgUnmatchedMapUiTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly RecordingAuditService _audit = new();
    private readonly PrtgHostMappingAdminController _controller;

    public PrtgUnmatchedMapUiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-unmatched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));

        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(x => x.PrtgEnabled = true);

        _controller = PrtgControllerFactory.HostMapping(_backend, _audit, _hosts,
            mapRefresher: new PrtgHostMapRefresher(settingsStore, _backend));

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "test-admin"),
            new Claim(JwtTokenService.AccountClaim, "test-admin")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private (long HostId, long[] DeviceIds) SeedBatchTargets()
    {
        var host = new HostStore(_backend.Blob("hosts")).Upsert(new WebHost
        {
            HostName = "batch-target", IpAddress = "10.30.1.20", Active = true
        });
        var ids = new long[] { 8101, 8102, 8103 };
        _backend.PrtgStore().UpsertDevices(ids.Select(id => new PrtgDeviceRow
        {
            Objid = id, Name = $"device-{id}", Ip = $"10.30.1.{id - 8100}",
            SyncedAt = DateTime.Now, CreatedAt = DateTime.Now
        }).ToList(), DateTime.Now);
        return (host.HostId, ids);
    }

    [Fact]
    public void 批次人工對應_先驗證全部裝置再寫_且正常逐筆稽核()
    {
        var (hostId, ids) = SeedBatchTargets();
        var badRequests = new[]
        {
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = hostId },
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = hostId, DeviceObjids = new() { ids[0], ids[0] } },
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = hostId, DeviceObjids = new() { ids[0], 99999 } },
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = hostId, DeviceObjids = Enumerable.Range(1, 101).Select(i => (long)i).ToList() },
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = 99999, DeviceObjids = new() { ids[0] } },
            new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), HostId = hostId, DeviceObjids = new() { ids[0] }, Note = new string('字', 513) }
        };
        foreach (var request in badRequests)
        {
            Assert.Throws<DomainException>(() => _controller.SetPrtgManualMapBatch(request));
            Assert.Empty(_backend.PrtgStore().GetManualMaps());
            Assert.Empty(_audit.Entries);
        }

        var result = _controller.SetPrtgManualMapBatch(new SetPrtgManualMapBatchRequest
        { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(),
            HostId = hostId, DeviceObjids = ids.ToList(), Note = "設備盤點後確認"
        });
        Assert.Equal(ids, result.Data!.SucceededIds);
        Assert.Null(result.Data.FailedDeviceObjid);
        Assert.Empty(result.Data.NotProcessedIds);
        Assert.Equal(ids, _backend.PrtgStore().GetManualMaps().Select(m => m.DeviceObjid).OrderBy(x => x));
        Assert.Equal(3, _audit.Entries.Count);
        Assert.All(_audit.Entries, e => Assert.Equal(AuditActions.PrtgManualMapSet, e.Action));
    }

    [Fact]
    public void 批次人工對應_說明長度的模型驗證也限制512字()
    {
        var request = new SetPrtgManualMapBatchRequest { ExpectedScopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(), Note = new string('字', 513) };
        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true));
    }

    [Fact]
    public void GetPrtgHostMap_只列衝突_unmatched不再提供_主機清單外的device不上畫面()
    {
        var store = _backend.PrtgStore();
        var date = DateTime.Today;

        // 故意以不規則 Objid 順序塞入 unmatched 資料
        var deviceIds = new[] { 3050, 3010, 3090, 3020, 3040, 3030, 3080, 3070, 3060 };
        var rows = deviceIds.Select(id => new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = id,
            Ip = $"10.20.0.{id % 100}",
            MapStatus = PrtgMapStatus.Unmatched,
            Note = "PRTG 有此 device，主機主檔查無對應 IP",
            CreatedAt = DateTime.Now
        }).ToList();

        // 另塞入 conflict 與 ok 資料，確保篩選隔離
        rows.Add(new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = 1001,
            Ip = "10.20.0.1",
            MapStatus = PrtgMapStatus.Conflict,
            Note = "衝突項目",
            CreatedAt = DateTime.Now
        });
        rows.Add(new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = 2001,
            Ip = "10.20.0.2",
            MapStatus = PrtgMapStatus.Ok,
            HostId = 1,
            HostName = "srv-01",
            CreatedAt = DateTime.Now
        });

        store.ReplaceHostMapForDate(date, rows);

        // unmatched（PRTG 端有、主機清單沒有）不再提供查詢（docs/PRTG-SPEC.md §1）
        var rejected = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("unmatched", page: 1, pageSize: 4));
        Assert.Contains("prtg-unmapped-hosts", rejected.Message);

        // 衝突照常分頁，且不混入 unmatched 與 ok 列
        var conflicts = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 4).Data!;
        Assert.Equal(1, conflicts.Total);
        Assert.Equal(1001, Assert.Single(conflicts.Items).DeviceObjid);
    }

    [Fact]
    public void GetPrtgHostMap_拒絕未知status並擲驗證例外()
    {
        var ex1 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("ok"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex1.Code);

        var ex2 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("all"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex2.Code);

        var ex3 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap(""));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex3.Code);

        var ex4 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("unknown"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex4.Code);
    }
}

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
    private readonly RecordingAuditService _audit = new();
    private readonly SettingsController _controller;

    public PrtgUnmatchedMapUiTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-unmatched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);

        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(x => x.PrtgEnabled = true);

        _controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: _backend,
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

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new();
        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
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
            new SetPrtgManualMapBatchRequest { HostId = hostId },
            new SetPrtgManualMapBatchRequest { HostId = hostId, DeviceObjids = new() { ids[0], ids[0] } },
            new SetPrtgManualMapBatchRequest { HostId = hostId, DeviceObjids = new() { ids[0], 99999 } },
            new SetPrtgManualMapBatchRequest { HostId = hostId, DeviceObjids = Enumerable.Range(1, 101).Select(i => (long)i).ToList() },
            new SetPrtgManualMapBatchRequest { HostId = 99999, DeviceObjids = new() { ids[0] } },
            new SetPrtgManualMapBatchRequest { HostId = hostId, DeviceObjids = new() { ids[0] }, Note = new string('字', 513) }
        };
        foreach (var request in badRequests)
        {
            Assert.Throws<DomainException>(() => _controller.SetPrtgManualMapBatch(request));
            Assert.Empty(_backend.PrtgStore().GetManualMaps());
            Assert.Empty(_audit.Entries);
        }

        var result = _controller.SetPrtgManualMapBatch(new SetPrtgManualMapBatchRequest
        {
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
        var request = new SetPrtgManualMapBatchRequest { Note = new string('字', 513) };
        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), new List<ValidationResult>(), true));
    }

    [Fact]
    public void GetPrtgHostMap_支援unmatched查詢_依DeviceObjid穩定排序且正確分頁()
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

        // Act: 查 unmatched 第 1 頁，頁大小 4
        var resPage1 = _controller.GetPrtgHostMap("unmatched", page: 1, pageSize: 4);

        Assert.True(resPage1.Success);
        Assert.NotNull(resPage1.Data);
        Assert.Equal(9, resPage1.Data.Total);
        Assert.Equal(1, resPage1.Data.Page);
        Assert.Equal(4, resPage1.Data.PageSize);
        Assert.Equal(4, resPage1.Data.Items.Count);

        // 依 DeviceObjid 排序：3010, 3020, 3030, 3040
        Assert.Equal(3010, resPage1.Data.Items[0].DeviceObjid);
        Assert.Equal(3020, resPage1.Data.Items[1].DeviceObjid);
        Assert.Equal(3030, resPage1.Data.Items[2].DeviceObjid);
        Assert.Equal(3040, resPage1.Data.Items[3].DeviceObjid);

        // 驗證 DTO 包含 MapStatus 與 ConflictKind，且絕不能錯標為 multi-host 或 multi-device
        foreach (var item in resPage1.Data.Items)
        {
            Assert.Equal(PrtgMapStatus.Unmatched, item.MapStatus);
            Assert.Equal("unmatched", item.ConflictKind);
            Assert.NotEqual("multi-host", item.ConflictKind);
            Assert.NotEqual("multi-device", item.ConflictKind);
        }

        // Act: 查 unmatched 第 2 頁，頁大小 4
        var resPage2 = _controller.GetPrtgHostMap("unmatched", page: 2, pageSize: 4);
        Assert.Equal(4, resPage2.Data!.Items.Count);
        Assert.Equal(3050, resPage2.Data.Items[0].DeviceObjid);
        Assert.Equal(3060, resPage2.Data.Items[1].DeviceObjid);
        Assert.Equal(3070, resPage2.Data.Items[2].DeviceObjid);
        Assert.Equal(3080, resPage2.Data.Items[3].DeviceObjid);

        // Act: 查 unmatched 第 3 頁，最後 1 筆
        var resPage3 = _controller.GetPrtgHostMap("unmatched", page: 3, pageSize: 4);
        Assert.Single(resPage3.Data!.Items);
        Assert.Equal(3090, resPage3.Data.Items[0].DeviceObjid);
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

    [Fact]
    public void GetPrtgHostMap_後端為null時維持空頁()
    {
        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: null);

        var res = controller.GetPrtgHostMap("unmatched", page: 1, pageSize: 20);
        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(0, res.Data.Total);
        Assert.Empty(res.Data.Items);
    }

    [Fact]
    public void 前端Init呼叫三種清單且支援重試與獨立分頁()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        // 1. init() 內呼叫三種清單
        var initStart = js.IndexOf("function init()", StringComparison.Ordinal);
        Assert.True(initStart >= 0, "prtg-admin.js 應有 init()");
        var initBody = js.Substring(initStart);
        Assert.Contains("refreshConflicts(1);", initBody);
        Assert.Contains("refreshUnmatched(1);", initBody);
        Assert.Contains("refreshIpExcludes();", initBody);

        // 2. 未對應獨立分頁與 pageSize 讀寫
        Assert.Contains("loadPageSize('prtg-unmatched')", js);
        Assert.Contains("savePageSize('prtg-unmatched'", js);
        Assert.Contains("prtg-unmatched-pagination", js);

        // 3. 未對應清單含指派操作呼叫 openAssignModal
        Assert.Contains("openAssignModal(item)", js);

        // 4. 重試按鈕與失敗文案
        Assert.Contains("載入衝突清單失敗", js);
        Assert.Contains("載入未對應清單失敗", js);
        Assert.Contains("載入 IP 排除清單失敗", js);
        Assert.Contains("retryBtn.textContent = '重試'", js);

        // 5. 無資料文案與失敗文案區隔
        Assert.Contains("無衝突項目", js);
        Assert.Contains("無未對應項目", js);
        Assert.Contains("無排除 IP", js);

        // 6. 指派後同步更新四者（鏡像摘要、衝突清單、未對應清單、排除清單）
        Assert.Contains("refreshUnmatched(unmatchedPage)", js);
    }

    [Fact]
    public void Prtg維護頁包含可折疊未對應表格與摘要未對應徽章可點選跳轉()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var cshtml = File.ReadAllText(cshtmlPath);

        // 1. 摘要未對應徽章為可點擊按鈕
        Assert.Contains("id=\"prtg-mirror-map-unmatched\"", cshtml);
        Assert.Contains("<button", cshtml);

        // 2. 未對應區塊與可折疊結構
        Assert.Contains("id=\"prtg-unmatched-section\"", cshtml);
        Assert.Contains("id=\"prtg-unmatched-toggle-btn\"", cshtml);
        Assert.Contains("id=\"prtg-unmatched-collapse\"", cshtml);
        Assert.Contains("id=\"prtg-unmatched-table\"", cshtml);
        Assert.Contains("id=\"prtg-unmatched-body\"", cshtml);
        Assert.Contains("id=\"prtg-unmatched-pagination\"", cshtml);

        // 3. 初始載入中狀態（而非無衝突/無未對應/無排除）
        Assert.Contains("<tbody id=\"prtg-mirror-conflicts-body\">\n                                    <tr><td colspan=\"5\" class=\"text-muted text-center py-2\">載入中…</td></tr>", cshtml.Replace("\r\n", "\n"));
        Assert.Contains("<tbody id=\"prtg-unmatched-body\">\n                                        <tr><td colspan=\"5\" class=\"text-muted text-center py-2\">載入中…</td></tr>", cshtml.Replace("\r\n", "\n"));
        Assert.Contains("<tbody id=\"prtg-ip-excludes-body\">\n                                    <tr><td colspan=\"5\" class=\"text-muted text-center py-2\">載入中…</td></tr>", cshtml.Replace("\r\n", "\n"));

        // 4. JS 綁定跳轉與展開
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        var js = File.ReadAllText(jsPath);
        Assert.Contains("bindUnmatchedControls()", js);
        Assert.Contains("scrollIntoView", js);
    }
}

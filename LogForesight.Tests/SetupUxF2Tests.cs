using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋第 50 輪批次F-2：匯入分組不再靜默落空、批次指派負責人、授權空白的可見主機數、
/// 設定頁存檔體驗（數字欄空白不送 0、錯誤路徑切頁籤）。
/// </summary>
public class SetupUxF2Tests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly string _snapshotDir = Path.Combine(Path.GetTempPath(), "lf-setup-snapshot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_snapshotDir)) Directory.Delete(_snapshotDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── 1. NetIQ 匯入分組 ────────────────────────────────────────────────────

    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly RecordingAuditService _audit = new();

    private NetiqDiscoveryService CreateNetiq(params (string Name, string Ip)[] found) =>
        new(new FakeNetiqServerCatalog(new SentinelServer { Name = "S1", BaseUrl = "https://x", Username = "u", Password = "p" }),
            new FakeClient(found), _hosts, _hostGroups, new FakeSentinelStore(),
            new FakeImportLogStore(), new FakeCurrentUser(), _audit);

    [Fact]
    public async Task 匯入_新群組名稱空白_整批拒絕且零主機寫入()
    {
        var svc = CreateNetiq(("A", "10.1.2.11"), ("B", "10.1.3.12"));
        var scan = await svc.ScanAsync("S1", "10.1", default);

        var ex = Assert.Throws<DomainException>(() => svc.Import(new NetiqImportRequest
        {
            Token = scan.Token,
            SelectedIps = new() { "10.1.2.11", "10.1.3.12" },
            GroupAssignments = new()
            {
                // 第一個網段合法（會建新群組）、第二個不合：驗證要在任何寫入之前
                new NetiqSubnetGroupAssignment { Cidr = "10.1.2.0/24", Mode = "new", NewGroupName = "合法群組" },
                new NetiqSubnetGroupAssignment { Cidr = "10.1.3.0/24", Mode = "new", NewGroupName = "" }
            }
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("10.1.3.0/24", ex.Message);
        Assert.Empty(_hosts.GetAll());
        Assert.Empty(_hostGroups.GetAll());
    }

    [Fact]
    public async Task 匯入_既有群組不存在_拒絕且零主機寫入()
    {
        var svc = CreateNetiq(("A", "10.1.2.11"));
        var scan = await svc.ScanAsync("S1", "10.1.2", default);

        var ex = Assert.Throws<DomainException>(() => svc.Import(new NetiqImportRequest
        {
            Token = scan.Token,
            SelectedIps = new() { "10.1.2.11" },
            GroupAssignments = new() { new NetiqSubnetGroupAssignment { Cidr = "10.1.2.0/24", Mode = "existing", HostGroupId = 9999 } }
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("10.1.2.0/24", ex.Message);
        Assert.Empty(_hosts.GetAll());
    }

    [Fact]
    public async Task 匯入_正常_結果帶匯入台數群組數與未分組台數()
    {
        var existing = _hostGroups.Upsert(new HostGroup { GroupName = "既有", Active = true });
        // 既有主機：群組不動（匯入不是隱性改權限），計數依實際落盤狀態——它原本就在「既有」群組
        _hosts.Upsert(new WebHost { HostName = "10.1.4.20", IpAddress = "10.1.4.20", Source = "netiq", Active = true, GroupIds = new() { existing.GroupId } });
        var svc = CreateNetiq(("A", "10.1.2.11"), ("B", "10.1.2.12"), ("C", "10.1.3.13"), ("D", "10.1.5.14"), ("E", "10.1.4.20"));
        var scan = await svc.ScanAsync("S1", "10.1", default);

        var result = svc.Import(new NetiqImportRequest
        {
            Token = scan.Token,
            SelectedIps = new() { "10.1.2.11", "10.1.2.12", "10.1.3.13", "10.1.5.14", "10.1.4.20" },
            GroupAssignments = new()
            {
                new NetiqSubnetGroupAssignment { Cidr = "10.1.2.0/24", Mode = "new", NewGroupName = "新群組" },
                new NetiqSubnetGroupAssignment { Cidr = "10.1.3.0/24", Mode = "existing", HostGroupId = existing.GroupId },
                new NetiqSubnetGroupAssignment { Cidr = "10.1.5.0/24", Mode = "skip" },
                // 既有主機的網段指派到新群組也不會改它的群組
                new NetiqSubnetGroupAssignment { Cidr = "10.1.4.0/24", Mode = "new", NewGroupName = "新群組" }
            }
        });

        Assert.Equal(5, result.ImportedCount);
        Assert.Equal(2, result.GroupCount);      // 新群組（2 台）＋既有（10.1.3.13 與既有主機）
        Assert.Equal(1, result.UngroupedCount);  // 10.1.5.14
        Assert.Equal(new[] { existing.GroupId }, _hosts.FindByName("10.1.4.20")!.GroupIds);
    }

    // ── 3. 批次指派負責人 ────────────────────────────────────────────────────

    private readonly FakeUserStore _users = new();

    private (HostAdminService Service, PermissionVersionStamp Stamp) CreateHostAdmin()
    {
        var stamp = new PermissionVersionStamp(_fx.Blob(PermissionVersionStamp.BlobKey));
        Directory.CreateDirectory(_snapshotDir);
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_snapshotDir, "snapshot.db")}" }, _snapshotDir);
        var syncState = new PrtgStructureSyncRunState();
        var lifetime = new FakeHostApplicationLifetime();
        var statusStore = new PrtgStructureSyncStatusStore(backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var backfillState = new PrtgBackfillRunState();
        var structureSync = new PrtgStructureSyncService(new FakeSystemSettingsStore(), backend, syncState, new SchedulerRunState(), _hosts, statusStore, backfillState, new FakeSentinelStore(), new DataVersionStamp(), lifetime);
        var probeState = new PrtgProbeRunState();
        var backfill = new PrtgBackfillService(new FakeSystemSettingsStore(), backend, backfillState, probeState, _hosts, new SchedulerRunState(), syncState, new FakeSentinelStore(), structureSync);
        var snapshotService = new PrtgSnapshotHostedService(new FakeSystemSettingsStore(), backend, new SchedulerRunState(), structureSync, backfill, _hosts, new FakeSentinelStore(), probeState, lifetime);

        var service = new HostAdminService(
            _hosts, _hostGroups, _users,
            new FakeNetiqServerCatalog("SENTINEL-A"),
            new FakeNetiqHostServiceForAdmin(),
            _audit,
            new UserDisplayNameService(new FakeSystemSettingsStore()),
            new EfPrtgStore(_fx.NewContext),
            new NoopMapRefresher(),
            new FakeSystemSettingsStore(), stamp,
            snapshotService);
        return (service, stamp);
    }

    private sealed class NoopMapRefresher : IPrtgHostMapRefresher
    {
        public string? TryRefreshToday() => null;
    }

    private (long H1, long H2, long U1, long U2, long U3) SeedOwners()
    {
        var u1 = _users.Upsert(new WebUser { Account = "u1", DisplayName = "甲" }).UserId;
        var u2 = _users.Upsert(new WebUser { Account = "u2", DisplayName = "乙" }).UserId;
        var u3 = _users.Upsert(new WebUser { Account = "u3", DisplayName = "丙" }).UserId;
        var h1 = _hosts.Upsert(new WebHost { HostName = "H1", Active = true, OwnerUserIds = new() { u1 } }).HostId;
        var h2 = _hosts.Upsert(new WebHost { HostName = "H2", Active = true, OwnerUserIds = new() { u2 } }).HostId;
        return (h1, h2, u1, u2, u3);
    }

    [Fact]
    public void 批次負責人_replace改為僅指定的人_稽核一筆且權限版本推進()
    {
        var (h1, h2, u1, _, u3) = SeedOwners();
        var (svc, stamp) = CreateHostAdmin();
        var before = stamp.Current;

        var result = svc.SetOwnersBatch(new[] { h1, h2 }, new[] { u1, u3 }, "replace");

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(new[] { u1, u3 }, _hosts.Get(h1)!.OwnerUserIds.OrderBy(x => x));
        Assert.Equal(new[] { u1, u3 }, _hosts.Get(h2)!.OwnerUserIds.OrderBy(x => x));
        Assert.Equal(2, result.Hosts.Count);
        Assert.Single(_audit.Entries);
        Assert.True(stamp.Current > before);
    }

    [Fact]
    public void 批次負責人_add保留既有再加入()
    {
        var (h1, h2, u1, u2, u3) = SeedOwners();
        var (svc, _) = CreateHostAdmin();

        svc.SetOwnersBatch(new[] { h1, h2 }, new[] { u3 }, "add");

        Assert.Equal(new[] { u1, u3 }, _hosts.Get(h1)!.OwnerUserIds.OrderBy(x => x));
        Assert.Equal(new[] { u2, u3 }, _hosts.Get(h2)!.OwnerUserIds.OrderBy(x => x));
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public void 批次負責人_remove只拿掉指定的人()
    {
        var (h1, h2, u1, u2, _) = SeedOwners();
        var (svc, _) = CreateHostAdmin();

        svc.SetOwnersBatch(new[] { h1, h2 }, new[] { u1 }, "remove");

        Assert.Empty(_hosts.Get(h1)!.OwnerUserIds);
        Assert.Equal(new[] { u2 }, _hosts.Get(h2)!.OwnerUserIds);
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public void 批次負責人_含不存在主機_整批拒絕零寫入()
    {
        var (h1, _, u1, _, u3) = SeedOwners();
        var (svc, stamp) = CreateHostAdmin();
        var before = stamp.Current;

        var ex = Assert.Throws<DomainException>(() => svc.SetOwnersBatch(new[] { h1, 9999L }, new[] { u3 }, "add"));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(new[] { u1 }, _hosts.Get(h1)!.OwnerUserIds);
        Assert.Empty(_audit.Entries);
        Assert.Equal(before, stamp.Current);
    }

    [Fact]
    public void 批次負責人_含已併入其他主機_整批拒絕零寫入()
    {
        var (h1, h2, u1, _, u3) = SeedOwners();
        _hosts.Get(h2)!.MergedInto = h1;
        var (svc, _) = CreateHostAdmin();

        Assert.Throws<DomainException>(() => svc.SetOwnersBatch(new[] { h1, h2 }, new[] { u3 }, "add"));

        Assert.Equal(new[] { u1 }, _hosts.Get(h1)!.OwnerUserIds);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 批次負責人_含停用使用者_400()
    {
        var (h1, _, u1, _, _) = SeedOwners();
        var disabled = _users.Upsert(new WebUser { Account = "gone", DisplayName = "離職", Active = false }).UserId;
        var (svc, _) = CreateHostAdmin();

        var ex = Assert.Throws<DomainException>(() => svc.SetOwnersBatch(new[] { h1 }, new[] { disabled }, "add"));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(new[] { u1 }, _hosts.Get(h1)!.OwnerUserIds);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 批次負責人_不存在的使用者_400()
    {
        var (h1, _, _, _, _) = SeedOwners();
        var (svc, _) = CreateHostAdmin();

        var ex = Assert.Throws<DomainException>(() => svc.SetOwnersBatch(new[] { h1 }, new[] { 9999L }, "add"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void 批次負責人_不合法模式_400()
    {
        var (h1, _, u1, _, _) = SeedOwners();
        var (svc, _) = CreateHostAdmin();

        Assert.Throws<DomainException>(() => svc.SetOwnersBatch(new[] { h1 }, new[] { u1 }, "toggle"));
    }

    [Fact]
    public void 單台設定負責人_與批次走同一條寫入_權限版本推進()
    {
        var (h1, _, _, u2, _) = SeedOwners();
        var (svc, stamp) = CreateHostAdmin();
        var before = stamp.Current;

        svc.SetHostOwners(h1, new[] { u2 });

        Assert.Equal(new[] { u2 }, _hosts.Get(h1)!.OwnerUserIds);
        Assert.True(stamp.Current > before);
    }

    [Fact]
    public void 批次負責人端點_與批次改群組同一個授權掛載()
    {
        var method = typeof(LogForesight.Web.Controllers.Api.AdminController).GetMethod("SetOwnersBatch");
        Assert.NotNull(method);
        var put = method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPutAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.HttpPutAttribute>().Single();
        Assert.Equal("hosts/owners/batch", put.Template);
        // 授權掛在 Controller 層級（Maintain），與 hosts/groups/batch 相同
        Assert.Contains(typeof(LogForesight.Web.Controllers.Api.AdminController).GetCustomAttributes(typeof(LogForesight.Web.Filters.PermissionAttribute), false)
            .Cast<LogForesight.Web.Filters.PermissionAttribute>(), a => ((Capability[])a.Arguments![0]).Contains(Capability.Maintain));
    }

    // ── 2. 儀表板：沒有任何授權的一般使用者 ──────────────────────────────────

    [Fact]
    public void 儀表板_沒有任何授權的一般使用者_可見主機數為0()
    {
        // 站上有主機（不是因為整站空才是 0）、有別人當負責人，這位使用者不屬任何部門群組
        _hosts.Upsert(new WebHost { HostName = "SRV-A", Active = true, GroupIds = new() { 1 } });
        var me = _users.Upsert(new WebUser { Account = "nobody", DisplayName = "無授權" });
        var currentUser = FakeCurrentUser.ForUser(me.UserId);
        var settingsStore = new FakeSystemSettingsStore();
        var severity = new FakeSystemSettingsService();
        var visibility = new VisibilityService(currentUser, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(), _hosts,
            new FakeIssueCaseStore(), settingsStore);
        var exclusions = new FixedIssueExclusionSource(IssueExclusion.None);
        var recordStore = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var handlingStore = new EfRecordHandlingStore(_fx.NewContext, new EfJsonLogStore(_fx.NewContext, "handling"));
        var issueHandlingStore = new EfIssueHandlingStore(_fx.NewContext);
        var caseStore = new FakeIssueCaseStore();
        var repository = new RecordRepository(recordStore, _hosts, visibility, severity);
        var progress = new HandlingProgressCalculator(issueHandlingStore, handlingStore, caseStore, settingsStore, exclusions);
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var handling = new HandlingHistoryQueryService(
            handlingStore, issueHandlingStore, caseStore, _hosts, _users, visibility, settingsStore, repository, progress, aggregates,
            new UserDisplayNameService(settingsStore), exclusions);
        var permissionChanges = new PermissionChangeService(new PermissionChangeStore(_fx.NewContext), _hosts, visibility, currentUser,
            new RecordingAuditService(), _users, new NullReportReader(), settingsStore);
        var statusResolver = new OccurrenceStatusResolver(_hosts, issueHandlingStore, caseStore, settingsStore);
        var dashboard = new DashboardService(
            visibility, new AuditLogStore(new EfJsonLogStore(_fx.NewContext, "audit")), currentUser, handling, permissionChanges,
            _hostGroups, new IssueRankingBuilder(aggregates, _hosts, exclusions), settingsStore, aggregates,
            new IssueTodoQuery(aggregates, statusResolver, exclusions), severity, new SummaryCache(new DataVersionStamp()),
            new EfPrtgStore(_fx.NewContext), exclusions);

        var summary = dashboard.GetSummary(7);

        // 儀表板 DTO 既有的 TotalHosts 就是「檢視者可見主機數」（BuildSummary 以可見主機集合計數），
        // 前端以它判斷要不要顯示「沒有任何主機的檢視權限」提示，不另開同義欄位
        Assert.Equal(0, summary.TotalHosts);
    }

    // ── 前端結構守門 ─────────────────────────────────────────────────────────

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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string Js(string name) => Read("LogForesight.Web", "wwwroot", "js", "pages", name);

    /// <summary>取 JS 檔中某個 function 的本體（到下一個頂層 function／宣告為止）</summary>
    private static string FunctionBody(string js, string name)
    {
        var m = Regex.Match(js, $@"(async\s+)?function\s+{name}\s*\(");
        Assert.True(m.Success, $"找不到函式 {name}");
        var rest = js[m.Index..];
        var end = Regex.Match(rest[1..], @"\r?\n(async\s+function|function|export|/\*\*|const|let)\b");
        return end.Success ? rest[..(end.Index + 1)] : rest;
    }

    /// <summary>取 settings-form submit 監聽器（bindForm）裡送出 payload 的那段</summary>
    private static string SettingsPayload(string js)
    {
        var body = FunctionBody(js, "bindForm");
        var start = body.IndexOf("api.put('/api/admin/settings'", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到設定存檔的 api.put");
        var end = body.IndexOf("});", start, StringComparison.Ordinal);
        return body[start..end];
    }

    [Fact]
    public void 設定頁_收集數字欄不把空白轉成0()
    {
        var js = Js("settings.js");
        var reader = FunctionBody(js, "readNumber");
        Assert.Contains("=== ''", reader);
        Assert.Contains("null", reader);
        Assert.DoesNotContain("|| 0", reader);

        var payload = SettingsPayload(js);
        // payload 裡的數字欄一律經 readNumber；不得再有 Number(…value) 或 || 0 這種把空白變 0 的寫法
        Assert.Contains("readNumber('ai-timeout-seconds')", payload);
        Assert.Contains("readNumber('ai-input-price')", payload);
        Assert.DoesNotContain("|| 0", payload);
        Assert.DoesNotContain("Number(document", payload);
        Assert.DoesNotContain("Number('')", payload);
    }

    [Fact]
    public void 設定頁_送出前擋下空白數字欄並提示請輸入數值()
    {
        var js = Js("settings.js");
        var finder = FunctionBody(js, "findInvalidNumberFields");
        Assert.Contains("input[type=\"number\"]", finder);
        Assert.Contains("請輸入數值", finder);

        var submit = FunctionBody(js, "bindForm");
        var check = submit.IndexOf("findInvalidNumberFields(form)", StringComparison.Ordinal);
        var send = submit.IndexOf("api.put('/api/admin/settings'", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < send, "數字欄檢查必須在送出之前");
    }

    [Fact]
    public void 設定頁_錯誤處理路徑切換頁籤並focus且標出欄位()
    {
        var js = Js("settings.js");
        var show = FunctionBody(js, "showFieldErrors");
        Assert.Contains("activateTabForElement(", show);
        Assert.Contains(".focus()", show);
        Assert.Contains("is-invalid", show);
        Assert.Contains("invalid-feedback", show);
        Assert.Contains("有 ${errors.length} 個欄位需要修正", show);
        Assert.Contains("showFieldErrors(invalidFields)", FunctionBody(js, "bindForm"));
    }

    [Fact]
    public void 設定頁_存檔成功後以整頁套用函式重繪所有頁籤()
    {
        var js = Js("settings.js");
        var apply = FunctionBody(js, "applySettings");
        foreach (var render in new[]
                 {
                     "renderSeverityChecks(", "renderAiFields(", "renderAdFields(", "renderAnalysisFields(",
                     "renderRetentionFields(", "renderMailFields(", "renderBrandFields(", "loadGuardFields(", "renderUpdatedAt("
                 })
        {
            Assert.Contains(render, apply);
        }

        var submit = FunctionBody(js, "bindForm");
        var success = submit[submit.IndexOf("toast('已儲存設定'", StringComparison.Ordinal)..];
        Assert.Contains("applySettings(current)", success);
        Assert.DoesNotContain("renderAiFields(", submit);
        Assert.Contains("applySettings(current)", FunctionBody(js, "loadSettings"));
    }

    [Fact]
    public void 設定頁_調校參數收在進階設定且欄位id不變()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        var sections = Regex.Matches(cshtml, @"<details class=""mt-3[^""]*"" id=""([a-z-]+)"" data-advanced>(.*?)</details>", RegexOptions.Singleline);
        Assert.Equal(4, sections.Count);
        var inside = string.Concat(sections.Select(m => m.Groups[2].Value));
        foreach (var id in new[]
                 {
                     "ai-timeout-seconds", "ai-retry-count", "ai-retry-delay-seconds", "ai-json-retry-count", "ai-max-tokens",
                     "ai-deep-dive-max-tokens", "ai-frequency-penalty", "ai-presence-penalty", "ai-extra-request-fields",
                     "ai-input-price", "ai-output-price",
                     "perm-operator-fields", "perm-member-fields", "perm-group-fields", "perm-object-fields",
                     "import-max-file-size-kb", "import-max-rows",
                     "prtg-guard-cpu-percent", "prtg-guard-memory-free-percent", "prtg-guard-check-seconds",
                     "prtg-guard-pause-minutes", "prtg-guard-strikes", "prtg-guard-max-pause-minutes"
                 })
        {
            Assert.Contains($"id=\"{id}\"", inside);
        }
        foreach (Match m in sections)
        {
            Assert.Contains("<summary class=\"fw-semibold\">進階設定", m.Groups[2].Value);
        }

        var bind = FunctionBody(Js("settings.js"), "bindAdvancedSections");
        Assert.Contains("localStorage", bind);
        Assert.Contains("catch", bind);
    }

    [Fact]
    public void 匯入精靈_分組未填不送出並標出欄位()
    {
        var js = Js("netiq-import-wizard.js");
        var collect = FunctionBody(js, "collectGroupAssignments");
        Assert.Contains("請輸入新群組名稱", collect);
        Assert.Contains("請選擇群組", collect);
        Assert.Contains(".focus()", collect);
        Assert.Contains("return null", collect);
        // 不再把空白名稱默默改成未分組：mode 一律照選的送
        Assert.DoesNotContain("if (name)", collect);
        Assert.Contains("if (!groupAssignments) return;", FunctionBody(js, "wizardSubmitImport"));
        Assert.Contains("is-invalid", FunctionBody(js, "markAssignmentError"));
        Assert.Contains("invalid-feedback", FunctionBody(js, "markAssignmentError"));
    }

    [Fact]
    public void 匯入精靈_完成畫面顯示分組結果與設定群組授權連結()
    {
        var js = Js("netiq-import-wizard.js");
        var done = FunctionBody(js, "renderImportDone");
        Assert.Contains("result.importedCount", done);
        Assert.Contains("result.groupCount", done);
        Assert.Contains("result.ungroupedCount", done);
        Assert.Contains("appUrl('/admin/groups')", done);
        Assert.Contains("設定群組授權", done);
    }

    [Fact]
    public void 授權矩陣_沒有部門群組時指路建立()
    {
        var matrix = FunctionBody(Js("groups.js"), "renderMatrix");
        Assert.Contains("還沒有部門群組", matrix);
        Assert.Contains("一般使用者要透過部門群組取得主機的檢視權限。", matrix);
        Assert.Contains("建立部門群組", matrix);
        Assert.Contains("openModal('user', null)", matrix);
    }

    [Fact]
    public void 儀表板_可見主機為0且不具Maintain時顯示權限提示()
    {
        var banner = FunctionBody(Js("dashboard.js"), "renderBanner");
        Assert.Contains("data.totalHosts === 0", banner);
        Assert.Contains("hasCapability(user, 'Maintain')", banner);
        Assert.Contains("lf-hint", banner);
        Assert.Contains("你目前沒有任何主機的檢視權限，所以這裡沒有資料。請聯絡系統管理員為你設定部門群組授權。", banner);
    }

    [Fact]
    public void 主機頁_批次列有指派負責人與CSV指路()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Hosts.cshtml");
        var bar = cshtml[cshtml.IndexOf("id=\"host-selection-bar\"", StringComparison.Ordinal)..];
        bar = bar[..bar.IndexOf("</div>", StringComparison.Ordinal)];
        Assert.Contains("id=\"btn-batch-owners\"", bar);
        Assert.Contains("btn-outline-primary\" id=\"btn-batch-owners\"", bar);
        Assert.Contains("大量指派也可以用 CSV：", bar);

        var js = Js("hosts.js");
        Assert.Contains("appUrl('/admin/imports')", js);
        Assert.Contains("'/api/admin/hosts/owners/batch'", js);
    }
}

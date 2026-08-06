using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel CRUD（docs/archive/HISTORY.md 定案 1）：密碼 write-only、
/// 刪除觸發孤兒流程、停用不動主機、改名同步主機顯示快照。</summary>
public class SentinelAdminServiceTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly RecordingAuditService _audit = new();

    private SentinelAdminService Create() => new(_sentinels, _hosts, _audit);

    [Fact]
    public void 新增_密碼被加密且不回傳明碼()
    {
        var dto = Create().SaveSentinel(new SaveSentinelRequest
        {
            Name = "S1", BaseUrl = "https://s1", Username = "svc", Password = "hunter2"
        });

        Assert.True(dto.HasPassword);
        Assert.True(dto.CanDiscover);

        var stored = _sentinels.Get(dto.SentinelId)!;
        Assert.True(CryptoHelper.IsEncrypted(stored.PasswordEnc));
        Assert.Equal("hunter2", CryptoHelper.Decrypt(stored.PasswordEnc));
    }

    [Fact]
    public void 新增_密碼絕不進稽核明細()
    {
        Create().SaveSentinel(new SaveSentinelRequest { Name = "S1", Password = "hunter2" });

        var detail = _audit.Entries.Single().DetailJson;
        Assert.DoesNotContain("hunter2", detail ?? "");
    }

    [Fact]
    public void 編輯_密碼留空時沿用既有密文()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", Password = "hunter2" });
        var originalEnc = _sentinels.Get(created.SentinelId)!.PasswordEnc;

        var updated = svc.SaveSentinel(new SaveSentinelRequest
        {
            SentinelId = created.SentinelId, Name = "S1", BaseUrl = "https://new-url", Password = null
        });

        Assert.True(updated.HasPassword);
        Assert.Equal(originalEnc, _sentinels.Get(created.SentinelId)!.PasswordEnc);
        Assert.Equal("https://new-url", _sentinels.Get(created.SentinelId)!.BaseUrl);
    }

    [Fact]
    public void 編輯_填新密碼時重新加密()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", Password = "old" });

        svc.SaveSentinel(new SaveSentinelRequest { SentinelId = created.SentinelId, Name = "S1", Password = "new" });

        Assert.Equal("new", CryptoHelper.Decrypt(_sentinels.Get(created.SentinelId)!.PasswordEnc));
    }

    [Fact]
    public void 名稱重複_擲衝突例外()
    {
        var svc = Create();
        svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });

        Assert.Throws<DomainException>(() => svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" }));
    }

    [Fact]
    public void 改名_同步所有掛在這台Sentinel下的主機顯示快照()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "改名前" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "改名前" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.2", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "改名前" });

        svc.SaveSentinel(new SaveSentinelRequest { SentinelId = sentinel.SentinelId, Name = "改名後" });

        Assert.Equal("改名後", _hosts.FindByName("10.1.2.1")!.NetiqServer);
        Assert.Equal("改名後", _hosts.FindByName("10.1.2.2")!.NetiqServer);
    }

    [Fact]
    public void 刪除_轄下使用中主機停用並標記孤兒_主機列不刪除()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1" });

        svc.DeleteSentinel(sentinel.SentinelId);

        Assert.Null(_sentinels.Get(sentinel.SentinelId));
        var host = _hosts.FindByName("10.1.2.1")!;
        Assert.False(host.Active);
        Assert.Equal("S1", host.OrphanedFromSentinel);
    }

    [Fact]
    public void 刪除_稽核記錄受影響主機數()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.2", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1" });

        svc.DeleteSentinel(sentinel.SentinelId);

        var deleteEntry = _audit.Entries.Last();
        Assert.Equal(AuditActions.SentinelDelete, deleteEntry.Action);
        Assert.Contains("2", deleteEntry.Summary);
    }

    [Fact]
    public void 停用_主機不動不標記孤兒()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1" });

        var updated = svc.SetActive(sentinel.SentinelId, false);

        Assert.False(updated.Active);
        var host = _hosts.FindByName("10.1.2.1")!;
        Assert.True(host.Active);
        Assert.Null(host.OrphanedFromSentinel);
        Assert.NotNull(_sentinels.Get(sentinel.SentinelId));   // Sentinel 列本身還在，只是 Active=false
    }

    // ── UseEsmDirectory（docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §五）────────────

    [Fact]
    public void ESM開關_預設關閉_儲存後可開啟並持久化()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });
        Assert.False(created.UseEsmDirectory);

        var enabled = svc.SaveSentinel(new SaveSentinelRequest
        {
            SentinelId = created.SentinelId, Name = "S1", UseEsmDirectory = true
        });

        Assert.True(enabled.UseEsmDirectory);
        Assert.True(_sentinels.Get(created.SentinelId)!.UseEsmDirectory);
    }

    /// <summary>編輯時省略欄位＝沿用既有值（同 Os 的語意）——API 呼叫端漏帶
    /// 不該把已開啟的開關靜默關掉</summary>
    [Fact]
    public void 編輯_未帶ESM欄位時沿用既有值()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", UseEsmDirectory = true });

        // 只改名，不帶 UseEsmDirectory
        var renamed = svc.SaveSentinel(new SaveSentinelRequest
        {
            SentinelId = created.SentinelId, Name = "S1-renamed"
        });

        Assert.True(renamed.UseEsmDirectory);
    }

    /// <summary>
    /// SetActive 走「逐欄重建」的路——新增模型欄位漏抄的症狀是
    /// 「停用再啟用，該欄位被靜默重置」。UseEsmDirectory 曾在這裡漏過，釘住。
    /// </summary>
    [Fact]
    public void 停用再啟用_不重置ESM開關()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", UseEsmDirectory = true });

        svc.SetActive(created.SentinelId, false);
        var reactivated = svc.SetActive(created.SentinelId, true);

        Assert.True(reactivated.UseEsmDirectory);
        Assert.True(_sentinels.Get(created.SentinelId)!.UseEsmDirectory);
    }

    // ── Os（項目 4 的新設計：Sentinel 層級單一 OS，見 docs/LINUX-RULES.md §3） ──

    [Fact]
    public void 新增_指定Os時儲存()
    {
        var dto = Create().SaveSentinel(new SaveSentinelRequest { Name = "S1", Os = "linux" });

        Assert.Equal("linux", dto.Os);
        Assert.Equal("linux", _sentinels.Get(dto.SentinelId)!.Os);
    }

    [Fact]
    public void 新增_省略Os時預設windows()
    {
        var dto = Create().SaveSentinel(new SaveSentinelRequest { Name = "S1" });

        Assert.Equal("windows", dto.Os);
    }

    [Fact]
    public void 新增_不合法Os值時預設windows()
    {
        var dto = Create().SaveSentinel(new SaveSentinelRequest { Name = "S1", Os = "solaris" });

        Assert.Equal("windows", dto.Os);
    }

    [Fact]
    public void 編輯_省略Os時沿用既有值_不被靜默重置()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", Os = "linux" });

        // 只改連線位址，沒帶 Os——不該把這台從 linux 重設回 windows
        var updated = svc.SaveSentinel(new SaveSentinelRequest
        {
            SentinelId = created.SentinelId, Name = "S1", BaseUrl = "https://new-url"
        });

        Assert.Equal("linux", updated.Os);
        Assert.Equal("linux", _sentinels.Get(created.SentinelId)!.Os);
    }

    [Fact]
    public void 停用_不影響Os()
    {
        var svc = Create();
        var created = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1", Os = "linux" });

        var updated = svc.SetActive(created.SentinelId, false);

        Assert.Equal("linux", updated.Os);
    }

    // ── TestConnectionAsync（項目 6）：這裡只測「送出真正連線前」的驗證分支——
    // 連線本身（成功/401/逾時等）是 SentinelClient 的協定層行為，已在 SentinelClientTests
    // 用 StubHandler 覆蓋；SentinelAdminService 直接 new SentinelClient(...)，
    // 沒有替身可插，故不在這裡重複測網路路徑。

    [Fact]
    public async Task TestConnection_連線位址空白_擲驗證例外()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { BaseUrl = "", Username = "svc", Password = "x" },
            new NetiqOptions(), CancellationToken.None));

        Assert.Contains("連線位址", ex.Message);
    }

    [Fact]
    public async Task TestConnection_帳號空白_擲驗證例外()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { BaseUrl = "https://s1", Username = " ", Password = "x" },
            new NetiqOptions(), CancellationToken.None));

        Assert.Contains("帳號", ex.Message);
    }

    [Fact]
    public async Task TestConnection_新增中且密碼空白_擲驗證例外()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { SentinelId = 0, BaseUrl = "https://s1", Username = "svc", Password = "" },
            new NetiqOptions(), CancellationToken.None));

        Assert.Contains("密碼", ex.Message);
    }

    [Fact]
    public async Task TestConnection_指定既有Sentinel但找不到_擲例外()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { SentinelId = 999, BaseUrl = "https://s1", Username = "svc", Password = "" },
            new NetiqOptions(), CancellationToken.None));

        Assert.Contains("找不到", ex.Message);
    }

    [Fact]
    public async Task TestConnection_連線位址格式不正確_回傳失敗結果而不是擲例外()
    {
        // 位址格式是 SentinelClient 建構期擋下的（不是連線期），但那同樣是「測試連線」
        // 該回報的結果之一——擲出去會變成 500，畫面看到的是系統錯誤而不是可修的原因
        var result = await Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { BaseUrl = "sentinel.corp.local:8443", Username = "svc", Password = "x" },
            new NetiqOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("連線位址格式不正確", result.Message);
        Assert.Null(result.ElapsedMs);
    }

    [Fact]
    public async Task TestConnection_失敗訊息不含密碼()
    {
        var result = await Create().TestConnectionAsync(
            new TestSentinelConnectionRequest { BaseUrl = "not a url", Username = "svc", Password = "hunter2" },
            new NetiqOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("hunter2", result.Message);
    }

    [Fact]
    public async Task TestConnection_既有Sentinel尚無密碼且未填新密碼_擲例外()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });   // 未帶密碼

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.TestConnectionAsync(
            new TestSentinelConnectionRequest { SentinelId = sentinel.SentinelId, BaseUrl = "https://s1", Username = "svc", Password = "" },
            new NetiqOptions(), CancellationToken.None));

        Assert.Contains("尚未設定密碼", ex.Message);
    }

    [Fact]
    public void GetSentinels_主機數只算使用中且未併入的NetIQ主機()
    {
        var svc = Create();
        var sentinel = svc.SaveSentinel(new SaveSentinelRequest { Name = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.1", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.2", Source = "netiq", SentinelId = sentinel.SentinelId, NetiqServer = "S1", Active = false });

        var dto = svc.GetSentinels().Single();

        Assert.Equal(1, dto.HostCount);
    }
}

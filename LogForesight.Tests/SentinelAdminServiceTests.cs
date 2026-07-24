using System.Text.Json;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel CRUD（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 1）：密碼 write-only、
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

/// <summary>捕捉 detail 物件（真正的 IAuditService 只印摘要，測試需要驗證密碼真的沒進去）</summary>
internal class RecordingAuditService : LogForesight.Web.Services.IAuditService
{
    public List<AuditEntry> Entries { get; } = new();

    public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
        object? detail = null, AuditResult result = AuditResult.Ok) =>
        Entries.Add(new AuditEntry
        {
            Action = action,
            Summary = summary,
            TargetKind = targetKind,
            TargetId = targetId,
            DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
            Result = result
        });

    public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) =>
        Entries.Add(new AuditEntry { Action = action, Account = account, UserId = userId, Summary = summary, Result = result });

    public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) =>
        Entries.Add(new AuditEntry { Action = action, Account = AuditActions.SystemAccount, Summary = summary });
}

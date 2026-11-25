using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 授權矩陣增減式寫入：兩位各持舊畫面的管理員不會互相覆蓋，驗證錯誤整筆不寫，
/// 沒有實際變更不寫稽核，矩陣欄標題的台數只算啟用且未併入的主機。
/// 授權用真實的 <see cref="GroupAccessStore"/>（單次讀改寫），其餘用替身。
/// </summary>
public sealed class GroupAccessChangeTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly GroupAccessStore _access;
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly RecordingAuditService _audit = new();
    private readonly GroupAdminService _service;
    private readonly long _dept;
    private readonly Dictionary<int, long> _hg = new();

    public GroupAccessChangeTests()
    {
        _access = new GroupAccessStore(_fx.Blob("group_access"));
        foreach (var n in new[] { 17, 18, 19, 20 })
            _hg[n] = _hostGroups.Upsert(new HostGroup { GroupName = $"機房{n}", Active = true }).GroupId;
        _dept = _userGroups.Upsert(new UserGroup { GroupName = "維運部", Role = UserRole.User, Active = true }).GroupId;
        _access.SetForUserGroup(_dept, new[] { _hg[19] });

        _service = new GroupAdminService(_userGroups, _hostGroups, _access, new FakeUserStore(), _hosts, _audit,
            new PermissionVersionStamp(_fx.Blob(PermissionVersionStamp.BlobKey)));
    }

    public void Dispose() => _fx.Dispose();

    private HashSet<long> Granted() => _access.GetAll().Where(a => a.UserGroupId == _dept).Select(a => a.HostGroupId).ToHashSet();

    [Fact]
    public void 兩人各持舊畫面_授權與收回互不覆蓋()
    {
        // A（舊畫面只有 19）授權 18；B（同一份舊畫面）收回 19
        _service.ChangeAccess(_dept, new[] { _hg[18] }, null);
        _service.ChangeAccess(_dept, null, new[] { _hg[19] });
        Assert.Equal(new[] { _hg[18] }.ToHashSet(), Granted());

        // B 授權 20；A 授權 17——最終 17、18、20 都在，19 仍然沒有
        _service.ChangeAccess(_dept, new[] { _hg[20] }, null);
        var result = _service.ChangeAccess(_dept, new[] { _hg[17] }, null);

        Assert.Equal(new[] { _hg[17], _hg[18], _hg[20] }.ToHashSet(), Granted());
        Assert.DoesNotContain(_hg[19], Granted());
        Assert.Equal(Granted(), result.GrantedHostGroupIds.ToHashSet());
    }

    [Fact]
    public void 同一群組同時授權與收回_驗證錯誤且資料不變()
    {
        var ex = Assert.Throws<DomainException>(() =>
            _service.ChangeAccess(_dept, new[] { _hg[18], _hg[20] }, new[] { _hg[18] }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 不存在的主機群組_驗證錯誤且資料不變(bool inGrant)
    {
        var good = new[] { _hg[18] };
        var bad = new[] { 9999L };

        var ex = Assert.Throws<DomainException>(() => inGrant
            ? _service.ChangeAccess(_dept, good.Concat(bad).ToArray(), new[] { _hg[19] })
            : _service.ChangeAccess(_dept, good, bad));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 授權給已停用的主機群組_驗證錯誤且整筆不寫入()
    {
        var disabled = _hostGroups.Upsert(new HostGroup { GroupName = "停用機房", Active = false }).GroupId;

        var ex = Assert.Throws<DomainException>(() =>
            _service.ChangeAccess(_dept, new[] { _hg[18], disabled }, null));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("停用機房", ex.Message);
        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 收回已停用主機群組的舊授權_允許()
    {
        var disabled = _hostGroups.Upsert(new HostGroup { GroupName = "停用機房", Active = true }).GroupId;
        _access.SetForUserGroup(_dept, new[] { _hg[19], disabled });
        var group = _hostGroups.Get(disabled)!;
        group.Active = false;
        _hostGroups.Upsert(group);

        var result = _service.ChangeAccess(_dept, null, new[] { disabled });

        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
        Assert.Equal(new[] { _hg[19] }, result.GrantedHostGroupIds);
    }

    [Fact]
    public void 使用者群組不存在_404()
    {
        var ex = Assert.Throws<DomainException>(() => _service.ChangeAccess(777, new[] { _hg[18] }, null));
        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void 舊請求形狀_grant與revoke都沒提供_驗證錯誤且資料不變()
    {
        // 舊格式 { hostGroupIds: [...] } 反序列化後 Grant／Revoke 都是 null
        var legacy = System.Text.Json.JsonSerializer.Deserialize<LogForesight.Web.Models.Dto.SetAccessRequest>(
            "{\"hostGroupIds\":[1,2]}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Null(legacy.Grant);
        Assert.Null(legacy.Revoke);

        var ex = Assert.Throws<DomainException>(() => { _service.ChangeAccess(_dept, legacy.Grant, legacy.Revoke); });

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("grant", ex.Message);
        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 空請求_回目前清單且不寫稽核()
    {
        var result = _service.ChangeAccess(_dept, Array.Empty<long>(), Array.Empty<long>());

        Assert.Equal(new[] { _hg[19] }, result.GrantedHostGroupIds);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 重複授權與重複收回_不算變更不寫稽核()
    {
        _service.ChangeAccess(_dept, new[] { _hg[19] }, new[] { _hg[20] });   // 19 已有、20 本來就沒有

        Assert.Empty(_audit.Entries);
        Assert.Equal(new[] { _hg[19] }.ToHashSet(), Granted());
    }

    [Fact]
    public void 同一請求授權又收回_各寫一筆且摘要是實際變更的名稱()
    {
        _service.ChangeAccess(_dept, new[] { _hg[18], _hg[19] }, new[] { _hg[20], _hg[17] });

        // 19 已有、20 與 17 本來就沒有 → 只有 18 是實際授權，沒有實際收回
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.AccessGrant, entry.Action);
        Assert.Contains("機房18", entry.Summary);
        Assert.DoesNotContain("機房19", entry.Summary);

        _audit.Entries.Clear();
        _service.ChangeAccess(_dept, new[] { _hg[17] }, new[] { _hg[19] });

        Assert.Equal(2, _audit.Entries.Count);
        var grant = _audit.Entries.Single(e => e.Action == AuditActions.AccessGrant);
        var revoke = _audit.Entries.Single(e => e.Action == AuditActions.AccessRevoke);
        Assert.Contains("機房17", grant.Summary);
        Assert.Contains("機房19", revoke.Summary);
        Assert.DoesNotContain("機房19", grant.Summary);
    }

    [Fact]
    public void 矩陣HostCount_只算啟用且未併入的主機_空群組為0()
    {
        void AddHost(string name, bool active, long? mergedInto, params long[] groups)
        {
            var h = _hosts.Upsert(new WebHost { HostName = name, Active = active, MergedInto = mergedInto });
            _hosts.SetGroups(h.HostId, groups);
        }

        AddHost("a", true, null, _hg[17], _hg[18]);
        AddHost("b", true, null, _hg[17]);
        AddHost("c", false, null, _hg[17]);        // 停用不算
        AddHost("d", true, 1, _hg[17], _hg[18]);   // 已併入不算

        var matrix = _service.GetAccessMatrix();
        int Count(int n) => matrix.HostGroups.Single(g => g.GroupId == _hg[n]).HostCount;

        Assert.Equal(2, Count(17));
        Assert.Equal(1, Count(18));
        Assert.Equal(0, Count(19));
        Assert.Equal(0, Count(20));
    }
}

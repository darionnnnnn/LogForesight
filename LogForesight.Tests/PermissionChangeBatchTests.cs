using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動批次核准與 ID 清單查詢測試。
/// </summary>
public sealed class PermissionChangeBatchTests : IDisposable
{
    private readonly EfSqliteFixture _fixture;
    private readonly PermissionChangeStore _store;
    private readonly FakeHostStore _hosts;
    private readonly FakeUserStore _users;
    private readonly FakeUserGroupStore _userGroups;
    private readonly FakeGroupAccessStore _access;
    private readonly RecordingAuditService _audit;

    public PermissionChangeBatchTests()
    {
        _fixture = new EfSqliteFixture();
        _store = new PermissionChangeStore(_fixture.NewContext);
        _hosts = new FakeHostStore();
        _users = new FakeUserStore();
        _userGroups = new FakeUserGroupStore();
        _access = new FakeGroupAccessStore();
        _audit = new RecordingAuditService();

        // 預設建立一台主機 SRV-01
        _hosts.Upsert(new WebHost { HostName = "SRV-01" });
    }

    public void Dispose() => _fixture.Dispose();

    private PermissionChangeService CreateService(ICurrentUser? currentUser = null)
    {
        var user = currentUser ?? FakeCurrentUser.WithCapabilities(Capability.ViewAll, Capability.ConfirmPermission);
        var visibility = new VisibilityService(
            user, _users, _userGroups, _access, _hosts, new FakeIssueCaseStore());
        return new PermissionChangeService(_store, _hosts, visibility, user, _audit, _users);
    }

    [Fact]
    public void 批次授權後_每一筆的status與確認人與確認時間都正確()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "c1", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(1), Category = PermissionCategory.FolderAcl },
            new PermissionChangeRecord { ChangeId = "c2", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(2), Category = PermissionCategory.FolderAcl },
            new PermissionChangeRecord { ChangeId = "c3", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(3), Category = PermissionCategory.FolderAcl }
        });

        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll, Capability.ConfirmPermission);
        var service = CreateService(currentUser);

        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "c1", "c2", "c3" },
            Status = PermissionConfirmStatuses.Authorized,
            Note = "主管同意批次核准"
        });

        Assert.Equal(3, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        var confirmations = _store.GetConfirmations(new[] { "c1", "c2", "c3" });
        Assert.Equal(3, confirmations.Count);
        foreach (var conf in confirmations)
        {
            Assert.Equal(PermissionConfirmStatuses.Authorized, conf.Status);
            Assert.Equal(currentUser.Account, conf.ConfirmedByAccount);
            Assert.Equal(currentUser.UserId, conf.ConfirmedBy);
            Assert.NotNull(conf.ConfirmedAt);
            Assert.Equal("主管同意批次核准", conf.Note);
        }
    }

    [Fact]
    public void 清單中混入一筆已被他人確認的項目時_該筆進skipped且原因可辨識_其餘各筆照樣成功()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "item-1", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(1) },
            new PermissionChangeRecord { ChangeId = "item-already", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(2) },
            new PermissionChangeRecord { ChangeId = "item-3", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(3) }
        });

        // 模擬 item-already 已被他人確認過
        _store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = "item-already",
            Status = PermissionConfirmStatuses.Authorized,
            ConfirmedBy = 123,
            ConfirmedByAccount = "other-user",
            ConfirmedAt = baseTime.AddMinutes(10),
            Note = "他人已先確認"
        });

        var currentUser = FakeCurrentUser.WithCapabilities(Capability.ViewAll, Capability.ConfirmPermission);
        var service = CreateService(currentUser);

        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "item-1", "item-already", "item-3" },
            Status = PermissionConfirmStatuses.Authorized,
            Note = "批次授權"
        });

        // 逐筆語意：item-1 與 item-3 成功，item-already 略過
        Assert.Equal(2, result.UpdatedCount);
        Assert.Single(result.Skipped);

        var skippedItem = result.Skipped[0];
        Assert.Equal("item-already", skippedItem.ChangeId);
        Assert.Equal("SRV-01", skippedItem.HostName);
        Assert.Contains("處理", skippedItem.Reason);

        // 驗證資料庫中各筆狀態
        var c1 = _store.GetConfirmations(new[] { "item-1" }).Single();
        Assert.Equal(PermissionConfirmStatuses.Authorized, c1.Status);
        Assert.Equal(currentUser.Account, c1.ConfirmedByAccount);

        var cAlready = _store.GetConfirmations(new[] { "item-already" }).Single();
        Assert.Equal("other-user", cAlready.ConfirmedByAccount);
        Assert.Equal("他人已先確認", cAlready.Note);

        var c3 = _store.GetConfirmations(new[] { "item-3" }).Single();
        Assert.Equal(PermissionConfirmStatuses.Authorized, c3.Status);
        Assert.Equal(currentUser.Account, c3.ConfirmedByAccount);
    }

    [Fact]
    public void 不可見主機的changeId進skipped_且回應內容不洩漏該筆是否存在()
    {
        var groupA = _userGroups.Upsert(new UserGroup { GroupName = "群組A", Role = UserRole.User });
        var groupB = _userGroups.Upsert(new UserGroup { GroupName = "群組B", Role = UserRole.User });

        var user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\user_a",
            GroupIds = new List<long> { groupA.GroupId }
        });

        var visibleHost = _hosts.Upsert(new WebHost { HostName = "SRV-VISIBLE", GroupIds = new List<long> { 101 } });
        var secretHost = _hosts.Upsert(new WebHost { HostName = "SRV-SECRET", GroupIds = new List<long> { 102 } });

        _access.ReplaceAll(new[]
        {
            new GroupAccess { UserGroupId = groupA.GroupId, HostGroupId = 101 },
            new GroupAccess { UserGroupId = groupB.GroupId, HostGroupId = 102 }
        });

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "c-vis", HostName = visibleHost.HostName, DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "c-sec", HostName = secretHost.HostName, DetectedAt = DateTime.Now }
        });

        var currentUser = FakeCurrentUser.ForUser(user.UserId, Capability.ConfirmPermission);
        var service = CreateService(currentUser);

        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "c-vis", "c-sec" },
            Status = PermissionConfirmStatuses.Authorized
        });

        Assert.Equal(1, result.UpdatedCount);
        Assert.Single(result.Skipped);

        var skipped = result.Skipped[0];
        Assert.Equal("c-sec", skipped.ChangeId);
        // 主機名稱不得洩漏
        Assert.True(string.IsNullOrEmpty(skipped.HostName));
        Assert.Contains("權限", skipped.Reason);
    }

    [Fact]
    public void 不存在的changeId進skipped()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "exist-1", HostName = "SRV-01", DetectedAt = DateTime.Now }
        });

        var service = CreateService();
        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "exist-1", "non-existent-id" },
            Status = PermissionConfirmStatuses.Authorized
        });

        Assert.Equal(1, result.UpdatedCount);
        Assert.Single(result.Skipped);

        var skipped = result.Skipped[0];
        Assert.Equal("non-existent-id", skipped.ChangeId);
        Assert.True(string.IsNullOrEmpty(skipped.HostName));
        Assert.Contains("找不到", skipped.Reason);
    }

    [Fact]
    public void status為suspicious但沒填note被擋下_錯誤訊息可辨識()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "susp-1", HostName = "SRV-01", DetectedAt = DateTime.Now }
        });

        var service = CreateService();
        var ex = Assert.Throws<DomainException>(() => service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "susp-1" },
            Status = PermissionConfirmStatuses.Suspicious,
            Note = "   "
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("可疑", ex.Message);
        Assert.Contains("說明", ex.Message);
    }

    [Fact]
    public void changeIds空陣列或全空白被擋下()
    {
        var service = CreateService();

        var ex1 = Assert.Throws<DomainException>(() => service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string>(),
            Status = PermissionConfirmStatuses.Authorized
        }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex1.Code);
        Assert.Contains("至少勾選一筆", ex1.Message);

        var ex2 = Assert.Throws<DomainException>(() => service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "  ", "" },
            Status = PermissionConfirmStatuses.Authorized
        }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex2.Code);
        Assert.Contains("至少勾選一筆", ex2.Message);
    }

    [Fact]
    public void 超過一次上限500筆被擋下_錯誤訊息含上限數字()
    {
        var service = CreateService();
        var list = Enumerable.Range(1, 501).Select(i => $"id-{i}").ToList();

        var ex = Assert.Throws<DomainException>(() => service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = list,
            Status = PermissionConfirmStatuses.Authorized
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public void status為pending被擋下()
    {
        var service = CreateService();
        var ex = Assert.Throws<DomainException>(() => service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "c1" },
            Status = PermissionConfirmStatuses.Pending
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("只能是", ex.Message);
    }

    [Fact]
    public void 批次確認成功後_稽核只寫一筆且action為perm_confirm_batch()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "aud-1", HostName = "SRV-01", DetectedAt = DateTime.Now, Category = "group_admin" },
            new PermissionChangeRecord { ChangeId = "aud-2", HostName = "SRV-01", DetectedAt = DateTime.Now, Category = "folder_acl" }
        });

        var service = CreateService();
        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "aud-1", "aud-2" },
            Status = PermissionConfirmStatuses.Authorized,
            Note = "批次授權通過"
        });

        Assert.Equal(2, result.UpdatedCount);
        Assert.Single(_audit.Entries);

        var entry = _audit.Entries[0];
        Assert.Equal(AuditActions.PermConfirmBatch, entry.Action);
        Assert.Equal("permission_change", entry.TargetKind);
        Assert.Equal("batch", entry.TargetId);
        Assert.Contains("批次確認", entry.Summary);
        Assert.Contains("2 筆", entry.Summary);
        Assert.NotNull(entry.DetailJson);

        using var doc = JsonDocument.Parse(entry.DetailJson!);
        var root = doc.RootElement;
        Assert.Equal("authorized", root.GetProperty("Status").GetString());
        Assert.Equal(2, root.GetProperty("Count").GetInt32());
        Assert.Equal("批次授權通過", root.GetProperty("Note").GetString());
        Assert.True(root.TryGetProperty("ChangeIds", out _));
        Assert.True(root.TryGetProperty("HostNames", out _));
        Assert.True(root.TryGetProperty("Categories", out _));
        Assert.True(root.TryGetProperty("Skipped", out _));
    }

    [Fact]
    public void ids端點與清單端點在同一組篩選條件下_回傳的id集合一致()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        var records = Enumerable.Range(1, 25).Select(i => new PermissionChangeRecord
        {
            ChangeId = $"test-id-{i:D2}",
            HostName = "SRV-01",
            Category = i % 2 == 0 ? PermissionCategory.FolderAcl : PermissionCategory.GroupMember,
            DetectedAt = baseTime.AddMinutes(i),
            Target = $"Target_{i}"
        }).ToList();
        _store.AppendChanges(records);

        var service = CreateService();
        var queryRequest = new PermissionChangeQueryRequest
        {
            Categories = new List<string> { PermissionCategory.FolderAcl },
            Sort = "detectedAt",
            Ascending = false,
            Page = 1,
            PageSize = 5 // 分頁查詢每頁 5 筆
        };

        // 從清單查詢端點取出所有分頁的全部 ChangeId
        var listIds = new List<string>();
        var firstPage = service.Query(queryRequest);
        listIds.AddRange(firstPage.Items.Select(x => x.ChangeId));

        var totalPages = (int)Math.Ceiling((double)firstPage.Total / 5);
        for (int p = 2; p <= totalPages; p++)
        {
            queryRequest.Page = p;
            var pageResult = service.Query(queryRequest);
            listIds.AddRange(pageResult.Items.Select(x => x.ChangeId));
        }

        // 呼叫 /ids 端點
        var idsResult = service.MatchingChangeIds(new PermissionChangeQueryRequest
        {
            Categories = new List<string> { PermissionCategory.FolderAcl },
            Sort = "detectedAt",
            Ascending = false
        });

        Assert.Equal(listIds.Count, idsResult.Total);
        Assert.False(idsResult.Truncated);
        Assert.Equal(listIds, idsResult.ChangeIds);
    }

    [Fact]
    public void ids端點只回pending的項目()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "pend-1", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(1) },
            new PermissionChangeRecord { ChangeId = "pend-2", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(2) },
            new PermissionChangeRecord { ChangeId = "auth-1", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(3) }
        });

        // 把 auth-1 標為已確認
        _store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = "auth-1",
            Status = PermissionConfirmStatuses.Authorized,
            ConfirmedBy = 1,
            ConfirmedByAccount = "alice",
            ConfirmedAt = DateTime.Now
        });

        var service = CreateService();
        var result = service.MatchingChangeIds(new PermissionChangeQueryRequest());

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.ChangeIds.Count);
        Assert.Contains("pend-1", result.ChangeIds);
        Assert.Contains("pend-2", result.ChangeIds);
        Assert.DoesNotContain("auth-1", result.ChangeIds);
    }

    [Fact]
    public void ids端點超過上限2000筆時_truncated為true且total仍是真實符合筆數()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        var records = Enumerable.Range(1, 2050).Select(i => new PermissionChangeRecord
        {
            ChangeId = $"bulk-id-{i}",
            HostName = "SRV-01",
            DetectedAt = baseTime.AddSeconds(i)
        }).ToList();

        _store.AppendChanges(records);

        var service = CreateService();
        var result = service.MatchingChangeIds(new PermissionChangeQueryRequest());

        Assert.Equal(2050, result.Total);
        Assert.True(result.Truncated);
        Assert.Equal(PermissionChangeService.MaxSelectAllChanges, result.ChangeIds.Count);
    }

    [Fact]
    public void 批次標記可疑成功後_每一筆異動狀態為suspicious且保留note說明()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "susp-ok-1", HostName = "SRV-01", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "susp-ok-2", HostName = "SRV-01", DetectedAt = DateTime.Now }
        });

        var service = CreateService();
        var result = service.ConfirmBatch(new BatchConfirmPermissionChangesRequest
        {
            ChangeIds = new List<string> { "susp-ok-1", "susp-ok-2" },
            Status = PermissionConfirmStatuses.Suspicious,
            Note = "發現未經申請的帳號變動"
        });

        Assert.Equal(2, result.UpdatedCount);
        Assert.Empty(result.Skipped);

        var confirmations = _store.GetConfirmations(new[] { "susp-ok-1", "susp-ok-2" });
        foreach (var c in confirmations)
        {
            Assert.Equal(PermissionConfirmStatuses.Suspicious, c.Status);
            Assert.Equal("發現未經申請的帳號變動", c.Note);
        }
    }

    [Fact]
    public void 全選上限不得大於批次確認上限()
    {
        // 兩者不一致時，使用者會選到一批「選得起來、送不出去」的項目，
        // 而且畫面上沒有取消掉多出來那些的路徑，等於卡死。
        Assert.True(
            PermissionChangeService.MaxSelectAllChanges <= PermissionChangeService.MaxBatchConfirmChanges,
            "全選上限不得大於批次確認上限");
    }
}

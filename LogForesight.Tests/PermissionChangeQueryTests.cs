using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動分頁、多條件篩選、排序、網段解析與 DTO 摘要生成測試。
/// </summary>
public sealed class PermissionChangeQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fixture;
    private readonly PermissionChangeStore _store;
    private readonly FakeHostStore _hosts;
    private readonly FakeUserStore _users;

    public PermissionChangeQueryTests()
    {
        _fixture = new EfSqliteFixture();
        _store = new PermissionChangeStore(_fixture.NewContext);
        _hosts = new FakeHostStore();
        _users = new FakeUserStore();
    }

    public void Dispose() => _fixture.Dispose();

    private PermissionChangeService CreateService(ICurrentUser? currentUser = null)
    {
        var user = currentUser ?? FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var visibility = new VisibilityService(
            user, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(), _hosts, new FakeIssueCaseStore());
        return new PermissionChangeService(_store, _hosts, visibility, user, new RecordingAuditService(), _users);
    }

    [Fact]
    public void 預設查詢依DetectedAt降冪排列_最新資料在最前面()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "c1", HostName = "SRV-01", DetectedAt = baseTime.AddHours(1), Target = "Group1" },
            new PermissionChangeRecord { ChangeId = "c2", HostName = "SRV-01", DetectedAt = baseTime.AddHours(3), Target = "Group2" },
            new PermissionChangeRecord { ChangeId = "c3", HostName = "SRV-01", DetectedAt = baseTime.AddHours(2), Target = "Group3" }
        });

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest());

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("c2", result.Items[0].ChangeId); // 13:00 (最新)
        Assert.Equal("c3", result.Items[1].ChangeId); // 12:00
        Assert.Equal("c1", result.Items[2].ChangeId); // 11:00
    }

    [Fact]
    public void 分頁查詢當PageSize為1時_Total仍等於全部符合筆數()
    {
        var baseTime = new DateTime(2026, 8, 20, 10, 0, 0);
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "p1", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(10) },
            new PermissionChangeRecord { ChangeId = "p2", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(20) },
            new PermissionChangeRecord { ChangeId = "p3", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(30) },
            new PermissionChangeRecord { ChangeId = "p4", HostName = "SRV-01", DetectedAt = baseTime.AddMinutes(40) }
        });

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest
        {
            Page = 1,
            PageSize = 1
        });

        Assert.Equal(4, result.Total);
        Assert.Single(result.Items);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
        Assert.Equal("p4", result.Items[0].ChangeId); // 第一頁是最新那筆
    }

    [Fact]
    public void 關鍵字q比對_能命中操作者帳號也能命中行為說明與目標帳號()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "q-init",
                HostName = "SRV-ALPHA",
                DetectedAt = DateTime.Now,
                InitiatorAccount = "DOMAIN\\SecurityAdmin",
                AlertText = "一般異動說明"
            },
            new PermissionChangeRecord
            {
                ChangeId = "q-alert",
                HostName = "SRV-BETA",
                DetectedAt = DateTime.Now,
                InitiatorAccount = "DOMAIN\\NormalUser",
                AlertText = "關鍵機密權限提權告警"
            },
            new PermissionChangeRecord
            {
                ChangeId = "q-target-acc",
                HostName = "SRV-GAMMA",
                DetectedAt = DateTime.Now,
                TargetAccount = "DOMAIN\\VipTargetUser",
                AlertText = "一般異動說明"
            },
            new PermissionChangeRecord
            {
                ChangeId = "q-target",
                HostName = "SRV-DELTA",
                DetectedAt = DateTime.Now,
                Target = "SensitiveShareFolder",
                AlertText = "一般異動說明"
            }
        });

        var service = CreateService();

        // 1. 命中操作者帳號
        var r1 = service.Query(new PermissionChangeQueryRequest { Keyword = "SecurityAdmin" });
        Assert.Single(r1.Items);
        Assert.Equal("q-init", r1.Items[0].ChangeId);

        // 2. 命中行為說明（AlertText）
        var r2 = service.Query(new PermissionChangeQueryRequest { Keyword = "機密權限提權" });
        Assert.Single(r2.Items);
        Assert.Equal("q-alert", r2.Items[0].ChangeId);

        // 3. 命中目標帳號
        var r3 = service.Query(new PermissionChangeQueryRequest { Keyword = "VipTargetUser" });
        Assert.Single(r3.Items);
        Assert.Equal("q-target-acc", r3.Items[0].ChangeId);

        // 4. 命中對象（Target）
        var r4 = service.Query(new PermissionChangeQueryRequest { Keyword = "SensitiveShare" });
        Assert.Single(r4.Items);
        Assert.Equal("q-target", r4.Items[0].ChangeId);
    }

    [Fact]
    public void 網段篩選_使用CIDR格式與萬用字元各正確命中預期主機()
    {
        _hosts.Upsert(new WebHost { HostId = 1, HostName = "SRV-SUBNET-A", IpAddress = "192.168.1.10", Active = true });
        _hosts.Upsert(new WebHost { HostId = 2, HostName = "SRV-SUBNET-B", IpAddress = "192.168.2.20", Active = true });
        _hosts.Upsert(new WebHost { HostId = 3, HostName = "10.0.0.5", IpAddress = null, Active = true }); // HostName 就是 IP

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "s1", HostName = "SRV-SUBNET-A", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "s2", HostName = "SRV-SUBNET-B", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "s3", HostName = "10.0.0.5", DetectedAt = DateTime.Now }
        });

        var service = CreateService();

        // 1. CIDR 命中 192.168.1.0/24
        var rCidr = service.Query(new PermissionChangeQueryRequest { Subnet = "192.168.1.0/24" });
        Assert.Single(rCidr.Items);
        Assert.Equal("s1", rCidr.Items[0].ChangeId);

        // 2. 萬用字元 命中 192.168.2.*
        var rWildcard = service.Query(new PermissionChangeQueryRequest { Subnet = "192.168.2.*" });
        Assert.Single(rWildcard.Items);
        Assert.Equal("s2", rWildcard.Items[0].ChangeId);

        // 3. 單一 IP 命中 10.0.0.5（來自 HostName 解析）
        var rSingle = service.Query(new PermissionChangeQueryRequest { Subnet = "10.0.0.5" });
        Assert.Single(rSingle.Items);
        Assert.Equal("s3", rSingle.Items[0].ChangeId);
    }

    [Fact]
    public void 網段篩選_格式錯誤時拋出DomainExceptionValidation且錯誤訊息含格式提示()
    {
        var service = CreateService();

        var ex = Assert.Throws<DomainException>(() => service.Query(new PermissionChangeQueryRequest
        {
            Subnet = "abc"
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("192.168.1.0/24", ex.Message);
        Assert.Contains("192.168.1.*", ex.Message);
    }

    [Fact]
    public void 類別篩選_多選類別時具備OR聯集語意()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "cat-gm", HostName = "SRV-01", Category = PermissionCategory.GroupMember, DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "cat-fa", HostName = "SRV-01", Category = PermissionCategory.FolderAcl, DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "cat-oc", HostName = "SRV-01", Category = PermissionCategory.OwnerChange, DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "cat-ap", HostName = "SRV-01", Category = PermissionCategory.AuditPolicy, DetectedAt = DateTime.Now }
        });

        var service = CreateService();

        // 勾選 group_member 與 owner_change (OR)
        var result = service.Query(new PermissionChangeQueryRequest
        {
            Categories = new List<string> { PermissionCategory.GroupMember, PermissionCategory.OwnerChange }
        });

        Assert.Equal(2, result.Total);
        Assert.Contains(result.Items, x => x.ChangeId == "cat-gm");
        Assert.Contains(result.Items, x => x.ChangeId == "cat-oc");
        Assert.DoesNotContain(result.Items, x => x.ChangeId == "cat-fa");
        Assert.DoesNotContain(result.Items, x => x.ChangeId == "cat-ap");
    }

    [Fact]
    public void 排序參數_四個排序欄位各自可用且正反向結果相反()
    {
        var time1 = new DateTime(2026, 8, 19, 10, 0, 0);
        var time2 = new DateTime(2026, 8, 19, 12, 0, 0);
        var time3 = new DateTime(2026, 8, 19, 14, 0, 0);

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "o1", HostName = "SRV-A", Category = "audit_policy", DetectedAt = time1 },
            new PermissionChangeRecord { ChangeId = "o2", HostName = "SRV-B", Category = "folder_acl", DetectedAt = time2 },
            new PermissionChangeRecord { ChangeId = "o3", HostName = "SRV-C", Category = "group_member", DetectedAt = time3 }
        });

        _store.SaveConfirmation(new PermissionChangeConfirmation { ChangeId = "o1", Status = PermissionConfirmStatuses.Authorized });
        _store.SaveConfirmation(new PermissionChangeConfirmation { ChangeId = "o2", Status = PermissionConfirmStatuses.Pending });
        _store.SaveConfirmation(new PermissionChangeConfirmation { ChangeId = "o3", Status = PermissionConfirmStatuses.Suspicious, Note = "疑點" });

        var service = CreateService();

        // 1. detectedAt
        var detAsc = service.Query(new PermissionChangeQueryRequest { Sort = "detectedAt", Ascending = true });
        var detDesc = service.Query(new PermissionChangeQueryRequest { Sort = "detectedAt", Ascending = false });
        Assert.Equal("o1", detAsc.Items.First().ChangeId);
        Assert.Equal("o3", detAsc.Items.Last().ChangeId);
        Assert.Equal("o3", detDesc.Items.First().ChangeId);
        Assert.Equal("o1", detDesc.Items.Last().ChangeId);

        // 2. hostName
        var hostAsc = service.Query(new PermissionChangeQueryRequest { Sort = "hostName", Ascending = true });
        var hostDesc = service.Query(new PermissionChangeQueryRequest { Sort = "hostName", Ascending = false });
        Assert.Equal("SRV-A", hostAsc.Items.First().HostName);
        Assert.Equal("SRV-C", hostAsc.Items.Last().HostName);
        Assert.Equal("SRV-C", hostDesc.Items.First().HostName);
        Assert.Equal("SRV-A", hostDesc.Items.Last().HostName);

        // 3. category
        var catAsc = service.Query(new PermissionChangeQueryRequest { Sort = "category", Ascending = true });
        var catDesc = service.Query(new PermissionChangeQueryRequest { Sort = "category", Ascending = false });
        Assert.Equal("audit_policy", catAsc.Items.First().Category);
        Assert.Equal("group_member", catAsc.Items.Last().Category);
        Assert.Equal("group_member", catDesc.Items.First().Category);
        Assert.Equal("audit_policy", catDesc.Items.Last().Category);

        // 4. status
        var statAsc = service.Query(new PermissionChangeQueryRequest { Sort = "status", Ascending = true });
        var statDesc = service.Query(new PermissionChangeQueryRequest { Sort = "status", Ascending = false });
        Assert.Equal(PermissionConfirmStatuses.Authorized, statAsc.Items.First().Status);
        Assert.Equal(PermissionConfirmStatuses.Suspicious, statAsc.Items.Last().Status);
        Assert.Equal(PermissionConfirmStatuses.Suspicious, statDesc.Items.First().Status);
        Assert.Equal(PermissionConfirmStatuses.Authorized, statDesc.Items.Last().Status);
    }

    [Fact]
    public void 可見範圍為全體ViewAll時_不下推主機名單傳null給Store()
    {
        var user = FakeCurrentUser.WithCapabilities(Capability.ViewAll);
        var service = CreateService(user);

        var filter = service.BuildFilter(new PermissionChangeQueryRequest());

        Assert.Null(filter.HostNames);
    }

    [Fact]
    public void 可見範圍受限時_結果為全體子集且不可見主機紀錄不出現在任何一頁()
    {
        // 建立一位一般使用者
        var createdUser = _users.Upsert(new WebUser { Account = "regular_user", Active = true });

        // 建立兩台主機（一台為該使用者所負責，另一台為他人負責）
        var hostVisible = new WebHost { HostName = "SRV-VISIBLE", Active = true, OwnerUserIds = new List<long> { createdUser.UserId } };
        var hostHidden = new WebHost { HostName = "SRV-HIDDEN", Active = true, OwnerUserIds = new List<long> { 999 } };
        _hosts.Upsert(hostVisible);
        _hosts.Upsert(hostHidden);

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "vis-1", HostName = "SRV-VISIBLE", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "vis-2", HostName = "SRV-VISIBLE", DetectedAt = DateTime.Now.AddMinutes(-5) },
            new PermissionChangeRecord { ChangeId = "hid-1", HostName = "SRV-HIDDEN", DetectedAt = DateTime.Now }
        });

        var limitedUser = FakeCurrentUser.ForUser(createdUser.UserId, Capability.Handle, Capability.ConfirmPermission);

        var service = CreateService(limitedUser);
        var result = service.Query(new PermissionChangeQueryRequest());

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, x => Assert.Equal("SRV-VISIBLE", x.HostName));
        Assert.DoesNotContain(result.Items, x => x.ChangeId == "hid-1");
    }

    [Fact]
    public void SummaryText_所有類別皆產出非空中文摘要且無樣板括號殘留()
    {
        var categories = PermissionCategory.GetAllLabels().Keys;

        foreach (var cat in categories)
        {
            var record = new PermissionChangeRecord
            {
                ChangeId = $"test-{cat}",
                HostName = "SRV-01",
                Category = cat,
                Target = "TestTarget",
                ChangeType = cat == PermissionCategory.GroupMember ? "成員新增" : "權限變更",
                Before = "OldVal",
                After = "NewVal",
                AlertText = "告警文字",
                DetectedAt = DateTime.Now
            };

            var summary = PermissionChangeService.GenerateSummaryText(record);

            Assert.False(string.IsNullOrWhiteSpace(summary), $"類別 {cat} 產生的 SummaryText 不應為空");
            Assert.DoesNotContain("{", summary);
            Assert.DoesNotContain("}", summary);
        }
    }

    [Fact]
    public void CategoryLabel_與PermissionCategory標籤查詢結果完全一致()
    {
        var allLabels = PermissionCategory.GetAllLabels();
        var records = allLabels.Keys.Select((cat, idx) => new PermissionChangeRecord
        {
            ChangeId = $"cat-{idx}",
            HostName = "SRV-01",
            Category = cat,
            DetectedAt = DateTime.Now.AddMinutes(-idx)
        }).ToList();

        _store.AppendChanges(records);
        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest { PageSize = 100 });

        foreach (var item in result.Items)
        {
            var expectedLabel = PermissionCategory.GetLabel(item.Category);
            Assert.Equal(expectedLabel, item.CategoryLabel);
        }
    }

    [Fact]
    public void HostIp取得順序_依序為IpAddress_可解析的主機名_以及null()
    {
        _hosts.Upsert(new WebHost { HostId = 1, HostName = "SRV-IP", IpAddress = "192.168.10.1", Active = true });
        _hosts.Upsert(new WebHost { HostId = 2, HostName = "172.16.1.1", IpAddress = null, Active = true });
        _hosts.Upsert(new WebHost { HostId = 3, HostName = "SRV-NO-IP", IpAddress = null, Active = true });

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "ip-1", HostName = "SRV-IP", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "ip-2", HostName = "172.16.1.1", DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "ip-3", HostName = "SRV-NO-IP", DetectedAt = DateTime.Now }
        });

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest { PageSize = 10 });

        var item1 = result.Items.Single(x => x.ChangeId == "ip-1");
        var item2 = result.Items.Single(x => x.ChangeId == "ip-2");
        var item3 = result.Items.Single(x => x.ChangeId == "ip-3");

        Assert.Equal("192.168.10.1", item1.HostIp);
        Assert.Equal("172.16.1.1", item2.HostIp);
        Assert.Null(item3.HostIp);
    }

    [Fact]
    public void 時間範圍From與To篩選_依DetectedAt正確過濾區間()
    {
        var t1 = new DateTime(2026, 8, 10, 10, 0, 0);
        var t2 = new DateTime(2026, 8, 15, 14, 0, 0);
        var t3 = new DateTime(2026, 8, 20, 18, 0, 0);

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "dt-1", HostName = "SRV-01", DetectedAt = t1 },
            new PermissionChangeRecord { ChangeId = "dt-2", HostName = "SRV-01", DetectedAt = t2 },
            new PermissionChangeRecord { ChangeId = "dt-3", HostName = "SRV-01", DetectedAt = t3 }
        });

        var service = CreateService();

        var result = service.Query(new PermissionChangeQueryRequest
        {
            From = new DateTime(2026, 8, 12),
            To = new DateTime(2026, 8, 18, 23, 59, 59)
        });

        Assert.Single(result.Items);
        Assert.Equal("dt-2", result.Items[0].ChangeId);
    }

    [Fact]
    public void 來源Source篩選_能正確篩選本機監控與NetIQ事件()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "src-local", HostName = "SRV-01", Source = PermissionChangeSources.Local, DetectedAt = DateTime.Now },
            new PermissionChangeRecord { ChangeId = "src-netiq", HostName = "SRV-01", Source = PermissionChangeSources.Netiq, DetectedAt = DateTime.Now }
        });

        var service = CreateService();

        var localResult = service.Query(new PermissionChangeQueryRequest { Source = PermissionChangeSources.Local });
        Assert.Single(localResult.Items);
        Assert.Equal("src-local", localResult.Items[0].ChangeId);

        var netiqResult = service.Query(new PermissionChangeQueryRequest { Source = PermissionChangeSources.Netiq });
        Assert.Single(netiqResult.Items);
        Assert.Equal("src-netiq", netiqResult.Items[0].ChangeId);
    }
}

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
            user, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(), _hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        return new PermissionChangeService(_store, _hosts, visibility, user, new RecordingAuditService(), _users, new NullReportReader(), new FakeSystemSettingsStore());
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

    [Fact]
    public void SummaryText_群組成員新增有操作者時_產生主動句且帳號為短名()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "gm-add-1",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = "admin_ad.brk",
            TargetAccount = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            After = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Target = "_6110H1220000000"
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Equal("admin_ad.brk 將 33951 [Li Zhihui] 加入群組 _6110H1220000000", summary);
    }

    [Fact]
    public void SummaryText_群組成員新增無操作者時_產生被動句且帳號為短名()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "gm-add-2",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = null,
            TargetAccount = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            After = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Target = "_6110H1220000000"
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Equal("33951 [Li Zhihui] 被加入群組 _6110H1220000000", summary);
    }

    [Fact]
    public void SummaryText_群組成員移除_有操作者與無操作者皆產生正確句型()
    {
        var recordWithInit = new PermissionChangeRecord
        {
            ChangeId = "gm-rem-1",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員移除",
            InitiatorAccount = "admin_ad.brk",
            TargetAccount = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Before = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Target = "_6110H1220000000"
        };

        var summaryWithInit = PermissionChangeService.GenerateSummaryText(recordWithInit);
        Assert.Equal("admin_ad.brk 將 33951 [Li Zhihui] 移出群組 _6110H1220000000", summaryWithInit);

        var recordNoInit = new PermissionChangeRecord
        {
            ChangeId = "gm-rem-2",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員移除",
            InitiatorAccount = null,
            TargetAccount = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Before = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Target = "_6110H1220000000"
        };

        var summaryNoInit = PermissionChangeService.GenerateSummaryText(recordNoInit);
        Assert.Equal("33951 [Li Zhihui] 被移出群組 _6110H1220000000", summaryNoInit);
    }

    [Fact]
    public void SummaryText_Target為空字串或空白時_降級為未能解析群組名稱或路徑()
    {
        var gmRecord = new PermissionChangeRecord
        {
            ChangeId = "gm-empty-target",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = null,
            TargetAccount = "CN=33951 [Li Zhihui],OU=User,DC=corp",
            After = "CN=33951 [Li Zhihui],OU=User,DC=corp",
            Target = "   "
        };

        var gmSummary = PermissionChangeService.GenerateSummaryText(gmRecord);
        Assert.Equal("33951 [Li Zhihui] 被加入群組（未能解析群組名稱）", gmSummary);

        var faRecord = new PermissionChangeRecord
        {
            ChangeId = "fa-empty-target",
            Category = PermissionCategory.FolderAcl,
            ChangeType = "權限變更",
            InitiatorAccount = null,
            Target = ""
        };

        var faSummary = PermissionChangeService.GenerateSummaryText(faRecord);
        Assert.Equal("（未能解析路徑）的權限被變更", faSummary);
    }

    [Fact]
    public void SummaryText_Target為舊資料壞形狀結尾帶EventId時_降級為未能解析()
    {
        var gmRecord = new PermissionChangeRecord
        {
            ChangeId = "gm-bad-target",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = null,
            TargetAccount = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            After = "CN=33951 [Li Zhihui],OU=1220000000,OU=User,DC=brk,DC=corp",
            Target = "Microsoft-Windows-Security-Auditing (EventId 4756)",
            EventId = 4756
        };

        var gmSummary = PermissionChangeService.GenerateSummaryText(gmRecord);
        Assert.Equal("33951 [Li Zhihui] 被加入群組（未能解析群組名稱）", gmSummary);

        var faRecord = new PermissionChangeRecord
        {
            ChangeId = "fa-bad-target",
            Category = PermissionCategory.FolderAcl,
            ChangeType = "權限變更",
            InitiatorAccount = "admin_ad.brk",
            Target = "Security-Audit-Source (EventId 4670)",
            EventId = 4670
        };

        var faSummary = PermissionChangeService.GenerateSummaryText(faRecord);
        Assert.Equal("admin_ad.brk 變更（未能解析路徑）的權限", faSummary);
    }

    [Fact]
    public void SummaryText_帳號為完整DN格式時_句中一律轉為短名()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "dn-test",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = "CN=Operator Admin,OU=IT,DC=corp",
            TargetAccount = "CN=John Doe\\, Jr.,OU=Users,DC=corp",
            After = "CN=John Doe\\, Jr.,OU=Users,DC=corp",
            Target = "DevGroup"
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Contains("Operator Admin", summary);
        Assert.Contains("John Doe, Jr.", summary);
        Assert.DoesNotContain("CN=", summary);
        Assert.DoesNotContain("DC=corp", summary);
        Assert.DoesNotContain("OU=", summary);
    }

    [Fact]
    public void SummaryText_資料夾權限異動與稽核政策_長SDDL字串不得進入句子()
    {
        var faRecord = new PermissionChangeRecord
        {
            ChangeId = "fa-sddl",
            Category = PermissionCategory.FolderAcl,
            ChangeType = "權限變更",
            InitiatorAccount = null,
            Target = @"C:\share\finance",
            Before = "D:(A;;GA;;;SY)(A;;GRGX;;;BA)",
            After = "D:(A;;GA;;;SY)(A;;GRGX;;;BA)(A;;0x1200a9;;;WD)"
        };

        var faSummary = PermissionChangeService.GenerateSummaryText(faRecord);
        Assert.Equal(@"C:\share\finance 的權限被變更", faSummary);
        Assert.DoesNotContain("D:(A;;", faSummary);
        Assert.DoesNotContain("SY", faSummary);

        var apRecord = new PermissionChangeRecord
        {
            ChangeId = "ap-sddl",
            Category = PermissionCategory.AuditPolicy,
            ChangeType = "稽核政策變更",
            InitiatorAccount = "DOMAIN\\auditor",
            Target = "Audit-Object-Access",
            Before = "S:(AU;FA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)",
            After = "S:(AU;FA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)(AU;SA;CCDCLCSWRPWPDTLOSDRCWDWO;;;WD)"
        };

        var apSummary = PermissionChangeService.GenerateSummaryText(apRecord);
        Assert.Equal("DOMAIN\\auditor 變更 Audit-Object-Access 的稽核政策", apSummary);
        Assert.DoesNotContain("S:(AU;", apSummary);
    }

    [Fact]
    public void SummaryText_擁有者變更_前後帳號轉為短名且正確入句()
    {
        var recordWithInit = new PermissionChangeRecord
        {
            ChangeId = "oc-1",
            Category = PermissionCategory.OwnerChange,
            ChangeType = "擁有者變更",
            InitiatorAccount = "admin_ad.brk",
            Target = @"C:\share\secret",
            Before = "CN=Alice Smith,OU=Users,DC=corp",
            After = "CN=Bob Jones,OU=Users,DC=corp"
        };

        var summaryWithInit = PermissionChangeService.GenerateSummaryText(recordWithInit);
        Assert.Equal(@"admin_ad.brk 將 C:\share\secret 的擁有者由 Alice Smith 變更為 Bob Jones", summaryWithInit);

        var recordNoInit = new PermissionChangeRecord
        {
            ChangeId = "oc-2",
            Category = PermissionCategory.OwnerChange,
            ChangeType = "擁有者變更",
            InitiatorAccount = null,
            Target = @"C:\share\secret",
            Before = "CN=Alice Smith,OU=Users,DC=corp",
            After = "CN=Bob Jones,OU=Users,DC=corp"
        };

        var summaryNoInit = PermissionChangeService.GenerateSummaryText(recordNoInit);
        Assert.Equal(@"C:\share\secret 的擁有者由 Alice Smith 變更為 Bob Jones", summaryNoInit);
    }

    [Fact]
    public void SummaryText_summary類別_句首不含類別標籤只留對象與AlertText()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "sum-1",
            Category = PermissionCategory.Summary,
            ChangeType = "權限異動（彙總）",
            Target = "SRV-01",
            AlertText = "共 12 筆權限異動"
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);
        Assert.Equal("SRV-01 共 12 筆權限異動", summary);
        Assert.DoesNotContain("例行同步彙總", summary);
    }

    // ── 回饋二十六輪作業 B3：4670 物件權限變更 ──────────────────────────────

    [Fact]
    public void SummaryText_4670物件權限變更_以物件類型與處理程序組句_不說路徑()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "obj-1",
            Category = PermissionCategory.ObjectAcl,
            ChangeType = "權限變更",
            EventId = 4670,
            Target = string.Empty,                    // 4670 的 Token 物件常見「物件名稱: -」
            InitiatorAccount = @"CORP\TP-CRECDC11$",
            ObjectType = "Token",
            ProcessName = @"C:\Windows\System32\svchost.exe"
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Contains("Token 物件", summary);
        Assert.Contains("svchost.exe", summary);
        Assert.Contains("TP-CRECDC11$", summary);
        Assert.DoesNotContain("未能解析路徑", summary);
        Assert.DoesNotContain(@"C:\Windows\System32", summary);   // 完整路徑不入句，只留檔名
    }

    [Fact]
    public void MapToDto_帶出物件類型處理程序與彙總涵蓋區間()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "obj-dto-1",
                HostName = "SRV-01",
                DetectedAt = new DateTime(2026, 8, 18, 10, 44, 0),
                Target = string.Empty,
                ChangeType = "權限變更",
                Category = PermissionCategory.ObjectAcl,
                EventId = 4670,
                ObjectType = "Token",
                ProcessName = @"C:\Windows\System32\svchost.exe",
                CoveredFrom = new DateTime(2026, 8, 18, 1, 5, 0),
                CoveredTo = new DateTime(2026, 8, 18, 2, 30, 0),
                PairCount = 51,
                Source = PermissionChangeSources.Netiq
            }
        });

        var dto = CreateService().Query(new PermissionChangeQueryRequest()).Items.Single();

        Assert.Equal("Token", dto.ObjectType);
        Assert.Equal(@"C:\Windows\System32\svchost.exe", dto.ProcessName);
        Assert.Equal(new DateTime(2026, 8, 18, 1, 5, 0), dto.CoveredFrom);
        Assert.Equal(new DateTime(2026, 8, 18, 2, 30, 0), dto.CoveredTo);
        Assert.Equal(51, dto.PairCount);
    }

    [Fact]
    public void MapToDto_補齊EventId與帳號顯示短名三個欄位()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "dto-1",
                HostName = "SRV-01",
                Category = PermissionCategory.GroupMember,
                ChangeType = "成員新增",
                InitiatorAccount = "CN=33950 [Admin],OU=User,DC=corp",
                TargetAccount = "CN=33951 [Li Zhihui],OU=User,DC=corp",
                After = "CN=33951 [Li Zhihui],OU=User,DC=corp",
                Target = "SecGroup",
                EventId = 4728,
                DetectedAt = DateTime.Now
            }
        });

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest { Keyword = "SecGroup" });

        var item = Assert.Single(result.Items);
        Assert.Equal(4728, item.EventId);
        Assert.Equal("CN=33950 [Admin],OU=User,DC=corp", item.InitiatorAccount);
        Assert.Equal("33950 [Admin]", item.InitiatorAccountDisplay);
        Assert.Equal("CN=33951 [Li Zhihui],OU=User,DC=corp", item.TargetAccount);
        Assert.Equal("33951 [Li Zhihui]", item.TargetAccountDisplay);
    }

    [Fact]
    public void MapToDto_帳號欄位為null或空白時_顯示欄位回傳空字串()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "dto-2",
                HostName = "SRV-01",
                Category = PermissionCategory.FolderAcl,
                ChangeType = "權限變更",
                InitiatorAccount = null,
                TargetAccount = "   ",
                Target = @"C:\share",
                EventId = null,
                DetectedAt = DateTime.Now
            }
        });

        var service = CreateService();
        var result = service.Query(new PermissionChangeQueryRequest { Keyword = "share" });

        var item = Assert.Single(result.Items);
        Assert.Null(item.EventId);
        Assert.Null(item.InitiatorAccount);
        Assert.Equal(string.Empty, item.InitiatorAccountDisplay);
        Assert.Equal("   ", item.TargetAccount);
        Assert.Equal(string.Empty, item.TargetAccountDisplay);
    }

    /// <summary>降級佔位字是全形括號起頭，接合處不得留下孤兒空格——句子中間開洞比截斷還難讀。</summary>
    [Fact]
    public void SummaryText_成員與對象皆降級時_句中不留孤兒空格()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "gm-both-fallback",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            InitiatorAccount = "admin_ad.brk",
            Target = string.Empty
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Equal("admin_ad.brk 將（未能解析成員）加入群組（未能解析群組名稱）", summary);
        Assert.DoesNotContain(" （", summary);
        Assert.DoesNotContain("） ", summary);
    }

    /// <summary>壞形狀辨識要比對本列 EventId：真的叫「Event 5」的群組不能被誤判成壞資料。</summary>
    [Fact]
    public void SummaryText_對象形狀像退路值但EventId不符時_視為正常對象()
    {
        var record = new PermissionChangeRecord
        {
            ChangeId = "gm-real-event-name",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            TargetAccount = @"CORP\alice",
            After = @"CORP\alice",
            Target = "Event 5",
            EventId = 4756
        };

        var summary = PermissionChangeService.GenerateSummaryText(record);

        Assert.Equal(@"CORP\alice 被加入群組 Event 5", summary);
        Assert.DoesNotContain("未能解析", summary);
    }

    /// <summary>資料夾存取狀態沒有操作者，句子仍要有動詞，不能只剩一個孤立路徑。</summary>
    [Fact]
    public void SummaryText_資料夾存取狀態_異動類型缺漏時仍是完整句()
    {
        var withType = PermissionChangeService.GenerateSummaryText(new PermissionChangeRecord
        {
            ChangeId = "fa-type",
            Category = PermissionCategory.FolderAccess,
            ChangeType = "無法存取",
            Target = @"D:\share\finance"
        });
        Assert.Equal(@"D:\share\finance 無法存取", withType);

        var withoutType = PermissionChangeService.GenerateSummaryText(new PermissionChangeRecord
        {
            ChangeId = "fa-no-type",
            Category = PermissionCategory.FolderAccess,
            Target = @"D:\share\finance"
        });
        Assert.Equal(@"D:\share\finance 存取狀態變更", withoutType);
    }

    /// <summary>本機監控的對象字面已含「群組」二字，句子不得再疊一次前綴
    /// （否則變成「加入群組 本機 Administrators 群組」——提權高風險列幾乎全在這條路徑上）。</summary>
    [Fact]
    public void SummaryText_本機監控群組成員_不重複群組前綴()
    {
        var summary = PermissionChangeService.GenerateSummaryText(new PermissionChangeRecord
        {
            ChangeId = "local-gm",
            Category = PermissionCategory.GroupMember,
            ChangeType = "成員新增",
            Target = "本機 Administrators 群組",
            TargetAccount = @"CORP\alice",
            Source = PermissionChangeSources.Local
        });

        Assert.Equal(@"CORP\alice 被加入本機 Administrators 群組", summary);
        Assert.DoesNotContain("群組 本機", summary);
    }

    /// <summary>例行同步彙總列在列表上的那一句要讀得懂「為什麼被合併」，
    /// 而不是只有一個代號（作業 C＋E 的接線）。</summary>
    [Fact]
    public void SummaryText_例行同步彙總列_句中含推測原因與未成對說明()
    {
        var summary = PermissionChangeService.GenerateSummaryText(new PermissionChangeRecord
        {
            ChangeId = "routine-1",
            HostName = "SRV-SYNC",
            Category = PermissionCategory.Summary,
            ChangeType = "例行同步（彙總）",
            Target = "SRV-SYNC（例行同步）",
            AlertText = "本日偵測到 120 對「成員新增＋成員移除」的對稱異動（涉及 8 個群組、110 個帳號），未逐則列出。" +
                        "此模式可能是 AD 自動化程序（例如每天以先清空再重建的方式同步群組成員的腳本）產生；未成對的異動仍逐則列出。"
        });

        Assert.Contains("120 對", summary);
        Assert.Contains("先清空再重建", summary);
        Assert.Contains("未成對的異動仍逐則列出", summary);
    }

    /// <summary>舊彙總列的 EventId 存 0（沒有對應的單一事件）——DTO 要給 null，
    /// 否則展開明細會顯示「0」。</summary>
    [Fact]
    public void MapToDto_EventId為0時給null()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = "evt-zero",
                HostName = "SRV-01",
                DetectedAt = DateTime.Now,
                Category = PermissionCategory.Summary,
                ChangeType = "權限異動（彙總）",
                Target = "SRV-01（彙總）",
                EventId = 0,
                Source = PermissionChangeSources.Netiq
            }
        });

        var dto = Assert.Single(CreateService().Query(new PermissionChangeQueryRequest()).Items);
        Assert.Null(dto.EventId);
    }
}

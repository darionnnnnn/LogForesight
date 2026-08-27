using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動真表儲存（lf_permission_changes）與確認狀態測試。
/// </summary>
public sealed class EfPermissionChangeStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fixture;
    private readonly PermissionChangeStore _store;

    public EfPermissionChangeStoreTests()
    {
        _fixture = new EfSqliteFixture();
        _store = new PermissionChangeStore(_fixture.NewContext);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void 異動寫入後透過Get查詢_取得欄位與寫入時一致()
    {
        var changeId = Guid.NewGuid().ToString("N");
        var detectedAt = new DateTime(2026, 8, 19, 14, 20, 0);

        var record = new PermissionChangeRecord
        {
            ChangeId = changeId,
            HostName = "SRV-DC01",
            DetectedAt = detectedAt,
            Target = "Enterprise Admins",
            ChangeType = "成員新增",
            Before = "（不在群組中）",
            After = "CONTOSO\\AdminUser",
            AlertText = "已將成員新增到安全性通用群組",
            Source = PermissionChangeSources.Netiq,
            EventId = 4756,
            Category = "group_admin",
            IsPrivilegedTarget = true,
            InitiatorAccount = "CONTOSO\\SuperAdmin",
            TargetAccount = "CONTOSO\\AdminUser"
        };

        _store.AppendChanges(new[] { record });

        var fetched = _store.Get(changeId);
        Assert.NotNull(fetched);
        Assert.Equal(changeId, fetched!.ChangeId);
        Assert.Equal("SRV-DC01", fetched.HostName);
        Assert.Equal(detectedAt, fetched.DetectedAt);
        Assert.Equal("Enterprise Admins", fetched.Target);
        Assert.Equal("成員新增", fetched.ChangeType);
        Assert.Equal("（不在群組中）", fetched.Before);
        Assert.Equal("CONTOSO\\AdminUser", fetched.After);
        Assert.Equal("已將成員新增到安全性通用群組", fetched.AlertText);
        Assert.Equal(PermissionChangeSources.Netiq, fetched.Source);
        Assert.Equal(4756, fetched.EventId);
        Assert.Equal("group_admin", fetched.Category);
        Assert.True(fetched.IsPrivilegedTarget);
        Assert.Equal("CONTOSO\\SuperAdmin", fetched.InitiatorAccount);
        Assert.Equal("CONTOSO\\AdminUser", fetched.TargetAccount);
    }

    [Fact]
    public void 併發確認同一筆_兩次呼叫只有第一次成功且資料庫確認人不被覆寫()
    {
        var changeId = Guid.NewGuid().ToString("N");
        var record = new PermissionChangeRecord
        {
            ChangeId = changeId,
            HostName = "SRV-DC01",
            DetectedAt = DateTime.Now,
            Target = "Schema Admins",
            ChangeType = "成員新增"
        };
        _store.AppendChanges(new[] { record });

        var confirmTime1 = new DateTime(2026, 8, 20, 9, 0, 0);
        var firstConfirmation = new PermissionChangeConfirmation
        {
            ChangeId = changeId,
            Status = PermissionConfirmStatuses.Authorized,
            ConfirmedBy = 101,
            ConfirmedByAccount = "alice",
            ConfirmedAt = confirmTime1,
            Note = "已由主管核准"
        };

        var confirmTime2 = new DateTime(2026, 8, 20, 9, 5, 0);
        var secondConfirmation = new PermissionChangeConfirmation
        {
            ChangeId = changeId,
            Status = PermissionConfirmStatuses.Suspicious,
            ConfirmedBy = 202,
            ConfirmedByAccount = "bob",
            ConfirmedAt = confirmTime2,
            Note = "可疑活動"
        };

        // 第一次確認應成功
        var firstResult = _store.SaveConfirmation(firstConfirmation);
        Assert.True(firstResult);

        // 第二次確認應失敗
        var secondResult = _store.SaveConfirmation(secondConfirmation);
        Assert.False(secondResult);

        // 驗證資料庫內的狀態與確認人仍然是第一次的 Alice，未被 Bob 覆寫
        var confirms = _store.GetConfirmations(new[] { changeId });
        Assert.Single(confirms);
        var actual = confirms[0];
        Assert.Equal(PermissionConfirmStatuses.Authorized, actual.Status);
        Assert.Equal(101, actual.ConfirmedBy);
        Assert.Equal("alice", actual.ConfirmedByAccount);
        Assert.Equal(confirmTime1, actual.ConfirmedAt);
        Assert.Equal("已由主管核准", actual.Note);

        // 直接查 DB row 確認資料庫欄位
        using var ctx = _fixture.NewContext();
        var row = ctx.PermissionChanges.Single(r => r.ChangeId == changeId);
        Assert.Equal(PermissionConfirmStatuses.Authorized, row.Status);
        Assert.Equal(101, row.ConfirmedBy);
        Assert.Equal("alice", row.ConfirmedByAccount);
        Assert.Equal(confirmTime1, row.ConfirmedAt);
        Assert.Equal("已由主管核准", row.ConfirmNote);
    }

    [Fact]
    public void CountPending_混合狀態資料下結果與逐列狀態一致()
    {
        var records = new List<PermissionChangeRecord>
        {
            new() { ChangeId = "c1", HostName = "SRV-A", DetectedAt = DateTime.Now },
            new() { ChangeId = "c2", HostName = "SRV-A", DetectedAt = DateTime.Now },
            new() { ChangeId = "c3", HostName = "SRV-B", DetectedAt = DateTime.Now },
            new() { ChangeId = "c4", HostName = "SRV-B", DetectedAt = DateTime.Now },
            new() { ChangeId = "c5", HostName = "SRV-C", DetectedAt = DateTime.Now }
        };
        _store.AppendChanges(records);

        // 確認 c2 與 c4
        _store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = "c2",
            Status = PermissionConfirmStatuses.Authorized
        });
        _store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = "c4",
            Status = PermissionConfirmStatuses.Suspicious,
            Note = "異常"
        });

        // 全體待確認：5 筆中已確認 2 筆，剩餘 3 筆（c1, c3, c5）
        Assert.Equal(3, _store.CountPending(null));

        // 依主機篩選
        Assert.Equal(1, _store.CountPending(new[] { "SRV-A" })); // c1
        Assert.Equal(1, _store.CountPending(new[] { "SRV-B" })); // c3
        Assert.Equal(2, _store.CountPending(new[] { "SRV-A", "SRV-B" })); // c1 + c3
        Assert.Equal(1, _store.CountPending(new[] { "SRV-C" })); // c5

        // 空集合授權範圍回傳 0
        Assert.Equal(0, _store.CountPending(Array.Empty<string>()));
    }

    [Fact]
    public void Prune依寫入時間刪列_事件時間久遠但剛寫入資料庫的列清理後仍存在()
    {
        var recentDetected = DateTime.Now;
        var oldDetected = DateTime.Today.AddDays(-100); // 100 天前的事件（例如 NetIQ 回補）

        var recordOldEventJustAppended = new PermissionChangeRecord
        {
            ChangeId = "recent-write-old-event",
            HostName = "SRV-DC01",
            DetectedAt = oldDetected,
            Target = "Old Group"
        };
        var recordRecentEventJustAppended = new PermissionChangeRecord
        {
            ChangeId = "recent-write-recent-event",
            HostName = "SRV-DC01",
            DetectedAt = recentDetected,
            Target = "Recent Group"
        };

        _store.AppendChanges(new[] { recordOldEventJustAppended, recordRecentEventJustAppended });

        // 模擬另一筆在 60 天前就寫入資料庫的過期列（手動調整 CreatedAt）
        var oldWrittenRowChangeId = "old-written-row";
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = oldWrittenRowChangeId,
                HostName = "SRV-DC01",
                DetectedAt = recentDetected,
                Target = "Old Written Row"
            }
        });

        using (var ctx = _fixture.NewContext())
        {
            var oldRow = ctx.PermissionChanges.Single(r => r.ChangeId == oldWrittenRowChangeId);
            oldRow.CreatedAt = DateTime.Today.AddDays(-60);
            ctx.SaveChanges();
        }

        // 清理超過 30 天保留期的資料（依 created_at）
        var deleted = _store.Prune(30);
        Assert.Equal(1, deleted);

        // 驗證：60 天前寫入的列被刪除
        Assert.Null(_store.Get(oldWrittenRowChangeId));

        // 驗證：detected_at 是 100 天前但剛寫進資料庫的列必須依然存在
        var preservedOldEvent = _store.Get("recent-write-old-event");
        Assert.NotNull(preservedOldEvent);
        Assert.Equal(oldDetected, preservedOldEvent!.DetectedAt);

        // 驗證：剛寫入的新事件列依然存在
        Assert.NotNull(_store.Get("recent-write-recent-event"));
    }

    [Fact]
    public void Query查詢_hostNames為null時不限_為空集合時回空結果()
    {
        var time1 = new DateTime(2026, 8, 19, 10, 0, 0);
        var time2 = new DateTime(2026, 8, 19, 12, 0, 0);
        var time3 = new DateTime(2026, 8, 19, 14, 0, 0);

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "q1", HostName = "SRV-1", DetectedAt = time1, Target = "T1" },
            new PermissionChangeRecord { ChangeId = "q2", HostName = "SRV-2", DetectedAt = time2, Target = "T2" },
            new PermissionChangeRecord { ChangeId = "q3", HostName = "SRV-1", DetectedAt = time3, Target = "T3" }
        });

        // 1. hostNames 為 null：不限主機，依 DetectedAt 降冪排列
        var all = _store.Query(new PermissionChangeQueryFilter { PageSize = 100 }).Items;
        Assert.Equal(3, all.Count);
        Assert.Equal("q3", all[0].ChangeId); // time3
        Assert.Equal("q2", all[1].ChangeId); // time2
        Assert.Equal("q1", all[2].ChangeId); // time1

        // 2. hostNames 為空集合：回傳空結果
        var empty = _store.Query(new PermissionChangeQueryFilter { HostNames = Array.Empty<string>(), PageSize = 100 }).Items;
        Assert.Empty(empty);

        // 3. hostNames 指定主機
        var srv1Only = _store.Query(new PermissionChangeQueryFilter { HostNames = new[] { "srv-1" }, PageSize = 100 }).Items; // 測試大小寫不敏感
        Assert.Equal(2, srv1Only.Count);
        Assert.All(srv1Only, r => Assert.Equal("SRV-1", r.HostName));

        // 4. status 篩選
        _store.SaveConfirmation(new PermissionChangeConfirmation
        {
            ChangeId = "q1",
            Status = PermissionConfirmStatuses.Authorized
        });

        var pendingOnly = _store.Query(new PermissionChangeQueryFilter { Status = PermissionConfirmStatuses.Pending, PageSize = 100 }).Items;
        Assert.Equal(2, pendingOnly.Count);
        Assert.Contains(pendingOnly, r => r.ChangeId == "q2");
        Assert.Contains(pendingOnly, r => r.ChangeId == "q3");

        var authorizedOnly = _store.Query(new PermissionChangeQueryFilter { Status = PermissionConfirmStatuses.Authorized, PageSize = 100 }).Items;
        Assert.Single(authorizedOnly);
        Assert.Equal("q1", authorizedOnly[0].ChangeId);
    }

    [Fact]
    public void GetDedupeKeys_回傳鍵與寫入紀錄的DedupeKey完全一致()
    {
        var time1 = new DateTime(2026, 8, 19, 10, 0, 0);
        var time2 = new DateTime(2026, 8, 19, 11, 0, 0);

        var r1 = new PermissionChangeRecord
        {
            ChangeId = "d1",
            HostName = "SRV-DC01",
            DetectedAt = time1,
            EventId = 4756,
            AlertText = "成員新增告警 1"
        };
        var r2 = new PermissionChangeRecord
        {
            ChangeId = "d2",
            HostName = "SRV-DC02",
            DetectedAt = time2,
            EventId = 4732,
            AlertText = "成員新增告警 2"
        };

        _store.AppendChanges(new[] { r1, r2 });

        var dedupeKeys = _store.GetDedupeKeys();
        Assert.Equal(2, dedupeKeys.Count);
        Assert.Contains(r1.DedupeKey(), dedupeKeys);
        Assert.Contains(r2.DedupeKey(), dedupeKeys);

        // 依 appendedSince 篩選
        var futureKeys = _store.GetDedupeKeys(DateTime.Now.AddHours(1));
        Assert.Empty(futureKeys);

        var pastKeys = _store.GetDedupeKeys(DateTime.Now.AddHours(-1));
        Assert.Equal(2, pastKeys.Count);
    }

    [Fact]
    public void SchemaUpgrader_對同一個資料庫連續執行兩次Upgrade具備冪等性()
    {
        using var ctx = _fixture.NewContext();

        // 第一次升級（通常在 EnsureCreated 後跑）
        SchemaUpgrader.Upgrade(ctx);

        // 第二次升級不應拋出任何例外
        var ex = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));
        Assert.Null(ex);

        // 驗證升級後資料表正常運作
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "idempotent-test", HostName = "SRV-IDEM", DetectedAt = DateTime.Now }
        });
        Assert.NotNull(_store.Get("idempotent-test"));
    }

    [Fact]
    public void PermissionChangeService_Confirm重複確認時拋出Conflict且訊息明確()
    {
        var changeId = Guid.NewGuid().ToString("N");
        var host = new WebHost { HostId = 1, HostName = "SRV-DC01", Active = true };
        var hosts = new FakeHostStore();
        hosts.Upsert(host);

        var users = new FakeUserStore();
        var currentUser = FakeCurrentUser.WithCapabilities(LogForesight.Web.Auth.Capability.ViewAll);
        var visibility = new VisibilityService(currentUser, users, new FakeUserGroupStore(), new FakeGroupAccessStore(), hosts, new FakeIssueCaseStore(), new FakeSystemSettingsStore());
        var service = new PermissionChangeService(_store, hosts, visibility, currentUser, new RecordingAuditService(), users, new NullReportReader(), new FakeSystemSettingsStore());

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = changeId, HostName = "SRV-DC01", DetectedAt = DateTime.Now, Target = "Group1", ChangeType = "成員新增" }
        });

        // 第一次確認成功
        var result = service.Confirm(changeId, new ConfirmPermissionChangeRequest
        {
            Status = PermissionConfirmStatuses.Authorized
        });
        Assert.Equal(PermissionConfirmStatuses.Authorized, result.Status);

        // 第二次確認應拋出 DomainException.Conflict
        var ex = Assert.Throws<DomainException>(() => service.Confirm(changeId, new ConfirmPermissionChangeRequest
        {
            Status = PermissionConfirmStatuses.Suspicious,
            Note = "已被確認過"
        }));

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Contains("已被其他使用者處理過", ex.Message);
    }

    [Fact]
    public void 超過500字的原始訊息_AlertText截斷而RawText保留全文()
    {
        // 展開明細要看完整原文（回饋二十八輪 P9），而清單與去重鍵仍用 500 字截斷版
        var changeId = Guid.NewGuid().ToString("N");
        var fullText = new string('事', 1200);

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = changeId,
                HostName = "SRV-DC01",
                DetectedAt = new DateTime(2026, 8, 25, 9, 0, 0),
                Target = "Domain Admins",
                ChangeType = "成員新增",
                AlertText = fullText[..500],
                RawText = fullText,
                Source = PermissionChangeSources.Netiq,
                EventId = 4756
            }
        });

        var fetched = _store.Get(changeId);

        Assert.NotNull(fetched);
        Assert.Equal(500, fetched!.AlertText.Length);
        Assert.Equal(1200, fetched.RawText!.Length);
        Assert.Equal(fullText, fetched.RawText);
    }

    [Fact]
    public void 未提供RawText的異動_讀回為null()
    {
        // 升級前寫入的資料與彙總列都是這種情形，不回填
        var changeId = Guid.NewGuid().ToString("N");

        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                ChangeId = changeId,
                HostName = "SRV-DC01",
                DetectedAt = new DateTime(2026, 8, 25, 9, 30, 0),
                Target = "SRV-DC01（例行同步）",
                ChangeType = "例行同步（彙總）",
                AlertText = "本日偵測到 60 對對稱異動",
                Source = PermissionChangeSources.Netiq
            }
        });

        var fetched = _store.Get(changeId);

        Assert.NotNull(fetched);
        Assert.Null(fetched!.RawText);
    }
}

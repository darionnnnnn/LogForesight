using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 保留清除抽出後的行為：未結案交辦單的事件清除（保留最新 200 筆與建單事件、已結案不碰），
/// 以及 <see cref="RetentionPruner.Run"/> 記錄當天執行日期。
/// </summary>
public class RetentionPrunerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfWorkOrderStore Store() => new(_fx.NewContext);

    private static WorkOrder NewOrder(string source) => new()
    {
        SourceName = source,
        EventId = 1,
        IssueLabel = source,
        HandlerId = 1,
        Origin = WorkOrderOrigins.Manual,
        ScopeKind = WorkOrderScopes.Hosts,
        CreatedByAccount = "admin",
        CreatedAt = DateTime.Today.AddDays(-500)
    };

    /// <summary>1 筆建單事件（最舊）＋249 筆其他事件，時間由舊到新、每筆差一分鐘，最新一筆落在 newestAt</summary>
    private void Add250Events(long workOrderId, DateTime newestAt)
    {
        using var ctx = _fx.NewContext();
        for (var i = 0; i < 250; i++)
        {
            ctx.WorkOrderEvents.Add(new WorkOrderEventRow
            {
                WorkOrderId = workOrderId,
                Action = i == 0 ? WorkOrderEventActions.Created : WorkOrderEventActions.Appended,
                ActorAccount = "a",
                CreatedAt = newestAt.AddMinutes(i - 249)
            });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public void 未結案單事件全部過期_剩最新200筆加建單事件()
    {
        var store = Store();
        var id = store.Insert(NewOrder("A"));
        Add250Events(id, DateTime.Today.AddDays(-100));

        var pruned = store.PruneOpenOrderEvents(30);

        var events = store.ListEvents(id);
        Assert.Equal(49, pruned);
        Assert.Equal(201, events.Count);
        Assert.Single(events, e => e.Action == WorkOrderEventActions.Created);
        // 留下的其他事件必須是時間最新的 200 筆
        var newestKept = events.Where(e => e.Action != WorkOrderEventActions.Created).Min(e => e.CreatedAt);
        Assert.Equal(DateTime.Today.AddDays(-100).AddMinutes(-199), newestKept);
    }

    [Fact]
    public void 未結案單事件全在保留期內_一筆不刪()
    {
        var store = Store();
        var id = store.Insert(NewOrder("A"));
        Add250Events(id, DateTime.Today.AddDays(-1));

        var pruned = store.PruneOpenOrderEvents(30);

        Assert.Equal(0, pruned);
        Assert.Equal(250, store.ListEvents(id).Count);
    }

    [Fact]
    public void 已結案單的事件不受影響()
    {
        var store = Store();
        var closed = NewOrder("A");
        closed.ClosedAt = DateTime.Today.AddDays(-1);
        var id = store.Insert(closed);
        Add250Events(id, DateTime.Today.AddDays(-100));

        var pruned = store.PruneOpenOrderEvents(30);

        Assert.Equal(0, pruned);
        Assert.Equal(250, store.ListEvents(id).Count);
    }

    [Fact]
    public void Run後LastRunDate為今天()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-retention-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "retention.db")}" }, dir);
            Assert.Null(RetentionPruner.LastRunDate(backend));

            RetentionPruner.Run(backend, new RetentionOptions(), new NullConsole(), batchRunStore: null);

            Assert.Equal(DateTime.Today, RetentionPruner.LastRunDate(backend));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FormalMailShardPruneUsesBoundedKeysetPagesAndResumesCursor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-formal-mail-prune-" + Guid.NewGuid());
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, directory);
        var oldDay = DateTime.UtcNow.Date.AddDays(-120);
        using (var db = backend.CreateContext())
        {
            for (var hostId = 1; hostId <= 18; hostId++)
            {
                var shard = new PrtgFormalMailClaimShard { HostId = hostId };
                db.Blobs.Add(new BlobRow
                {
                    BlobKey = PrtgFormalMailClaimStore.BlobKey(hostId, oldDay),
                    Content = System.Text.Json.JsonSerializer.Serialize(shard, LfJsonOptions.Pretty),
                    UpdatedAt = DateTime.UtcNow.AddDays(-120), Version = 1
                });
            }
            db.SaveChanges();
        }

        var first = PrtgFormalMailClaimStore.PruneExpiredShards(backend, 30, null, DateTime.UtcNow, maximumKeys: 4);
        Assert.Equal(4, first.DeletedShards);
        Assert.True(first.HasMore);
        Assert.False(string.IsNullOrWhiteSpace(first.Cursor));

        var second = PrtgFormalMailClaimStore.PruneExpiredShards(backend, 30, first.Cursor, DateTime.UtcNow);
        Assert.Equal(14, second.DeletedShards);
        Assert.False(second.HasMore);
        Assert.Null(second.Cursor);
        using var verify = backend.CreateContext();
        Assert.Empty(verify.Blobs.Where(row => row.BlobKey.StartsWith("prtg_formal_mail_claims_v2_")));
    }

    [Fact]
    public void FormalMailShardPruneKeepsOldShardReferencedByPendingOutbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-formal-mail-ref-prune-" + Guid.NewGuid());
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, directory);
        var oldDay = DateTime.UtcNow.Date.AddDays(-120);
        var shardKey = PrtgFormalMailClaimStore.BlobKey(77, oldDay);
        using (var db = backend.CreateContext())
        {
            db.Blobs.Add(new BlobRow
            {
                BlobKey = shardKey,
                Content = System.Text.Json.JsonSerializer.Serialize(new PrtgFormalMailClaimShard { HostId = 77 }, LfJsonOptions.Pretty),
                UpdatedAt = DateTime.UtcNow.AddDays(-120), Version = 1
            });
            db.SaveChanges();
        }
        var state = new MailNotifyState();
        state.UrgentOutbox["durable-pending"] = new MailUrgentIntent
        {
            Key = "durable-pending", HostId = 77, ParentRecordId = 9001, RecordDate = oldDay,
            Status = "pending",
            FormalStartFenceRefs = new Dictionary<string, PrtgFormalMailFenceReference>(StringComparer.Ordinal)
            {
                ["recipient|issue"] = new(77, shardKey, "fence", "hash")
            }
        };
        backend.Blob(MailNotifyStateStore.BlobKey).Mutate(_ =>
            (System.Text.Json.JsonSerializer.Serialize(state, LfJsonOptions.Pretty), true));

        var result = PrtgFormalMailClaimStore.PruneExpiredShards(backend, 30, null, DateTime.UtcNow);

        Assert.Equal(0, result.DeletedShards);
        using var verify = backend.CreateContext();
        Assert.True(verify.Blobs.Any(row => row.BlobKey == shardKey));
    }

    [Fact]
    public void FormalMailRetentionReleasesOnlyExpiredFullyTerminalOutboxReferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-formal-mail-terminal-outbox-" + Guid.NewGuid());
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, directory);
        var oldDate = DateTime.UtcNow.Date.AddDays(-120);
        var acceptedState = new MailNotifyState();
        acceptedState.UrgentOutbox["accepted-old"] = new MailUrgentIntent
        {
            Key = "accepted-old", HostId = 77, ParentRecordId = 9001, RecordDate = oldDate,
            Status = "smtp-accepted", Recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["ops@example.test"] = "smtp-accepted" },
            FormalIssueStates = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["ops@example.test|issue"] = "smtp-accepted" }
        };
        acceptedState.UrgentOutbox["unknown-old"] = new MailUrgentIntent
        {
            Key = "unknown-old", HostId = 77, ParentRecordId = 9002, RecordDate = oldDate,
            Status = "pending", Recipients = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["ops@example.test"] = "failed-or-unknown" }
        };
        backend.Blob(MailNotifyStateStore.BlobKey).Mutate(_ =>
            (System.Text.Json.JsonSerializer.Serialize(acceptedState, LfJsonOptions.Pretty), true));

        var retired = new MailNotifyStateStore(backend.Blob(MailNotifyStateStore.BlobKey))
            .PruneTerminalUrgentIntents(30, DateTime.UtcNow);

        Assert.Equal(1, retired);
        var after = new MailNotifyStateStore(backend.Blob(MailNotifyStateStore.BlobKey)).Get();
        Assert.DoesNotContain("accepted-old", after.UrgentOutbox.Keys);
        Assert.Contains("unknown-old", after.UrgentOutbox.Keys);
    }

    [Fact]
    public void 過期診斷與退役證據清理_保留試點及未完成有效判定的證據()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-retention-prtg-" + Guid.NewGuid());
        var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, directory);
        new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => p.SensorIds = [10]);
        foreach (var key in new[] { "prtg_import_diagnostic_test", PrtgSensorTimelineStore.Prefix + 10,
            PrtgSensorTimelineStore.Prefix + 11, PrtgSensorTimelineStore.Prefix + 12 }) backend.Blob(key).Mutate(_ => ("{}", true));
        using (var db = backend.CreateContext())
        {
            db.Blobs.Where(b => b.BlobKey.StartsWith("prtg_")).ExecuteUpdate(u => u.SetProperty(b => b.UpdatedAt, DateTime.Now.AddDays(-100)));
            db.PrtgObservations.Add(new() { SnapshotId = "pending", ActiveKey = "pending", SensorObjid = 11,
                FormatVersion = 2, SupplementStatus = "waiting-netiq", RecordedAtUtc = DateTime.UtcNow.AddDays(-100) });
            db.SaveChanges();
        }
        RetentionPruner.Run(backend, new RetentionOptions { PrtgRetentionDays = 30 }, new NullConsole(), null);
        using var result = backend.CreateContext();
        Assert.False(result.Blobs.Any(b => b.BlobKey == "prtg_import_diagnostic_test"));
        Assert.False(result.Blobs.Any(b => b.BlobKey == PrtgSensorTimelineStore.Prefix + 12));
        Assert.True(result.Blobs.Any(b => b.BlobKey == PrtgSensorTimelineStore.Prefix + 10));
        Assert.True(result.Blobs.Any(b => b.BlobKey == PrtgSensorTimelineStore.Prefix + 11));
        Assert.Single(result.PrtgObservations);
    }

    private sealed class NullConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}

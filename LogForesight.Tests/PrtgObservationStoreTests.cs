using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgObservationStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Day = new(2026, 9, 29);
    private EfPrtgObservationStore Store => new(_fx.NewContext);
    public void Dispose() => _fx.Dispose();

    private static (PrtgFinding, LogIssueSignature) Finding(long sensor = 10, string detail = "Down")
    {
        var finding = new PrtgFinding(1, sensor, "down", detail, 60,
            new KnownIssueRule { Id = "test-down", Severity = IssueSeverity.High });
        return (finding, PrtgFindingMapper.ToSignature(finding, Day));
    }

    [Fact]
    public void 無日誌仍保存_五次重跑只存一次_不污染日誌與正式問題()
    {
        for (var i = 0; i < 5; i++)
            Assert.Equal(i == 0 ? 1 : 0, Store.Capture(7, Day, "revision-a", [Finding()]));
        using var ctx = _fx.NewContext();
        var row = Assert.Single(ctx.PrtgObservations);
        Assert.Empty(ctx.DailyRecords);
        Assert.Empty(ctx.TopIssues);
        Assert.Empty(ctx.IssueCases);
        Assert.Empty(ctx.WorkOrders);
        Assert.Null(row.SourceGeneration);
        Assert.Null(row.ResourceGeneration);
        Assert.Equal(EfPrtgObservationStore.PendingQuality, row.QualityReason);
        Assert.Contains("test-down", row.ContentJson);
    }

    [Fact]
    public void 修訂保留舊證據_空評估不刪除也不宣稱恢復()
    {
        Store.Capture(7, Day, "revision-a", [Finding()]);
        Store.Capture(7, Day, "revision-b", [Finding(detail: "Acknowledged Down")]);
        Store.Capture(7, Day, "revision-b", []);
        Assert.Equal(2, Store.ReadPage([7], Day, Day, 0, 100).Count);
    }

    [Fact]
    public void 日誌刪除不影響獨立證據()
    {
        Store.Capture(7, Day, "revision-a", [Finding()]);
        using var ctx = _fx.NewContext();
        ctx.DailyRecords.Add(new DailyRecordRow
        {
            HostId = 7, HostName = "host", RecordDate = Day, ContentJson = "{}", RiskLevel = "低"
        });
        ctx.SaveChanges();
        ctx.DailyRecords.ExecuteDelete();
        Assert.Single(ctx.PrtgObservations);
    }

    [Fact]
    public void 授權在分頁前生效_空範圍無資料()
    {
        Store.Capture(1, Day, "a", [Finding()]);
        Store.Capture(2, Day, "a", [Finding(11), Finding(12)]);
        Assert.Equal(2, Assert.Single(Store.ReadPage([2], Day, Day, 1, 1)).HostId);
        Assert.Empty(Store.ReadPage([], Day, Day, 0, 10));
        Assert.Empty(Store.ReadPage([2], Day.AddDays(1), Day.AddDays(1), 0, 10));
    }

    [Fact]
    public void 第二批寫入失敗回滾第一批_重試完整保存()
    {
        using (var ctx = _fx.NewContext())
            ctx.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_observation BEFORE INSERT ON lf_prtg_observations
                WHEN NEW.sensor_objid = 301 BEGIN SELECT RAISE(ABORT, 'injected'); END;
                """);
        var batch = Enumerable.Range(1, 301).Select(i => Finding(i)).ToArray();
        Assert.Throws<DbUpdateException>(() => Store.Capture(7, Day, "a", batch));
        using (var ctx = _fx.NewContext())
        {
            Assert.Empty(ctx.PrtgObservations);
            ctx.Database.ExecuteSqlRaw("DROP TRIGGER fail_observation");
        }
        Assert.Equal(301, Store.Capture(7, Day, "a", batch));
        Assert.Equal(0, Store.Capture(7, Day, "a", batch));
    }

    [Fact]
    public void 既有資料庫升級可重跑且保留內容()
    {
        using var ctx = _fx.NewContext();
        ctx.Database.ExecuteSqlRaw("DROP TABLE lf_prtg_observations");
        SchemaUpgrader.Upgrade(ctx);
        Store.Capture(7, Day, "a", [Finding()]);
        SchemaUpgrader.Upgrade(ctx);
        Assert.Single(Store.ReadPage([7], Day, Day, 0, 10));
    }

    [Fact]
    public void SqlServer模型與授權分頁可翻譯()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=localhost;Database=translation-only;Integrated Security=true;TrustServerCertificate=true").Options);
        long[] ids = [7];
        var sql = ctx.PrtgObservations.Where(r => ids.Contains(r.HostId) && r.RecordDate == Day)
            .OrderByDescending(r => r.RecordDate).ThenBy(r => r.HostId).ThenBy(r => r.SnapshotId)
            .Skip(10).Take(10).ToQueryString();
        Assert.Contains("OFFSET", sql);
        Assert.Contains("host_id", sql);
        Assert.Empty(ctx.Model.FindEntityType(typeof(PrtgObservationRow))!.GetForeignKeys());
    }

    [Fact]
    public void 清理只處理過期影子版本_新取得的歷史判定仍保留()
    {
        Store.Capture(7, Day, "a", [Finding(1), Finding(2), Finding(3)]);
        var now = DateTime.UtcNow;
        using (var ctx = _fx.NewContext())
        {
            var rows = ctx.PrtgObservations.OrderBy(r => r.SensorObjid).ToList();
            rows[0].RecordedAtUtc = now.AddDays(-181);
            rows[1].RecordedAtUtc = now.AddDays(-181);
            rows[1].FormatVersion = 2;
            ctx.SaveChanges();
        }
        Assert.Equal(1, Store.PruneShadow(180, now));
        Assert.Equal(2, Store.ReadPage([7], Day, Day, 0, 100).Count);
    }

    [Fact]
    public void 來源位址只存遮蔽提示_憑證輪替不改提示_換端點不能混同()
    {
        var a = "https://alice:secret@prtg.example:443/site?apitoken=do-not-save";
        var rotated = "https://bob:another@prtg.example:443/site?apitoken=rotated";
        var other = "https://prtg-two.example/site";
        Assert.Equal(EfPrtgObservationStore.SourceHintFor(a), EfPrtgObservationStore.SourceHintFor(rotated));
        Assert.NotEqual(EfPrtgObservationStore.SourceHintFor(a), EfPrtgObservationStore.SourceHintFor(other));
        Store.Capture(7, Day, "a", [Finding()], a);
        var row = Assert.Single(Store.ReadPage([7], Day, Day, 0, 100));
        Assert.DoesNotContain("secret", row.ContentJson);
        Assert.DoesNotContain("do-not-save", row.ContentJson);
        Assert.Equal(0, Store.Capture(7, Day, "a", [Finding()], rotated));
        Assert.Equal(1, Store.Capture(7, Day, "a", [Finding()], other));
        Assert.Equal(2, Store.ReadPage([7], Day, Day, 0, 100).Count);
    }

    [Fact]
    public void 有效版本單一_修訂與回復舊版本都保留歷程()
    {
        Store.Capture(7, Day, "a", [Finding(detail: "Down")]);
        Store.Capture(7, Day, "b", [Finding(detail: "Acknowledged Down")]);
        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.PrtgObservations.Count());
            Assert.Single(ctx.PrtgObservations.Where(r => r.ActiveKey != null));
        }
        Assert.Equal(1, Store.Capture(7, Day, "a", [Finding(detail: "Down")]));
        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.PrtgObservations.Count());
            var active = Assert.Single(ctx.PrtgObservations.Where(r => r.ActiveKey != null));
            Assert.Contains("Down", active.ContentJson);
            Assert.DoesNotContain("Acknowledged Down", active.ContentJson);
        }
    }

    [Fact]
    public void 遷移預覽四來源矩陣_舊附掛只記重疊_授權前置()
    {
        Store.Capture(2, Day, "a", [Finding(20)]);
        Store.Capture(3, Day, "a", [Finding(30)]);
        using (var ctx = _fx.NewContext())
        {
            foreach (var hostId in new long[] { 1, 3 })
            {
                var record = new DailyRecordRow
                {
                    HostId = hostId, HostName = $"H{hostId}", RecordDate = Day,
                    RiskLevel = "低", ContentJson = "{}"
                };
                ctx.DailyRecords.Add(record);
                ctx.SaveChanges();
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = record.RecordId, HostId = hostId, RecordDate = Day,
                    LogName = "System", SourceName = "NetIQ", EventId = 1,
                    Category = "Hardware", SeverityRank = 1, EventKey = "netiq:1"
                });
                if (hostId == 3)
                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = record.RecordId, HostId = hostId, RecordDate = Day,
                        LogName = "PRTG", SourceName = "PRTG:down", EventId = 0,
                        Category = "Hardware", SeverityRank = 2, EventKey = "prtg:down:30"
                    });
            }
            ctx.SaveChanges();
        }
        var all = Store.Preview(Day, Day, [1, 2, 3, 4]);
        Assert.Equal(2, all.ActiveSnapshots);
        Assert.Equal(1, all.LegacyIssueRows);
        Assert.Equal(1, all.LegacyOverlaps);
        Assert.Equal(1, all.IndependentSnapshots);
        Assert.Equal(0, all.LegacyWithoutSnapshotRows);
        Assert.False(all.ReadyForCutover);

        var limited = Store.Preview(Day, Day, [2]);
        Assert.Equal(1, limited.ActiveSnapshots);
        Assert.Equal(0, limited.LegacyIssueRows);
        Assert.Equal(1, limited.IndependentSnapshots);
        var empty = Store.Preview(Day, Day, []);
        Assert.Equal(0, empty.ActiveSnapshots);
        Assert.False(empty.ReadyForCutover);
    }
}

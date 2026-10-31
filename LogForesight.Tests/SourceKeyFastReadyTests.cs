using System.Data.Common;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>來源鍵就緒閘門：啟動快判、閘門 B 持久化、旗標單調與背景服務重試。</summary>
public sealed class SourceKeyFastReadyTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Date = new(2026, 8, 12);

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LogForesight.sln")) &&
               !Directory.Exists(Path.Combine(dir, "LogForesight.Core"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir! }.Concat(parts).ToArray());
    }

    private static long AddRecord(LfDbContext ctx)
    {
        var rec = new DailyRecordRow { HostId = 1, HostName = "A", RecordDate = Date, RiskLevel = "高", ContentJson = "{}" };
        ctx.DailyRecords.Add(rec);
        ctx.SaveChanges();
        return rec.RecordId;
    }

    private void SeedIssue(string? sourceKey, DateTime? recordDate = null)
    {
        using var ctx = _fx.NewContext();
        ctx.TopIssues.Add(new TopIssueRow
        {
            RecordId = AddRecord(ctx), SourceName = "Disk", EventId = 1,
            SourceKey = sourceKey, RecordDate = recordDate ?? Date
        });
        ctx.SaveChanges();
    }

    private void SetRekeyBlob(string? version)
    {
        using var ctx = _fx.NewContext();
        if (version == null) return;
        ctx.Blobs.Add(new BlobRow
        {
            BlobKey = SchemaUpgrader.IssueFirstSeenSourceKeyRekeyDoneBlobKey,
            Content = version, UpdatedAt = DateTime.Now, Version = 1
        });
        ctx.SaveChanges();
    }

    // ── 驗收 1、2：快判 ──
    [Fact]
    public void 快判_完整資料成立()
    {
        SeedIssue("DISK");
        SetRekeyBlob("1");
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.True(b.TryFastReady());
        Assert.True(b.IssueSourceKeyReady);
        Assert.True(b.Progress.Completed);
        Assert.True(b.SourceKeyProgress.Completed);
    }

    [Fact]
    public void 快判_有NULL來源鍵不成立()
    {
        SeedIssue("DISK");
        SeedIssue(null);
        SetRekeyBlob("1");
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.False(b.TryFastReady());
        Assert.False(b.IssueSourceKeyReady);
        Assert.False(b.SourceKeyProgress.Completed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public void 快判_重鍵標記缺或版本不符不成立(string? version)
    {
        SeedIssue("DISK");
        SetRekeyBlob(version);
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.False(b.TryFastReady());
        Assert.False(b.IssueSourceKeyReady);
    }

    [Fact]
    public void 快判_有未回填聚合欄不成立()
    {
        SeedIssue("DISK");
        SeedIssue("DISK", DateTime.MinValue);
        SetRekeyBlob("1");
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.False(b.TryFastReady());
        Assert.False(b.IssueSourceKeyReady);
        Assert.False(b.Progress.Completed);
    }

    // ── 驗收 3：單調 ──
    [Fact]
    public void 旗標單調_快判後Run不倒退()
    {
        SeedIssue("DISK");
        SetRekeyBlob("1");
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.True(b.TryFastReady());
        b.Run(CancellationToken.None);
        Assert.True(b.IssueSourceKeyReady);

        var src = File.ReadAllText(RepoFile("LogForesight.Core", "Persistence", "Sql", "TopIssueBackfiller.cs"));
        var start = src.IndexOf("public void Run(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("public void RunRiskySourceKeys(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.DoesNotContain("Volatile.Write(ref _issueSourceKeyReady, 0)", src[start..end]);
    }

    // ── 驗收 8：ToQueryString 形狀 ──
    [Fact]
    public void 快判SQL_是EXISTS形狀且不含COUNT()
    {
        using var ctx = _fx.NewContext();
        var sql = ctx.TopIssues.Where(x => x.SourceKey == null).Select(x => x.IssueId).Take(1).ToQueryString();
        Assert.Contains("IS NULL", sql);
        var src = File.ReadAllText(RepoFile("LogForesight.Core", "Persistence", "Sql", "TopIssueBackfiller.cs"));
        var start = src.IndexOf("public bool TryFastReady(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("/// <summary>", start, StringComparison.Ordinal);
        var body = src[start..end];
        Assert.Contains("TopIssues.Any(x => x.SourceKey == null)", body);
        Assert.DoesNotContain(".Count(", body);
    }

    // ── 驗收 4、5：閘門 B ──
    private sealed class RiskyQueryCounter : DbCommandInterceptor
    {
        public int RiskyQueries;
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("lf_risky_events", StringComparison.OrdinalIgnoreCase)) RiskyQueries++;
            return result;
        }
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            if (command.CommandText.Contains("lf_risky_events", StringComparison.OrdinalIgnoreCase)) RiskyQueries++;
            return result;
        }
    }

    private static void SetRiskyBlob(Func<LfDbContext> f, string version)
    {
        using var ctx = f();
        ctx.Blobs.Add(new BlobRow
        {
            BlobKey = SchemaUpgrader.RiskySourceKeyDoneBlobKey, Content = version, UpdatedAt = DateTime.Now, Version = 1
        });
        ctx.SaveChanges();
    }

    private void SeedRisky(string? sourceKey)
    {
        using var ctx = _fx.NewContext();
        ctx.RiskyEvents.Add(new RiskyEventRow
        {
            HostId = 1, Date = Date, LogName = "System", Source = "Disk", SourceKey = sourceKey, EventId = 1,
            EntryType = System.Diagnostics.EventLogEntryType.Error, EventTime = Date, Message = "m", CreatedAt = Date
        });
        ctx.SaveChanges();
    }

    [Fact]
    public void 閘門B_blob已成立時不查風險事件表()
    {
        var counter = new RiskyQueryCounter();
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        LfDbContext Make() => new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(conn).AddInterceptors(counter).Options);
        using (var c = Make()) c.Database.EnsureCreated();
        SetRiskyBlob(Make, "1");
        counter.RiskyQueries = 0;

        var b = new TopIssueBackfiller(Make);
        b.RunRiskySourceKeys(new EfRiskyEventStore(Make), CancellationToken.None);

        Assert.True(b.RiskySourceKeyProgress.Completed);
        Assert.Equal(0, counter.RiskyQueries);
    }

    [Fact]
    public void 閘門B_回填完成寫入blob_未完成不寫()
    {
        SeedRisky(null);
        var b = new TopIssueBackfiller(_fx.NewContext);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            b.RunRiskySourceKeys(new EfRiskyEventStore(_fx.NewContext), cancelled.Token);
        }
        Assert.False(b.RiskySourceKeyProgress.Completed);
        using (var ctx = _fx.NewContext())
            Assert.DoesNotContain(ctx.Blobs, x => x.BlobKey == SchemaUpgrader.RiskySourceKeyDoneBlobKey);

        b.RunRiskySourceKeys(new EfRiskyEventStore(_fx.NewContext), CancellationToken.None);
        Assert.True(b.RiskySourceKeyProgress.Completed);
        using (var ctx = _fx.NewContext())
            Assert.Equal("1", ctx.Blobs.Single(x => x.BlobKey == SchemaUpgrader.RiskySourceKeyDoneBlobKey).Content);
    }

    [Fact]
    public void 兩閘門獨立_A成立B不成立()
    {
        SeedIssue("DISK");
        SetRekeyBlob("1");
        SeedRisky(null);
        var b = new TopIssueBackfiller(_fx.NewContext);
        Assert.True(b.TryFastReady());
        Assert.True(b.IssueSourceKeyReady);
        Assert.False(b.RiskySourceKeyProgress.Completed);
    }

    // ── 驗收 6、7：HostedService ──
    [Fact]
    public async Task HostedService_取數執行中gate不放行_快判仍先執行()
    {
        SeedIssue("DISK");
        SetRekeyBlob("1");
        var backfiller = new TopIssueBackfiller(_fx.NewContext);
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("test", out _));
        var gate = new BackgroundWorkGate(runState, TimeSpan.FromMilliseconds(20));
        var runCalls = 0;
        var svc = new TopIssueBackfillHostedService(
            backfiller.TryFastReady, _ => Interlocked.Increment(ref runCalls), new DataVersionStamp(), gate)
        { InitialDelay = TimeSpan.Zero };

        await svc.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && !backfiller.IssueSourceKeyReady; i++) await Task.Delay(20);
        Assert.True(backfiller.IssueSourceKeyReady);
        Assert.Equal(0, runCalls);
        await svc.StopAsync(CancellationToken.None);
    }

    private static async Task<int> RunWithFailures(int failTimes)
    {
        var calls = 0;
        var gate = new BackgroundWorkGate(new SchedulerRunState(), TimeSpan.FromMilliseconds(20));
        var svc = new TopIssueBackfillHostedService(
            () => false,
            _ => { if (Interlocked.Increment(ref calls) <= failTimes) throw new InvalidOperationException("boom"); },
            new DataVersionStamp(), gate)
        { InitialDelay = TimeSpan.Zero, RetryInterval = TimeSpan.Zero };
        await svc.StartAsync(CancellationToken.None);
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        return calls;
    }

    [Fact]
    public async Task 重試_前兩次失敗第三次成功共呼叫三次()
        => Assert.Equal(3, await RunWithFailures(2));

    [Fact]
    public async Task 重試_四次都失敗呼叫四次後停止不擲出()
        => Assert.Equal(4, await RunWithFailures(99));
}

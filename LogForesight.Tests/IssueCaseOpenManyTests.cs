using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 多台主機的進行中案件（<see cref="IIssueCaseStore.GetOpenMany"/>，第 77 輪 D-3）：已結案案件在保留期內不清除，
/// 3000 台時遠多於進行中的；只要進行中的呼叫端不得把已結案的一起讀回。
/// </summary>
public sealed class IssueCaseOpenManyTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly SqlCommandRecorder _recorder = new();

    public void Dispose() => _fx.Dispose();

    private EfIssueCaseStore Store() => new(() =>
        new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_fx.NewContext().Database.GetDbConnection())
            .AddInterceptors(_recorder).Options));

    private static IssueCase Case(string id, string host, bool closed) => new()
    {
        CaseId = id, HostName = host, IssueKey = $"System|Disk|153|{id}", IssueLabel = "Disk 153",
        Status = closed ? IssueHandlingStatuses.Resolved : IssueHandlingStatuses.InProgress, HandlerId = 1,
        FirstLinkedDate = DateTime.Today, LastLinkedDate = DateTime.Today,
        CreatedAt = DateTime.Now, CreatedByAccount = "admin", UpdatedAt = DateTime.Now,
        ClosedAt = closed ? DateTime.Now : null
    };

    [Fact]
    public void 只回進行中案件_GetMany仍含已結案()
    {
        var store = Store();
        store.Save(Case("open-a", "SRV-01", closed: false));
        store.Save(Case("closed-a", "SRV-01", closed: true));
        store.Save(Case("open-b", "SRV-02", closed: false));
        store.Save(Case("other", "SRV-09", closed: false));

        var open = store.GetOpenMany(new[] { "srv-01", "SRV-02" });
        Assert.Equal(new[] { "open-a", "open-b" }, open.Select(c => c.CaseId).OrderBy(x => x));
        Assert.Equal(3, store.GetMany(new[] { "SRV-01", "SRV-02" }).Count);
        Assert.Empty(store.GetOpenMany(Array.Empty<string>()));
    }

    [Fact]
    public void 讀回列數不隨已結案案件數成長()
    {
        var store = Store();
        store.Save(Case("open", "SRV-01", closed: false));
        for (var i = 0; i < 10; i++) store.Save(Case($"c{i}", "SRV-01", closed: true));
        var (_, few) = _recorder.Measure(() => store.GetOpenMany(new[] { "SRV-01" }));

        for (var i = 10; i < 200; i++) store.Save(Case($"c{i}", "SRV-01", closed: true));
        var (_, many) = _recorder.Measure(() => store.GetOpenMany(new[] { "SRV-01" }));

        Assert.Equal(few, many);
    }

    /// <summary>介面預設實作（測試替身走這條）與正式實作結果一致。</summary>
    [Fact]
    public void 替身的預設實作與正式實作一致()
    {
        var ef = Store();
        var fake = new FakeIssueCaseStore();
        foreach (var c in new[] { Case("o", "A", false), Case("x", "A", true), Case("p", "B", false) })
        {
            ef.Save(c);
            fake.Save(c);
        }

        Assert.Equal(
            ef.GetOpenMany(new[] { "A", "B" }).Select(c => c.CaseId).OrderBy(x => x),
            ((IIssueCaseStore)fake).GetOpenMany(new[] { "A", "B" }).Select(c => c.CaseId).OrderBy(x => x));
    }
}

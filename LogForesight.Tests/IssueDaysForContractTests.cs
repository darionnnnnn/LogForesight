using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordQuery.IssueDaysFor"/> 的合約：EF（走 lf_top_issues 事實表）與兩個測試替身
/// （反序列化紀錄逐筆組鍵）必須逐筆相同——IssueCaseCoordinator 的合格日規則只有一份，
/// 替身與正式實作語意一分岔，案件測試就是在驗一個不存在的行為。
/// </summary>
public class IssueDaysForContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static readonly DateTime D1 = new(2026, 9, 1);
    private static readonly DateTime D2 = new(2026, 9, 2);
    private static readonly DateTime D3 = new(2026, 9, 3);

    private static LogIssueSignature Win(string source, int eventId) => new()
    {
        LogName = "System", Source = source, EventId = eventId, EntryType = EventLogEntryType.Error, Count = 1
    };

    private static LogIssueSignature Linux(string eventKey) => new()
    {
        LogName = "syslog", Source = "sshd", EventId = 0, EntryType = EventLogEntryType.Warning, EventKey = eventKey, Count = 1
    };

    private static readonly string WinKey = IssueSignatureKey.For(Win("disk", 153));
    private static readonly string BruteKey = IssueSignatureKey.For(Linux("ssh-bruteforce"));
    private static readonly string AcceptKey = IssueSignatureKey.For(Linux("ssh-accept"));

    /// <summary>主機：SRV-A(1)、墓碑 SRV-OLD(2) 併入 SRV-A、LNX-1(3)、別台 SRV-X(4)</summary>
    private static FakeHostStore Hosts()
    {
        var hosts = new FakeHostStore();
        hosts.Upsert(new WebHost { HostName = "SRV-A" });
        hosts.Upsert(new WebHost { HostName = "SRV-OLD" });
        hosts.Upsert(new WebHost { HostName = "LNX-1" });
        hosts.Upsert(new WebHost { HostName = "SRV-X" });
        hosts.Merge(hosts.FindByName("SRV-OLD")!.HostId, hosts.FindByName("SRV-A")!.HostId);
        return hosts;
    }

    private static List<DailyAnalysisRecord> Records(FakeHostStore hosts)
    {
        DailyAnalysisRecord Rec(string host, DateTime date, params LogIssueSignature[] issues) => new()
        {
            HostId = hosts.FindByName(host)!.HostId, Host = host, Date = date, RiskLevel = "高",
            TopIssues = issues.ToList()
        };

        return new List<DailyAnalysisRecord>
        {
            Rec("SRV-A", D1, Win("disk", 153), Win("disk", 7)),
            Rec("SRV-A", D2, Win("disk", 153)),
            Rec("SRV-OLD", D3, Win("disk", 153)),
            // 同 EventId 不同來源：SQL 預篩會撈到，C# 組鍵後必須排除
            Rec("SRV-A", D3, Win("ntfs", 153)),
            Rec("LNX-1", D1, Linux("ssh-bruteforce"), Linux("ssh-accept")),
            Rec("LNX-1", D2, Linux("ssh-bruteforce")),
            // 別台主機：不在查詢範圍
            Rec("SRV-X", D1, Win("disk", 153))
        };
    }

    private static List<HostKey> QueryHosts(FakeHostStore hosts)
    {
        var keys = HostIdentityResolver.Expand(hosts.GetAll(), hosts.FindByName("SRV-A")!.HostId);
        keys.AddRange(HostIdentityResolver.Expand(hosts.GetAll(), hosts.FindByName("LNX-1")!.HostId));
        return keys;
    }

    private static List<string> Normalize(IEnumerable<IssueDayHit> hits) =>
        hits.Select(h => $"{h.HostId}|{h.Date:yyyy-MM-dd}|{h.IssueKey}").OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public void EF與兩個替身結果逐筆相同_四段五段鍵與墓碑主機()
    {
        var hosts = Hosts();
        var ef = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var fakeQuery = new FakeAnalysisRecordQuery();
        var fakeRepo = new FakeRecordRepository(hosts);
        foreach (var r in Records(hosts)) ef.Append(r);
        foreach (var r in Records(hosts)) fakeQuery.Add(r);
        foreach (var r in Records(hosts)) fakeRepo.Add(r);

        var queryHosts = QueryHosts(hosts);
        var keys = new[] { WinKey, BruteKey, AcceptKey };

        var fromEf = Normalize(ef.IssueDaysFor(queryHosts, keys));
        var fromFakeQuery = Normalize(fakeQuery.IssueDaysFor(queryHosts, keys));
        var fromFakeRepo = Normalize(((IAnalysisRecordQuery)fakeRepo).IssueDaysFor(queryHosts, keys));

        var a = hosts.FindByName("SRV-A")!.HostId;
        var old = hosts.FindByName("SRV-OLD")!.HostId;
        var lnx = hosts.FindByName("LNX-1")!.HostId;
        var expected = Normalize(new[]
        {
            new IssueDayHit(a, WinKey, D1), new IssueDayHit(a, WinKey, D2), new IssueDayHit(old, WinKey, D3),
            new IssueDayHit(lnx, BruteKey, D1), new IssueDayHit(lnx, AcceptKey, D1), new IssueDayHit(lnx, BruteKey, D2)
        });

        Assert.Equal(expected, fromEf);
        Assert.Equal(expected, fromFakeQuery);
        Assert.Equal(expected, fromFakeRepo);
    }

    [Fact]
    public void EF_只查單一五段鍵不混入同program的另一個鍵()
    {
        var hosts = Hosts();
        var ef = new EfAnalysisRecordStore(_fx.NewContext, "test");
        foreach (var r in Records(hosts)) ef.Append(r);

        var hits = ef.IssueDaysFor(QueryHosts(hosts), new[] { AcceptKey });

        var hit = Assert.Single(hits);
        Assert.Equal(AcceptKey, hit.IssueKey);
        Assert.Equal(D1, hit.Date);
    }

    [Fact]
    public void EF_未回填的事實表列不回傳()
    {
        var hosts = Hosts();
        var ef = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var a = hosts.FindByName("SRV-A")!.HostId;
        ef.Append(new DailyAnalysisRecord { HostId = a, Host = "SRV-A", Date = D1, RiskLevel = "高" });

        using (var ctx = _fx.NewContext())
        {
            var recordId = ctx.DailyRecords.Single().RecordId;
            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = recordId, HostId = a, RecordDate = DateTime.MinValue,
                LogName = "System", SourceName = "disk", EventId = 153, EntryType = (int)EventLogEntryType.Error,
                Category = "Other", EventKey = string.Empty
            });
            ctx.SaveChanges();
        }

        Assert.Empty(ef.IssueDaysFor(QueryHosts(hosts), new[] { WinKey }));
    }

    [Fact]
    public void EF_600台主機一次查詢_讀取命令數等於主機批數()
    {
        var ef = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var hostKeys = new List<HostKey>();
        for (var id = 1; id <= 600; id++)
        {
            var name = $"H{id:D4}";
            hostKeys.Add(new HostKey { HostId = id, HostName = name });
            ef.Append(new DailyAnalysisRecord
            {
                HostId = id, Host = name, Date = D1, RiskLevel = "高", TopIssues = { Win("disk", 153) }
            });
        }

        var counter = new ReaderCounter();
        DbConnection connection;
        using (var probe = _fx.NewContext()) connection = probe.Database.GetDbConnection();
        var counting = new EfAnalysisRecordStore(() => new LfDbContext(
            new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(counter).Options), "test");

        var hits = counting.IssueDaysFor(hostKeys, new[] { WinKey });

        Assert.Equal(600, hits.Count);
        Assert.Equal(600, hits.Select(h => h.HostId).Distinct().Count());
        Assert.Equal(2, counter.Readers);   // 500 + 100 兩批，不是 600 次
    }

    [Fact]
    public void 空主機或空鍵回空清單_三個實作一致()
    {
        var hosts = Hosts();
        var ef = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var fakeQuery = new FakeAnalysisRecordQuery();
        IAnalysisRecordQuery fakeRepo = new FakeRecordRepository(hosts);
        foreach (var r in Records(hosts)) { ef.Append(r); fakeQuery.Add(r); ((FakeRecordRepository)fakeRepo).Add(r); }

        foreach (IAnalysisRecordQuery q in new IAnalysisRecordQuery[] { ef, fakeQuery, fakeRepo })
        {
            Assert.Empty(q.IssueDaysFor(Array.Empty<HostKey>(), new[] { WinKey }));
            Assert.Empty(q.IssueDaysFor(QueryHosts(hosts), Array.Empty<string>()));
        }
    }

    private sealed class ReaderCounter : DbCommandInterceptor
    {
        public int Readers { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Readers++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }
}

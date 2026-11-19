using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦時問題鍵超過欄位上限：該筆不建案件、不寫處理紀錄（不留孤立紀錄），其餘成員照常建立，
/// 結果明確指出哪一筆未建立。上限來自 <see cref="IssueKeyLimit"/>（HasMaxLength），超長是拒絕不是截斷。
/// </summary>
public class WorkOrderOverlongKeyTests
{
    // 來源與記錄名各自在入口截斷到 255，所以超長的問題鍵要靠 EventKey 段撐長；來源取 100 字讓長度可精準落在上限兩側
    private static readonly string Source = new string('S', 100);
    private const int EventId = 153;
    private static readonly DateTime Day = new(2026, 9, 15);
    private static readonly DateTime T0 = new(2026, 9, 17, 10, 0, 0);

    private static readonly string LogName = new('L', 255);
    private static readonly string NormalKey = IssueSignatureKey.For("System", Source, EventId, LogEntryType.Error);

    /// <summary>指定長度的問題鍵（四段鍵加 EventKey 第五段補足長度）</summary>
    private static string KeyOfLength(int length)
    {
        var baseKey = IssueSignatureKey.For(LogName, Source, EventId, LogEntryType.Error, string.Empty);
        var key = IssueSignatureKey.For(LogName, Source, EventId, LogEntryType.Error, new string('e', length - baseKey.Length - 1));
        Assert.Equal(length, key.Length);
        return key;
    }

    private sealed class World
    {
        public readonly FakeHostStore Hosts = new();
        public readonly FakeAnalysisRecordQuery Records = new();
        public readonly FakeIssueCaseStore Cases = new();
        public readonly FakeIssueHandlingStore IssueHandlings = new();
        public readonly CaseDaySyncTests.RecordingHandlingStore HandlingLog = new();
        public readonly FakeIssueOwnerStore IssueProfiles = new();
        public readonly FakeWorkOrderStore Orders;
        public readonly WorkOrderCoordinator Coordinator;

        public World()
        {
            Orders = new FakeWorkOrderStore(Cases);
            var caseCoordinator = new IssueCaseCoordinator(Cases, IssueHandlings, HandlingLog, Records, Hosts, IssueProfiles);
            Coordinator = new WorkOrderCoordinator(Orders, Cases, IssueHandlings, caseCoordinator, HandlingLog, Hosts);
        }

        public void AddHostDay(string host, string key)
        {
            var existing = Hosts.FindByName(host) ?? Hosts.Upsert(new WebHost { HostName = host });
            var parsed = IssueSignatureKey.TryParseFull(key)!.Value;
            Records.Add(new DailyAnalysisRecord
            {
                Date = Day, HostId = existing.HostId, Host = host,
                TopIssues = new List<LogIssueSignature>
                {
                    new() { LogName = parsed.LogName, Source = Source, EventId = EventId, EntryType = LogEntryType.Error, EventKey = parsed.EventKey }
                }
            });
        }
    }

    private static WorkOrderCreateRequest Request(params (string Host, string Key)[] members) => new()
    {
        Source = Source, EventId = EventId, IssueLabel = "disk 153", HandlerId = 1,
        Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
        Note = "請處理",
        Members = members.Select(m => new WorkOrderMember
        {
            HostName = m.Host, IssueKey = m.Key, IssueLabel = "disk 153", TriggerDate = Day
        }).ToList(),
        Actor = new WorkOrderActor { ActorId = 9, ActorAccount = "boss", OccurredAt = T0 }
    };

    [Fact]
    public void 一次交辦含超長鍵與正常鍵_沒有孤立處理紀錄_正常那筆建立_結果指出超長那筆()
    {
        var w = new World();
        var overlong = KeyOfLength(IssueKeyLimit.Max + 1);
        w.AddHostDay("OK1", NormalKey);
        w.AddHostDay("LONG1", overlong);

        var outcome = w.Coordinator.Create(Request(("OK1", NormalKey), ("LONG1", overlong)));

        Assert.True(outcome.CreatedOrder);
        Assert.Equal(1, outcome.NewCases);
        var skipped = Assert.Single(outcome.SkippedOverlong);
        Assert.Equal("LONG1", skipped.HostName);
        Assert.Equal(IssueKeyLimit.Max + 1, skipped.KeyLength);
        Assert.Equal(IssueKeyLimit.Max, skipped.MaxLength);

        // 正常那筆：案件、逐日列、處理紀錄都在
        Assert.NotNull(w.Cases.GetOpen("OK1", NormalKey));
        Assert.Contains(w.HandlingLog.Logs, l => l.HostName == "OK1");
        // 超長那筆：沒有案件、沒有逐日列、沒有任何處理紀錄（孤立紀錄）
        Assert.Null(w.Cases.GetOpen("LONG1", overlong));
        Assert.Empty(w.IssueHandlings.GetForDay("LONG1", Day));
        Assert.DoesNotContain(w.HandlingLog.Logs, l => l.HostName == "LONG1");
        // 交辦單事件只計實際建立的成員
        Assert.Equal(1, w.Orders.ListEvents(outcome.WorkOrderId).Single(e => e.Action == WorkOrderEventActions.Created).MemberDelta);
    }

    [Fact]
    public void 所有成員的鍵都超長_不建單也不寫任何紀錄_全部回報未建立()
    {
        var w = new World();
        var overlong = KeyOfLength(IssueKeyLimit.Max + 50);
        w.AddHostDay("LONG1", overlong);
        w.AddHostDay("LONG2", overlong);

        var outcome = w.Coordinator.Create(Request(("LONG1", overlong), ("LONG2", overlong)));

        Assert.Equal(0, outcome.WorkOrderId);
        Assert.False(outcome.CreatedOrder);
        Assert.Equal(new[] { "LONG1", "LONG2" }, outcome.SkippedOverlong.Select(o => o.HostName).OrderBy(x => x));
        Assert.Empty(w.Orders.All);
        Assert.Empty(w.HandlingLog.Logs);
    }

    [Fact]
    public void 鍵長度剛好等於上限_視為正常_照常建立並寫處理紀錄()
    {
        var w = new World();
        var atLimit = KeyOfLength(IssueKeyLimit.Max);
        w.AddHostDay("EDGE", atLimit);

        var outcome = w.Coordinator.Create(Request(("EDGE", atLimit)));

        Assert.True(outcome.CreatedOrder);
        Assert.Equal(1, outcome.NewCases);
        Assert.Empty(outcome.SkippedOverlong);
        Assert.NotNull(w.Cases.GetOpen("EDGE", atLimit));
        Assert.Contains(w.HandlingLog.Logs, l => l.HostName == "EDGE");
    }

    [Fact]
    public void 全部鍵都正常_行為不變_沒有超長回報()
    {
        var w = new World();
        w.AddHostDay("A", NormalKey);
        w.AddHostDay("B", NormalKey);

        var outcome = w.Coordinator.Create(Request(("A", NormalKey), ("B", NormalKey)));

        Assert.True(outcome.CreatedOrder);
        Assert.Equal(2, outcome.NewCases);
        Assert.Empty(outcome.SkippedOverlong);
        Assert.Equal(2, w.Orders.ListEvents(outcome.WorkOrderId).Single(e => e.Action == WorkOrderEventActions.Created).MemberDelta);
        Assert.All(new[] { "A", "B" }, h => Assert.Contains(w.HandlingLog.Logs, l => l.HostName == h));
    }

    [Fact]
    public void 上限取自欄位設定_案件與逐日列兩表同為512()
    {
        Assert.Equal(512, IssueKeyLimit.Max);
    }
}

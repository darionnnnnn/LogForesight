using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

public sealed class IssueSignatureKeyComparerTests
{
    [Fact]
    public void 完整簽章只正規化Source_其餘欄位仍區分且Hash一致()
    {
        var comparer = IssueSignatureKeyComparer.Instance;
        var lower = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Error, "rule-a");
        var upper = IssueSignatureKey.For("System", "DISK", 153, EventLogEntryType.Error, "rule-a");
        var differentLog = IssueSignatureKey.For("Application", "DISK", 153, EventLogEntryType.Error, "rule-a");
        var differentEvent = IssueSignatureKey.For("System", "DISK", 154, EventLogEntryType.Error, "rule-a");
        var differentType = IssueSignatureKey.For("System", "DISK", 153, EventLogEntryType.Warning, "rule-a");
        var differentEventKey = IssueSignatureKey.For("System", "DISK", 153, EventLogEntryType.Error, "rule-b");

        Assert.True(comparer.Equals(lower, upper));
        Assert.Equal(comparer.GetHashCode(lower), comparer.GetHashCode(upper));
        Assert.Single(new HashSet<string>(comparer) { lower, upper });
        Assert.False(comparer.Equals(lower, differentLog));
        Assert.False(comparer.Equals(lower, differentEvent));
        Assert.False(comparer.Equals(lower, differentType));
        Assert.False(comparer.Equals(lower, differentEventKey));
        Assert.Equal("System|disk|153|1|rule-a", lower);
    }

    [Fact]
    public void 不合法legacy鍵保留Ordinal_不因Source大小寫合併()
    {
        var comparer = IssueSignatureKeyComparer.Instance;
        const string lower = "System|disk|not-an-event|1";
        const string upper = "System|DISK|not-an-event|1";

        Assert.False(comparer.Equals(lower, upper));
        Assert.Equal(comparer.GetHashCode(lower), comparer.GetHashCode(lower));
        Assert.Equal(2, new HashSet<string>(comparer) { lower, upper }.Count);
    }

    [Fact]
    public void AttachNewDay_legacy混合大小寫雙列取最新更新且不因ToDictionary撞鍵()
    {
        var hosts = new FakeHostStore();
        const string host = "SRV-K";
        var webHost = hosts.Upsert(new WebHost { HostName = host, Active = true });
        var cases = new FakeIssueCaseStore();
        var handlings = new FakeIssueHandlingStore();
        var coordinator = new IssueCaseCoordinator(
            cases, handlings, new FakeHandlingStore(), new FakeAnalysisRecordQuery(), hosts,
            new FakeIssueOwnerStore());

        var oldKey = IssueSignatureKey.For("System", "DISK", 153, EventLogEntryType.Error);
        var newKey = IssueSignatureKey.For("System", "disk", 153, EventLogEntryType.Error);
        var day = DateTime.Today;
        var oldUpdated = day.AddHours(1);
        var newUpdated = day.AddHours(2);
        cases.Save(new IssueCase
        {
            CaseId = "old-case",
            HostName = host,
            IssueKey = oldKey,
            IssueLabel = "old",
            Status = IssueHandlingStatuses.InProgress,
            HandlerId = 1,
            FirstLinkedDate = day.AddDays(-1),
            LastLinkedDate = day.AddDays(-1),
            CreatedAt = oldUpdated,
            UpdatedAt = oldUpdated
        });
        cases.Save(new IssueCase
        {
            CaseId = "new-case",
            HostName = host,
            IssueKey = newKey,
            IssueLabel = "new",
            Status = IssueHandlingStatuses.Observing,
            HandlerId = 2,
            FirstLinkedDate = day.AddDays(-1),
            LastLinkedDate = day.AddDays(-1),
            CreatedAt = newUpdated,
            UpdatedAt = newUpdated
        });

        var result = coordinator.AttachNewDay(
            host, day,
            new[]
            {
                new LogIssueSignature
                {
                    LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error
                },
                new LogIssueSignature
                {
                    LogName = "System", Source = "DISK", EventId = 153, EntryType = EventLogEntryType.Error
                }
            },
            day.AddHours(3));

        Assert.Equal(1, result.AttachedCount);
        var attached = Assert.Single(handlings.GetForDay(host, day));
        Assert.Equal(newKey, attached.IssueKey);
        Assert.Equal(IssueHandlingStatuses.Observing, attached.Status);
        Assert.Equal("new-case", attached.CaseId);
        Assert.Equal(2, cases.Get("new-case")!.HandlerId);
        Assert.Equal(webHost.HostId, hosts.FindByName(host)!.HostId);
    }
}

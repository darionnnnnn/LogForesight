using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 重新分析候選日挑選器（RerunDateFinder）單元測試。
/// </summary>
public sealed class RerunDateFinderTests
{
    private const string Host = "SRV-01";

    [Fact]
    public void Find_模式為None_回傳空清單()
    {
        var dates = new[] { DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-2) };
        var store = new FakeAnalysisRecordReader(dates);
        var handlingStore = new FakeIssueHandlingStore();

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 2, RerunMode.None);

        Assert.Empty(result);
        Assert.Equal(0, handlingStore.GetManyCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Find_LookbackDays小於等於0_回傳空清單(int lookbackDays)
    {
        var dates = new[] { DateTime.Today.AddDays(-1) };
        var store = new FakeAnalysisRecordReader(dates);
        var handlingStore = new FakeIssueHandlingStore();

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays, RerunMode.Unhandled);

        Assert.Empty(result);
        Assert.Equal(0, handlingStore.GetManyCallCount);
    }

    [Fact]
    public void Find_窗口內沒有紀錄的日子_不回傳()
    {
        var d1 = DateTime.Today.AddDays(-1);
        var d2 = DateTime.Today.AddDays(-2); // 無分析紀錄
        var d3 = DateTime.Today.AddDays(-3);

        var store = new FakeAnalysisRecordReader(new[] { d1, d3 });
        var handlingStore = new FakeIssueHandlingStore();

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 3, RerunMode.Unhandled);

        Assert.Equal(new[] { d3, d1 }, result);
    }

    [Fact]
    public void Find_Unhandled模式_有任何處理列即排除_完全無列才納入()
    {
        var d1 = DateTime.Today.AddDays(-1); // 無列
        var d2 = DateTime.Today.AddDays(-2); // in_progress
        var d3 = DateTime.Today.AddDays(-3); // open
        var d4 = DateTime.Today.AddDays(-4); // 無列
        var d5 = DateTime.Today.AddDays(-5); // resolved

        var store = new FakeAnalysisRecordReader(new[] { d1, d2, d3, d4, d5 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = d2, Status = IssueHandlingStatuses.InProgress, IssueKey = "k1" },
            new IssueHandling { HostName = Host, Date = d3, Status = IssueHandlingStatuses.Open, IssueKey = "k2" },
            new IssueHandling { HostName = Host, Date = d5, Status = IssueHandlingStatuses.Resolved, IssueKey = "k3" }
        });

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 5, RerunMode.Unhandled);

        Assert.Equal(new[] { d4, d1 }, result);
    }

    [Fact]
    public void Find_UnhandledAndAssigned模式_只有非結案列納入_有任一結案列排除()
    {
        var d1 = DateTime.Today.AddDays(-1); // 無列 -> 納入
        var d2 = DateTime.Today.AddDays(-2); // in_progress -> 納入
        var d3 = DateTime.Today.AddDays(-3); // observing -> 納入
        var d4 = DateTime.Today.AddDays(-4); // escalated -> 納入
        var d5 = DateTime.Today.AddDays(-5); // open -> 納入
        var d6 = DateTime.Today.AddDays(-6); // resolved -> 排除
        var d7 = DateTime.Today.AddDays(-7); // wont_fix -> 排除
        var d8 = DateTime.Today.AddDays(-8); // false_positive -> 排除
        var d9 = DateTime.Today.AddDays(-9); // known_noise -> 排除

        var store = new FakeAnalysisRecordReader(new[] { d1, d2, d3, d4, d5, d6, d7, d8, d9 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = d2, Status = IssueHandlingStatuses.InProgress, IssueKey = "k2" },
            new IssueHandling { HostName = Host, Date = d3, Status = IssueHandlingStatuses.Observing, IssueKey = "k3" },
            new IssueHandling { HostName = Host, Date = d4, Status = IssueHandlingStatuses.Escalated, IssueKey = "k4" },
            new IssueHandling { HostName = Host, Date = d5, Status = IssueHandlingStatuses.Open, IssueKey = "k5" },
            new IssueHandling { HostName = Host, Date = d6, Status = IssueHandlingStatuses.Resolved, IssueKey = "k6" },
            new IssueHandling { HostName = Host, Date = d7, Status = IssueHandlingStatuses.WontFix, IssueKey = "k7" },
            new IssueHandling { HostName = Host, Date = d8, Status = IssueHandlingStatuses.FalsePositive, IssueKey = "k8" },
            new IssueHandling { HostName = Host, Date = d9, Status = IssueHandlingStatuses.KnownNoise, IssueKey = "k9" }
        });

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 9, RerunMode.UnhandledAndAssigned);

        Assert.Equal(new[] { d5, d4, d3, d2, d1 }, result);
    }

    [Fact]
    public void Find_All模式_無列_非結案列_結案列全部納入()
    {
        var d1 = DateTime.Today.AddDays(-1); // 無列
        var d2 = DateTime.Today.AddDays(-2); // in_progress
        var d3 = DateTime.Today.AddDays(-3); // resolved

        var store = new FakeAnalysisRecordReader(new[] { d1, d2, d3 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = d2, Status = IssueHandlingStatuses.InProgress, IssueKey = "k2" },
            new IssueHandling { HostName = Host, Date = d3, Status = IssueHandlingStatuses.Resolved, IssueKey = "k3" }
        });

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 3, RerunMode.All);

        Assert.Equal(new[] { d3, d2, d1 }, result);
    }

    [Fact]
    public void Find_混合日_同日同時有resolved與in_progress_只有All納入_其餘模式排除()
    {
        var d1 = DateTime.Today.AddDays(-1);

        var store = new FakeAnalysisRecordReader(new[] { d1 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = d1, Status = IssueHandlingStatuses.Resolved, IssueKey = "k1" },
            new IssueHandling { HostName = Host, Date = d1, Status = IssueHandlingStatuses.InProgress, IssueKey = "k2" }
        });

        var unhandledResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.Unhandled);
        var unhandledAndAssignedResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.UnhandledAndAssigned);
        var allResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.All);

        Assert.Empty(unhandledResult);
        Assert.Empty(unhandledAndAssignedResult);
        Assert.Equal(new[] { d1 }, allResult);
    }

    [Fact]
    public void Find_回傳結果依日期升冪排序()
    {
        var d1 = DateTime.Today.AddDays(-1);
        var d2 = DateTime.Today.AddDays(-2);
        var d3 = DateTime.Today.AddDays(-3);

        var store = new FakeAnalysisRecordReader(new[] { d1, d2, d3 });
        var handlingStore = new FakeIssueHandlingStore();

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 3, RerunMode.Unhandled);

        Assert.Equal(new[] { d3, d2, d1 }, result);
    }

    [Fact]
    public void Find_批次查詢_GetMany只呼叫一次_GetForDay從未被呼叫()
    {
        var dates = Enumerable.Range(1, 5).Select(i => DateTime.Today.AddDays(-i)).ToArray();
        var store = new FakeAnalysisRecordReader(dates);
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = dates[0], Status = IssueHandlingStatuses.InProgress, IssueKey = "k1" }
        });

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 5, RerunMode.Unhandled);

        Assert.Equal(1, handlingStore.GetManyCallCount);
        Assert.Equal(0, handlingStore.GetForDayCallCount);
    }

    [Fact]
    public void Find_別台主機同日的處理列_不影響本主機判定()
    {
        var d1 = DateTime.Today.AddDays(-1);
        var otherHost = "SRV-OTHER";

        var store = new FakeAnalysisRecordReader(new[] { d1 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = otherHost, Date = d1, Status = IssueHandlingStatuses.Resolved, IssueKey = "k1" }
        });

        var unhandledResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.Unhandled);
        var unhandledAndAssignedResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.UnhandledAndAssigned);

        Assert.Equal(new[] { d1 }, unhandledResult);
        Assert.Equal(new[] { d1 }, unhandledAndAssignedResult);
    }

    [Fact]
    public void Find_主機名比對不分大小寫()
    {
        var d1 = DateTime.Today.AddDays(-1);

        var store = new FakeAnalysisRecordReader(new[] { d1 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = "srv-01", Date = d1, Status = IssueHandlingStatuses.Resolved, IssueKey = "k1" }
        });

        var unhandledResult = RerunDateFinder.Find(store, handlingStore, "SRV-01", lookbackDays: 1, RerunMode.Unhandled);
        var unhandledAndAssignedResult = RerunDateFinder.Find(store, handlingStore, "SRV-01", lookbackDays: 1, RerunMode.UnhandledAndAssigned);
        var allResult = RerunDateFinder.Find(store, handlingStore, "SRV-01", lookbackDays: 1, RerunMode.All);

        Assert.Empty(unhandledResult);
        Assert.Empty(unhandledAndAssignedResult);
        Assert.Equal(new[] { d1 }, allResult);
    }

    [Fact]
    public void Find_IssueHandling日期帶時間_仍能正確以Date比對()
    {
        var d1 = DateTime.Today.AddDays(-1);
        var dateWithTime = d1.AddHours(14).AddMinutes(35).AddSeconds(12);

        var store = new FakeAnalysisRecordReader(new[] { d1 });
        var handlingStore = new FakeIssueHandlingStore(new[]
        {
            new IssueHandling { HostName = Host, Date = dateWithTime, Status = IssueHandlingStatuses.Resolved, IssueKey = "k1" }
        });

        var unhandledResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.Unhandled);
        var unhandledAndAssignedResult = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.UnhandledAndAssigned);

        Assert.Empty(unhandledResult);
        Assert.Empty(unhandledAndAssignedResult);
    }

    [Fact]
    public void Find_今天不包含在窗口內()
    {
        var today = DateTime.Today;
        var yesterday = DateTime.Today.AddDays(-1);

        var store = new FakeAnalysisRecordReader(new[] { today, yesterday });
        var handlingStore = new FakeIssueHandlingStore();

        var result = RerunDateFinder.Find(store, handlingStore, Host, lookbackDays: 1, RerunMode.Unhandled);

        Assert.Single(result);
        Assert.Equal(yesterday, result[0]);
    }

    private sealed class FakeAnalysisRecordReader : IAnalysisRecordReader
    {
        private readonly HashSet<DateTime> _existingDates;

        public FakeAnalysisRecordReader(IEnumerable<DateTime>? dates = null)
        {
            _existingDates = dates != null
                ? new HashSet<DateTime>(dates.Select(d => d.Date))
                : new HashSet<DateTime>();
        }

        public bool HasRecord(DateTime date) => _existingDates.Contains(date.Date);
        public bool HasAnyRecord() => _existingDates.Count > 0;
        public List<DailyAnalysisRecord> ReadRecent(DateTime anchorDate, int days) => new();
        public DateTime? LastWeeklyCheckupDate() => null;
    }

    private sealed class FakeIssueHandlingStore : IIssueHandlingStore
    {
        private readonly List<IssueHandling> _items = new();

        public int GetManyCallCount { get; private set; }
        public int GetForDayCallCount { get; private set; }

        public FakeIssueHandlingStore(IEnumerable<IssueHandling>? items = null)
        {
            if (items != null)
            {
                _items.AddRange(items);
            }
        }

        public List<IssueHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
        {
            GetManyCallCount++;
            var hostSet = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
            var f = from.Date;
            var t = to.Date;
            return _items
                .Where(h => hostSet.Contains(h.HostName) && h.Date.Date >= f && h.Date.Date <= t)
                .ToList();
        }

        public List<IssueHandling> GetForDay(string hostName, DateTime date)
        {
            GetForDayCallCount++;
            return _items
                .Where(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) && h.Date.Date == date.Date)
                .ToList();
        }

        public List<IssueHandling> GetByCase(string caseId) => new();
        public void Save(IssueHandling handling) => _items.Add(handling);
        public void SaveMany(IEnumerable<IssueHandling> handlings) => _items.AddRange(handlings);
        public void Clear(string hostName, DateTime date, string issueKey) =>
            _items.RemoveAll(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) && h.Date.Date == date.Date && h.IssueKey == issueKey);
    }
}

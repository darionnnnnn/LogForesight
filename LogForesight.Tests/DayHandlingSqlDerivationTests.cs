using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 SQL 端推導風險日處理狀態與記憶體路徑完全等價。
/// 這是唯一的同步保證，不得隨意修改或刪除。
/// </summary>
public class DayHandlingSqlDerivationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly EfAnalysisRecordStore _records;
    private readonly FakeHostStore _hosts = new();

    public DayHandlingSqlDerivationTests()
    {
        _records = new EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfIssueAggregateQuery Query() => new(_fx.NewContext, _hosts);

    private static LogIssueSignature Issue(
        string source, int eventId, int count = 1,
        IssueSeverity severity = IssueSeverity.Low, bool elevates = false,
        string logName = "System", EventLogEntryType entryType = EventLogEntryType.Warning, string eventKey = "") => new()
        {
            LogName = logName, Source = source, EventId = eventId, EntryType = entryType,
            Category = IssueCategory.Other, Severity = severity, ElevatesDayRisk = elevates, Count = count,
            EventKey = eventKey
        };

    private void Add(long hostId, string host, DateTime date, string riskLevel, params LogIssueSignature[] issues) =>
        _records.Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = date, RiskLevel = riskLevel,
            TopIssues = issues.ToList()
        });

    private void AddRecordHandling(string host, DateTime date, string status, long? handlerId = null)
    {
        using var ctx = _fx.NewContext();
        var row = ctx.RecordHandlings.FirstOrDefault(x => x.HostNameKey == host.ToUpperInvariant() && x.RecordDate == date);
        if (row != null)
        {
            row.Status = status;
            row.HandlerId = handlerId;
            row.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            ctx.RecordHandlings.Add(new RecordHandlingRow
            {
                HostName = host,
                HostNameKey = host.ToUpperInvariant(),
                RecordDate = date,
                Status = status,
                HandlerId = handlerId,
                UpdatedAt = DateTime.UtcNow
            });
        }
        ctx.SaveChanges();
    }

    private void AddIssueHandling(string host, DateTime date, LogIssueSignature issue, string status)
    {
        using var ctx = _fx.NewContext();
        var key = IssueSignatureKey.For(issue);
        var row = ctx.IssueHandlings.FirstOrDefault(x => x.HostNameKey == host.ToUpperInvariant() && x.RecordDate == date && x.IssueKey == key);
        if (row != null)
        {
            row.Status = status;
            row.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            ctx.IssueHandlings.Add(new IssueHandlingRow
            {
                HostName = host,
                HostNameKey = host.ToUpperInvariant(),
                RecordDate = date,
                IssueKey = key,
                Status = status,
                UpdatedAt = DateTime.UtcNow
            });
        }
        ctx.SaveChanges();
    }

    private void AddIssueCase(string host, LogIssueSignature issue, DateTime? closedAt = null, long? handlerId = null)
    {
        using var ctx = _fx.NewContext();
        ctx.IssueCases.Add(new IssueCaseRow
        {
            CaseId = Guid.NewGuid().ToString(),
            HostName = host,
            HostNameKey = host.ToUpperInvariant(),
            IssueKey = IssueSignatureKey.For(issue),
            ClosedAt = closedAt,
            HandlerId = handlerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        ctx.SaveChanges();
    }

    [Fact]
    public void 等價性測試_隨機資料比對()
    {
        var rand = new Random(1337);
        var hosts = Enumerable.Range(1, 30).Select(i => (Id: (long)i, Name: $"Host{i}")).ToList();
        var d0 = new DateTime(2026, 8, 1);
        var unhandledSeverities = new HashSet<IssueSeverity> { IssueSeverity.High, IssueSeverity.Medium };

        using (var ctx = _fx.NewContext())
        {
            foreach (var h in hosts)
            {
                for (int d = 0; d < 20; d++)
                {
                    var date = d0.AddDays(d);
                    var issueCount = rand.Next(0, 5);
                    var issues = new List<LogIssueSignature>();

                    for (int i = 0; i < issueCount; i++)
                    {
                        var severity = (IssueSeverity)rand.Next(1, 4);
                        var issue = Issue($"src{rand.Next(1, 3)}", rand.Next(100, 110), 1, severity);
                        issues.Add(issue);
                    }

                    var riskLevel = rand.Next(0, 10) < 2 ? RiskLevels.Low : (rand.Next(0, 2) == 0 ? RiskLevels.Medium : RiskLevels.High);
                    Add(h.Id, h.Name, date, riskLevel, issues.ToArray());

                    // 隨機日層級處理狀態
                    if (rand.Next(0, 3) == 0)
                    {
                        var statuses = new[] { HandlingStatuses.Open, HandlingStatuses.InProgress, HandlingStatuses.Resolved, HandlingStatuses.WontFix };
                        AddRecordHandling(h.Name, date, statuses[rand.Next(statuses.Length)], rand.Next(0, 2) == 0 ? null : 99L);
                    }

                    // 隨機問題層級處理狀態
                    foreach (var issue in issues)
                    {
                        if (rand.Next(0, 3) == 0)
                        {
                            var ihStatuses = IssueHandlingStatuses.All;
                            AddIssueHandling(h.Name, date, issue, ihStatuses[rand.Next(ihStatuses.Length)]);
                        }

                        // 隨機案件
                        if (rand.Next(0, 10) == 0)
                        {
                            AddIssueCase(h.Name, issue, rand.Next(0, 2) == 0 ? null : date.AddDays(1), rand.Next(0, 2) == 0 ? null : 88L);
                        }
                    }
                }
            }
        }

        var sqlResult = Query().DeriveDayHandling(d0, d0.AddDays(19), null, unhandledSeverities, new List<long>());
        var sqlDict = sqlResult.ToDictionary(x => (x.HostId, x.Date));

        using (var ctx = _fx.NewContext())
        {
            var memoryCheckedCount = 0;
            foreach (var h in hosts)
            {
                for (int d = 0; d < 20; d++)
                {
                    var date = d0.AddDays(d);

                    var dr = ctx.DailyRecords.FirstOrDefault(x => x.HostId == h.Id && x.RecordDate == date);
                    if (dr == null || (dr.RiskLevel != RiskLevels.High && dr.RiskLevel != RiskLevels.Medium))
                    {
                        Assert.False(sqlDict.ContainsKey((h.Id, date)));
                        continue;
                    }

                    var issues = ctx.TopIssues.Where(x => x.RecordId == dr.RecordId).ToList().Select(x => new LogIssueSignature
                    {
                        LogName = x.LogName,
                        Source = x.SourceName,
                        EventId = x.EventId,
                        EntryType = (EventLogEntryType)x.EntryType,
                        EventKey = x.EventKey,
                        Severity = (IssueSeverity)x.SeverityRank
                    }).ToList();

                    var dayLevelStatus = ctx.RecordHandlings.Where(rh => rh.HostNameKey == h.Name.ToUpperInvariant() && rh.RecordDate == date).Select(rh => rh.Status).FirstOrDefault();
                    var dayLevelHandlerId = ctx.RecordHandlings.Where(rh => rh.HostNameKey == h.Name.ToUpperInvariant() && rh.RecordDate == date).Select(rh => rh.HandlerId).FirstOrDefault();

                    var issueHandlings = ctx.IssueHandlings.Where(ih => ih.HostNameKey == h.Name.ToUpperInvariant() && ih.RecordDate == date).ToList()
                        .Select(ih => new IssueHandling { IssueKey = ih.IssueKey, Status = ih.Status }).ToList();

                    var memoryDerivation = DayHandlingDerivation.Derive(issues, issueHandlings, dayLevelStatus, unhandledSeverities);

                    var anyCaseHandler = issues.Any(issue =>
                        ctx.IssueCases.Any(ic => ic.HostNameKey == h.Name.ToUpperInvariant() && ic.IssueKey == IssueSignatureKey.For(issue) && ic.ClosedAt == null && ic.HandlerId != null)
                    );

                    var memoryHasHandler = dayLevelHandlerId != null || anyCaseHandler;

                    Assert.True(sqlDict.TryGetValue((h.Id, date), out var sqlItem));
                    Assert.Equal(memoryDerivation.DayStatus, sqlItem.DayStatus);
                    Assert.Equal(memoryHasHandler, sqlItem.HasHandler);
                    memoryCheckedCount++;
                }
            }
            Assert.True(memoryCheckedCount > 0);
        }
    }

    [Fact]
    public void 邊界_沒有任何問題標記且日層級也沒有_回傳Open()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("src", 100));

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        Assert.Equal(HandlingStatuses.Open, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_沒有問題標記但日層級狀態是wont_fix_回傳wont_fix()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("src", 100));
        AddRecordHandling("A", d0, HandlingStatuses.WontFix);

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        Assert.Equal(HandlingStatuses.WontFix, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_全部counted問題都結案_回傳Resolved()
    {
        var d0 = new DateTime(2026, 8, 1);
        var issue1 = Issue("src1", 100, severity: IssueSeverity.High);
        var issue2 = Issue("src2", 200, severity: IssueSeverity.High);
        Add(1, "A", d0, RiskLevels.High, issue1, issue2);

        AddIssueHandling("A", d0, issue1, IssueHandlingStatuses.Resolved);
        AddIssueHandling("A", d0, issue2, IssueHandlingStatuses.WontFix);

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        Assert.Equal(HandlingStatuses.Resolved, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_只有一個問題標成Observing其餘未標記_回傳InProgress()
    {
        var d0 = new DateTime(2026, 8, 1);
        var issue1 = Issue("src1", 100, severity: IssueSeverity.High);
        var issue2 = Issue("src2", 200, severity: IssueSeverity.High);
        Add(1, "A", d0, RiskLevels.High, issue1, issue2);

        AddIssueHandling("A", d0, issue1, IssueHandlingStatuses.Observing);

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        Assert.Equal(HandlingStatuses.InProgress, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_嚴重度不在unhandledSeverities且從未被明確標記的問題_不計入total()
    {
        var d0 = new DateTime(2026, 8, 1);
        var issueLow = Issue("src1", 100, severity: IssueSeverity.Low);
        Add(1, "A", d0, RiskLevels.High, issueLow);

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        // 不計入 total -> total = 0, fallback to day level which is Open
        Assert.Equal(HandlingStatuses.Open, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_嚴重度不在unhandledSeverities但曾被明確標記的問題_計入total()
    {
        var d0 = new DateTime(2026, 8, 1);
        var issueLow = Issue("src1", 100, severity: IssueSeverity.Low);
        Add(1, "A", d0, RiskLevels.High, issueLow);

        // 曾明確標記 open
        AddIssueHandling("A", d0, issueLow, IssueHandlingStatuses.Open);

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        // 計入 total -> total = 1, closed = 0, anyInProgress = false -> Open (but from derivation logic, fallback is Open anyway).
        // Let's test with resolved to see if it becomes resolved!

        AddIssueHandling("A", d0, issueLow, IssueHandlingStatuses.Resolved);
        sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Single(sqlResult);
        Assert.Equal(HandlingStatuses.Resolved, sqlResult[0].DayStatus);
    }

    [Fact]
    public void 邊界_低風險日不出現在結果中()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.Low, Issue("src", 100));

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, Array.Empty<long>());
        Assert.Empty(sqlResult);
    }

    [Fact]
    public void 邊界_excludedHostIds內的主機不出現在結果中()
    {
        var d0 = new DateTime(2026, 8, 1);
        Add(1, "A", d0, RiskLevels.High, Issue("src", 100));
        Add(2, "B", d0, RiskLevels.High, Issue("src", 200));

        var sqlResult = Query().DeriveDayHandling(d0, d0, null, new HashSet<IssueSeverity> { IssueSeverity.High }, new[] { 1L });
        Assert.Single(sqlResult);
        Assert.Equal(2, sqlResult[0].HostId);
    }
}

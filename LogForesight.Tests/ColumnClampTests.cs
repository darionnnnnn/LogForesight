using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>寫入前依欄位上限截斷的單一出口（<see cref="ColumnClamp"/>）與各 store 寫入點。</summary>
public sealed class ColumnClampTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Date = new(2026, 10, 1);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Clamp_null_未超長_超長()
    {
        Assert.Null(ColumnClamp.Clamp(null, 5));
        Assert.Equal("abc", ColumnClamp.Clamp("abc", 5));
        Assert.Equal("abcde", ColumnClamp.Clamp("abcde", 5));
        Assert.Equal("abcde", ColumnClamp.Clamp("abcdef", 5));
    }

    [Fact]
    public void For_與LfDbContext模型所有string上限對帳()
    {
        using var ctx = _fx.NewContext();
        var checkedCount = 0;
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType != typeof(string) || prop.GetMaxLength() is not { } max) continue;
                var method = typeof(ColumnClamp).GetMethod(nameof(ColumnClamp.For))!.MakeGenericMethod(entity.ClrType);
                var actual = (int?)method.Invoke(null, new object[] { prop.Name });
                Assert.True(actual == max, $"{entity.ClrType.Name}.{prop.Name} 期望 {max}，實際 {actual}");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 50, $"對帳欄位數過少：{checkedCount}");
    }

    [Fact]
    public void ClampFor_無上限欄位原樣_有上限欄位截斷()
    {
        var max = ColumnClamp.For<TopIssueRow>(nameof(TopIssueRow.SourceName))!.Value;
        var longText = new string('x', max + 20);
        Assert.Equal(max, ColumnClamp.ClampFor<TopIssueRow>(longText, nameof(TopIssueRow.SourceName))!.Length);
        Assert.Null(ColumnClamp.For<TopIssueRow>("NoSuchProperty"));
        Assert.Equal(longText, ColumnClamp.ClampFor<TopIssueRow>(longText, "NoSuchProperty"));
    }

    [Fact]
    public void AnalysisRecordStore_TopIssue超長Source落地依上限截斷()
    {
        var max = ColumnClamp.For<TopIssueRow>(nameof(TopIssueRow.SourceName))!.Value;
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        store.Append(new DailyAnalysisRecord
        {
            Date = Date,
            RiskLevel = "高",
            Headline = "h",
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = new string('s', 300), EventId = 1, Count = 1, Severity = IssueSeverity.Critical }
            }
        });

        using var ctx = _fx.NewContext();
        Assert.Equal(max, Assert.Single(ctx.TopIssues.ToList()).SourceName.Length);
        Assert.All(ctx.IssueFirstSeen.ToList(), f => Assert.True(f.SourceName.Length <= ColumnClamp.For<IssueFirstSeenRow>(nameof(IssueFirstSeenRow.SourceName))!.Value));
    }

    [Fact]
    public void RiskyEventStore_ReplaceDay超長RuleId落地依上限截斷()
    {
        var max = ColumnClamp.For<RiskyEventRow>(nameof(RiskyEventRow.RuleId))!.Value;
        var store = new EfRiskyEventStore(_fx.NewContext);
        store.ReplaceDay(1, Date, new List<RiskyEvent>
        {
            new()
            {
                HostId = 1, Date = Date, LogName = "System", Source = "src", EventId = 1,
                EntryType = System.Diagnostics.EventLogEntryType.Error, EventTime = Date, Message = "m",
                RuleId = new string('r', max + 10), CreatedAt = Date
            }
        });

        using var ctx = _fx.NewContext();
        Assert.Equal(max, Assert.Single(ctx.RiskyEvents.ToList()).RuleId!.Length);
    }

    [Fact]
    public void PermissionChangeStore_超長InitiatorAccount落地依上限截斷()
    {
        var max = ColumnClamp.For<PermissionChangeRow>(nameof(PermissionChangeRow.InitiatorAccount))!.Value;
        var store = new PermissionChangeStore(_fx.NewContext);
        store.AppendChanges(new[]
        {
            new PermissionChangeRecord
            {
                HostName = "h1", DetectedAt = Date, Target = "t", ChangeType = "add",
                InitiatorAccount = new string('a', max + 10), AlertText = "x"
            }
        });

        using var ctx = _fx.NewContext();
        Assert.Equal(max, Assert.Single(ctx.PermissionChanges.ToList()).InitiatorAccount!.Length);
    }

    [Fact]
    public void PrtgStore_UpsertManualMap_Note600字截為512()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 10, HostId = 1, Note = new string('n', 600), CreatedBy = "u" });

        using var ctx = _fx.NewContext();
        Assert.Equal(512, Assert.Single(ctx.PrtgManualMaps.ToList()).Note!.Length);
    }
}

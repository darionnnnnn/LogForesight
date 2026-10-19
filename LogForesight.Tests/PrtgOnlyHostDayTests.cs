using LogForesight;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 54 輪 C2：當日沒有日誌分析、只因 PRTG finding 而成立的主機日（<see cref="AnalysisLogSource.Prtg"/>）。
/// 建立只在「至少一筆未抑制 finding、且同主機日沒有任何紀錄」時發生；日誌分析晚到時原地更新同一列（record id 不變），
/// PRTG finding 併回；NetIQ 涵蓋類判定（缺日、重跑候選、執行監看）不把它算成已分析。
/// </summary>
public sealed class PrtgOnlyHostDayTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Day = DateTime.Today.AddDays(-2);
    private static readonly KnownIssueRule DownAvailability =
        KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-down-availability");

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfAnalysisRecordStore Store() => new(_fx.NewContext, "sqlite-in-memory");

    private static LogIssueSignature Down(long sensor, bool suppressed = false)
    {
        var signature = PrtgFindingMapper.ToSignature(new PrtgFinding(10, sensor, "down", "Ping down", 90, DownAvailability), Day);
        signature.Suppressed = suppressed;
        return signature;
    }

    private static DailyAnalysisRecord Netiq(long hostId, string riskLevel = RiskLevels.Low) => new()
    {
        HostId = hostId, Host = $"host-{hostId}", Date = Day, LogSource = AnalysisLogSource.Netiq,
        LatestNetiqAttemptStatus = "success", ErrorCount = 3, RiskLevel = riskLevel, RiskBasis = "netiq-rule",
        TopIssues = new List<LogIssueSignature>
        {
            new() { LogName = "System", Source = "disk", EventId = 153, EventKey = "", Count = 3, Severity = IssueSeverity.Medium }
        }
    };

    private List<DailyRecordRow> Rows(long hostId)
    {
        using var ctx = _fx.NewContext();
        return ctx.DailyRecords.AsNoTracking().Where(r => r.HostId == hostId).ToList();
    }

    [Fact]
    public void 只有PRTG的主機日建立為獨立來源_風險依PRTG規則_欄位與JSON來源一致()
    {
        var store = Store();

        Assert.True(store.AppendPrtgOnlyIfAbsent(7, "host-7", Day, [Down(2001)], aiConfigured: true));

        var row = Assert.Single(Rows(7));
        Assert.Equal((short)AnalysisLogSource.Prtg, row.LogSource);
        Assert.Equal(DailyRecordBackfiller.CurrentVersion, row.ExtractVersion);
        var record = Assert.Single(store.ReadRecent(Day, 1));
        Assert.Equal(AnalysisLogSource.Prtg, record.LogSource);
        Assert.Equal(RiskLevels.High, record.RiskLevel);          // availability down 是重大規則
        Assert.StartsWith("prtg:", record.RiskBasis);
        Assert.Equal(0, record.ErrorCount);
        Assert.True(record.AiPending);
        Assert.Single(record.TopIssues, PrtgFindingMapper.IsPrtg);
    }

    [Fact]
    public void 只剩被抑制的finding不建立主機日()
    {
        var store = Store();

        Assert.False(store.AppendPrtgOnlyIfAbsent(8, "host-8", Day, [Down(2001, suppressed: true)], aiConfigured: true));
        Assert.Empty(Rows(8));
    }

    [Fact]
    public void 同主機日已有日誌分析紀錄時不另建_重複建立五次只有一列()
    {
        var store = Store();
        store.Append(Netiq(9));
        Assert.False(store.AppendPrtgOnlyIfAbsent(9, "host-9", Day, [Down(2001)], aiConfigured: false));

        for (var i = 0; i < 5; i++)
            store.AppendPrtgOnlyIfAbsent(11, "host-11", Day, [Down(2001)], aiConfigured: false);
        Assert.Single(Rows(9));
        Assert.Single(Rows(11));
    }

    [Fact]
    public void NetIQ晚到原地更新同一列_來源改為NetIQ_PRTG_finding保留且風險只升不降()
    {
        var store = Store();
        Assert.True(store.AppendPrtgOnlyIfAbsent(12, "host-12", Day, [Down(2001)], aiConfigured: false));
        var prtgOnlyId = Assert.Single(Rows(12)).RecordId;

        var late = Netiq(12);
        store.Append(late);

        var row = Assert.Single(Rows(12));
        Assert.Equal(prtgOnlyId, row.RecordId);
        Assert.Equal(prtgOnlyId, late.RecordId);
        Assert.Equal((short)AnalysisLogSource.Netiq, row.LogSource);
        var merged = Assert.Single(store.ReadRecent(Day, 1));
        Assert.Equal(AnalysisLogSource.Netiq, merged.LogSource);
        Assert.Equal(3, merged.ErrorCount);
        Assert.Contains(merged.TopIssues, i => i.Source == "disk" && i.EventId == 153);
        Assert.Single(merged.TopIssues, PrtgFindingMapper.IsPrtg);
        Assert.Equal(RiskLevels.High, merged.RiskLevel);          // NetIQ 低、PRTG 高 → 取高
        Assert.Equal(RiskLevels.Low, merged.PrtgBaselineRiskLevel); // NetIQ 下限保留，PRTG 判定被取代時可還原
        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.TopIssues.Count(t => t.RecordId == prtgOnlyId));

        // 同一份 NetIQ 結果再寫一次走一般路徑（已非 PRTG 獨立主機日），不會再併一次 PRTG
        Assert.False(store.AppendPrtgOnlyIfAbsent(12, "host-12", Day, [Down(2001)], aiConfigured: false));
    }

    [Fact]
    public void 缺日與重跑判定_PRTG獨立主機日仍是待日誌分析的缺日_不是重跑候選()
    {
        var store = Store();
        store.AppendPrtgOnlyIfAbsent(13, "host-13", Day, [Down(2001)], aiConfigured: false);

        Assert.False(store.HasRecord(Day));
        Assert.False(store.HasAnyRecord());
        Assert.Contains(Day.Date, MissingDateFinder.Find(store, 3));
        Assert.Empty(store.ListHostDates(Day, Day, logAnalysisOnly: true));
        Assert.Single(store.ListHostDates(Day, Day)); // 郵件預填等其他用途照常包含

        store.Append(Netiq(13));
        Assert.True(store.HasRecord(Day));
        Assert.DoesNotContain(Day.Date, MissingDateFinder.Find(store, 3));
        Assert.Single(store.ListHostDates(Day, Day, logAnalysisOnly: true));
    }

    [Fact]
    public void 詳情已清的列仍保留來源_涵蓋類統計靠抽出欄排除()
    {
        var store = Store();
        store.AppendPrtgOnlyIfAbsent(14, "host-14", Day, [Down(2001)], aiConfigured: false);
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single(r => r.HostId == 14);
            row.DetailPruned = true;
            row.ContentJson = string.Empty;
            ctx.SaveChanges();
        }

        Assert.Equal(AnalysisLogSource.Prtg, Assert.Single(store.ReadRecent(Day, 1)).LogSource);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlserver")]
    public void 排除PRTG獨立主機日的條件兩個後端都在SQL端篩選(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);
        var prtgOnly = (short)AnalysisLogSource.Prtg;

        var sql = ctx.DailyRecords.Where(r => r.RecordDate == Day.Date && r.LogSource != prtgOnly).ToQueryString();

        Assert.Contains("log_source", sql);
        Assert.Contains("<>", sql);
    }
}

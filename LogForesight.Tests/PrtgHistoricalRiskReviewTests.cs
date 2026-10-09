using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgHistoricalRiskReviewTests
{
    private static DailyAnalysisRecord Legacy(bool independent = false, bool ai = false) => new()
    {
        HostId = 101, Host = "TEST", Date = DateTime.Today.AddDays(-3), LogSource = AnalysisLogSource.Netiq,
        RiskLevel = RiskLevels.High, RiskBasis = CorrelationPatternIds.PrtgStorageCorroborated,
        Headline = "舊雙重確認", Summary = "舊高風險敘事", AiAnalyzed = ai,
        TopIssues = [new() { Source = "disk", EventId = 153, LogName = "System", Severity = IssueSeverity.High, ElevatesDayRisk = independent }],
        CorrelationAlerts = ["【儲存故障雙重確認】舊推論"],
        CorrelationAlertRefs = [new() { Text = "【儲存故障雙重確認】舊推論", PatternId = CorrelationPatternIds.PrtgStorageCorroborated }]
    };

    [Theory]
    [InlineData(false, false, "中", "revised")]
    [InlineData(true, false, "高", "revised")]
    [InlineData(false, true, "高", "pending")]
    public void 弱佐證修訂只移除可證明的配對加權(bool independent, bool ai, string risk, string status)
    {
        var record = Legacy(independent, ai);
        Assert.True(PrtgHistoricalRiskReview.Apply(record));
        Assert.Equal(risk, record.RiskLevel);
        Assert.Equal(status, record.RiskReview!.Status);
        Assert.Equal("高", record.RiskReview.OriginalRiskLevel);
        Assert.Equal("舊高風險敘事", record.RiskReview.OriginalSummary);
        Assert.DoesNotContain("雙重確認", record.Headline);
        Assert.StartsWith("【儲存異常同日訊號】", Assert.Single(record.CorrelationAlerts));
        Assert.False(record.AiPending);
    }

    [Fact]
    public void 缺独立證據或其他關聯_保留等級並標待重評()
    {
        var record = Legacy();
        record.TopIssues.Clear();
        Assert.True(PrtgHistoricalRiskReview.Apply(record));
        Assert.Equal("pending", record.RiskReview!.Status);
        Assert.Equal("高", record.RiskLevel);
    }

    [Fact]
    public void 新增的精確來源對齊仍不算風險關聯_不阻止舊弱配對重算()
    {
        var record = Legacy();
        const string alignmentText = "【儲存異常來源對齊】已核對的來源參照";
        record.CorrelationAlerts.Add(alignmentText);
        record.CorrelationAlertRefs.Add(new CorrelationAlertRef
        {
            Text = alignmentText,
            PatternId = CorrelationPatternIds.PrtgStorageEvidenceAligned
        });

        Assert.False(PrtgHistoricalRiskReview.IsWeakPattern(CorrelationPatternIds.PrtgStorageEvidenceAligned));
        Assert.True(PrtgHistoricalRiskReview.Apply(record));

        Assert.Equal("revised", record.RiskReview!.Status);
        Assert.Equal("中", record.RiskLevel);
        Assert.Contains(alignmentText, record.CorrelationAlerts);
        Assert.Contains(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
    }

    [Fact]
    public void 歷史投影原子保留原文_SQL與JSON一致_重啟重試不再修訂()
    {
        using var fixture = new EfSqliteFixture();
        var original = JsonSerializer.Serialize(Legacy());
        using (var ctx = fixture.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 101, HostName = "TEST", RecordDate = DateTime.Today.AddDays(-3),
                RiskLevel = "高", ContentJson = original, CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }
        var projection = new PrtgRiskProjectionStore(fixture.NewContext);
        Assert.Equal(0, projection.RunBatch([]));
        Assert.Equal(1, projection.RunBatch([101]));
        Assert.Equal(0, new PrtgRiskProjectionStore(fixture.NewContext).RunBatch([101]));
        using var read = fixture.NewContext();
        var row = Assert.Single(read.DailyRecords);
        Assert.Equal(original, row.OriginalRiskContentJson);
        Assert.Equal("中", row.RiskLevel);
        Assert.Equal(row.RiskLevel, JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!.RiskLevel);
        Assert.Equal("revised", row.RiskReviewStatus);
        Assert.Equal(new PrtgRiskReviewCounts(0, 0, 1), projection.Counts([101]));
        Assert.Equal(new PrtgRiskReviewCounts(0, 0, 0), projection.Counts([102]));
    }

    [Fact]
    public void 資料毀損不會被空紀錄降級()
    {
        using var fixture = new EfSqliteFixture();
        using (var ctx = fixture.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow { HostId = 101, RecordDate = DateTime.Today, RiskLevel = "高", ContentJson = "broken" });
            ctx.SaveChanges();
        }
        Assert.Equal(1, new PrtgRiskProjectionStore(fixture.NewContext).RunBatch());
        using var read = fixture.NewContext();
        var row = Assert.Single(read.DailyRecords);
        Assert.Equal("高", row.RiskLevel);
        Assert.Equal("broken", row.ContentJson);
        Assert.Equal("unavailable", row.RiskReviewStatus);
    }
}

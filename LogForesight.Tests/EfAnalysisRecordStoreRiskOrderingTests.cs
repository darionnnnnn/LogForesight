using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// QueryPage 的 SQL 端排序（EfAnalysisRecordStore.cs 的內嵌 CASE WHEN，因 EF 表達式樹限制
/// 無法呼叫 <see cref="RiskLevels.Rank"/> 本身）必須與 Core.RiskLevels 的權重定義一致——
/// 這是唯一允許存在的複本，靠這個測試把它釘在正確的值上，改 Core 版時這裡會紅燈。
/// </summary>
public class EfAnalysisRecordStoreRiskOrderingTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date, string risk) => new()
    {
        HostId = hostId, Host = host, Date = date, RiskLevel = risk
    };

    [Fact]
    public void QueryPage_SQL端排序權重與RiskLevels_Rank一致()
    {
        // 全部帶正常 HostId（非 0），走 SQL 全下推分頁路徑（QueryPage 的 CASE WHEN 分支）
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        store.Append(Rec(1, "LOW-HOST", new DateTime(2026, 7, 1), RiskLevels.Low));
        store.Append(Rec(2, "HIGH-HOST", new DateTime(2026, 7, 1), RiskLevels.High));
        store.Append(Rec(3, "MEDIUM-HOST", new DateTime(2026, 7, 1), RiskLevels.Medium));
        store.Append(Rec(4, "UNKNOWN-HOST", new DateTime(2026, 7, 1), "未知"));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10);

        Assert.Equal(
            new[] { "HIGH-HOST", "MEDIUM-HOST", "LOW-HOST", "UNKNOWN-HOST" },
            page.Items.Select(r => r.Host).ToArray());
    }
}

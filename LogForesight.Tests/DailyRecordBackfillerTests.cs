using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 抽出欄回填器（<see cref="DailyRecordBackfiller"/>）的版本機制。
///
/// 重點是回饋三十五輪批次C 的存量校正：<c>ai_pending</c> 欄位在
/// <c>AttachAiResult</c> 只更新 ContentJson 的年代沒有被同步過，
/// 「AI 已完成」的舊列欄位因此永遠停在 1。批次C 起 AI 排程改以欄位為事實來源，
/// 這些髒列若不校正，會被整批當成待補而重跑一遍。
/// </summary>
public class DailyRecordBackfillerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>
    /// 存量校正：欄位說「待補」但 ContentJson 說「已完成」的舊列，回填後欄位歸 0。
    /// 這正是升級前既有資料的形狀（欄位漂移）。
    /// </summary>
    [Fact]
    public void 回填_欄位與內容不一致的舊列_以ContentJson為準校正AiPending()
    {
        var date = new DateTime(2026, 8, 1);
        using (var ctx = _fx.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1,
                HostName = "A",
                RecordDate = date,
                RiskLevel = "高",
                // 內容說 AI 已完成、不待補
                ContentJson = System.Text.Json.JsonSerializer.Serialize(new DailyAnalysisRecord
                {
                    HostId = 1, Host = "A", Date = date, RiskLevel = "高",
                    AiAnalyzed = true, AiPending = false
                }),
                // 欄位卻停在待補（漂移）
                AiPending = true,
                AiAnalyzed = false,
                ExtractVersion = 1,   // 舊版本，才會落進回填候選
                CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }

        new DailyRecordBackfiller(_fx.NewContext).Run(CancellationToken.None);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.DailyRecords.Single();
            Assert.False(row.AiPending);
            Assert.True(row.AiAnalyzed);
            Assert.Equal(DailyRecordBackfiller.CurrentVersion, row.ExtractVersion);
        }
    }

    /// <summary>
    /// 真的待補的列不能被誤清：內容與欄位都說待補，回填後仍是待補。
    /// （校正的判準是「以 ContentJson 為準」，不是「一律清成 0」。）
    /// </summary>
    [Fact]
    public void 回填_內容確實待補的列_維持AiPending為真()
    {
        var date = new DateTime(2026, 8, 2);
        using (var ctx = _fx.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 2, HostName = "B", RecordDate = date, RiskLevel = "高",
                ContentJson = System.Text.Json.JsonSerializer.Serialize(new DailyAnalysisRecord
                {
                    HostId = 2, Host = "B", Date = date, RiskLevel = "高",
                    AiAnalyzed = false, AiPending = true
                }),
                AiPending = true, AiAnalyzed = false, ExtractVersion = 1, CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }

        new DailyRecordBackfiller(_fx.NewContext).Run(CancellationToken.None);

        using var check = _fx.NewContext();
        Assert.True(check.DailyRecords.Single().AiPending);
    }

    /// <summary>
    /// 新寫入的列必須帶當前版本，否則會永遠落在待回填集合裡被反覆處理
    /// （Append 與回填器共用同一個常數，就是為了防這件事）。
    /// </summary>
    [Fact]
    public void 新寫入的列_帶當前版本且不落入待回填集合()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        store.Append(new DailyAnalysisRecord
        {
            HostId = 3, Host = "C", Date = new DateTime(2026, 8, 3), RiskLevel = "低"
        });

        Assert.Equal(0, new DailyRecordBackfiller(_fx.NewContext).CountPending());
    }

    /// <summary>回填冪等：跑第二次沒有候選列，不會出錯也不會改動任何值。</summary>
    [Fact]
    public void 回填_重複執行_冪等()
    {
        var date = new DateTime(2026, 8, 4);
        using (var ctx = _fx.NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 4, HostName = "D", RecordDate = date, RiskLevel = "中",
                ContentJson = System.Text.Json.JsonSerializer.Serialize(new DailyAnalysisRecord
                {
                    HostId = 4, Host = "D", Date = date, RiskLevel = "中", AiPending = false
                }),
                AiPending = true, ExtractVersion = 0, CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }

        var backfiller = new DailyRecordBackfiller(_fx.NewContext);
        backfiller.Run(CancellationToken.None);
        backfiller.Run(CancellationToken.None);

        Assert.Equal(0, backfiller.CountPending());
        using var check = _fx.NewContext();
        Assert.False(check.DailyRecords.Single().AiPending);
    }
}

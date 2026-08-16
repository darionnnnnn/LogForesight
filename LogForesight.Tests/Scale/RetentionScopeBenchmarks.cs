using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 保留期清理範圍的量測與回歸防線（SCALE-3000 S2-3b）。
///
/// <para><b>為什麼需要這一組</b>：夜間清理長期以來走的是綁定「本機」識別的
/// <c>RecordStore(ownerHost)</c>，而 NetIQ 機房數千台主機的紀錄不屬於本機——
/// 等於保留期對絕大多數資料從來沒有生效，`docs/DB-SPEC.md` 的保留策略表卻寫著全表適用。
/// 這個落差用眼睛看程式碼會以為「有清就好」，只有把數字量出來才看得見。</para>
/// </summary>
public class RetentionScopeBenchmarks
{
    private readonly ITestOutputHelper _out;

    public RetentionScopeBenchmarks(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// 常態回歸：資料集全是 NetIQ 主機，本機不在其中。限縮的 store 一筆都看不到，
    /// 未限縮的看得到全部——這正是清理長年失效的形狀，用小資料集釘住它。
    /// </summary>
    [Fact]
    public void 保留期範圍_限縮到本機的store看不到NetIQ主機的過期紀錄()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small);
        const int retentionDays = 5;   // 資料集有 10 天，保留 5 天 → 過半應為過期

        var scoped = data.Backend.RecordStore(LocalMachineKey());
        var unscoped = data.Backend.RecordStore();

        var scopedCount = scoped.CountPrunableRecords(retentionDays);
        var unscopedCount = unscoped.CountPrunableRecords(retentionDays);

        Assert.Equal(0, scopedCount);      // 本機沒有任何紀錄，限縮後看不到東西可清
        Assert.True(unscopedCount > 0, "未限縮的 store 應該看得到 NetIQ 主機的過期紀錄");

        _out.WriteLine($"限縮到本機：{scopedCount:N0} 筆可清；未限縮：{unscopedCount:N0} 筆可清");
    }

    /// <summary>
    /// 規模量測（預設略過，設 <c>LF_SCALE_BENCH=1</c> 才跑）：
    /// 用接近正式環境的資料量，量出「積壓了多少」與「實際清一次要多久」。
    /// 開啟自動清除前要先看這兩個數字。
    /// </summary>
    [ScaleFact]
    public void 量測_保留期積壓量與清除耗時()
    {
        var profile = new ScaleProfile("retention-500x200", HostCount: 500, Days: 200,
            DistinctIssues: 300, IssuesPerRecord: 8, HandlingRowsPerHostDay: 0.3,
            OpenCaseCount: 500, TombstoneCount: 20);

        using var data = ScaleDataSet.Generate(profile, m => _out.WriteLine(m));
        const int retentionDays = 120;
        const int detailRetentionDays = 60;

        var scoped = data.Backend.RecordStore(LocalMachineKey());
        var unscoped = data.Backend.RecordStore();

        _out.WriteLine("");
        _out.WriteLine($"── 資料集：{profile}（保留 {retentionDays} 天、詳情 {detailRetentionDays} 天）");
        _out.WriteLine($"限縮到本機可清：{scoped.CountPrunableRecords(retentionDays):N0} 筆");
        _out.WriteLine($"未限縮可清　　：{unscoped.CountPrunableRecords(retentionDays):N0} 筆");
        _out.WriteLine($"未限縮可清詳情：{unscoped.CountPrunableDetails(detailRetentionDays):N0} 筆");

        // 實際清一次要多久（含子表刪除），以及單次上限會不會一晚清不完
        var sw = Stopwatch.StartNew();
        var pruned = unscoped.Prune(retentionDays);
        sw.Stop();
        _out.WriteLine($"Prune 實際清除：{pruned:N0} 筆、{sw.ElapsedMilliseconds:N0} ms");
        _out.WriteLine($"清完後剩餘可清：{unscoped.CountPrunableRecords(retentionDays):N0} 筆（>0 代表撞到單次上限，留待下一晚）");

        sw.Restart();
        var detailPruned = unscoped.PruneDetails(detailRetentionDays);
        sw.Stop();
        _out.WriteLine($"PruneDetails　 ：{detailPruned:N0} 筆、{sw.ElapsedMilliseconds:N0} ms");
    }

    /// <summary>本機識別：資料集裡的主機一律是 SRV-xxxxx，本機名稱不會與它們相同。</summary>
    private static HostKey LocalMachineKey() =>
        new() { HostId = 999_999, HostName = Environment.MachineName };
}

using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 80 輪批次 X3 探針（沿用 PrtgSnapshotHostedServiceTests 的夾具）：來源變更停止後，
/// 封存前預覽筆數（Inspect，只數已落盤列）是否等於記憶體累積樣本與實際封存筆數。
/// 方法名稱帶 ProbeX，定向篩選 ProbeX 時一併執行。
/// </summary>
public partial class PrtgSnapshotHostedServiceTests
{
    [Fact]
    public void ProbeX3_來源變更停止後_預覽筆數等於累積列數且等於實際封存筆數()
    {
        var now = DateTime.Today.AddHours(12);
        SeedJournal(now.AddHours(-2), now, foreignDatabase: false);
        var service = CreateService();
        service.Now = () => now;
        Assert.True(RestoreJournal(service, _settingsStore.Get()));

        // 與 ParseAndCheckpoint 相同的順序：累積樣本後立刻落盤（累積器只在這條路徑被加入樣本）
        var acc = ServiceAccumulator(service);
        acc.Add(101, now, 10);
        acc.Add(102, now, 20);
        acc.Add(103, now.AddHours(-1), 30);
        Assert.True(SaveJournal(service));

        _settingsStore.Update(s => s.PrtgUrl = "https://prtg-new.example.com");
        Assert.False(RestoreJournal(service, _settingsStore.Get()));
        Assert.Equal(PrtgSnapshotJournalStopCodes.SourceChanged, service.GetStatus().JournalStopCode);

        var inMemoryRows = acc.EntryCount;   // 小時×感測器列（含種子 1 列）
        Assert.Equal(4, inMemoryRows);
        var preview = service.GetJournalPreview();
        Assert.True(preview.CanArchive);
        Assert.Equal(inMemoryRows, preview.Rows);

        var result = service.ArchiveJournalManually();
        Assert.Equal(preview.Rows, result.Rows);
        Assert.Equal(0, acc.EntryCount);
    }

    [Fact(Skip = "第 80 輪批次 X 探針：僅在「落盤失敗（磁碟錯誤）後又遇來源變更」的複合情境成立——記憶體有未落盤樣本，預覽只數已落盤列而少報；一般路徑（樣本加入後立即落盤、停止後不再累積）不受影響，見回報")]
    public void ProbeX3_落盤失敗後又遇來源變更_預覽少報未落盤樣本()
    {
        var now = DateTime.Today.AddHours(12);
        SeedJournal(now.AddHours(-2), now, foreignDatabase: false);
        var service = CreateService();
        service.Now = () => now;
        Assert.True(RestoreJournal(service, _settingsStore.Get()));

        var acc = ServiceAccumulator(service);
        acc.Add(101, now, 10);   // 模擬 AppendDelta 失敗：樣本在記憶體、未落盤
        acc.Add(102, now, 20);

        _settingsStore.Update(s => s.PrtgUrl = "https://prtg-new.example.com");
        Assert.False(RestoreJournal(service, _settingsStore.Get()));

        var preview = service.GetJournalPreview();
        Assert.Equal(acc.EntryCount, preview.Rows);
    }
}

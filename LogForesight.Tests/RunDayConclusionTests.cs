using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>執行總表每個資料日一句結論（F21-2）：五種資料日各一句，邊界兩側各一例。</summary>
public class RunDayConclusionTests
{
    private static RunDaySummaryDto Day(Action<RunDaySummaryDto>? tweak = null)
    {
        var s = new RunDaySummaryDto { Date = "2026-11-20", TotalHosts = 10, SuccessCount = 10 };
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void 全部成功()
    {
        Assert.Equal("全部成功", RunDayConclusion.Build(Day()));
        // 有警告仍是成功，但註明台數
        Assert.Equal("全部成功，其中 2 台有警告", RunDayConclusion.Build(Day(s => s.WarningCount = 2)));
    }

    [Fact]
    public void 部分失敗_點名失敗主機且可補跑()
    {
        var text = RunDayConclusion.Build(Day(s =>
        {
            s.SuccessCount = 8; s.FailedCount = 2; s.FailedHostNames = new List<string> { "HOST-A", "HOST-B" };
            s.RerunHostCount = 2;
        }));
        Assert.Equal("2 台失敗：HOST-A、HOST-B，可補跑", text);
    }

    [Fact]
    public void 失敗主機超過三台只點名前三台()
    {
        var text = RunDayConclusion.Build(Day(s =>
        {
            s.FailedCount = 5; s.FailedHostNames = new List<string> { "A", "B", "C", "D", "E" }; s.RerunHostCount = 5;
        }));
        Assert.Equal("5 台失敗：A、B、C 等 5 台，可補跑", text);
        // 恰好三台不加「等」
        Assert.Equal("3 台失敗：A、B、C，可補跑", RunDayConclusion.Build(Day(s =>
        {
            s.FailedCount = 3; s.FailedHostNames = new List<string> { "A", "B", "C" }; s.RerunHostCount = 3;
        })));
    }

    [Fact]
    public void 未執行_過去的日子可補跑_今天那列是尚未處理且不補跑()
    {
        Assert.Equal("56 台未執行，可補跑", RunDayConclusion.Build(Day(s => { s.NotRunCount = 56; s.RerunHostCount = 56; })));
        Assert.Equal("56 台尚未處理", RunDayConclusion.Build(Day(s => { s.IsToday = true; s.NotRunCount = 56; })));
    }

    [Fact]
    public void 異常中斷()
    {
        Assert.Equal("1 台異常中斷：H1，可補跑", RunDayConclusion.Build(Day(s =>
        {
            s.StuckCount = 1; s.FailedHostNames = new List<string> { "H1" }; s.RerunHostCount = 1;
        })));
        // 今天那列最後一趟異常中斷、主機尚未轉成異常中斷
        Assert.Equal("最後一趟異常中斷", RunDayConclusion.Build(Day(s => { s.IsToday = true; s.PendingSummary = "最後一趟異常中斷"; })));
    }

    [Fact]
    public void 空資料待複查_不是漏跑_附何時再查()
    {
        Assert.Equal("3 台回空資料，待複查", RunDayConclusion.Build(Day(s => s.RecheckCount = 3)));
        Assert.Equal("3 台回空資料，待複查（約 14:30 複查）", RunDayConclusion.Build(Day(s => { s.RecheckCount = 3; s.RecheckNote = "約 14:30 複查"; })));
        // 與失敗並存：兩句用分號接
        Assert.Equal("1 台失敗：A，可補跑；2 台回空資料，待複查", RunDayConclusion.Build(Day(s =>
        {
            s.FailedCount = 1; s.FailedHostNames = new List<string> { "A" }; s.RerunHostCount = 1; s.RecheckCount = 2;
        })));
    }

    [Fact]
    public void 沒有主機_取數進行中_已停止()
    {
        Assert.Equal("這天沒有需要處理的主機", RunDayConclusion.Build(Day(s => { s.TotalHosts = 0; s.SuccessCount = 0; })));
        Assert.Equal("取數進行中，尚未處理完", RunDayConclusion.Build(Day(s => { s.IsToday = true; s.InProgress = true; s.NotRunCount = 4; })));
        Assert.Equal("全部成功；2 台已停止，下次執行自動回補", RunDayConclusion.Build(Day(s => s.StoppedCount = 2)));
    }

    [Fact]
    public void 可補跑旗標跟著台數()
    {
        Assert.False(Day().CanRerun);
        Assert.True(Day(s => s.RerunHostCount = 1).CanRerun);
    }
}

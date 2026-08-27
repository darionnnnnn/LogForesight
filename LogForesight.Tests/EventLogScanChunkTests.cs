using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 本機回補分塊掃描（回饋三十四輪 A1）：整個回補區間一次全載會讓記憶體爆掉，
/// 改成依日期切塊、逐塊取數逐塊釋放。這裡驗證切塊本身的邊界正確——
/// 區塊必須連續、不重疊、由舊到新，且完整涵蓋原區間。
/// </summary>
public class EventLogScanChunkTests
{
    [Fact]
    public void 區間長度整除區塊天數時每塊等長且連續()
    {
        var start = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 15);   // 14 天

        var chunks = EventLogService.SplitDateRange(start, end, 7);

        Assert.Equal(2, chunks.Count);
        Assert.Equal((start, new DateTime(2026, 8, 8)), chunks[0]);
        Assert.Equal((new DateTime(2026, 8, 8), end), chunks[1]);
    }

    [Fact]
    public void 區間長度不整除時最後一塊較短且不超出區間終點()
    {
        var start = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 11);   // 10 天

        var chunks = EventLogService.SplitDateRange(start, end, 7);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new DateTime(2026, 8, 8), chunks[1].Start);
        Assert.Equal(end, chunks[1].EndExclusive);
        Assert.Equal(3, (chunks[1].EndExclusive - chunks[1].Start).Days);
    }

    [Fact]
    public void 只有一天的區間切出單一區塊()
    {
        var start = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 2);

        var chunks = EventLogService.SplitDateRange(start, end, 7);

        Assert.Single(chunks);
        Assert.Equal((start, end), chunks[0]);
    }

    [Fact]
    public void 區塊由舊到新且完整涵蓋整個區間不重疊()
    {
        var start = new DateTime(2026, 5, 1);
        var end = new DateTime(2026, 8, 29);   // 120 天，首次執行的回補長度

        var chunks = EventLogService.SplitDateRange(start, end, EventLogService.DefaultScanChunkDays);

        Assert.Equal(start, chunks[0].Start);
        Assert.Equal(end, chunks[^1].EndExclusive);
        for (var i = 1; i < chunks.Count; i++)
        {
            // 前一塊的結束就是下一塊的開始：不重疊也不留空隙，任一天恰好落在一塊裡
            Assert.Equal(chunks[i - 1].EndExclusive, chunks[i].Start);
            Assert.True(chunks[i].Start > chunks[i - 1].Start);
        }
    }

    [Fact]
    public void 起點不早於終點時回傳空清單()
    {
        var day = new DateTime(2026, 8, 1);

        Assert.Empty(EventLogService.SplitDateRange(day, day, 7));
        Assert.Empty(EventLogService.SplitDateRange(day.AddDays(1), day, 7));
    }

    [Fact]
    public void 區塊天數必須大於零()
    {
        var start = new DateTime(2026, 8, 1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => EventLogService.SplitDateRange(start, start.AddDays(7), 0));
    }
}

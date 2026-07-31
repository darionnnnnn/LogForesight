using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 排程時間窗純函數（docs/WEB-SCHEDULER-PLAN.md §1.4.3）：格式驗證、重疊偵測（含跨午夜）、
/// 命中判斷、下一次觸發時刻、漏跑補償語意。
/// </summary>
public class ScheduleCalculatorTests
{
    private static ScheduleWindow W(string start, string end) => new() { Start = start, End = end };

    // ── TryParseMinutes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("01:00", 60)]
    [InlineData("23:59", 1439)]
    public void TryParseMinutes_合法格式解析正確(string input, int expected)
    {
        Assert.True(ScheduleCalculator.TryParseMinutes(input, out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("1:00")]
    [InlineData("01:60")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryParseMinutes_不合法格式回false(string input)
    {
        Assert.False(ScheduleCalculator.TryParseMinutes(input, out _));
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_空清單擋下()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow>());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("至少"));
    }

    [Fact]
    public void Validate_超過上限擋下()
    {
        var windows = Enumerable.Range(0, ScheduleCalculator.MaxWindows + 1)
            .Select(i => W($"{i:D2}:00", $"{i:D2}:30")).ToList();

        var result = ScheduleCalculator.Validate(windows);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("最多"));
    }

    [Fact]
    public void Validate_開始結束相同擋下()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("01:00", "01:00") });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不可相同"));
    }

    [Fact]
    public void Validate_格式不合法逐一列出()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("25:00", "07:00") });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("開始時間"));
    }

    [Fact]
    public void Validate_單一窗口合法通過()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("01:00", "07:00") });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_不重疊的多窗口通過()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("01:00", "07:00"), W("12:00", "13:00") });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_一般窗口重疊擋下()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("01:00", "07:00"), W("06:00", "09:00") });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("第 1 組") && e.Contains("第 2 組") && e.Contains("重疊"));
    }

    [Fact]
    public void Validate_跨午夜窗口與凌晨窗口重疊擋下()
    {
        // 22:00→06:00 正規化為 [22:00,24:00)+[00:00,06:00)，與 05:00→08:00 的 [05:00,08:00) 重疊在 05:00~06:00
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("22:00", "06:00"), W("05:00", "08:00") });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("重疊"));
    }

    [Fact]
    public void Validate_跨午夜窗口彼此不重疊時通過()
    {
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("22:00", "02:00"), W("03:00", "05:00") });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_同一組重疊本身不誤判()
    {
        // 跨午夜窗口正規化後拆成兩段，兩段同屬第 1 組不該互相判定重疊
        var result = ScheduleCalculator.Validate(new List<ScheduleWindow> { W("22:00", "06:00") });

        Assert.True(result.IsValid);
    }

    // ── IsWithinWindow ───────────────────────────────────────────────────────

    [Fact]
    public void IsWithinWindow_一般窗口內為true()
    {
        Assert.True(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 3, 0, 0), W("01:00", "07:00")));
    }

    [Fact]
    public void IsWithinWindow_一般窗口外為false()
    {
        Assert.False(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 8, 0, 0), W("01:00", "07:00")));
    }

    [Fact]
    public void IsWithinWindow_跨午夜窗口在深夜為true()
    {
        Assert.True(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 23, 0, 0), W("22:00", "06:00")));
    }

    [Fact]
    public void IsWithinWindow_跨午夜窗口在凌晨為true()
    {
        Assert.True(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 2, 0, 0), W("22:00", "06:00")));
    }

    [Fact]
    public void IsWithinWindow_跨午夜窗口白天為false()
    {
        Assert.False(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 12, 0, 0), W("22:00", "06:00")));
    }

    [Fact]
    public void IsWithinWindow_End邊界不含在內()
    {
        Assert.False(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 7, 0, 0), W("01:00", "07:00")));
    }

    [Fact]
    public void IsWithinWindow_Start邊界含在內()
    {
        Assert.True(ScheduleCalculator.IsWithinWindow(new DateTime(2026, 7, 31, 1, 0, 0), W("01:00", "07:00")));
    }

    // ── CurrentWindowInstanceStart ───────────────────────────────────────────

    [Fact]
    public void CurrentWindowInstanceStart_一般窗口回今天的Start()
    {
        var instance = ScheduleCalculator.CurrentWindowInstanceStart(new DateTime(2026, 7, 31, 3, 0, 0), W("01:00", "07:00"));

        Assert.Equal(new DateTime(2026, 7, 31, 1, 0, 0), instance);
    }

    [Fact]
    public void CurrentWindowInstanceStart_跨午夜凌晨回昨晚的Start()
    {
        // now=07-31 02:00，落在 22:00→06:00 窗口內——這個實例是「07-30 22:00 開始的那次」，不是今天新的一次
        var instance = ScheduleCalculator.CurrentWindowInstanceStart(new DateTime(2026, 7, 31, 2, 0, 0), W("22:00", "06:00"));

        Assert.Equal(new DateTime(2026, 7, 30, 22, 0, 0), instance);
    }

    [Fact]
    public void CurrentWindowInstanceStart_不在窗口內回null()
    {
        var instance = ScheduleCalculator.CurrentWindowInstanceStart(new DateTime(2026, 7, 31, 12, 0, 0), W("01:00", "07:00"));

        Assert.Null(instance);
    }

    // ── NextTriggerTime ──────────────────────────────────────────────────────

    [Fact]
    public void NextTriggerTime_今天尚未到的Start回今天()
    {
        var next = ScheduleCalculator.NextTriggerTime(new DateTime(2026, 7, 31, 0, 0, 0), new[] { W("01:00", "07:00") });

        Assert.Equal(new DateTime(2026, 7, 31, 1, 0, 0), next);
    }

    [Fact]
    public void NextTriggerTime_今天已過的Start回明天()
    {
        var next = ScheduleCalculator.NextTriggerTime(new DateTime(2026, 7, 31, 8, 0, 0), new[] { W("01:00", "07:00") });

        Assert.Equal(new DateTime(2026, 8, 1, 1, 0, 0), next);
    }

    [Fact]
    public void NextTriggerTime_多窗口取最近的一個()
    {
        var next = ScheduleCalculator.NextTriggerTime(
            new DateTime(2026, 7, 31, 2, 0, 0), new[] { W("01:00", "05:00"), W("12:00", "13:00") });

        Assert.Equal(new DateTime(2026, 7, 31, 12, 0, 0), next);
    }

    [Fact]
    public void NextTriggerTime_空清單回null()
    {
        Assert.Null(ScheduleCalculator.NextTriggerTime(DateTime.Now, new List<ScheduleWindow>()));
    }

    [Fact]
    public void NextTriggerTime_now剛好等於Start算已過_回明天()
    {
        var now = new DateTime(2026, 7, 31, 1, 0, 0);
        var next = ScheduleCalculator.NextTriggerTime(now, new[] { W("01:00", "07:00") });

        Assert.Equal(new DateTime(2026, 8, 1, 1, 0, 0), next);
    }

    // ── ShouldTriggerNow（常態輪詢＋漏跑補償共用同一語意）──────────────────

    [Fact]
    public void ShouldTriggerNow_在窗口內且今天未觸發過_應觸發()
    {
        var now = new DateTime(2026, 7, 31, 2, 0, 0);
        var should = ScheduleCalculator.ShouldTriggerNow(now, new[] { W("01:00", "07:00") }, Array.Empty<DateTime>());

        Assert.True(should);
    }

    [Fact]
    public void ShouldTriggerNow_同一窗口實例內已觸發過_不再觸發()
    {
        var now = new DateTime(2026, 7, 31, 3, 0, 0);
        var alreadyTriggeredAt = new DateTime(2026, 7, 31, 1, 5, 0); // 同一窗口實例（01:00 開始）內已觸發過一次

        var should = ScheduleCalculator.ShouldTriggerNow(now, new[] { W("01:00", "07:00") }, new[] { alreadyTriggeredAt });

        Assert.False(should);
    }

    [Fact]
    public void ShouldTriggerNow_不在任何窗口內_不觸發()
    {
        var now = new DateTime(2026, 7, 31, 12, 0, 0);
        var should = ScheduleCalculator.ShouldTriggerNow(now, new[] { W("01:00", "07:00") }, Array.Empty<DateTime>());

        Assert.False(should);
    }

    [Fact]
    public void ShouldTriggerNow_昨天的觸發紀錄不算今天已觸發_跨午夜窗口漏跑補償()
    {
        // 服務啟動時位於 22:00→06:00 窗口的凌晨段（now=07-31 02:00），這個實例起於 07-30 22:00；
        // 若上次記錄的觸發是 07-29 的（更早窗口實例），今天這次仍應觸發（漏跑補償）
        var now = new DateTime(2026, 7, 31, 2, 0, 0);
        var oldTrigger = new DateTime(2026, 7, 29, 22, 30, 0);

        var should = ScheduleCalculator.ShouldTriggerNow(now, new[] { W("22:00", "06:00") }, new[] { oldTrigger });

        Assert.True(should);
    }

    [Fact]
    public void ShouldTriggerNow_跨午夜窗口本次實例已觸發過_不再觸發()
    {
        var now = new DateTime(2026, 7, 31, 2, 0, 0);
        var triggeredThisInstance = new DateTime(2026, 7, 30, 23, 0, 0); // 落在本次實例（07-30 22:00 起）內

        var should = ScheduleCalculator.ShouldTriggerNow(now, new[] { W("22:00", "06:00") }, new[] { triggeredThisInstance });

        Assert.False(should);
    }

    [Fact]
    public void ShouldTriggerNow_多窗口只要有一個未觸發就觸發()
    {
        var now = new DateTime(2026, 7, 31, 2, 0, 0);
        var windows = new[] { W("01:00", "03:00"), W("12:00", "13:00") };
        // 只有第一個窗口目前在範圍內；它尚未觸發過
        var should = ScheduleCalculator.ShouldTriggerNow(now, windows, Array.Empty<DateTime>());

        Assert.True(should);
    }
}

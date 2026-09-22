using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class ScheduleCatchUpTests
{
    public static IEnumerable<object?[]> FindMissedWindowData()
    {
        // 1. 現在 23:00（窗口內）→ null。
        yield return new object?[]
        {
            new DateTime(2026, 9, 18, 23, 0, 0),
            new[] { new DateTime(2026, 9, 17, 22, 1, 0) },
            null,
            null
        };

        // 2. 現在隔天 08:00，觸發紀錄只有前天 22:01 → 回傳（昨天 22:00, 今天 06:00）。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 8, 0, 0),
            new[] { new DateTime(2026, 9, 17, 22, 1, 0) },
            new DateTime(2026, 9, 18, 22, 0, 0),
            new DateTime(2026, 9, 19, 6, 0, 0)
        };

        // 3. 現在隔天 11:00（超過 4 小時）→ null。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 11, 0, 0),
            new[] { new DateTime(2026, 9, 17, 22, 1, 0) },
            null,
            null
        };

        // 4. 現在隔天 08:00，觸發紀錄有昨天 22:05 → null（跑過了）。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 8, 0, 0),
            new[] { new DateTime(2026, 9, 18, 22, 5, 0) },
            null,
            null
        };

        // 5. 現在隔天 08:00，觸發紀錄有今天 07:30（補跑本身）→ null。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 8, 0, 0),
            new[] { new DateTime(2026, 9, 19, 7, 30, 0) },
            null,
            null
        };

        // 6. 現在隔天 08:00，沒有任何觸發紀錄 → null（全新安裝）。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 8, 0, 0),
            Array.Empty<DateTime>(),
            null,
            null
        };

        // 7. 時鐘回撥：現在隔天 07:00、觸發紀錄有今天 07:30（未來時間）→ null。
        yield return new object?[]
        {
            new DateTime(2026, 9, 19, 7, 0, 0),
            new[] { new DateTime(2026, 9, 19, 7, 30, 0) },
            null,
            null
        };
    }

    [Theory]
    [MemberData(nameof(FindMissedWindowData))]
    public void FindMissedWindow_涵蓋各種邊界情境(
        DateTime now,
        DateTime[] triggerTimes,
        DateTime? expectedStart,
        DateTime? expectedEnd)
    {
        var windows = new[] { new ScheduleWindow { Start = "22:00", End = "06:00" } };
        var actual = ScheduleCalculator.FindMissedWindow(now, windows, triggerTimes, TimeSpan.FromHours(4));

        if (expectedStart == null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.NotNull(actual);
            Assert.Equal(expectedStart.Value, actual.Value.Start);
            Assert.Equal(expectedEnd!.Value, actual.Value.End);
        }
    }

    public static IEnumerable<object[]> LastEndedWindowInstanceData()
    {
        // 跨午夜
        yield return new object[]
        {
            new DateTime(2026, 9, 19, 8, 0, 0),
            new[] { new ScheduleWindow { Start = "22:00", End = "06:00" } },
            new DateTime(2026, 9, 18, 22, 0, 0),
            new DateTime(2026, 9, 19, 6, 0, 0)
        };

        // 當日窗口 01:00→03:00 且現在 04:00
        yield return new object[]
        {
            new DateTime(2026, 9, 19, 4, 0, 0),
            new[] { new ScheduleWindow { Start = "01:00", End = "03:00" } },
            new DateTime(2026, 9, 19, 1, 0, 0),
            new DateTime(2026, 9, 19, 3, 0, 0)
        };
    }

    [Theory]
    [MemberData(nameof(LastEndedWindowInstanceData))]
    public void LastEndedWindowInstance_跨午夜與當日窗口(
        DateTime now,
        ScheduleWindow[] windows,
        DateTime expectedStart,
        DateTime expectedEnd)
    {
        var actual = ScheduleCalculator.LastEndedWindowInstance(now, windows);

        Assert.NotNull(actual);
        Assert.Equal(expectedStart, actual.Value.Start);
        Assert.Equal(expectedEnd, actual.Value.End);
    }
}

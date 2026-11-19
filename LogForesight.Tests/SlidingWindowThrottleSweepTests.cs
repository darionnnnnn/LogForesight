using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 79 輪批次 G9：鍵數滿載時不得每次呼叫都全表掃描</summary>
public class SlidingWindowThrottleSweepTests
{
    [Fact]
    public void 鍵數滿載下連續呼叫一千次_掃描次數不超過經過的視窗數()
    {
        var window = TimeSpan.FromMinutes(1);
        var throttle = new SlidingWindowThrottle<string>(maxCalls: 1, window, maxKeys: 100);
        var now = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 100; i++) Assert.True(throttle.TryAcquire("k" + i, now));
        var before = throttle.SweepCount;

        // 同一個視窗內灌一千個新鍵：全部被拒，且只允許至多一次掃描
        for (var i = 0; i < 1000; i++) Assert.False(throttle.TryAcquire("flood" + i, now.AddMilliseconds(i)));
        Assert.True(throttle.SweepCount - before <= 1, $"掃描 {throttle.SweepCount - before} 次");

        // 過了一個視窗：舊鍵過期被清掉，新鍵可以進來
        Assert.True(throttle.TryAcquire("after-window", now.Add(window).AddSeconds(1)));
    }
}

/// <summary>第 79 輪批次 G10：灌入大量帳號不能把已封鎖的帳號擠出登入節流</summary>
public class LoginThrottleEvictionTests
{
    [Fact]
    public void 灌滿上限數量的亂數帳號後_原本封鎖的帳號仍在封鎖中()
    {
        var throttle = new LogForesight.Web.Auth.LoginThrottle();
        var now = new DateTime(2026, 11, 19, 8, 0, 0);
        for (var i = 0; i < 20; i++) throttle.RecordFailure("victim", null, now);
        Assert.True(throttle.IsBlocked("victim", null, now, out _));

        for (var i = 0; i < LogForesight.Web.Auth.LoginThrottle.MaxKeys + 10; i++)
            throttle.RecordFailure("flood-" + i, null, now.AddMilliseconds(i));

        Assert.True(throttle.IsBlocked("victim", null, now.AddSeconds(30), out _));
    }
}

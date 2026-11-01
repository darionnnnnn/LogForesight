using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>案件逐日同步節拍判定：閒置退避 1,2,4,6,6 拍、版本變動立即喚醒、有資料每拍執行、自己的 Bump 不自我喚醒。</summary>
public class CaseDaySyncScheduleTests
{
    /// <summary>模擬 n 拍，回傳實際執行的拍序號；<paramref name="run"/> 回傳（是否處理到資料、完成後版本）</summary>
    private static List<int> Simulate(CaseDaySyncSchedule s, int beats, Func<int, long> version, Func<int, (bool Processed, long VersionAfter)> run)
    {
        var ran = new List<int>();
        for (var beat = 1; beat <= beats; beat++)
        {
            if (!s.ShouldRun(version(beat))) continue;
            ran.Add(beat);
            var (processed, after) = run(beat);
            s.OnRunCompleted(processed, after);
        }
        return ran;
    }

    private static List<int> Diffs(List<int> ran) =>
        ran.Zip(ran.Skip(1), (a, b) => b - a).ToList();

    [Fact]
    public void Idle_BacksOffByOneTwoFourSixSix()
    {
        var s = new CaseDaySyncSchedule();
        var ran = Simulate(s, 40, _ => 0, _ => (false, 0));
        Assert.Equal(new[] { 1, 2, 4, 6, 6, 6 }, Diffs(ran).Take(6));
    }

    [Fact]
    public void ProcessedData_RunsEveryBeat()
    {
        var s = new CaseDaySyncSchedule();
        var ran = Simulate(s, 10, _ => 0, _ => (true, 0));
        Assert.Equal(Enumerable.Range(1, 10), ran);
    }

    [Fact]
    public void VersionChange_WakesNextBeat_AndResetsBackoff()
    {
        var s = new CaseDaySyncSchedule();
        long ver = 0;
        // 先閒置到退避拉長
        var ran = Simulate(s, 12, b => { if (b == 10) ver = 5; return ver; }, _ => (false, ver));
        // 拍 1,2,4,8 之後原本下一次應在 14；第 10 拍版本變動 → 第 10 拍立即執行
        Assert.Contains(10, ran);
        Assert.DoesNotContain(9, ran);
        // 喚醒後等待重設為 1：下一次在第 11 拍
        Assert.Contains(11, ran);
        Assert.DoesNotContain(12, ran);
    }

    [Fact]
    public void VersionBumpedByRunItself_DoesNotSelfWake()
    {
        var s = new CaseDaySyncSchedule();
        long ver = 0;
        // 有資料的那一趟自己 Bump 一次；之後閒置，版本不再變動
        var ran = Simulate(s, 30, _ => ver, b =>
        {
            if (b == 1) { ver++; return (true, ver); }
            return (false, ver);
        });
        var diffs = Diffs(ran);
        // 第一趟處理到資料 → 拍 2 執行（閒置）→ 之後退避 1,2,4,6...；若自我喚醒會永遠是 1
        Assert.Contains(6, diffs);
    }
}

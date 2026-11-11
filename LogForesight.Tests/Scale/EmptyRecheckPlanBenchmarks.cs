using System.Diagnostics;
using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 排程續跑趟「計畫階段」的規模量測（第 70 輪 B-1）：續跑趟開跑時要逐台掃描缺漏日，
/// 這個成本隨主機數線性成長，且在續跑與空資料複查變多趟後會被重複付出。
/// 預設略過，設 <c>LF_SCALE_BENCH=1</c> 才跑；本機不必每次執行。
/// </summary>
public class EmptyRecheckPlanBenchmarks
{
    private readonly ITestOutputHelper _out;

    public EmptyRecheckPlanBenchmarks(ITestOutputHelper output) => _out = output;

    [ScaleFact]
    public void 量測_3000台逐台掃缺漏日的計畫階段耗時()
    {
        const int hostCount = 3000;
        const int lookback = 14;
        const int hostsMissingLastDay = 500;
        // 紀錄涵蓋今天往前 15 天（含今天）；掃描窗是昨天往前 14 天，所以 14 天都有紀錄
        var profile = new ScaleProfile("recheck-plan-3000x15", HostCount: hostCount, Days: lookback + 1,
            DistinctIssues: 200, IssuesPerRecord: 3, HandlingRowsPerHostDay: 0,
            OpenCaseCount: 0, TombstoneCount: 0);

        using var data = ScaleDataSet.Generate(profile, m => _out.WriteLine(m));
        var yesterday = DateTime.Today.AddDays(-1);

        // 其中 500 台缺最後一天（昨天）：掃描要回報 1 個缺漏日
        using (var ctx = data.Backend.CreateContext())
        {
            ctx.DailyRecords
                .Where(r => r.HostId <= hostsMissingLastDay && r.RecordDate == yesterday)
                .ExecuteDelete();
        }

        var targets = data.LivingHosts.Take(hostCount).ToList();
        var missingHostDays = 0;
        var sw = Stopwatch.StartNew();
        foreach (var host in targets)
        {
            var store = data.Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
            missingHostDays += MissingDateFinder.FindWithExistingDates(store, DateTime.Today, lookback).MissingDates.Count;
        }
        sw.Stop();

        _out.WriteLine($"── {targets.Count:N0} 台 × {lookback} 天回望，其中 {hostsMissingLastDay} 台缺最後一天");
        _out.WriteLine($"逐台 FindWithExistingDates 總耗時：{sw.Elapsed.TotalSeconds:N1} 秒（缺漏主機日 {missingHostDays:N0}）");
        Assert.Equal(hostsMissingLastDay, missingHostDays);
    }
}

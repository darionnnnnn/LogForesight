using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 壓測資料產生器的常態驗證（跑 <see cref="ScaleProfile.Small"/>，秒級）。
///
/// 為什麼需要這組測試：<see cref="ScaleBenchmarks"/> 預設不執行，產生器因此沒有任何
/// 常態保護——往後幾輪改到 schema 或模型時它會悄悄爛掉，等到真要跑壓測才發現。
/// 這裡驗的是「產生器產出的資料，產品的查詢路徑讀得出來且形狀正確」，不驗效能。
/// </summary>
public class ScaleDataSetTests
{
    [Fact]
    public void 產生器產出的資料_查詢路徑讀得到且筆數正確()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small);
        var profile = ScaleProfile.Small;

        Assert.Equal(profile.HostCount, data.LivingHosts.Count);
        Assert.Equal(profile.HostCount + profile.TombstoneCount, data.Hosts.Count);
        Assert.Equal(profile.RecordCount, data.Stats.RecordRows);

        // 走產品的查詢路徑（不是直接讀表）：產生器寫的資料必須能被 EfAnalysisRecordStore 讀回來
        var store = data.Backend.RecordStore();
        var records = store.Query(new RecordQueryFilter());
        Assert.Equal(profile.RecordCount, records.Count);
        Assert.All(records, r => Assert.NotEmpty(r.TopIssues));
    }

    [Fact]
    public void 產生器寫的主機與授權_走真正的store讀得回來()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small);

        var hosts = new HostStore(data.Backend.Blob("hosts")).GetAll();
        Assert.Equal(data.Hosts.Count, hosts.Count);

        // 墓碑列必須真的指向存活主機——別名展開的成本只有在這個關係成立時才量得到
        var tombstones = hosts.Where(h => h.MergedInto != null).ToList();
        Assert.Equal(ScaleProfile.Small.TombstoneCount, tombstones.Count);
        Assert.All(tombstones, t => Assert.Contains(hosts, h => h.HostId == t.MergedInto));

        var users = new UserStore(data.Backend.Blob("users")).GetAll();
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public void 處理狀態與案件_走真正的store讀得回來()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small);

        var handlings = new IssueHandlingStore(data.Backend.Blob("issue_handling"));
        var cases = new IssueCaseStore(data.Backend.Blob("issue_cases"));

        Assert.Equal(ScaleProfile.Small.OpenCaseCount, cases.GetOpenByHandler(2).Count);

        // 每一列處理狀態都要對得上一台存活主機（否則 HostLookup 會全部查無、量測失真）
        var livingNames = data.LivingHosts.Select(h => h.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sample = handlings.GetMany(livingNames, DateTime.Today.AddDays(-ScaleProfile.Small.Days), DateTime.Today);
        Assert.Equal(data.Stats.HandlingRows, sample.Count);
    }

    [Fact]
    public void 問題分布為重尾_前兩個簽章涵蓋絕大多數主機()
    {
        using var data = ScaleDataSet.Generate(ScaleProfile.Small);

        var records = data.Backend.RecordStore().Query(new RecordQueryFilter());
        var hostsPerSignature = records
            .SelectMany(r => r.TopIssues.Select(i => (Key: (i.Source, i.EventId), r.Host)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // DCOM 10016 的角色：幾乎每台都有。沒有這個形狀，「數量排序讓最普遍的壓過最重要的」
        // （體檢 §10.1）就重現不出來，整個問題主視角改版也就無從驗證
        var universal = hostsPerSignature[("DistributedCOM", 10016)];
        Assert.True(universal >= ScaleProfile.Small.HostCount * 0.9,
            $"最普遍的簽章只涵蓋 {universal}/{ScaleProfile.Small.HostCount} 台，重尾分布沒有生效");

        // 尾端必須真的稀有——全部都很普遍等於沒有分布
        var median = hostsPerSignature.Values.OrderBy(v => v).ElementAt(hostsPerSignature.Count / 2);
        Assert.True(median < universal / 2, $"中位數 {median} 太接近最大值 {universal}，分布不夠偏斜");
    }
}

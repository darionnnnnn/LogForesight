using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 5b（5-8）：執行總表展開某日、以「整體工作流」排序時批次讀取工作流。
/// 對帳：批次讀取（整體工作流排序）與逐台單筆讀取（其他排序只讀當頁）要得到逐欄相同的列。
/// </summary>
public sealed class RunDayDetailBatch5bTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly EfSqliteFixture _fx = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-day5b-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly HostDayWorkflowStore _store;
    private static readonly DateTime DataDate = DateTime.Today.AddDays(-1);

    public RunDayDetailBatch5bTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "wf.db")};Pooling=False"
        }, _dir);
        _store = new HostDayWorkflowStore(_backend);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _fx.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private RunMonitorService Service() => new(
        new BatchRunStore(_fx.LogStore("runs"), _fx.LogStore("run_logs")), _hosts,
        new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeUserStore(),
        new ScheduleOptionsStore(_fx.Blob("schedule_options")), new UserDisplayNameService(new FakeSystemSettingsStore()),
        workflow: new HostDayWorkflowService(_store));

    /// <summary>各種工作流形狀輪替：完成、父紀錄失敗、恢復等待、部分、進行中、沒有工作流紀錄。</summary>
    private List<WebHost> SeedHosts(int count)
    {
        var list = new List<WebHost>();
        for (var i = 0; i < count; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"10.2.{i / 250}.{i % 250}", Source = "netiq" });
            list.Add(host);
            switch (i % 6)
            {
                case 0: Complete(host.HostId); break;
                case 1: _store.Update(host.HostId, DataDate, s => s.Parent = WorkflowLegState.Failed); break;
                case 2:
                    _store.Update(host.HostId, DataDate, s =>
                    {
                        s.Parent = WorkflowLegState.Succeeded;
                        s.RecoveryWaitingRecordId = 9; s.RecoveryWaitingWriteRevision = 1; s.RecoveryWaitingReason = "too-large";
                    });
                    break;
                case 3:
                    Complete(host.HostId);
                    _store.Update(host.HostId, DataDate, s => s.Ai = WorkflowLegState.Failed);
                    break;
                case 4: _store.Update(host.HostId, DataDate, s => s.Parent = WorkflowLegState.Running); break;
                default: break;   // 沒有工作流紀錄
            }
        }
        return list;
    }

    private void Complete(long hostId) => _store.Update(hostId, DataDate, s =>
    {
        s.Parent = WorkflowLegState.Succeeded; s.Prtg = WorkflowLegState.Disabled; s.Ai = WorkflowLegState.Disabled;
        s.CasePlanClosed = true; s.MailPlanClosed = true;
    });

    private static string Json(RunDayHostStatusDto d) => JsonSerializer.Serialize(d);

    /// <summary>A6：每種排序、升降冪、第 1／2 頁、狀態篩選的列，與逐台單筆讀取的結果逐欄相同。</summary>
    [Fact]
    public void A6_各種排序與分頁的回應與逐台單筆讀取逐欄相同()
    {
        var hosts = SeedHosts(30);
        var service = Service();

        // 預言機：用「只讀當頁」的路徑（逐台單筆 Get）取得每台主機的完整列
        var byHost = service.GetDayDetailPage(DataDate, 1, 200, "hostName", "asc").Items.ToDictionary(r => r.HostName, Json);
        Assert.Equal(30, byHost.Count);

        foreach (var sort in new string?[] { null, "hostName", "status", "daysAnalyzed", "errorCount", "workflowComplete", "bogus" })
        foreach (var dir in new[] { "asc", "desc" })
        foreach (var page in new[] { 1, 2 })
        foreach (var status in new string?[] { null, "none" })
        {
            var result = service.GetDayDetailPage(DataDate, page, 7, sort, dir, status);
            foreach (var row in result.Items)
                Assert.Equal(byHost[row.HostName], Json(row));
            if (status == null) Assert.Equal(30, result.Total);
        }

        // 整體工作流：升冪依「沒有紀錄(-1)＜未完成(0)＜完成(1)」非遞減，降冪恰為升冪反轉，兩頁接續
        int Key(RunDayHostStatusDto d) => d.WorkflowComplete is { } c ? (c ? 1 : 0) : -1;
        var asc = service.GetDayDetailPage(DataDate, 1, 200, "workflowComplete", "asc").Items;
        var desc = service.GetDayDetailPage(DataDate, 1, 200, "workflowComplete", "desc").Items;
        Assert.Equal(asc.Select(Key).OrderBy(x => x), asc.Select(Key));
        Assert.Equal(asc.Select(r => r.HostName).Reverse(), desc.Select(r => r.HostName));
        var p1 = service.GetDayDetailPage(DataDate, 1, 12, "workflowComplete", "asc").Items;
        var p2 = service.GetDayDetailPage(DataDate, 2, 12, "workflowComplete", "asc").Items;
        Assert.Equal(asc.Take(24).Select(r => r.HostName), p1.Concat(p2).Select(r => r.HostName));
        Assert.Contains(asc, r => Key(r) == -1);
        Assert.Contains(asc, r => Key(r) == 0);
        Assert.Contains(asc, r => Key(r) == 1);
        _output.WriteLine("ok");
    }

    [Fact]
    public void A6_整體工作流排序_工作流讀取往返數不隨主機數成長()
    {
        long Measure(Action action)
        {
            var before = _backend.Performance.Snapshot().TotalOperations;
            action();
            return _backend.Performance.Snapshot().TotalOperations - before;
        }
        var service = Service();
        SeedHosts(40);
        var few = Measure(() => service.GetDayDetailPage(DataDate, 1, 20, "workflowComplete", "asc"));
        SeedMore(40, 160);
        PagedResult<RunDayHostStatusDto>? large = null;
        var many = Measure(() => large = service.GetDayDetailPage(DataDate, 1, 20, "workflowComplete", "asc"));

        Assert.Equal(200, large!.Total);
        Assert.Equal(few, many);
        Assert.True(many <= 2, $"讀取操作數 {many}");
        _output.WriteLine($"40 台 {few}、200 台 {many}");
    }

    private void SeedMore(int from, int count)
    {
        for (var i = from; i < from + count; i++)
        {
            var host = _hosts.Upsert(new WebHost { HostName = $"10.2.{i / 250}.{i % 250}", Source = "netiq" });
            if (i % 2 == 0) Complete(host.HostId);
        }
    }

    // ── 批次讀取與單筆讀取的邊界一致 ─────────────────────────

    private static string Blob(long hostId) => "workflow_" + hostId + "_" + DataDate.ToString("yyyyMMdd");

    private void PutRaw(long hostId, string content)
    {
        using var ctx = _backend.CreateContext();
        var key = Blob(hostId);
        var row = ctx.Blobs.SingleOrDefault(b => b.BlobKey == key);
        if (row == null) ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, UpdatedAt = DateTime.Now, Version = 1 });
        else { row.Content = content; row.Version++; }
        ctx.SaveChanges();
    }

    [Fact]
    public void GetMany_與單筆Get結果相同_不存在與空白內容都不在結果裡()
    {
        var hosts = SeedHosts(12);
        PutRaw(hosts[5].HostId, "   ");   // 空白內容：單筆回 null
        var keys = hosts.Select(h => (h.HostId, DataDate)).Append((HostId: 999_999L, Date: DataDate)).ToList();

        var many = _store.GetMany(keys);

        foreach (var (hostId, date) in keys)
        {
            var single = _store.Get(hostId, date);
            if (single == null) Assert.False(many.ContainsKey((hostId, date)), $"host {hostId}");
            else Assert.Equal(JsonSerializer.Serialize(single), JsonSerializer.Serialize(many[(hostId, date)]));
        }
        Assert.False(many.ContainsKey((hosts[5].HostId, DataDate)));
        Assert.False(many.ContainsKey((999_999L, DataDate)));
    }

    [Fact]
    public void GetMany_超限內容與單筆Get擲同樣的例外_畫面輸出同樣是失敗()
    {
        var hosts = SeedHosts(8);
        PutRaw(hosts[3].HostId, "{\"hostId\":" + hosts[3].HostId + ",\"hostName\":\"" + new string('x', 70_000) + "\"}");

        var single = Assert.Throws<InvalidDataException>(() => _store.Get(hosts[3].HostId, DataDate));
        var batch = Assert.Throws<InvalidDataException>(() => _store.GetMany(hosts.Select(h => (h.HostId, DataDate))));
        Assert.Equal(single.Message, batch.Message);

        // 執行總表：整體工作流排序（批次）與該主機在當頁的其他排序（單筆）都因同一份超限內容失敗
        var service = Service();
        Assert.Throws<InvalidDataException>(() => service.GetDayDetailPage(DataDate, 1, 50, "workflowComplete", "asc"));
        Assert.Throws<InvalidDataException>(() => service.GetDayDetailPage(DataDate, 1, 50, "hostName", "asc"));
    }

    [Fact]
    public void GetMany_含NUL字元的內容與單筆Get同樣被拒絕()
    {
        var hosts = SeedHosts(4);
        PutRaw(hosts[1].HostId, "{\"hostId\":1}\0{\"x\":1}");

        Exception? single = Record.Exception(() => _store.Get(hosts[1].HostId, DataDate));
        Exception? batch = Record.Exception(() => _store.GetMany(hosts.Select(h => (h.HostId, DataDate))));

        Assert.NotNull(single);
        Assert.NotNull(batch);
        Assert.Equal(single!.GetType(), batch!.GetType());
        Assert.Equal(single.Message, batch.Message);
    }

    [Fact]
    public void GetMany_超過一段的鍵數仍逐一對應()
    {
        var count = HostDayWorkflowStore.GetManyChunkSize + 37;
        var keys = new List<(long, DateTime)>();
        for (var i = 1; i <= count; i++)
        {
            _store.Update(i, DataDate, s => s.Parent = i % 2 == 0 ? WorkflowLegState.Succeeded : WorkflowLegState.Failed);
            keys.Add((i, DataDate));
        }
        keys.Add((1, DataDate));   // 重複的鍵不影響結果

        var many = _store.GetMany(keys);

        Assert.Equal(count, many.Count);
        foreach (var (id, date) in keys)
            Assert.Equal(id % 2 == 0 ? WorkflowLegState.Succeeded : WorkflowLegState.Failed, many[(id, date)].Parent);
        Assert.Empty(_store.GetMany(Array.Empty<(long, DateTime)>()));
    }
}

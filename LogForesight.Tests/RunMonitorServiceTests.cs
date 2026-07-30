using LogForesight.Sql;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 執行監控（docs/WEB-SPEC.md §9.10）的狀態判定，重點在 NetIQ 主機修正前的 bug：
/// NetIQ 主機沒有個別的 <see cref="BatchRun"/>（管線只登記彙總的一筆），舊邏輯逐台比對
/// BatchRun.HostName 因此永遠比不到，恆顯示「未執行」。修正後改以「前一天是否有分析紀錄」
/// 判定（見 <see cref="RunMonitorService"/> 類別註解）。
/// </summary>
public class RunMonitorServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private BatchRunStore Runs() => new(_fx.LogStore("runs"), _fx.LogStore("run_logs"));
    private EfAnalysisRecordStore Records() => new(_fx.NewContext, "test");

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private RunMonitorService Create() => new(Runs(), _hosts, Records());

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date) =>
        new() { HostId = hostId, Host = host, Date = date, RiskLevel = "低" };

    [Fact]
    public void NetIQ主機_昨日有分析紀錄_今日列為成功()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.5", Source = "netiq" });
        Records().Append(Rec(host.HostId, host.HostName, DateTime.Today.AddDays(-1)));

        var detail = Create().GetDayDetail(DateTime.Today);

        var row = Assert.Single(detail, d => d.HostName == host.HostName);
        Assert.Equal("success", row.Status);
        Assert.Null(row.RunId);   // 沒有個別 BatchRun，前端「查看執行」鈕不出現
    }

    [Fact]
    public void NetIQ主機_昨日無分析紀錄_今日列為未執行()
    {
        _hosts.Upsert(new WebHost { HostName = "10.0.0.6", Source = "netiq" });

        var detail = Create().GetDayDetail(DateTime.Today);

        var row = Assert.Single(detail, d => d.HostName == "10.0.0.6");
        Assert.Equal("none", row.Status);
    }

    [Fact]
    public void NetIQ主機_未回填BatchRun_不因舊邏輯永遠顯示未執行()
    {
        // 修正前的 bug：BatchRunRecorder 只以跑批次的那台機器名義登記一筆 run，
        // NetIQ 主機（HostName 通常是 IP）永遠比對不到、恆為 none。
        // 這裡刻意登記一筆與 NetIQ 主機名稱完全無關的 BatchRun，驗證 NetIQ 主機的狀態
        // 只看分析紀錄、不受這筆無關 run 影響。
        var runs = Runs();
        runs.StartRun(new BatchRun { HostName = "DEV-BATCH-HOST", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });

        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.7", Source = "netiq" });
        Records().Append(Rec(host.HostId, host.HostName, DateTime.Today.AddDays(-1)));

        var summaries = new RunMonitorService(runs, _hosts, Records()).GetDaySummaries(3);
        var today = summaries.Single(s => s.Date == DateTime.Today.ToString("yyyy-MM-dd"));

        // DEV-BATCH-HOST（跑批次的機器本身）與 10.0.0.7（NetIQ 主機）都算成功——
        // 重點是後者不受前者這筆無關 run 影響、也不會落回未執行
        Assert.Equal(2, today.SuccessCount);
        Assert.Equal(0, today.NotRunCount);
    }

    [Fact]
    public void 本機主機_邏輯不受影響_無執行紀錄仍為未執行()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var detail = Create().GetDayDetail(DateTime.Today);

        var row = Assert.Single(detail, d => d.HostName == "SRV-LOCAL");
        Assert.Equal("none", row.Status);
    }

    [Fact]
    public void 本機主機_有成功執行紀錄_列為成功()
    {
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var detail = new RunMonitorService(runs, _hosts, Records()).GetDayDetail(DateTime.Today);

        var row = Assert.Single(detail, d => d.HostName == "SRV-LOCAL");
        Assert.Equal("success", row.Status);
        Assert.Equal(runId, row.RunId);
    }

    [Fact]
    public void 混合清單_計數正確()
    {
        var netiqOk = _hosts.Upsert(new WebHost { HostName = "10.0.0.8", Source = "netiq" });
        _hosts.Upsert(new WebHost { HostName = "10.0.0.9", Source = "netiq" });   // 沒有分析紀錄
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var records = Records();
        records.Append(Rec(netiqOk.HostId, netiqOk.HostName, DateTime.Today.AddDays(-1)));

        var summaries = new RunMonitorService(runs, _hosts, records).GetDaySummaries(1);
        var today = Assert.Single(summaries);

        Assert.Equal(3, today.TotalHosts);
        Assert.Equal(2, today.SuccessCount);   // netiqOk + SRV-LOCAL
        Assert.Equal(1, today.NotRunCount);    // 10.0.0.9
    }
}

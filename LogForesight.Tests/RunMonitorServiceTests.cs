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

    private RunMonitorService Create() => new(Runs(), _hosts, Records(), new FakeUserStore());

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

        var summaries = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore()).GetDaySummaries(3);
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

        var detail = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore()).GetDayDetail(DateTime.Today);

        var row = Assert.Single(detail, d => d.HostName == "SRV-LOCAL");
        Assert.Equal("success", row.Status);
        Assert.Equal(runId, row.RunId);
    }

    [Fact]
    public void 優雅停止的執行_列為已停止而非失敗()
    {
        // docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4：手動停止或窗口 End 的優雅停止「是已停止不是失敗」。
        // Stopped 優先於錯誤計數——停止前累積的警告/錯誤仍在計數欄，但狀態不因此變失敗
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun
        {
            RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now,
            ExitCode = 0, Stopped = true, ErrorCount = 2
        });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var service = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore());

        var row = Assert.Single(service.GetDayDetail(DateTime.Today), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("stopped", row.Status);

        var today = service.GetDaySummaries(1).Single();
        Assert.Equal(1, today.StoppedCount);
        Assert.Equal(0, today.FailedCount);
        Assert.Empty(today.FailedHostNames);   // 已停止不列失敗主機清單

        Assert.True(service.GetDetail(runId).Stopped);
    }

    [Fact]
    public void 舊紀錄缺Stopped欄位_反序列化為未停止_狀態判定不變()
    {
        // Trigger 欄位同款零遷移保證：舊 JSON 行沒有 Stopped 欄，讀出來是 false，
        // exit 0 無錯誤照舊列為成功
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var row = Assert.Single(
            new RunMonitorService(runs, _hosts, Records(), new FakeUserStore()).GetDayDetail(DateTime.Today),
            d => d.HostName == "SRV-LOCAL");
        Assert.Equal("success", row.Status);
    }

    [Fact]
    public void BatchRunRecorder_取消權杖已觸發時Dispose_回填為已停止而非exit1()
    {
        // AnalysisOrchestrator 的優雅停止路徑：OperationCanceledException 離開 using 範圍時
        // 由 Dispose 回填——取消權杖已觸發＝使用者停止（Stopped＋exit 0），不是異常中斷（exit 1）
        var runs = Runs();
        using var cts = new CancellationTokenSource();
        long runId;

        using (var recorder = new BatchRunRecorder(runs, "SRV-LOCAL", Array.Empty<string>(), "manual:tester", cts.Token))
        {
            runId = recorder.RunId;
            cts.Cancel();
        }   // 未呼叫 Finish 就 Dispose——模擬 OCE 展開堆疊

        var run = runs.GetRun(runId)!;
        Assert.True(run.Stopped);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains(runs.GetLogs(runId), l => l.Message.Contains("優雅停止"));
    }

    [Fact]
    public void BatchRunRecorder_未取消時Dispose_仍回填exit1異常中斷()
    {
        // 對照組：沒有取消訊號的意外 Dispose（未預期例外）維持既有語意——exit 1、非 Stopped
        var runs = Runs();
        long runId;

        using (var recorder = new BatchRunRecorder(runs, "SRV-LOCAL", Array.Empty<string>(), "console"))
        {
            runId = recorder.RunId;
        }

        var run = runs.GetRun(runId)!;
        Assert.False(run.Stopped);
        Assert.Equal(1, run.ExitCode);
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

        var summaries = new RunMonitorService(runs, _hosts, records, new FakeUserStore()).GetDaySummaries(1);
        var today = Assert.Single(summaries);

        Assert.Equal(3, today.TotalHosts);
        Assert.Equal(2, today.SuccessCount);   // netiqOk + SRV-LOCAL
        Assert.Equal(1, today.NotRunCount);    // 10.0.0.9
    }

    /// <summary>docs/archive/FEEDBACK-8-PLAN.md #6：「誰跑的」統一顯示格式「顯示名稱(帳號)」；
    /// 查無對應使用者（帳號已刪除等）時退回只顯示帳號，不因此整筆出錯。</summary>
    [Fact]
    public void 手動觸發的執行紀錄_TriggerText顯示顯示名稱與帳號()
    {
        var users = new FakeUserStore();
        users.Upsert(new WebUser { Account = "DOMAIN\\svc-lfadmin", DisplayName = "系統維護" });

        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now, Trigger = "manual:DOMAIN\\svc-lfadmin" });
        runs.FinishRun(new BatchRun
        {
            RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now,
            ExitCode = 0, Trigger = "manual:DOMAIN\\svc-lfadmin"
        });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var service = new RunMonitorService(runs, _hosts, Records(), users);

        Assert.Equal("手動（系統維護(DOMAIN\\svc-lfadmin)）", service.GetDetail(runId).TriggerText);
    }

    /// <summary>查無對應使用者時退回只顯示帳號，不拋例外</summary>
    [Fact]
    public void 手動觸發的執行紀錄_帳號查無使用者時只顯示帳號()
    {
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now, Trigger = "manual:DOMAIN\\deleted-user" });
        runs.FinishRun(new BatchRun
        {
            RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now,
            ExitCode = 0, Trigger = "manual:DOMAIN\\deleted-user"
        });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var service = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore());

        Assert.Equal("手動（DOMAIN\\deleted-user）", service.GetDetail(runId).TriggerText);
    }
}

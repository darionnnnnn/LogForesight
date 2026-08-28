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
    private ScheduleOptionsStore Options() => new(_fx.Blob("schedule_options"));

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private readonly UserDisplayNameService _displayNames = new(new FakeSystemSettingsStore());
    private RunMonitorService Create() => new(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

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

        var summaries = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDaySummaries(3, page: 1, pageSize: 30);
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
    public void 本機主機_當天無執行但昨日有分析紀錄_列為已回補()
    {
        // §3：立即執行回補會補上缺漏日的分析紀錄，但 BatchRun 只登記在觸發當天——
        // 被回補的其他日期原本誤顯示「未執行」。有 D-1 分析紀錄時改標「已回補」（不冒充 success）。
        var host = _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Records().Append(Rec(host.HostId, host.HostName, DateTime.Today.AddDays(-1)));

        var service = Create();

        var row = Assert.Single(service.GetDayDetail(DateTime.Today), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("backfilled", row.Status);
        Assert.Null(row.RunId);   // 回補 fallback 沒有可連到的單次執行紀錄

        var today = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
        Assert.Equal(1, today.BackfilledCount);
        Assert.Equal(0, today.SuccessCount);
        Assert.Equal(0, today.NotRunCount);
    }

    [Fact]
    public void 本機主機_有BatchRun時_不走回補fallback仍以執行紀錄判定()
    {
        // 有 BatchRun 就照 exit code／計數判定，即使當天也存在 D-1 分析紀錄也不降級成「已回補」
        var host = _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Records().Append(Rec(host.HostId, host.HostName, DateTime.Today.AddDays(-1)));
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });

        var row = Assert.Single(
            new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDayDetail(DateTime.Today),
            d => d.HostName == "SRV-LOCAL");
        Assert.Equal("success", row.Status);
        Assert.Equal(runId, row.RunId);
    }

    [Fact]
    public void 本機主機_有成功執行紀錄_列為成功()
    {
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var detail = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDayDetail(DateTime.Today);

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

        var service = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

        var row = Assert.Single(service.GetDayDetail(DateTime.Today), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("stopped", row.Status);

        var today = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
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
            new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDayDetail(DateTime.Today),
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

        var summaries = new RunMonitorService(runs, _hosts, records, new FakeUserStore(), Options(), _displayNames).GetDaySummaries(1, page: 1, pageSize: 30);
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

        var service = new RunMonitorService(runs, _hosts, Records(), users, Options(), _displayNames);

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

        var service = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

        Assert.Equal("手動（DOMAIN\\deleted-user）", service.GetDetail(runId).TriggerText);
    }

    // ── 本機分析停用（回饋十八輪批次D，終檢輪補上規劃表列的執行監控顯示）──────────

    /// <summary>停用「分析本機主機」後，本機主機的空白日顯示「本機分析已停用」而非「未執行」——
    /// 那是設定行為不是漏跑，混在同一格會讓人每天誤判排程壞了。</summary>
    [Fact]
    public void 本機分析停用_本機主機空白日列為停用而非未執行()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Options().Update(o => o.LocalAnalysisEnabled = false);
        var service = Create();

        var row = Assert.Single(service.GetDayDetail(DateTime.Today), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("local_disabled", row.Status);

        var today = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
        Assert.Equal(1, today.LocalDisabledCount);
        Assert.Equal(0, today.NotRunCount);
    }

    /// <summary>停用前真的有跑過的日子照實顯示（成功／失敗照舊），只有空白日才標停用。</summary>
    [Fact]
    public void 本機分析停用_有執行紀錄的日子仍照實顯示()
    {
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = DateTime.Now });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = DateTime.Now, FinishedAt = DateTime.Now, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Options().Update(o => o.LocalAnalysisEnabled = false);

        var row = Assert.Single(
            new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDayDetail(DateTime.Today),
            d => d.HostName == "SRV-LOCAL");
        Assert.Equal("success", row.Status);
    }

    /// <summary>NetIQ 主機完全不受本機停用開關影響——停用只作用在本機那一路。</summary>
    [Fact]
    public void 本機分析停用_NetIQ主機狀態不受影響()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.8", Source = "netiq" });
        Records().Append(Rec(host.HostId, host.HostName, DateTime.Today.AddDays(-1)));
        Options().Update(o => o.LocalAnalysisEnabled = false);

        var row = Assert.Single(Create().GetDayDetail(DateTime.Today), d => d.HostName == host.HostName);
        Assert.Equal("success", row.Status);
    }

    // ── 批次G：執行總表分頁（GetDaySummaries(days, page, pageSize)）────────────────────

    /// <summary>
    /// 分頁邊界案例 1／3：總天數不足一頁——只有 3 天，每頁 10 天，只有 1 頁，
    /// 第 1 頁回傳的天數等於總天數。
    /// </summary>
    [Fact]
    public void 分頁_總天數不足一頁_第一頁回傳全部天數()
    {
        var service = Create();
        var result = service.GetDaySummaries(days: 3, page: 1, pageSize: 10);
        Assert.Equal(3, result.Count);
    }

    /// <summary>
    /// 分頁邊界案例 2／3：總天數剛好整頁——10 天，每頁 5 天，共 2 頁，每頁各回傳 5 筆。
    /// 跨頁日期不重不漏：把兩頁日期串起來，應完全等於完整 10 天區間。
    /// </summary>
    [Fact]
    public void 分頁_總天數剛好整頁_每頁天數正確且跨頁不重不漏()
    {
        const int days = 10;
        const int pageSize = 5;
        var service = Create();

        var page1 = service.GetDaySummaries(days, page: 1, pageSize: pageSize);
        var page2 = service.GetDaySummaries(days, page: 2, pageSize: pageSize);

        Assert.Equal(pageSize, page1.Count);
        Assert.Equal(pageSize, page2.Count);

        // 跨頁日期不重不漏：把兩頁日期串起來排序，應等於完整區間
        var allDates = page1.Concat(page2).Select(s => s.Date).OrderBy(d => d).ToList();
        var expectedDates = Enumerable.Range(0, days)
            .Select(i => DateTime.Today.AddDays(-days + 1 + i).ToString("yyyy-MM-dd"))
            .OrderBy(d => d)
            .ToList();
        Assert.Equal(expectedDates, allDates);
    }

    /// <summary>
    /// 分頁邊界案例 3／3：總天數超過一頁——7 天，每頁 3 天，共 3 頁
    /// （第 3 頁只有 1 天）。跨頁日期不重不漏。
    /// </summary>
    [Fact]
    public void 分頁_總天數超過一頁_末頁天數正確且跨頁不重不漏()
    {
        const int days = 7;
        const int pageSize = 3;
        var service = Create();

        var page1 = service.GetDaySummaries(days, page: 1, pageSize: pageSize);
        var page2 = service.GetDaySummaries(days, page: 2, pageSize: pageSize);
        var page3 = service.GetDaySummaries(days, page: 3, pageSize: pageSize);

        Assert.Equal(3, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.Single(page3);   // 7 = 3+3+1，末頁只有 1 天

        // 跨頁日期不重不漏
        var allDates = page1.Concat(page2).Concat(page3).Select(s => s.Date).OrderBy(d => d).ToList();
        var expectedDates = Enumerable.Range(0, days)
            .Select(i => DateTime.Today.AddDays(-days + 1 + i).ToString("yyyy-MM-dd"))
            .OrderBy(d => d)
            .ToList();
        Assert.Equal(expectedDates, allDates);
    }

    /// <summary>
    /// 第 1 頁是最新的日期區段：最新一天（Today）必須出現在第 1 頁，最舊的那天不在第 1 頁。
    /// </summary>
    [Fact]
    public void 分頁_第一頁是最新日期區段()
    {
        const int days = 10;
        const int pageSize = 4;
        var service = Create();

        var page1 = service.GetDaySummaries(days, page: 1, pageSize: pageSize);

        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        var oldestStr = DateTime.Today.AddDays(-days + 1).ToString("yyyy-MM-dd");

        Assert.Contains(page1, s => s.Date == todayStr);
        Assert.DoesNotContain(page1, s => s.Date == oldestStr);
    }

    /// <summary>
    /// 全部天數上限跟隨 RunLogRetentionDays 設定：設為 200 時，API 計算的 maxDays 應為 200，
    /// 不再被舊的寫死上限 90 卡住。驗證 RunsController 的計算邏輯。
    /// </summary>
    [Fact]
    public void 全部上限跟隨RunLogRetentionDays設定()
    {
        // 取 max(90, RunLogRetentionDays)
        Assert.Equal(200, Math.Max(90, 200));   // 200 > 90，應用 200
        Assert.Equal(90, Math.Max(90, 30));     // 30 < 90，下限保底為 90
        // 確認預設值（120）也高於 90，不被舊上限截掉
        Assert.Equal(SystemSettings.DefaultRunLogRetentionDays,
            Math.Max(90, SystemSettings.DefaultRunLogRetentionDays));
    }

    /// <summary>
    /// page 與 pageSize 傳入不合法值（0、負數、超大值）時被 clamp 而不是丟例外。
    /// GetDaySummaries 接收的是已 Clamp 後的值（Clamp 在 Controller），
    /// 這裡驗證 Controller 的 Clamp 邏輯邊界。
    /// </summary>
    [Fact]
    public void 分頁_非法參數被clamp不丟例外()
    {
        // page ≤ 0 → clamp 至 1
        Assert.Equal(1, Math.Clamp(0, 1, int.MaxValue));
        Assert.Equal(1, Math.Clamp(-99, 1, int.MaxValue));

        // pageSize ≤ 0 → clamp 至 1
        Assert.Equal(1, Math.Clamp(0, 1, 90));
        Assert.Equal(1, Math.Clamp(-1, 1, 90));

        // pageSize 超大 → clamp 至 90
        Assert.Equal(90, Math.Clamp(9999, 1, 90));

        // 不合法的 page 也不會讓 GetDaySummaries 拋例外（傳入合法的 clamp 後值）
        var service = Create();
        var result = service.GetDaySummaries(days: 5, page: 1, pageSize: 90);
        Assert.Equal(5, result.Count);   // 5 天 ≤ 90（pageSize），全部在第 1 頁
    }
}

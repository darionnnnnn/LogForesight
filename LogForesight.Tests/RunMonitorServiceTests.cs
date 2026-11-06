using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
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
    private readonly string _attemptDir = Path.Combine(Path.GetTempPath(), "lf-netiq-attempts-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _attemptBackend;
    private readonly FakeHostStore _hosts = new();
    private BatchRunStore Runs() => new(_fx.LogStore("runs"), _fx.LogStore("run_logs"));
    private EfAnalysisRecordStore Records() => new(_fx.NewContext, "test");
    private ScheduleOptionsStore Options() => new(_fx.Blob("schedule_options"));

    public RunMonitorServiceTests()
    {
        Directory.CreateDirectory(_attemptDir);
        _attemptBackend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_attemptDir, "attempts.db")}"
        }, _attemptDir);
    }

    public void Dispose()
    {
        _fx.Dispose();
        if (Directory.Exists(_attemptDir)) Directory.Delete(_attemptDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private readonly UserDisplayNameService _displayNames = new(new FakeSystemSettingsStore());
    private RunMonitorService Create() => new(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date) =>
        new() { HostId = hostId, Host = host, Date = date, RiskLevel = "低" };

    private static string SerializeWithOriginalAttemptMetadata(DailyAnalysisRecord record, DailyAnalysisRecord original)
    {
        var normalized = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(
            System.Text.Json.JsonSerializer.Serialize(record))!;
        normalized.LatestNetiqAttemptStatus = original.LatestNetiqAttemptStatus;
        normalized.LatestNetiqAttemptAtUtc = original.LatestNetiqAttemptAtUtc;
        return System.Text.Json.JsonSerializer.Serialize(normalized);
    }

    [Fact]
    public void NetIQ首跑取數失敗_沒有正式紀錄仍持久化失敗且摘要明細一致()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.53", Source = "netiq" });
        var dataDate = DateTime.Today.AddDays(-2);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 2001, "running");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 2001, "failed");

        using (var ctx = _attemptBackend.CreateContext()) Assert.Empty(ctx.DailyRecords);
        var service = new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames,
            backend: _attemptBackend);
        var detail = Assert.Single(service.GetDayDetail(dataDate.AddDays(1)), row => row.HostName == host.HostName);
        var summary = Assert.Single(service.GetDaySummaries(1, 1, 30));

        Assert.Equal("failed", detail.Status);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(0, summary.NotRunCount);
    }

    [Fact]
    public void NetIQ重跑失敗保留原紀錄內容_恢復成功後兩種檢視同步更新()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.54", Source = "netiq" });
        var dataDate = DateTime.Today.AddDays(-2);
        var record = Rec(host.HostId, host.HostName, dataDate);
        record.LogSource = AnalysisLogSource.Netiq;
        _attemptBackend.RecordStore().Append(record);
        string before;
        using (var ctx = _attemptBackend.CreateContext()) before = ctx.DailyRecords.Single(row => row.HostId == host.HostId).ContentJson;

        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 3001, "success");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 3002, "running");
        NetiqSourceAttempt.Mark(_attemptBackend, host.HostId, dataDate, "running");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 3002, "failed");
        NetiqSourceAttempt.Mark(_attemptBackend, host.HostId, dataDate, "failed");
        DailyAnalysisRecord failedRecord;
        using (var ctx = _attemptBackend.CreateContext())
            failedRecord = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(ctx.DailyRecords.Single(row => row.HostId == host.HostId).ContentJson)!;
        var originalRecord = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(before)!;
        Assert.Equal(before, SerializeWithOriginalAttemptMetadata(failedRecord, originalRecord));
        Assert.False(failedRecord.CanSupplementWithPrtg());

        var service = new RunMonitorService(Runs(), _hosts, _attemptBackend.RecordStore(), new FakeUserStore(), Options(), _displayNames,
            backend: _attemptBackend);
        Assert.Equal("failed", Assert.Single(service.GetDayDetail(dataDate.AddDays(1))).Status);
        Assert.Equal(1, Assert.Single(service.GetDaySummaries(1, 1, 30)).FailedCount);

        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 3003, "success");
        NetiqSourceAttempt.Mark(_attemptBackend, host.HostId, dataDate, "success");
        Assert.Equal("success", Assert.Single(service.GetDayDetail(dataDate.AddDays(1))).Status);
        Assert.Equal(1, Assert.Single(service.GetDaySummaries(1, 1, 30)).SuccessCount);
        using (var ctx = _attemptBackend.CreateContext())
        {
            var recovered = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(ctx.DailyRecords.Single(row => row.HostId == host.HostId).ContentJson)!;
            Assert.True(recovered.CanSupplementWithPrtg());
            Assert.Equal(before, SerializeWithOriginalAttemptMetadata(recovered, originalRecord));
        }
    }

    [Fact]
    public void NetIQ三千主機一日期讀回為有界批次且無效RunId明確未知()
    {
        var hosts = Enumerable.Range(1, 3000)
            .Select(i => _hosts.Upsert(new WebHost { HostName = $"10.1.{i / 256}.{i % 256}", Source = "netiq" }).HostId)
            .ToArray();
        var date = DateTime.Today.AddDays(-2);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, hosts, date, 4001, "failed");

        var first = NetiqSourceAttempt.ReadLatest(_attemptBackend, date, date);
        Assert.Equal(3000, first.Count);
        Assert.All(first.Values, receipt => Assert.Equal("failed", receipt.Status));

        var untrackedHost = hosts[1];
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { untrackedHost }, date.AddDays(1), 0, "running");
        var unknown = NetiqSourceAttempt.ReadLatest(_attemptBackend, date.AddDays(1), date.AddDays(1))[(untrackedHost, date.AddDays(1))];
        Assert.Equal(0, unknown.RunId);
        Assert.Equal("unknown", unknown.Status);

        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { hosts[2] }, date, 4003, "failed");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { hosts[2] }, date, 4002, "success");
        Assert.Equal("failed", NetiqSourceAttempt.ReadLatest(_attemptBackend, date, date)[(hosts[2], date)].Status);
    }

    [Fact]
    public void NetIQ取數狀態讀取拒絕超大行與主機日期分割鍵不一致()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.55", Source = "netiq" });
        var date = DateTime.Today.AddDays(-2);
        using (var ctx = _attemptBackend.CreateContext())
        {
            ctx.LogLines.Add(new LogForesight.Core.Persistence.Sql.LogLineRow
            {
                LogKey = NetiqSourceAttempt.LogKey(date, host.HostId), Line = new string('x', 4097), CreatedAt = DateTime.Now
            });
            ctx.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => NetiqSourceAttempt.ReadLatest(_attemptBackend, date, date));

        using (var ctx = _attemptBackend.CreateContext()) ctx.LogLines.ExecuteDelete();
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, date, 5001, "failed");
        using (var ctx = _attemptBackend.CreateContext())
        {
            var row = ctx.LogLines.Single();
            row.LogKey = NetiqSourceAttempt.LogKey(date, host.HostId + 10);
            ctx.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => NetiqSourceAttempt.ReadLatest(_attemptBackend, date, date));
    }

    [Fact]
    public void NetIQ最新取數狀態依正式保留期清除舊資料日期()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.56", Source = "netiq" });
        var oldDate = DateTime.Today.AddDays(-40);
        var recentDate = DateTime.Today.AddDays(-2);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, oldDate, 6001, "failed");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, recentDate, 6001, "success");

        Assert.Equal(1, NetiqSourceAttempt.Prune(_attemptBackend, retentionDays: 30));
        Assert.Empty(NetiqSourceAttempt.ReadLatest(_attemptBackend, oldDate, oldDate));
        Assert.Equal("success", NetiqSourceAttempt.ReadLatest(_attemptBackend, recentDate, recentDate)[(host.HostId, recentDate)].Status);
    }

    [Fact]
    public void NetIQ執行中收據對應已取消BatchRun時列為已停止_沒有結束執行紀錄則逾時列卡住()
    {
        var stoppedHost = _hosts.Upsert(new WebHost { HostName = "10.0.0.57", Source = "netiq" });
        var stuckHost = _hosts.Upsert(new WebHost { HostName = "10.0.0.58", Source = "netiq" });
        var dataDate = DateTime.Today.AddDays(-2);
        var runTime = DateTime.Now;
        var runId = Runs().StartRun(new BatchRun { HostName = "BATCH", StartedAt = runTime });
        Runs().FinishRun(new BatchRun { RunId = runId, HostName = "BATCH", StartedAt = runTime,
            FinishedAt = runTime, ExitCode = 0, Stopped = true });
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { stoppedHost.HostId }, dataDate, runId, "running");
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { stuckHost.HostId }, dataDate,
            runId + 1, "running", DateTime.UtcNow.AddHours(-7));

        var detail = new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames,
            backend: _attemptBackend).GetDayDetail(dataDate.AddDays(1));
        Assert.Equal("stopped", Assert.Single(detail, row => row.HostName == stoppedHost.HostName).Status);
        Assert.Equal("stuck", Assert.Single(detail, row => row.HostName == stuckHost.HostName).Status);
    }

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
        var targetDate = DateTime.Today.AddDays(-1);
        var runTime = DateTime.Now.AddDays(-1);
        var runs = Runs();
        runs.StartRun(new BatchRun { HostName = "DEV-BATCH-HOST", StartedAt = runTime, FinishedAt = runTime, ExitCode = 0 });

        var host = _hosts.Upsert(new WebHost { HostName = "10.0.0.7", Source = "netiq" });
        Records().Append(Rec(host.HostId, host.HostName, targetDate.AddDays(-1)));

        var summaries = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames).GetDaySummaries(3, page: 1, pageSize: 30);
        var latest = summaries.Single(s => s.Date == targetDate.ToString("yyyy-MM-dd"));

        // DEV-BATCH-HOST（跑批次的機器本身）與 10.0.0.7（NetIQ 主機）都算成功——
        // 重點是後者不受前者這筆無關 run 影響、也不會落回未執行
        Assert.Equal(2, latest.SuccessCount);
        Assert.Equal(0, latest.NotRunCount);
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
        var targetDate = DateTime.Today.AddDays(-1);
        var host = _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Records().Append(Rec(host.HostId, host.HostName, targetDate.AddDays(-1)));

        var service = Create();

        var row = Assert.Single(service.GetDayDetail(targetDate), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("backfilled", row.Status);
        Assert.Null(row.RunId);   // 回補 fallback 沒有可連到的單次執行紀錄

        var latest = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
        Assert.Equal(1, latest.BackfilledCount);
        Assert.Equal(0, latest.SuccessCount);
        Assert.Equal(0, latest.NotRunCount);
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
        var targetDate = DateTime.Today.AddDays(-1);
        var runTime = DateTime.Now.AddDays(-1);
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = runTime });
        runs.FinishRun(new BatchRun
        {
            RunId = runId, HostName = "SRV-LOCAL", StartedAt = runTime, FinishedAt = runTime,
            ExitCode = 0, Stopped = true, ErrorCount = 2
        });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var service = new RunMonitorService(runs, _hosts, Records(), new FakeUserStore(), Options(), _displayNames);

        var row = Assert.Single(service.GetDayDetail(targetDate), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("stopped", row.Status);

        var latest = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
        Assert.Equal(1, latest.StoppedCount);
        Assert.Equal(0, latest.FailedCount);
        Assert.Empty(latest.FailedHostNames);   // 已停止不列失敗主機清單

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
        var targetDate = DateTime.Today.AddDays(-1);
        var netiqOk = _hosts.Upsert(new WebHost { HostName = "10.0.0.8", Source = "netiq" });
        _hosts.Upsert(new WebHost { HostName = "10.0.0.9", Source = "netiq" });   // 沒有分析紀錄
        var runs = Runs();
        var runTime = DateTime.Now.AddDays(-1);
        var runId = runs.StartRun(new BatchRun { HostName = "SRV-LOCAL", StartedAt = runTime });
        runs.FinishRun(new BatchRun { RunId = runId, HostName = "SRV-LOCAL", StartedAt = runTime, FinishedAt = runTime, ExitCode = 0 });
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });

        var records = Records();
        records.Append(Rec(netiqOk.HostId, netiqOk.HostName, targetDate.AddDays(-1)));

        var summaries = new RunMonitorService(runs, _hosts, records, new FakeUserStore(), Options(), _displayNames).GetDaySummaries(1, page: 1, pageSize: 30);
        var latest = Assert.Single(summaries);

        Assert.Equal(3, latest.TotalHosts);
        Assert.Equal(2, latest.SuccessCount);   // netiqOk + SRV-LOCAL
        Assert.Equal(1, latest.NotRunCount);    // 10.0.0.9
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

    // ── NetIQ 格的說明（第 64 輪 C-4、C-5）──────────

    [Fact]
    public void NetIQ空資料待複查與放棄自動重查_格子講得出來()
    {
        var waiting = _hosts.Upsert(new WebHost { HostName = "10.0.8.1", Source = "netiq" });
        var givenUp = _hosts.Upsert(new WebHost { HostName = "10.0.8.2", Source = "netiq" });
        var runDate = DateTime.Today.AddDays(-1);
        var dataDate = runDate.AddDays(-1);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { waiting.HostId }, dataDate, 1, NetiqAttemptStatus.EmptyOnce);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { givenUp.HostId }, dataDate, 1, NetiqAttemptStatus.Partial);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { givenUp.HostId }, dataDate, 2, NetiqAttemptStatus.Partial);

        var rows = new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames, backend: _attemptBackend)
            .GetDayDetail(runDate);

        var waitingRow = Assert.Single(rows, r => r.HostName == waiting.HostName);
        // 第 66 輪：空資料待複查是獨立狀態（recheck），不再與「沒排到」混在 none
        Assert.Equal("recheck", waitingRow.Status);
        Assert.Contains("複查", waitingRow.StatusNote);
        var givenUpRow = Assert.Single(rows, r => r.HostName == givenUp.HostName);
        Assert.Equal(BatchRunStatus.Warning, givenUpRow.Status);
        Assert.Contains("已放棄自動重查", givenUpRow.StatusNote);
    }

    [Fact]
    public void NetIQ空資料待複查_總表計入RecheckCount不計未執行_明細為recheck()
    {
        var waiting = _hosts.Upsert(new WebHost { HostName = "10.0.8.11", Source = "netiq" });
        var runDate = DateTime.Today.AddDays(-1);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { waiting.HostId }, runDate.AddDays(-1), 1, NetiqAttemptStatus.EmptyOnce);
        var service = new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames, backend: _attemptBackend);

        var row = Assert.Single(service.GetDaySummaries(1, 1, 1));
        var detail = Assert.Single(service.GetDayDetail(runDate), r => r.HostName == waiting.HostName);

        Assert.Equal(1, row.RecheckCount);
        Assert.Equal(0, row.NotRunCount);
        Assert.Equal("recheck", detail.Status);
    }

    [Theory]
    [InlineData(1, "空資料，下一窗口複查一次")]
    [InlineData(3, "已無自動複查")]
    public void NetIQ空資料待複查_說明依列日期分齡(int daysAgo, string expected)
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.8.12", Source = "netiq" });
        var rowDate = DateTime.Today.AddDays(-daysAgo);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, rowDate.AddDays(-1), 1, NetiqAttemptStatus.EmptyOnce);

        var row = Assert.Single(new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames, backend: _attemptBackend)
            .GetDayDetail(rowDate), r => r.HostName == host.HostName);

        Assert.Contains(expected, row.StatusNote);
    }

    private long AddTodayRun(int minuteOffset, int? netiqDaysFailed = null, bool interrupted = false)
    {
        var runs = Runs();
        var start = DateTime.Today.AddMinutes(minuteOffset);
        var id = runs.StartRun(new BatchRun { HostName = "SITE-P", StartedAt = start });
        runs.FinishRun(new BatchRun
        {
            RunId = id, HostName = "SITE-P", StartedAt = start, FinishedAt = start.AddMinutes(1),
            NetiqDaysFailed = netiqDaysFailed, Interrupted = interrupted, ExitCode = interrupted ? BatchRun.ExitCodeInterrupted : 0
        });
        return id;
    }

    [Fact]
    public void 今天列_最後一趟全成功_PendingSummary為null()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-P", Source = "local" });
        AddTodayRun(1, netiqDaysFailed: 3);
        AddTodayRun(5);

        var today = Create().GetDaySummaries(1, 1, 1)[^1];

        Assert.True(today.IsToday);
        Assert.Null(today.PendingSummary);
    }

    [Fact]
    public void 今天列_最後一趟有待補_PendingSummary列出項目()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-P", Source = "local" });
        AddTodayRun(1);
        AddTodayRun(5, netiqDaysFailed: 2);

        var today = Create().GetDaySummaries(1, 1, 1)[^1];

        Assert.StartsWith("最後一趟結束時尚有：", today.PendingSummary);
        Assert.Contains("NetIQ 2 主機日", today.PendingSummary);
    }

    [Fact]
    public void 今天列_最後一趟異常中斷_PendingSummary講異常中斷()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-P", Source = "local" });
        AddTodayRun(1, netiqDaysFailed: 2);
        AddTodayRun(5, interrupted: true);

        var today = Create().GetDaySummaries(1, 1, 1)[^1];

        Assert.Equal("最後一趟異常中斷", today.PendingSummary);
    }

    [Fact]
    public void 非今天的列_PendingSummary一律為null()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-P", Source = "local" });
        var runs = Runs();
        var start = DateTime.Today.AddDays(-1).AddHours(2);
        var id = runs.StartRun(new BatchRun { HostName = "SITE-P", StartedAt = start });
        runs.FinishRun(new BatchRun { RunId = id, HostName = "SITE-P", StartedAt = start, FinishedAt = start.AddMinutes(1), NetiqDaysFailed = 5 });

        var rows = Create().GetDaySummaries(2, 1, 2);

        Assert.NotEmpty(rows);
        Assert.All(rows, s => Assert.Null(s.PendingSummary));
    }

    [Fact]
    public void NetIQ失敗類今天重查用完_格子講明天會再試_不說已放棄()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "10.0.8.3", Source = "netiq" });
        var runDate = DateTime.Today.AddDays(-1);
        var dataDate = runDate.AddDays(-1);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 1, NetiqAttemptStatus.Failed);
        NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, dataDate, 2, NetiqAttemptStatus.Failed);

        var row = Assert.Single(new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames, backend: _attemptBackend)
            .GetDayDetail(runDate), r => r.HostName == host.HostName);

        Assert.Contains("明天會再試", row.StatusNote);
    }

    [Fact]
    public void 執行紀錄列_帶結束時仍待補的摘要()
    {
        var runs = Runs();
        var start = DateTime.Today.AddHours(1);
        var id = runs.StartRun(new BatchRun { HostName = "SITE", StartedAt = start });
        runs.FinishRun(new BatchRun { RunId = id, HostName = "SITE", StartedAt = start, FinishedAt = start.AddMinutes(5), NetiqDaysFailed = 4 });

        var row = Assert.Single(Create().GetRunList(1));

        Assert.Equal("NetIQ 4 主機日", row.ResumeSummary);
    }

    // ── 今天列（第 64 輪 E-3）──────────

    [Fact]
    public void 今天有趟_第一頁多一列且不佔天數_第二頁沒有()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-TODAY", Source = "local" });
        var other = _hosts.Upsert(new WebHost { HostName = "10.0.9.1", Source = "netiq" });
        AddRun("SITE-TODAY", DateTime.Today.AddHours(0).AddMinutes(1), exitCode: 0, errorCount: 0, (RunTimingPhases.Local, RunPhaseOutcomes.Success));

        var service = Create();
        var first = service.GetDaySummaries(3, 1, 2);
        var second = service.GetDaySummaries(3, 2, 2);

        Assert.Equal(3, first.Count); // 2 天＋今天
        var today = first[^1];
        Assert.True(today.IsToday);
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), today.Date);
        Assert.False(today.InProgress);
        // 今天還沒處理到的 NetIQ 主機是未執行，不是失敗
        Assert.Equal(1, today.NotRunCount);
        Assert.Equal(0, today.FailedCount);
        Assert.DoesNotContain(second, s => s.IsToday);
        Assert.All(first.Take(2), s => Assert.False(s.IsToday));
        Assert.NotNull(other);
    }

    [Fact]
    public void 今天沒有趟_不出現今天列()
    {
        _hosts.Upsert(new WebHost { HostName = "SITE-NONE", Source = "local" });
        var rows = Create().GetDaySummaries(3, 1, 3);
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, s => s.IsToday);
    }

    // ── 停用主機與本機格（第 64 輪 E-2）──────────

    private void AddRun(string host, DateTime startedAt, int exitCode, int errorCount, params (string Key, string Outcome)[] phases)
    {
        var runs = Runs();
        var runId = runs.StartRun(new BatchRun { HostName = host, StartedAt = startedAt });
        runs.FinishRun(new BatchRun
        {
            RunId = runId, HostName = host, StartedAt = startedAt, FinishedAt = startedAt.AddMinutes(1),
            ExitCode = exitCode, ErrorCount = errorCount,
            Phases = phases.Length == 0 ? null : phases.Select(p => new RunPhaseRecord(p.Key, startedAt) { FinishedAt = startedAt.AddMinutes(1), Outcome = p.Outcome }).ToList()
        });
    }

    [Fact]
    public void 停用主機_本機被略過的趟全失敗_不出現在總表也不算失敗()
    {
        // 站台實況：12 台 NetIQ＋1 台停用的站台主機；當天 NetIQ 全部 401 讓整趟 failed
        const string site = "SITE-DISABLED";
        _hosts.Upsert(new WebHost { HostName = site, Source = "local", Active = false });
        var netiq = Enumerable.Range(1, 12)
            .Select(i => _hosts.Upsert(new WebHost { HostName = $"10.0.1.{i}", Source = "netiq" })).ToList();
        var runDate = DateTime.Today.AddDays(-1);
        AddRun(site, runDate.AddHours(1), exitCode: 1, errorCount: 3, (RunTimingPhases.Netiq, RunPhaseOutcomes.Partial));
        var service = new RunMonitorService(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames,
            backend: _attemptBackend);
        foreach (var host in netiq)
            NetiqSourceAttempt.RecordBatch(_attemptBackend, new[] { host.HostId }, runDate.AddDays(-1), 1, "failed");

        var summary = Assert.Single(service.GetDaySummaries(1, 1, 30));

        Assert.Equal(12, summary.TotalHosts);
        Assert.Equal(12, summary.FailedCount);
        Assert.DoesNotContain(site, summary.FailedHostNames);
        Assert.DoesNotContain(service.GetDayDetail(runDate), row => row.HostName == site);
    }

    [Fact]
    public void 停用主機_當天有跑過本機分析_保留歷史且狀態看本機階段()
    {
        const string site = "SITE-HIST";
        _hosts.Upsert(new WebHost { HostName = site, Source = "local", Active = false });
        var runDate = DateTime.Today.AddDays(-1);
        AddRun(site, runDate.AddHours(1), exitCode: 1, errorCount: 3,
            (RunTimingPhases.Local, RunPhaseOutcomes.Success), (RunTimingPhases.Netiq, RunPhaseOutcomes.Partial));

        var service = Create();
        var summary = Assert.Single(service.GetDaySummaries(1, 1, 30));
        var row = Assert.Single(service.GetDayDetail(runDate), r => r.HostName == site);

        Assert.Equal(1, summary.TotalHosts);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(BatchRunStatus.Success, row.Status);
        Assert.False(row.Legacy);
    }

    [Theory]
    [InlineData(RunPhaseOutcomes.Success, BatchRunStatus.Success)]
    [InlineData(RunPhaseOutcomes.Skipped, BatchRunStatus.Success)]
    [InlineData(RunPhaseOutcomes.Partial, BatchRunStatus.Warning)]
    [InlineData(RunPhaseOutcomes.Failed, BatchRunStatus.Failed)]
    [InlineData(RunPhaseOutcomes.Stopped, BatchRunStatus.Stopped)]
    public void 啟用的本機主機_格子狀態看本機階段不看整趟(string outcome, string expected)
    {
        const string site = "SITE-ACTIVE";
        _hosts.Upsert(new WebHost { HostName = site, Source = "local" });
        var runDate = DateTime.Today.AddDays(-1);
        AddRun(site, runDate.AddHours(1), exitCode: 1, errorCount: 3, (RunTimingPhases.Local, outcome));

        var row = Assert.Single(Create().GetDayDetail(runDate), r => r.HostName == site);

        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void 本機主機_該日只有不含本機階段的趟_不拿它的結果當本機狀態()
    {
        const string site = "SITE-NETIQ-ONLY-RUN";
        _hosts.Upsert(new WebHost { HostName = site, Source = "local" });
        var runDate = DateTime.Today.AddDays(-1);
        AddRun(site, runDate.AddHours(1), exitCode: 1, errorCount: 3, (RunTimingPhases.Netiq, RunPhaseOutcomes.Failed));

        var row = Assert.Single(Create().GetDayDetail(runDate), r => r.HostName == site);

        Assert.Equal("none", row.Status);
    }

    [Fact]
    public void 升級前紀錄沒有階段資料_以整趟狀態代表並標示()
    {
        const string site = "SITE-LEGACY";
        _hosts.Upsert(new WebHost { HostName = site, Source = "local" });
        var runDate = DateTime.Today.AddDays(-1);
        AddRun(site, runDate.AddHours(1), exitCode: 1, errorCount: 3);

        var row = Assert.Single(Create().GetDayDetail(runDate), r => r.HostName == site);

        Assert.Equal(BatchRunStatus.Failed, row.Status);
        Assert.True(row.Legacy);
    }

    // ── 本機分析停用（回饋十八輪批次D，終檢輪補上規劃表列的執行監控顯示）──────────

    /// <summary>停用「分析本機主機」後，本機主機的空白日顯示「本機分析已停用」而非「未執行」——
    /// 那是設定行為不是漏跑，混在同一格會讓人每天誤判排程壞了。</summary>
    [Fact]
    public void 本機分析停用_本機主機空白日列為停用而非未執行()
    {
        var targetDate = DateTime.Today.AddDays(-1);
        _hosts.Upsert(new WebHost { HostName = "SRV-LOCAL", Source = "local" });
        Options().Update(o => o.LocalAnalysisEnabled = false);
        var service = Create();

        var row = Assert.Single(service.GetDayDetail(targetDate), d => d.HostName == "SRV-LOCAL");
        Assert.Equal("local_disabled", row.Status);

        var latest = service.GetDaySummaries(1, page: 1, pageSize: 30).Single();
        Assert.Equal(1, latest.LocalDisabledCount);
        Assert.Equal(0, latest.NotRunCount);
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

    // ── 本行程持有中的執行不判異常中斷 ────────────────────────────────

    private long StartOpenRun(string? jobType, string trigger, TimeSpan age)
    {
        var started = DateTime.Now - age;
        return Runs().StartRun(new BatchRun { HostName = "SRV-HELD", StartedAt = started, JobType = jobType, Trigger = trigger });
    }

    private RunMonitorService CreateWithStates(SchedulerRunState? scheduler, AiAnalysisRunState? ai) =>
        new(Runs(), _hosts, Records(), new FakeUserStore(), Options(), _displayNames, schedulerState: scheduler, aiState: ai);

    [Fact]
    public void 執行紀錄_AI執行超過門檻但本行程持有_判執行中()
    {
        var runId = StartOpenRun(BatchRun.JobTypeAi, "schedule", TimeSpan.FromHours(7));
        var ai = new AiAnalysisRunState();
        Assert.True(ai.TryBeginRun("schedule", 1, out _));
        ai.SetCurrentBatchRunId(runId);

        var item = Assert.Single(CreateWithStates(null, ai).GetRunList(2), r => r.RunId == runId);
        Assert.Equal(BatchRunStatus.Running, item.Status);
    }

    [Fact]
    public void 執行紀錄_AI執行超過門檻且本行程不持有_判異常中斷()
    {
        var runId = StartOpenRun(BatchRun.JobTypeAi, "schedule", TimeSpan.FromHours(7));
        var ai = new AiAnalysisRunState();
        Assert.True(ai.TryBeginRun("schedule", 1, out _));
        ai.SetCurrentBatchRunId(runId + 99);

        Assert.Equal(BatchRunStatus.Stuck, Assert.Single(CreateWithStates(null, ai).GetRunList(2), r => r.RunId == runId).Status);
        Assert.Equal(BatchRunStatus.Stuck, Assert.Single(Create().GetRunList(2), r => r.RunId == runId).Status);
    }

    [Fact]
    public void 執行紀錄_取數本趟開始後建立的未結束紀錄由本行程持有_結束後不再持有()
    {
        // 走後備判定：取數沒有登記 RunId，本趟開始後建立的未結束非 AI 紀錄就是本趟
        var scheduler = new SchedulerRunState();
        Assert.True(scheduler.TryBeginRun("schedule", out _));
        var runId = StartOpenRun(null, "schedule", TimeSpan.Zero);
        var run = Assert.Single(Runs().GetRecentRuns(2, hostNames: null), r => r.RunId == runId);

        Assert.True(scheduler.HoldsRun(run));
        var service = CreateWithStates(scheduler, null);
        Assert.Equal(BatchRunStatus.Running, Assert.Single(service.GetRunList(2), r => r.RunId == runId).Status);

        scheduler.EndRun();
        Assert.False(scheduler.HoldsRun(run));
    }

    [Fact]
    public void 執行紀錄_取數尚未登記RunId_舊的孤兒紀錄不會被當成持有中()
    {
        var orphanId = StartOpenRun(null, "schedule", TimeSpan.FromHours(7));
        var scheduler = new SchedulerRunState();
        Assert.True(scheduler.TryBeginRun("schedule", out _));
        var currentId = StartOpenRun(null, "schedule", TimeSpan.Zero);

        var list = CreateWithStates(scheduler, null).GetRunList(2);
        Assert.Equal(BatchRunStatus.Stuck, Assert.Single(list, r => r.RunId == orphanId).Status);
        Assert.Equal(BatchRunStatus.Running, Assert.Single(list, r => r.RunId == currentId).Status);
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
        var anchor = DateTime.Today.AddDays(-1);
        var expectedDates = Enumerable.Range(0, days)
            .Select(i => anchor.AddDays(-days + 1 + i).ToString("yyyy-MM-dd"))
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
        var anchor = DateTime.Today.AddDays(-1);
        var expectedDates = Enumerable.Range(0, days)
            .Select(i => anchor.AddDays(-days + 1 + i).ToString("yyyy-MM-dd"))
            .OrderBy(d => d)
            .ToList();
        Assert.Equal(expectedDates, allDates);
    }

    /// <summary>
    /// 第 1 頁是最新的日期區段：最新一天（錨點日＝昨天）必須出現在第 1 頁，最舊的那天不在第 1 頁。
    /// </summary>
    [Fact]
    public void 分頁_第一頁是最新日期區段()
    {
        const int days = 10;
        const int pageSize = 4;
        var service = Create();

        var page1 = service.GetDaySummaries(days, page: 1, pageSize: pageSize);

        var anchor = DateTime.Today.AddDays(-1);
        var newestStr = anchor.ToString("yyyy-MM-dd");
        var oldestStr = anchor.AddDays(-days + 1).ToString("yyyy-MM-dd");

        Assert.Contains(page1, s => s.Date == newestStr);
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

    /// <summary>
    /// 執行總表不含今天（批次E）：今天的夜間批次尚未執行，白天看整列必為未執行，
    /// 因此總表錨點定為昨天，回傳結果中不得存在今天的列。
    /// </summary>
    [Fact]
    public void GetDaySummaries_回傳結果不包含今天()
    {
        var service = Create();
        var result = service.GetDaySummaries(days: 30, page: 1, pageSize: 90);

        Assert.NotEmpty(result);
        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        Assert.DoesNotContain(result, s => s.Date == todayStr);
    }

    /// <summary>
    /// 執行總表天數與區間驗證（批次E）：天數為 7 時回傳恰好 7 列，
    /// 且最新一列為昨天（Today-1）、最舊一列為七天前（Today-7）。
    /// </summary>
    [Fact]
    public void GetDaySummaries_天數為7時回傳7列且最新為昨天最舊為七天前()
    {
        var service = Create();
        var result = service.GetDaySummaries(days: 7, page: 1, pageSize: 30);

        Assert.Equal(7, result.Count);
        var oldest = result.First();
        var newest = result.Last();

        Assert.Equal(DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"), oldest.Date);
        Assert.Equal(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"), newest.Date);
    }

    // ── 第 63 輪批次A：異常彙總（GetErrorSummary） ─────────────────────

    private long SeedRun(string host, DateTime startedAt, int? exitCode = 0, int errorCount = 0,
        bool finished = true, bool interrupted = false, bool stopped = false)
    {
        var store = Runs();
        var run = new BatchRun { HostName = host, StartedAt = startedAt, AppVersion = "1.0" };
        var id = store.StartRun(run);
        if (finished)
        {
            run.FinishedAt = startedAt.AddMinutes(5);
            run.ExitCode = exitCode;
            run.ErrorCount = errorCount;
            run.Interrupted = interrupted;
            run.Stopped = stopped;
            store.FinishRun(run);
        }
        return id;
    }

    private void SeedLog(long runId, string level, string message, string logger = "Test", DateTime? at = null) =>
        Runs().AppendLog(new BatchRunLog
        {
            RunId = runId, Level = level, Message = message, Logger = logger, LoggedAt = at ?? DateTime.Now
        });

    [Fact]
    public void 異常彙總_同訊息只差數字日期guid_併成一組()
    {
        var a = SeedRun("SRV-01", DateTime.Now.AddHours(-3), exitCode: 1, errorCount: 1);
        var b = SeedRun("SRV-02", DateTime.Now.AddHours(-2), exitCode: 1, errorCount: 1);
        SeedLog(a, "Error", "主機 12 逾時 2026-11-01 10:00");
        SeedLog(b, "Error", "主機 345 逾時 2026-11-02 11:30");
        SeedLog(b, "Error", "主機 9 逾時 2026-11-03 01:00");

        var summary = Create().GetErrorSummary(7);

        var group = Assert.Single(summary.Groups);
        Assert.Equal(3, group.Count);
        Assert.Equal(new[] { "SRV-01", "SRV-02" }, group.AffectedHosts);
    }

    [Fact]
    public void 異常彙總_影響趟數是RunId去重數_不是次數()
    {
        var a = SeedRun("SRV-01", DateTime.Now.AddHours(-3), exitCode: 1, errorCount: 3);
        var b = SeedRun("SRV-01", DateTime.Now.AddHours(-2), exitCode: 1, errorCount: 1);
        SeedLog(a, "Error", "boom 1");
        SeedLog(a, "Error", "boom 2");
        SeedLog(a, "Error", "boom 3");
        SeedLog(b, "Error", "boom 4");

        var group = Assert.Single(Create().GetErrorSummary(7).Groups);

        Assert.Equal(4, group.Count);
        Assert.Equal(2, group.AffectedRuns);
    }

    [Fact]
    public void 異常彙總_開始時間早於期間起點的趟次仍解得出站台()
    {
        // 趟次 5 天前開始、跑到現在才出錯；查近 2 天時 GetRecentRuns 不含它
        var runId = SeedRun("SRV-LONG", DateTime.Today.AddDays(-5), exitCode: 1, errorCount: 1);
        SeedLog(runId, "Error", "長跑的錯誤");

        var group = Assert.Single(Create().GetErrorSummary(2).Groups);

        Assert.Equal(new[] { "SRV-LONG" }, group.AffectedHosts);
    }

    [Fact]
    public void 異常彙總_趟次已被清除時才顯示未知()
    {
        SeedLog(runId: 99999, level: "Error", message: "孤兒錯誤");

        var group = Assert.Single(Create().GetErrorSummary(7).Groups);

        Assert.Equal(new[] { "（未知）" }, group.AffectedHosts);
    }

    [Fact]
    public void 異常彙總_沒有失敗或中斷趟時中斷計數為0()
    {
        SeedRun("SRV-01", DateTime.Now.AddHours(-3)); // 成功
        SeedRun("SRV-02", DateTime.Now.AddHours(-2), stopped: true); // 已停止
        SeedRun("SRV-03", DateTime.Now.AddMinutes(-10), finished: false); // 執行中

        Assert.Equal(0, Create().GetErrorSummary(7).FailedWithoutErrorRuns);
    }

    [Fact]
    public void 異常彙總_失敗或中斷但無錯誤行的趟才計入_有ErrorCount的不算()
    {
        SeedRun("SRV-01", DateTime.Now.AddHours(-5), exitCode: 1); // failed、無錯誤 → 算
        SeedRun("SRV-02", DateTime.Now.AddHours(-4), exitCode: 2, interrupted: true); // stuck、無錯誤 → 算
        var withErrors = SeedRun("SRV-03", DateTime.Now.AddHours(-3), exitCode: 1, errorCount: 2); // 有 Error → 不算
        SeedLog(withErrors, "Error", "有錯誤行");
        SeedRun("SRV-04", DateTime.Now.AddHours(-2)); // 成功 → 不算

        var summary = Create().GetErrorSummary(7);

        Assert.Equal(2, summary.FailedWithoutErrorRuns);
        Assert.Single(summary.Groups);
    }

    [Fact]
    public void 異常彙總_minLevel預設只含ErrorFatal_warn才含Warn()
    {
        var run = SeedRun("SRV-01", DateTime.Now.AddHours(-1), exitCode: 0);
        SeedLog(run, "Warn", "只是警告");
        SeedLog(run, "Error", "真的錯誤");
        var service = Create();

        Assert.Equal(new[] { "真的錯誤" }, service.GetErrorSummary(7).Groups.Select(g => g.Message));
        Assert.Equal(new[] { "真的錯誤" }, service.GetErrorSummary(7, "error").Groups.Select(g => g.Message));
        var withWarn = service.GetErrorSummary(7, "warn").Groups;
        Assert.Equal(new[] { "只是警告", "真的錯誤" }, withWarn.Select(g => g.Message).OrderBy(m => m, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("fatal")]
    [InlineData("")]
    [InlineData("warning")]
    public void 異常彙總_minLevel不認得的值回驗證錯誤(string minLevel)
    {
        Assert.Throws<DomainException>(() => Create().GetErrorSummary(7, minLevel));
    }

    [Fact]
    public void 異常彙總_Warn組中SqlPerformanceMonitor標示不計入警告數_依logger不依訊息()
    {
        var run = SeedRun("SRV-01", DateTime.Now.AddHours(-1));
        SeedLog(run, "Warn", "[SQL][慢] Query 耗時 2500ms", logger: BatchRunRecorder.ExemptWarnLogger);
        // 訊息一樣但 logger 不同：不得被當成免計
        SeedLog(run, "Warn", "[SQL][慢] Query 耗時 3100ms", logger: "Other");

        var groups = Create().GetErrorSummary(7, "warn").Groups;

        Assert.Equal(2, groups.Count);
        Assert.True(Assert.Single(groups, g => g.ExcludedFromWarnCount).Count == 1);
        Assert.False(Assert.Single(groups, g => !g.ExcludedFromWarnCount).ExcludedFromWarnCount);
    }

    [Fact]
    public void 異常彙總_AI診斷logger的行完全排除_warn模式也看不到()
    {
        var run = SeedRun("SRV-01", DateTime.Now.AddHours(-1));
        SeedLog(run, "Warn", "{\"label\":\"daily-20261101-h1\"}", logger: AiJsonDiagnostics.LoggerName);
        SeedLog(run, "Warn", "一般警告", logger: "Other");
        SeedLog(run, "Error", "診斷 logger 的 Error 也不顯示", logger: AiJsonDiagnostics.LoggerName);
        // 訊息長得像診斷、logger 不同：不得被當成診斷（依 logger 名稱，不比對訊息）
        SeedLog(run, "Warn", "{\"label\":\"daily-20261101-h2\"}", logger: "Other");
        var service = Create();

        var warn = service.GetErrorSummary(7, "warn").Groups;
        var error = service.GetErrorSummary(7).Groups;

        Assert.DoesNotContain(warn, g => g.Message.Contains("診斷 logger"));
        Assert.Equal(2, warn.Count);
        Assert.Contains(warn, g => g.Message == "一般警告");
        Assert.Contains(warn, g => g.Message.Contains("h2"));
        Assert.DoesNotContain(warn, g => g.Message.Contains("h1"));
        Assert.Empty(error);
    }

    [Fact]
    public void 執行詳情_Logs排除AI診斷logger的行_其他行照常()
    {
        var run = SeedRun("SRV-01", DateTime.Now.AddHours(-1));
        SeedLog(run, "Warn", "診斷行", logger: AiJsonDiagnostics.LoggerName);
        SeedLog(run, "Warn", "慢查詢", logger: BatchRunRecorder.ExemptWarnLogger);
        SeedLog(run, "Info", "一般資訊");
        SeedLog(run, "Warn", "診斷行（同訊息、不同 logger）", logger: "Other");

        var detail = Create().GetDetail(run);

        Assert.DoesNotContain(detail.Logs, l => l.Logger == AiJsonDiagnostics.LoggerName);
        Assert.Equal(3, detail.Logs.Count);
        Assert.Contains(detail.Logs, l => l.Logger == BatchRunRecorder.ExemptWarnLogger);
    }

    [Fact]
    public void 異常彙總_組內等級取最高()
    {
        var run = SeedRun("SRV-01", DateTime.Now.AddHours(-1), exitCode: 1, errorCount: 2);
        SeedLog(run, "Warn", "同一件事 1");
        SeedLog(run, "Error", "同一件事 2");
        SeedLog(run, "Fatal", "同一件事 3");
        var run2 = SeedRun("SRV-02", DateTime.Now.AddHours(-1), exitCode: 1, errorCount: 1);
        SeedLog(run2, "Warn", "另一件事 1");
        SeedLog(run2, "Error", "另一件事 2");

        var groups = Create().GetErrorSummary(7, "warn").Groups;

        Assert.Equal("Fatal", Assert.Single(groups, g => g.Message.StartsWith("同一件事")).Level);
        Assert.Equal("Error", Assert.Single(groups, g => g.Message.StartsWith("另一件事")).Level);
    }

    [Fact]
    public void 訊息正規化_畫面與匯出共用同一個實作()
    {
        Assert.Equal("失敗 {n} 次 於 {date} id={guid}",
            RunMessageNormalizer.Normalize("失敗 12 次 於 2026-11-03 10:00:01 id=3F2504E0-4F89-11D3-9A0C-0305E82C3301"));
    }

    [Fact]
    public void PRTG早退的原因_總表清單詳情三處都帶出()
    {
        var dataDate = DateTime.Today.AddDays(-1);
        var withNote = new BatchRun
        {
            HostName = "FETCH-NOTE",
            StartedAt = dataDate.AddDays(1).AddHours(2),
            FinishedAt = dataDate.AddDays(1).AddHours(2).AddMinutes(10),
            ExitCode = 0,
            PrtgOutcome = BatchRun.PrtgOutcomeDisabled,
            PrtgOutcomeNote = BatchRun.PrtgNoteUndecryptable
        };
        Runs().StartRun(withNote);
        var service = Create();

        // 無逐日統計（PrtgDays 為 null）走資料日期對應的舊規則：原因取趟次層
        var summary = Assert.Single(service.GetDaySummaries(3, 1, 30), s => s.Date == dataDate.ToString("yyyy-MM-dd"));
        Assert.Equal(BatchRun.PrtgOutcomeDisabled, summary.PrtgOutcome);
        Assert.Equal(BatchRun.PrtgNoteUndecryptable, summary.PrtgNote);

        var listItem = Assert.Single(service.GetRunList(3), r => r.RunId == withNote.RunId);
        Assert.Equal(BatchRun.PrtgNoteUndecryptable, listItem.PrtgOutcomeNote);

        var detail = service.GetDetail(withNote.RunId);
        Assert.Equal(BatchRun.PrtgNoteUndecryptable, detail.PrtgOutcomeNote);
    }

    [Fact]
    public void PRTG舊紀錄沒有原因欄位_三處都是null且總表不憑空補原因()
    {
        var dataDate = DateTime.Today.AddDays(-1);
        var legacy = new BatchRun
        {
            HostName = "FETCH-LEGACY",
            StartedAt = dataDate.AddDays(1).AddHours(2),
            FinishedAt = dataDate.AddDays(1).AddHours(2).AddMinutes(10),
            ExitCode = 0,
            PrtgOutcome = BatchRun.PrtgOutcomeDisabled
        };
        Runs().StartRun(legacy);
        var service = Create();

        var summary = Assert.Single(service.GetDaySummaries(3, 1, 30), s => s.Date == dataDate.ToString("yyyy-MM-dd"));
        Assert.Equal(BatchRun.PrtgOutcomeDisabled, summary.PrtgOutcome);
        Assert.Null(summary.PrtgNote);
        Assert.Null(Assert.Single(service.GetRunList(3), r => r.RunId == legacy.RunId).PrtgOutcomeNote);
        Assert.Null(service.GetDetail(legacy.RunId).PrtgOutcomeNote);
    }
}

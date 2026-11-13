using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using NLog;
using NLog.Config;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 67 輪 A-2：執行紀錄記憶體里程碑、OOM 可辨識、診斷匯出記憶體區塊、GC 啟動 log。</summary>
public class RunMemoryMarkTests
{
    private static MemorySnapshot Snapshot() => MemorySnapshot.Capture(
        () => (1024L * 1024 * 10, 1024L * 1024 * 20),
        () => 3);

    [Fact]
    public void Recorder_開始與結束里程碑寫進最終列_診斷行兩筆_階段數不變()
    {
        using var fx = new EfSqliteFixture();
        LogManager.Configuration ??= new LoggingConfiguration();
        var store = new BatchRunStore(fx.LogStore("runs"), fx.LogStore("run_logs"));

        long withMemory, without;
        using (var recorder = new BatchRunRecorder(store, "H", Array.Empty<string>()))
        {
            withMemory = recorder.RunId;
            recorder.RecordMemory(true, Snapshot());
            recorder.Phase(RunTimingPhases.Local).Complete(RunPhaseOutcomes.Success);
            recorder.RecordMemory(false, Snapshot());
            recorder.Finish(0);
        }

        using (var recorder = new BatchRunRecorder(store, "H", Array.Empty<string>()))
        {
            without = recorder.RunId;
            recorder.Phase(RunTimingPhases.Local).Complete(RunPhaseOutcomes.Success);
            recorder.Finish(0);
        }

        var run = store.GetRun(withMemory)!;
        Assert.NotNull(run.MemoryStart);
        Assert.NotNull(run.MemoryEnd);
        Assert.True(run.MemoryEnd!.At >= run.MemoryStart!.At);
        Assert.Equal(20L * 1024 * 1024, run.MemoryStart.SqliteHeapHighwaterBytes);
        Assert.Equal(3, run.MemoryStart.NlogRuleCount);

        var memoryLogs = store.GetLogs(withMemory).Where(l => l.Logger == "Milestone" && l.Message.StartsWith("記憶體（")).ToList();
        Assert.Equal(2, memoryLogs.Count);
        Assert.StartsWith("記憶體（開始）", memoryLogs[0].Message);
        Assert.StartsWith("記憶體（結束）", memoryLogs[1].Message);
        Assert.Contains("SQLite 峰值 20 MB", memoryLogs[0].Message);

        Assert.Equal(store.GetRun(without)!.Phases!.Count, run.Phases!.Count);
        Assert.Null(store.GetRun(without)!.MemoryStart);
    }

    [Fact]
    public void Recorder_無store時RecordMemory直接返回不擲例外()
    {
        using var recorder = new BatchRunRecorder(null, "H", Array.Empty<string>());
        recorder.RecordMemory(true, Snapshot());
        recorder.Finish(0);
    }

    [Fact]
    public void 舊紀錄JSON沒有記憶體欄位_反序列化為null()
    {
        const string json = """{"runId":1,"hostName":"H","startedAt":"2026-01-01T00:00:00","exitCode":0}""";
        var run = JsonSerializer.Deserialize<BatchRun>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(run);
        Assert.Null(run!.MemoryStart);
        Assert.Null(run.MemoryEnd);
    }

    [Fact]
    public void RunFailureReasons_OOM回記憶體上限_一般例外回Message()
    {
        Assert.Contains("記憶體上限", RunFailureReasons.Describe(new OutOfMemoryException()));
        Assert.Equal("一般錯誤", RunFailureReasons.Describe(new InvalidOperationException("一般錯誤")));
    }

    [Fact]
    public void RunFailureReasons_內層或彙總例外裡的記憶體不足也辨識()
    {
        Assert.Contains("記憶體上限", RunFailureReasons.Describe(new InvalidOperationException("外層", new OutOfMemoryException())));
        Assert.Contains("記憶體上限", RunFailureReasons.Describe(new AggregateException(new TimeoutException(), new OutOfMemoryException())));
        Assert.Equal("一般", RunFailureReasons.Describe(new InvalidOperationException("一般", new TimeoutException())));
    }

    [Fact]
    public void DescribeGcStartup_沒設硬上限時不把實體記憶體標成硬上限()
    {
        var text = GcStartupInfo.DescribeGcStartup(new Dictionary<string, object> { ["GCConserveMem"] = 5 }, 1048576000);
        Assert.DoesNotContain("GCHeapHardLimit=", text);
        Assert.Contains("未設硬上限", text);
    }

    [Fact]
    public void GC硬上限百分比_runtime未設時值為0_視為未設定()
    {
        // 實測 .NET 8：未設 HeapHardLimitPercent 時 GC.GetConfigurationVariables() 仍回這個鍵、值為 0
        var unset = new Dictionary<string, object> { ["GCHeapHardLimitPercent"] = 0L, ["GCConserveMem"] = 0L };
        Assert.Null(MemorySnapshot.ReadPositiveLong(unset, "GCHeapHardLimitPercent"));
        Assert.Equal(60L, MemorySnapshot.ReadPositiveLong(new Dictionary<string, object> { ["GCHeapHardLimitPercent"] = 60L }, "GCHeapHardLimitPercent"));

        var text = GcStartupInfo.DescribeGcStartup(unset, 24_000L * 1048576);
        Assert.Contains("GCHeapHardLimitPercent=未設定", text);
        Assert.Contains("未設硬上限", text);
        Assert.DoesNotContain("GCHeapHardLimit=", text);
    }

    [Fact]
    public void Recorder_開始標記在任何階段結束前就已落地()
    {
        using var fx = new EfSqliteFixture();
        var store = new BatchRunStore(fx.LogStore("runs2"), fx.LogStore("run_logs2"));
        using var recorder = new BatchRunRecorder(store, "H", Array.Empty<string>());
        var phase = recorder.Phase(RunTimingPhases.Local); // 開著不結束：模擬第一個階段途中行程被殺

        recorder.RecordMemory(true, Snapshot());

        // 站台重啟補「異常中斷」列時沿用最後一列：開始標記必須已經在資料列上
        var run = Assert.Single(store.GetRecentRuns(1, null));
        Assert.NotNull(run.MemoryStart);
        Assert.Null(run.MemoryEnd);
        phase.Complete(RunPhaseOutcomes.Success);
    }

    [Fact]
    public void DescribeGcStartup_有值與未設定()
    {
        var dict = new Dictionary<string, object> { ["GCConserveMem"] = 5, ["GCHeapHardLimitPercent"] = 60L };
        var text = GcStartupInfo.DescribeGcStartup(dict, 1048576000);
        Assert.Contains("GCConserveMem=5", text);
        Assert.Contains("GCHeapHardLimitPercent=60", text);
        Assert.Contains("1000 MB", text);
        Assert.Contains("ServerGC=未設定", text);

        var empty = GcStartupInfo.DescribeGcStartup(new Dictionary<string, object>(), null);
        Assert.Contains("未設定", empty);
        Assert.DoesNotContain("MB", empty);
    }

    [Fact]
    public void 匯出_meta記憶體帶出注入的SQLite堆峰值()
    {
        using var h = new RunExportHarness();
        var service = new RunExportService(h.Runs, h.Monitor, h.Settings, h.Schedule, h.Hosts, h.Records,
            () => h.Factory.Create(), sqliteHeap: () => (11L, 22L));
        var result = service.Build(14, CancellationToken.None);
        using var doc = System.Text.Json.JsonDocument.Parse(result.Bytes);
        var memory = doc.RootElement.GetProperty("meta").GetProperty("memory");
        Assert.Equal(22L, memory.GetProperty("sqliteHeapHighwaterBytes").GetInt64());
    }

    [Fact]
    public void 匯出_期間內帶記憶體的趟進runMemory_meta含memory與GC設定_SchemaVersion為目前版號()
    {
        using var h = new RunExportHarness();
        var at = RunExportHarness.Y.AddHours(1);
        var start = new RunMemoryMark(at, 100L << 20, 120L << 20, 50L << 20, 80L << 20, 10L << 20, 1000L << 20, 30L << 20, 4);
        var end = start with { At = at.AddHours(2), WorkingSetBytes = 200L << 20 };
        h.AddRun("SRV-MEM", at, r => { r.MemoryStart = start; r.MemoryEnd = end; });
        h.AddRun("SRV-PLAIN", at.AddMinutes(10));

        var (root, _, doc) = h.Export();
        using (doc)
        {
            Assert.Equal(RunExportService.SchemaVersion, root.G("meta").G("schemaVersion").GetInt32());
            var memory = root.G("meta").G("memory");
            Assert.True(memory.TryGetProperty("isServerGc", out _));
            Assert.True(memory.TryGetProperty("gcConserveMemory", out _));
            Assert.True(memory.TryGetProperty("gcHeapHardLimitPercent", out _));

            var runMemory = root.G("runMemory").L();
            var item = Assert.Single(runMemory);
            Assert.Equal("fetch", item.S("jobType"));
            Assert.Equal(100L << 20, item.G("start").G("workingSetBytes").GetInt64());
            Assert.Equal(200L << 20, item.G("end").G("workingSetBytes").GetInt64());
        }
    }

    [Fact]
    public void 執行詳情_帶出記憶體里程碑()
    {
        using var h = new RunExportHarness();
        var at = RunExportHarness.Y.AddHours(1);
        var start = new RunMemoryMark(at, 100L << 20, null, null, null, null, null, null, null);
        var run = h.AddRun("SRV-MEM", at, r => r.MemoryStart = start);

        var detail = h.Monitor.GetDetail(run.RunId);
        Assert.NotNull(detail.MemoryStart);
        Assert.Equal(100L << 20, detail.MemoryStart!.WorkingSetBytes);
        Assert.Null(detail.MemoryEnd);
    }
}

using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 4（PRTG 每日評估與快照的重複載入）共用的測試台：真實 SQLite 後端＋SELECT 記錄器，
/// 種一組跨午夜、同裝置多感測器、沉默、政策外感測器的狀態變更，再跑每日路徑或校準，
/// 取出「與日期無關的簽名」。只用不隨本批改動的公開／內部 API，所以同一份檔案能在改動前的建置上跑出基準。
/// </summary>
internal sealed class PrtgBatch4Rig : IDisposable
{
    internal sealed class SelectRecorder : DbCommandInterceptor
    {
        private readonly List<(string Text, List<object?> Params)> _selects = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var values = command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList();
                lock (_selects) _selects.Add((command.CommandText, values));
            }
            return base.ReaderExecuting(command, eventData, result);
        }

        public void Reset() { lock (_selects) _selects.Clear(); }

        public List<(string Text, List<object?> Params)> Where(Func<string, bool> predicate)
        {
            lock (_selects) return _selects.Where(s => predicate(s.Text)).ToList();
        }
    }

    public const long PolicyExcludedSensor = 999;
    public static readonly long[] PolicySensors = [101, 102, 201, 202, 203, 205, 301, 302, 303];

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "lf-batch4-" + Guid.NewGuid().ToString("N"));
    public readonly SelectRecorder Recorder = new();
    public readonly StorageBackend Backend;
    public readonly HostStore Hosts;
    public readonly DateTime Newest = DateTime.Today.AddDays(-1);
    public readonly List<WebHost> HostRows = [];

    public PrtgBatch4Rig()
    {
        Directory.CreateDirectory(Dir);
        Backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(Dir, "t.db")}" }, Dir, Recorder);
        Hosts = new HostStore(Backend.Blob("hosts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }

    public DateTime D(int daysBeforeNewest) => Newest.AddDays(-daysBeforeNewest);

    public void EnablePrtg(bool seedPolicy)
    {
        new SystemSettingsStore(Backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        if (seedPolicy)
            new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p => p.SensorIds = PolicySensors.ToList());
    }

    private static PrtgStateChangeRow Change(long sensor, DateTime at, string status) =>
        new() { SensorObjid = sensor, ChangedAt = at, Status = status, Quality = PrtgDataQuality.Ok, CreatedAt = DateTime.Now };

    /// <summary>種資料：三台裝置（含沉默那台）、跨午夜 Down／Warning、同裝置多顆感測器、跨回望邊界的 Down、政策外感測器。</summary>
    public void Seed()
    {
        var store = Backend.PrtgStore();
        var now = DateTime.Now;
        for (var i = 0; i < 3; i++)
        {
            var host = Hosts.Upsert(new WebHost { Source = "netiq", HostName = $"SRV-{(char)('A' + i)}", Active = true, IpAddress = $"192.168.9.{i + 1}" });
            HostRows.Add(host);
        }
        store.UpsertDevices(
        [
            new PrtgDeviceRow { Objid = 1, Name = "DEV-A", Ip = "192.168.9.1" },
            new PrtgDeviceRow { Objid = 2, Name = "DEV-B", Ip = "192.168.9.2" },
            new PrtgDeviceRow { Objid = 3, Name = "DEV-C", Ip = "192.168.9.3" }
        ], now);
        store.UpsertSensors(
        [
            new PrtgSensorRow { Objid = 101, DeviceObjid = 1, Name = "Ping-A", SensorType = "Ping", Status = "Up" },
            new PrtgSensorRow { Objid = 102, DeviceObjid = 1, Name = "Disk-A", SensorType = "SNMP Disk Free", Status = "Up" },
            new PrtgSensorRow { Objid = 201, DeviceObjid = 2, Name = "Cpu-B", SensorType = "SNMP CPU Load", Status = "Down" },
            new PrtgSensorRow { Objid = 202, DeviceObjid = 2, Name = "Disk-B1", SensorType = "SNMP Disk Free", Status = "Up" },
            new PrtgSensorRow { Objid = 203, DeviceObjid = 2, Name = "Disk-B2", SensorType = "SNMP Disk Free", Status = "Warning" },
            new PrtgSensorRow { Objid = 205, DeviceObjid = 2, Name = "Cpu-B2", SensorType = "SNMP CPU Load", Status = "Up" },
            new PrtgSensorRow { Objid = PolicyExcludedSensor, DeviceObjid = 2, Name = "Cpu-B3", SensorType = "SNMP CPU Load", Status = "Down" },
            new PrtgSensorRow { Objid = 301, DeviceObjid = 3, Name = "Ping-C", SensorType = "Ping", Status = "Up" },
            new PrtgSensorRow { Objid = 302, DeviceObjid = 3, Name = "Cpu-C", SensorType = "SNMP CPU Load", Status = "Unknown" },
            new PrtgSensorRow { Objid = 303, DeviceObjid = 3, Name = "Disk-C", SensorType = "SNMP Disk Free", Status = "Unknown" }
        ], now);
        store.ApplyAutoCategories(PrtgSensorTypeCategoryMap.ParseOverrides(
            new SystemSettingsStore(Backend.Blob("system_settings")).Get().PrtgSensorTypeCategoryOverrides).Map);

        var changes = new List<PrtgStateChangeRow>
        {
            // 101：D3 22:00 起 Down、D1 10:00 恢復（跨午夜持續失聯）
            Change(101, D(3).AddHours(22), "Down"), Change(101, D(1).AddHours(10), "Up"),
            // 201：D4 20:00 起 Down，一路到最新日 05:00 才恢復
            Change(201, D(4).AddHours(20), "Down"), Change(201, Newest.AddHours(5), "Up"),
            // 203：D3 20:00 起 Warning 到 D2 10:00（跨午夜 Warning）；最新日再 Warning 14 小時
            Change(203, D(3).AddHours(20), "Warning"), Change(203, D(2).AddHours(10), "Up"),
            Change(203, Newest.AddHours(6), "Warning"), Change(203, Newest.AddHours(20), "Up"),
            // 205：回望 5 天邊界外一天（D5 23:00）進入 Down，D3 01:00 恢復——只有把前一天讀進來才看得到
            Change(205, D(5).AddHours(23), "Down"), Change(205, D(3).AddHours(1), "Up"),
            // 政策外感測器
            Change(PolicyExcludedSensor, D(1).AddHours(3), "Down"),
            Change(PolicyExcludedSensor, Newest.AddHours(2), "Down")
        };
        // 202：D1 與最新日各震盪 6 次（同裝置另一顆感測器）
        foreach (var day in new[] { D(1), Newest })
            for (var k = 0; k < 6; k++)
            {
                changes.Add(Change(202, day.AddHours(1 + k * 2), "Down"));
                changes.Add(Change(202, day.AddHours(1 + k * 2).AddMinutes(20), "Up"));
            }
        store.AppendStateChanges(changes);

        for (var n = 0; n <= 6; n++)
        {
            store.ReplaceHostMapForDate(D(n), HostRows.Select((host, i) => new PrtgHostMapRow
            {
                MapDate = D(n), DeviceObjid = i + 1, HostId = host.HostId, HostName = host.HostName,
                Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            }).ToList());
        }
    }

    private sealed class NullConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private AnalysisRunContext CreateContext(out PrtgFindingsRegistry registry)
    {
        registry = new PrtgFindingsRegistry();
        var settings = new SystemSettingsStore(Backend.Blob("system_settings")).Get();
        var caseCoordinator = new IssueCaseCoordinator(
            Backend.IssueCaseStore(), Backend.IssueHandlingStore(), Backend.RecordHandlingStore(), Backend.RecordStore(),
            Hosts, new IssueOwnerStore(Backend.Blob("issue_owners")));
        var dispatch = NightlyDispatchFakes.Create(
            Backend.IssueCaseStore(), Backend.IssueHandlingStore(), Backend.RecordHandlingStore(),
            Hosts, new IssueOwnerStore(Backend.Blob("issue_owners")), caseCoordinator);
        var recorder = new BatchRunRecorder(
            new BatchRunStore(Backend.LogStore("batch_runs"), Backend.LogStore("batch_run_logs")), "test-host", Array.Empty<string>());
        return new AnalysisRunContext(
            new RunRequest(), new AppSettings(), new RetentionOptions(), new NullConsole(), default,
            new EventLogService(), caseCoordinator, Backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), UseAi: false, null, registry, dispatch, PrtgEnabled: settings.PrtgEnabled);
    }

    /// <summary>
    /// 跑每日路徑（沿用本日同步，不打 PRTG）：回望 <paramref name="lookbackDays"/> 天，由近到遠。
    /// 回傳與日期無關的簽名：逐日逐主機的 finding（事件鍵、嚴重度、說明），日期一律換成「相對最新日的天數」。
    /// </summary>
    public async Task<string> RunPipelineAsync(int lookbackDays)
    {
        var days = Enumerable.Range(0, lookbackDays).Select(D).ToArray();
        // 每個主機日要有 NetIQ 父紀錄，工作流才會往下走
        foreach (var day in days)
            foreach (var host in HostRows)
                Backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName }).Append(new DailyAnalysisRecord
                {
                    LogSource = AnalysisLogSource.Netiq, Date = day.Date, HostId = host.HostId, Host = host.HostName,
                    RiskLevel = RiskLevels.Low, AiAnalyzed = true, RiskBasis = "fixture", PrtgBaselineRiskLevel = RiskLevels.Low,
                    PrtgBaselineRiskBasis = "fixture", TopIssues = []
                });
        var ctx = CreateContext(out var registry);
        await PrtgDailyPipeline.RunAsync(ctx, Backend, Hosts, days, Task.CompletedTask, hostIds: null, guard: null,
            reuseSyncFrom: DateTime.Today.AddHours(1));

        var sb = new StringBuilder();
        foreach (var day in days)
            foreach (var host in HostRows.OrderBy(h => h.HostName, StringComparer.Ordinal))
                foreach (var sig in registry.For(host.HostId, day).OrderBy(s => s.EventKey, StringComparer.Ordinal))
                    sb.AppendLine($"D-{(Newest - day).Days}|{host.HostName}|{sig.EventKey}|{sig.Severity}|{sig.ElevatesDayRisk}|" +
                                  Normalize(string.Join(" ## ", sig.SampleMessages)));
        return sb.ToString().Replace("\r\n", "\n");   // 換行固定為 LF，摘要不隨平台改變
    }

    /// <summary>把訊息裡的絕對日期換成相對最新日的天數，簽名才不隨執行日改變。</summary>
    public string Normalize(string text) =>
        Regex.Replace(text, @"\d{4}-\d{2}-\d{2}", m =>
            DateTime.TryParse(m.Value, out var date) ? $"D-{(Newest - date).Days}" : m.Value);

    /// <summary>
    /// 校準匯出：種 56 天窗口內跨午夜、跨窗口起點前一天、同感測器多次震盪／Warning、政策外感測器的狀態變更，
    /// 取出規則門檻分佈樣本與正式命中數的簽名（日期換成相對錨定日的天數）。
    /// </summary>
    public static string BuildCalibrationSignature(Func<LfDbContext> newContext)
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example.invalid/";
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
        });
        var ruleStore = new FakeRuleStore { Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() } };
        var anchor = new DateTime(2026, 9, 1);
        var store = new EfPrtgStore(newContext);
        var now = DateTime.Now;
        store.UpsertSensors(
        [
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, Name = "P", SensorType = "Ping", Category = "availability", Status = "Up" },
            new PrtgSensorRow { Objid = 1002, DeviceObjid = 10, Name = "D", SensorType = "SNMP Disk Free", Category = "disk", Status = "Up" },
            new PrtgSensorRow { Objid = 1003, DeviceObjid = 11, Name = "X", SensorType = "SNMP Disk Free", Category = "disk", Status = "Up" }
        ], now);
        for (var n = 0; n < CalibrationConstants.RuleThresholdWindowDays; n++)
            store.ReplaceHostMapForDate(anchor.AddDays(-n), [new PrtgHostMapRow
            {
                MapDate = anchor.AddDays(-n), DeviceObjid = 10, HostId = 1, HostName = "HOST-1",
                MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            }]);
        new PrtgMonitoringPolicyStore(new EfJsonBlobStore(newContext, PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        {
            p.HostIds = [1];
            p.SensorIds = [1001, 1002];
        });
        var from = anchor.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1));
        PrtgStateChangeRow C(long sensor, DateTime at, string status) =>
            new() { SensorObjid = sensor, ChangedAt = at, Status = status, Quality = PrtgDataQuality.Ok, CreatedAt = now };
        var changes = new List<PrtgStateChangeRow>
        {
            // 窗口起點前一天 23:00 進入 Down：只有讀到前一天才看得到
            C(1001, from.AddDays(-1).AddHours(23), "Down"), C(1001, from.AddDays(1).AddHours(4), "Up"),
            C(1001, anchor.AddDays(-30).AddHours(22), "Down"), C(1001, anchor.AddDays(-29).AddHours(9), "Up"),
            C(1001, anchor.AddDays(-5).AddHours(8), "Down"), C(1001, anchor.AddDays(-4).AddHours(12), "Up"),
            C(1001, anchor.AddHours(23).AddMinutes(30), "Down"),
            C(1002, anchor.AddDays(-3).AddHours(20), "Warning"), C(1002, anchor.AddDays(-2).AddHours(12), "Up"),
            C(1003, anchor.AddDays(-1).AddHours(3), "Down")   // 政策外
        };
        for (var k = 0; k < 4; k++)
        {
            changes.Add(C(1002, anchor.AddDays(-10).AddHours(2 + k * 3), "Down"));
            changes.Add(C(1002, anchor.AddDays(-10).AddHours(2 + k * 3).AddMinutes(30), "Up"));
        }
        store.AppendStateChanges(changes);
        CalibrationService.ClearAssessmentCache();
        var service = new CalibrationService(newContext, store, new EfIssueAggregateQuery(newContext, new FakeHostStore()),
            settings, ruleStore, null);

        var dataset = service.BuildExportPackage(anchor).RuleThresholds;

        var sb = new StringBuilder();
        foreach (var s in dataset.MagnitudeSamples.OrderBy(s => s.Date).ThenBy(s => s.RuleCode, StringComparer.Ordinal)
                     .ThenBy(s => s.DeviceObjid).ThenBy(s => s.SensorObjid))
            sb.AppendLine($"sample|{s.RuleCode}|D-{(anchor - s.Date).Days}|{s.DeviceObjid}|{s.SensorObjid}|{s.Magnitude}");
        foreach (var h in dataset.FormalCurrentHitCounts)
            sb.AppendLine($"hit|{JsonSerializer.Serialize(h)}");
        return sb.ToString().Replace("\r\n", "\n");   // 換行固定為 LF，摘要不隨平台改變
    }
}

using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Persistence;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 鏡像資料的跨後端搬運格式（正式機 SQL Server → 開發機 SQLite）。
/// 自描述：帶格式版本與各表筆數，匯入端據此驗證與回報。
/// </summary>
public sealed class PrtgDataPackage
{
    /// <summary>格式版本。目前為 1；日後欄位變動時遞增，匯入端拒絕不認得的版本。</summary>
    public int FormatVersion { get; set; } = PrtgDataTransfer.CurrentFormatVersion;
    public DateTime ExportedAt { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<PrtgDeviceRow> Devices { get; set; } = new();
    public List<PrtgSensorRow> Sensors { get; set; } = new();
    public List<PrtgStateChangeRow> StateChanges { get; set; } = new();
    public List<PrtgValueRow> Values { get; set; } = new();
    public List<PrtgHostMapRow> HostMaps { get; set; } = new();
    public List<PrtgManualMapRow> ManualMaps { get; set; } = new();
    public string Purpose { get; set; } = "diagnostic-only";
    public PrtgMonitoringPolicy? SourcePolicy { get; set; }
    public List<PrtgObservationRow> Observations { get; set; } = [];
    public List<PrtgSensorTimelineEvidence> Timelines { get; set; } = [];
    public List<PrtgDiskSemanticEvidence> SemanticEvidence { get; set; } = [];
    public List<PrtgDiskVerificationResult> SemanticResults { get; set; } = [];
}

public sealed record PrtgImportResult(
    int Devices, int Sensors, int StateChanges, int Values, int HostMaps, int ManualMaps);

public static class PrtgDataTransfer
{
    public const int CurrentFormatVersion = 2;

    public static PrtgDataPackage Export(StorageBackend backend, DateTime fromDate, DateTime toDate)
    {
        var package = Export(backend.PrtgStore(), fromDate, toDate);
        package.SourcePolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        using var db = backend.CreateContext();
        package.Observations = db.PrtgObservations.AsNoTracking().Where(o => o.RecordDate >= fromDate.Date && o.RecordDate <= toDate.Date).ToList();
        package.Timelines = package.SourcePolicy.SensorIds.Select(id =>
            new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + id)).Get()).ToList();
        package.SemanticEvidence = new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).GetAll().ToList();
        package.SemanticResults = new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Get().Values.ToList();
        return package;
    }

    public static PrtgImportResult Import(StorageBackend backend, PrtgDataPackage package)
    {
        if (package.FormatVersion is not (1 or CurrentFormatVersion)) throw new InvalidOperationException("不支援的資料包版本。");
        if (package.Purpose != "diagnostic-only") throw new InvalidOperationException("匯入僅接受診斷包，不接受正式判定授權。");
        // 跨站台 host_id 不能沿用；先落隔離包，不載入正式判定、意圖或語意證據。
        var json = JsonSerializer.Serialize(package);
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        backend.Blob("prtg_import_diagnostic_" + id).Mutate(current => (json, true));
        var safe = JsonSerializer.Deserialize<PrtgDataPackage>(json)!;
        foreach (var map in safe.HostMaps) { map.HostId = null; map.MapStatus = PrtgMapStatus.Unmatched; }
        safe.ManualMaps = [];
        // 匯入後必須重新核對站台身分與範圍，開始新暖機；匯入不造成派工或通知。
        new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
        { p.SourceGeneration = ""; p.Revision = Guid.NewGuid().ToString("N"); p.ValidFrom = default; });
        return Import(backend.PrtgStore(), safe);
    }

    /// <summary>
    /// 匯出指定期間的鏡像資料。結構表（devices／sensors／manual map）一律全量，
    /// 時序表（state changes／values／host map）依期間篩選。
    /// </summary>
    public static PrtgDataPackage Export(EfPrtgStore store, DateTime fromDate, DateTime toDate)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        var from = fromDate.Date;
        var to = toDate.Date;
        var toExclusive = to.AddDays(1);

        var package = new PrtgDataPackage
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAt = DateTime.Now,
            FromDate = from,
            ToDate = to,
            Devices = store.GetAllDevices(),
            Sensors = store.GetAllSensors(),
            ManualMaps = store.GetManualMaps(),
            Values = store.GetValues(from, toExclusive),
            StateChanges = store.GetStateChanges(from, toExclusive),
            HostMaps = new List<PrtgHostMapRow>()
        };

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var dailyMaps = store.GetHostMapForDate(day);
            if (dailyMaps.Count > 0)
            {
                package.HostMaps.AddRange(dailyMaps);
            }
        }

        return package;
    }

    /// <summary>
    /// 匯入資料包，全部走既有的冪等寫入方法，回傳各表實際寫入筆數。
    /// </summary>
    public static PrtgImportResult Import(EfPrtgStore store, PrtgDataPackage package)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (package == null) throw new ArgumentNullException(nameof(package));

        if (package.FormatVersion is not (1 or CurrentFormatVersion))
        {
            throw new InvalidOperationException(
                $"不支援的格式版本 {package.FormatVersion}（目前支援版本為 1 與 {CurrentFormatVersion}）。");
        }

        var devices = store.UpsertDevices(package.Devices ?? new List<PrtgDeviceRow>(), package.ExportedAt);
        var sensors = store.UpsertSensors(package.Sensors ?? new List<PrtgSensorRow>(), package.ExportedAt);
        var stateChanges = store.AppendStateChanges(package.StateChanges ?? new List<PrtgStateChangeRow>());
        var values = store.UpsertValues(package.Values ?? new List<PrtgValueRow>());

        var hostMaps = 0;
        if (package.HostMaps != null && package.HostMaps.Count > 0)
        {
            var groups = package.HostMaps.GroupBy(m => m.MapDate.Date);
            foreach (var group in groups)
            {
                var rows = group.ToList();
                hostMaps += store.ReplaceHostMapForDate(group.Key, rows);
            }
        }

        var manualMaps = 0;
        if (package.ManualMaps != null && package.ManualMaps.Count > 0)
        {
            foreach (var map in package.ManualMaps)
            {
                store.UpsertManualMap(map);
                manualMaps++;
            }
        }

        return new PrtgImportResult(devices, sensors, stateChanges, values, hostMaps, manualMaps);
    }
}

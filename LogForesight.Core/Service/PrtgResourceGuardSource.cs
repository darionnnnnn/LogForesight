using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 資源守門自動偵測的裝置／感測器來源（docs/PRTG-SPEC.md §12）。
///
/// 有兩個實作：夜間批次讀鏡像表（零成本、無外部呼叫），維護頁的「自動偵測並填入」
/// 可直接查 PRTG——PRTG 剛啟用而鏡像還是空的時候，讀鏡像必然一無所獲。
/// **判定邏輯只有一份**（位址比對、corehealth fallback、cpu／memory 篩選都在
/// <see cref="PrtgResourceGuardTargets"/>），這裡只負責把資料撈出來。
/// </summary>
public interface IPrtgResourceGuardSource
{
    /// <summary>全部裝置。</summary>
    IReadOnlyList<PrtgDeviceRow> GetDevices();

    /// <summary>全部感測器。</summary>
    IReadOnlyList<PrtgSensorRow> GetSensors();

    /// <summary>來源標示，供畫面說明這次偵測的資料是哪裡來的。</summary>
    string SourceLabel { get; }
}

/// <summary>讀鏡像表的來源：夜間批次用這個，零成本、不打 PRTG。</summary>
public sealed class PrtgMirrorGuardSource : IPrtgResourceGuardSource
{
    private readonly EfPrtgStore _store;

    public PrtgMirrorGuardSource(EfPrtgStore store) => _store = store;

    public string SourceLabel => "mirror";

    public IReadOnlyList<PrtgDeviceRow> GetDevices() => _store.GetAllDevices();

    public IReadOnlyList<PrtgSensorRow> GetSensors() => _store.GetAllSensors();
}

/// <summary>
/// 直接查 PRTG 的來源：給維護頁與設定頁的「預覽／自動偵測並填入」用。
///
/// 只抓自動偵測需要的欄位。裝置數量級遠小於感測器，一次全量可接受；
/// 感測器同樣全量抓，但只取 objid／parentid／type／status 四欄，
/// 不寫入任何鏡像表——這條路徑是唯讀的偵測，不是同步。
/// </summary>
public sealed class PrtgLiveGuardSource : IPrtgResourceGuardSource
{
    private const int PageSize = 500;

    private readonly PrtgClient _client;
    private readonly CancellationToken _ct;

    private List<PrtgDeviceRow>? _devices;
    private List<PrtgSensorRow>? _sensors;

    public PrtgLiveGuardSource(PrtgClient client, CancellationToken ct = default)
    {
        _client = client;
        _ct = ct;
    }

    public string SourceLabel => "live";

    public IReadOnlyList<PrtgDeviceRow> GetDevices() =>
        _devices ??= FetchAll("devices", "objid,device,host", el => new PrtgDeviceRow
        {
            Objid = GetLong(el, "objid") ?? 0,
            Name = GetString(el, "device") ?? string.Empty,
            Ip = GetString(el, "host")
        });

    public IReadOnlyList<PrtgSensorRow> GetSensors() =>
        _sensors ??= FetchAll("sensors", "objid,parentid,sensor,type,status", el => new PrtgSensorRow
        {
            Objid = GetLong(el, "objid") ?? 0,
            DeviceObjid = GetLong(el, "parentid") ?? 0,
            Name = GetString(el, "sensor") ?? string.Empty,
            SensorType = GetString(el, "type") ?? string.Empty,
            Status = GetString(el, "status"),
            // 鏡像同步時 Category 也是 null（分類由 SensorType 推導），兩個來源在這一欄一致。
            Category = null,
            Paused = IsPaused(el)
        });

    private List<T> FetchAll<T>(string content, string columns, Func<JsonElement, T> map)
    {
        var results = new List<T>();
        var offset = 0;

        while (true)
        {
            _ct.ThrowIfCancellationRequested();

            var query = $"api/table.json?content={content}&columns={columns}&start={offset}&count={PageSize}";
            var json = _client.GetJsonAsync(query, _ct).GetAwaiter().GetResult();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(content, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var el in arr.EnumerateArray())
            {
                results.Add(map(el));
                count++;
            }

            if (count < PageSize) break;
            offset += count;
        }

        return results;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? GetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    /// <summary>
    /// PRTG 的暫停狀態在 status 文字裡（"Paused"、"Paused by Dependency" 等）。
    /// 判錯的後果是把暫停中的 sensor 納入監看，讀到的值恆為舊值——寧可保守。
    /// </summary>
    private static bool IsPaused(JsonElement el)
    {
        var status = GetString(el, "status");
        return status != null && status.Contains("paus", StringComparison.OrdinalIgnoreCase);
    }
}

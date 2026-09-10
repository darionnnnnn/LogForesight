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
    /// <summary>
    /// 單次查詢的筆數上限。這條路徑跑在 HTTP 請求執行緒上，分頁翻上百頁的逾時風險
    /// 比一次取回數萬筆的記憶體成本實際得多——每筆只有四個欄位。
    /// 實際筆數少於 treesize（或無 treesize 而剛好取到這個數）時視為被截斷並發警告，
    /// 絕不靜默：偵測結果少一半而畫面顯示「未偵測到」是查不出原因的。
    /// </summary>
    private const int MaxSingleFetchCount = 50000;

    private readonly PrtgClient _client;
    private readonly CancellationToken _ct;
    private readonly IRunConsole? _console;

    private List<PrtgDeviceRow>? _devices;
    private List<PrtgSensorRow>? _sensors;

    /// <param name="console">截斷警告的去處；null＝不回報（僅測試與不需要警告的呼叫端）。</param>
    public PrtgLiveGuardSource(PrtgClient client, CancellationToken ct = default, IRunConsole? console = null)
    {
        _client = client;
        _ct = ct;
        _console = console;
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

    /// <summary>
    /// 單次取回整份表格（不分頁），並在筆數對不上 treesize 時發出截斷警告。
    /// </summary>
    private List<T> FetchAll<T>(string content, string columns, Func<JsonElement, T> map)
    {
        var results = new List<T>();
        _ct.ThrowIfCancellationRequested();

        var query = $"api/table.json?content={content}&columns={columns}&count={MaxSingleFetchCount}";
        var json = _client.GetJsonAsync(query, _ct).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(content, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var el in arr.EnumerateArray())
        {
            results.Add(map(el));
        }

        var treeSize = GetLong(root, "treesize");
        if (treeSize is > 0 && results.Count < treeSize.Value)
        {
            Warn(content, results.Count, treeSize.Value.ToString());
        }
        else if (treeSize is null or <= 0 && results.Count >= MaxSingleFetchCount)
        {
            Warn(content, results.Count, "未知");
        }

        return results;
    }

    private void Warn(string content, int got, string total)
    {
        var label = content == "devices" ? "裝置" : "感測器";
        _console?.WriteLine(
            $"[PRTG資源守門] 只取到 {got} 個{label}（總數 {total}），自動偵測的結果可能漏掉部分{label}。" +
            "請改用「受監看 sensor 覆寫清單」直接指定 objid。");
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

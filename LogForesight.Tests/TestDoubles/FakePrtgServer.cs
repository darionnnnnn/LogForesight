using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace LogForesight.Tests.TestDoubles;

/// <summary>
/// 以真實 HTTP（<see cref="HttpListener"/>，綁 127.0.0.1 隨機 port）扮演 PRTG 的測試伺服器。
///
/// 目的是讓「PRTG 回應 → 結構同步 → 主機對應 → 取數範圍 → 快照／歷史值 → 每日管線 → 畫面」整條鏈
/// 都走真正的 <c>PrtgClient</c>（SocketsHttpHandler、逾時、取消、查詢字串），而不是替身 handler。
/// 回應形狀貼近 PRTG 24.x 實機：表格回 <c>{"prtg-version","treesize","&lt;content&gt;":[…]}</c>，
/// 每個被要求的欄位後面都跟一個 <c>*_raw</c>（狀態、時間、數值），historicdata 一列有重複的 value／value_raw 鍵。
///
/// 可設定的行為：每頁依 <c>count</c> 切、<c>start</c> 越界夾回末頁、故意少給（截斷，treesize 仍是全量）、
/// 指定請求不回應（逾時）、sensors 一律回空、權限縮小（只看得到部分裝置）、記錄每一筆收到的請求。
/// 關閉一律用 <c>HttpListener.Close()</c>（Unix 上先 Stop 再 Dispose 會重新綁 port，見 PlatformGuardTests）。
/// </summary>
internal sealed class FakePrtgServer : IDisposable
{
    internal sealed record Device(long Objid, string Name, string Host, string Group = "Servers");

    internal sealed class Sensor
    {
        public long Objid { get; init; }
        public long ParentId { get; init; }
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string Status { get; set; } = "Up";
        public int StatusRaw { get; set; } = 3;
        public double LastValue { get; set; }
        public string Unit { get; init; } = "%";
        public string Interval { get; init; } = "60 s";
        public bool Paused { get; set; }
    }

    /// <summary><paramref name="LocalTime"/> 是伺服器所在時區的本地時間；回應時換成 UTC 的 OLE 日期（datetime_raw）。</summary>
    internal sealed record Message(long SensorObjid, DateTime LocalTime, string Status, int StatusRaw, string Text);

    /// <summary>一筆收到的請求（已解析常用參數；<see cref="FilterObjids"/> 保留重複的 filter_objid）。</summary>
    internal sealed record RecordedRequest(
        string Path, string RawQuery, string? Content, bool HasId, long? Id, int? Start, int? Count,
        string? DateRel, IReadOnlyList<long> FilterObjids, string? Sdate, string? Edate);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly object _gate = new();
    private readonly Task _loop;

    public string BaseUrl { get; }
    public string Token { get; }

    public List<Device> Devices { get; } = new();
    public List<Sensor> Sensors { get; } = new();
    public List<Message> Messages { get; } = new();

    /// <summary>historicdata 的值提供者：(sensor objid, 時段起點本地時間) → 值；回 null＝該小時沒有資料列。</summary>
    public Func<long, DateTime, double?> HistoricValue { get; set; } = (_, _) => null;

    /// <summary>權限縮小：非 null 時只看得到這些裝置（及其底下感測器、訊息）；treesize 也跟著縮小。</summary>
    public HashSet<long>? VisibleDeviceIds { get; set; }

    /// <summary>某 content 只回前 N 列，但 treesize 仍是全量（模擬截斷）。</summary>
    public Dictionary<string, int> TruncateTo { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>符合條件的請求不回應（直到伺服器關閉）；用來驗證用戶端逾時與取消。</summary>
    public Func<RecordedRequest, bool>? HangWhen { get; set; }

    /// <summary>sensors 一律回 <c>{"treesize":0,"sensors":[]}</c>（PRTG 偶發回空陣列的形狀）。</summary>
    public bool EmptySensors { get; set; }

    /// <summary>這些裝置的 sensors 查詢回空陣列（其餘裝置照常），模擬單台裝置的偶發空回應。</summary>
    public HashSet<long> EmptySensorDevices { get; } = new();

    public FakePrtgServer(string token = "fake-token")
    {
        Token = token;
        var started = false;
        string baseUrl = "";
        for (var attempt = 0; attempt < 10 && !started; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            baseUrl = $"http://127.0.0.1:{port}";
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(baseUrl + "/");
                _listener.Start();
                started = true;
            }
            catch (HttpListenerException)
            {
                // 剛好被別的行程占走：換一個 port 再試
            }
        }
        if (!started) throw new InvalidOperationException("找不到可用的本機 port 啟動假 PRTG 伺服器。");
        BaseUrl = baseUrl;
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>目前為止收到的全部請求（快照複本）。</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public void ClearRequests()
    {
        lock (_gate) _requests.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var url = context.Request.Url!;
            var query = HttpUtility.ParseQueryString(url.Query);
            var recorded = Record(url.AbsolutePath, url.Query, query);

            if (HangWhen?.Invoke(recorded) == true)
            {
                try { await Task.Delay(Timeout.Infinite, _stop.Token); }
                catch (OperationCanceledException) { }
                response.Abort();
                return;
            }

            if (query["apitoken"] != Token)
            {
                await WriteAsync(response, 401, "<div class=\"errormsg\">Unauthorized</div>", "text/html");
                return;
            }

            string body;
            switch (url.AbsolutePath.ToLowerInvariant())
            {
                case "/api/table.json":
                    body = BuildTable(recorded, query);
                    break;
                case "/api/historicdata.json":
                    body = BuildHistoric(recorded);
                    break;
                case "/api/status.json":
                    body = "{\"Version\":\"24.1.92.1234\",\"NewMessages\":\"0\",\"NewAlarms\":\"0\"}";
                    break;
                default:
                    await WriteAsync(response, 404, "{\"error\":\"not found\"}", "application/json");
                    return;
            }
            await WriteAsync(response, 200, body, "application/json");
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // 用戶端已斷線或伺服器關閉：沒有人在等這個回應
        }
        catch (Exception ex)
        {
            try { await WriteAsync(response, 500, "{\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}", "application/json"); }
            catch (Exception) { /* 連線已關 */ }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType + "; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private RecordedRequest Record(string path, string rawQuery, System.Collections.Specialized.NameValueCollection query)
    {
        long? id = long.TryParse(query["id"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId) ? parsedId : null;
        int? start = int.TryParse(query["start"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart) ? parsedStart : null;
        int? count = int.TryParse(query["count"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) ? parsedCount : null;
        var filters = (query.GetValues("filter_objid") ?? [])
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : -1L).ToList();
        var recorded = new RecordedRequest(path, rawQuery, query["content"], query["id"] != null, id, start, count,
            query["filter_drel"], filters, query["sdate"], query["edate"]);
        lock (_gate) _requests.Add(recorded);
        return recorded;
    }

    // ---- table.json ----

    private bool DeviceVisible(long deviceObjid) => VisibleDeviceIds == null || VisibleDeviceIds.Contains(deviceObjid);

    private string BuildTable(RecordedRequest request, System.Collections.Specialized.NameValueCollection query)
    {
        var content = request.Content ?? "";
        var columns = (query["columns"] ?? "objid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = new List<List<KeyValuePair<string, object?>>>();

        switch (content.ToLowerInvariant())
        {
            case "devices":
                foreach (var device in Devices.Where(d => DeviceVisible(d.Objid)).OrderBy(d => d.Objid))
                    rows.Add(DeviceRow(device, columns));
                break;
            case "sensors":
                if (!EmptySensors)
                {
                    IEnumerable<Sensor> sensors = Sensors.Where(s => DeviceVisible(s.ParentId) && !EmptySensorDevices.Contains(s.ParentId));
                    if (request.HasId && request.Id is { } deviceId && deviceId != 0)
                        sensors = sensors.Where(s => s.ParentId == deviceId);
                    if (request.FilterObjids.Count > 0)
                        sensors = sensors.Where(s => request.FilterObjids.Contains(s.Objid));
                    foreach (var sensor in sensors.OrderBy(s => s.Objid))
                        rows.Add(SensorRow(sensor, columns));
                }
                break;
            case "messages":
                rows.AddRange(SelectMessages(request).Select(m => MessageRow(m, columns)));
                break;
            default:
                return "{\"prtg-version\":\"24.1.92.1234\",\"treesize\":0,\"" + content + "\":[]}";
        }

        var treesize = rows.Count;
        if (TruncateTo.TryGetValue(content, out var cap)) rows = rows.Take(cap).ToList();

        var count = request.Count ?? 500;
        var start = Math.Max(request.Start ?? 0, 0);
        // PRTG 對超出範圍的 start 回末頁（不是空頁）：分頁器靠「本頁沒有新列」收尾，這裡照實模擬
        if (rows.Count > 0 && start >= rows.Count) start = (rows.Count - 1) / Math.Max(count, 1) * Math.Max(count, 1);
        var page = rows.Skip(start).Take(Math.Max(count, 0)).ToList();

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("prtg-version", "24.1.92.1234");
            writer.WriteNumber("treesize", treesize);
            writer.WriteStartArray(content);
            foreach (var row in page) WriteObject(writer, row);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private IEnumerable<Message> SelectMessages(RecordedRequest request)
    {
        var sensorParent = Sensors.ToDictionary(s => s.Objid, s => s.ParentId);
        IEnumerable<Message> messages = Messages.Where(m => sensorParent.TryGetValue(m.SensorObjid, out var parent) && DeviceVisible(parent));
        // id＝裝置：回底下所有感測器的訊息；id＝感測器：只回該顆；沒帶 id 或 0＝整台 PRTG（正式程式不得走到這裡）
        if (request.HasId && request.Id is { } id && id != 0)
        {
            messages = messages.Where(m => m.SensorObjid == id || sensorParent[m.SensorObjid] == id);
        }
        if (request.DateRel is { } rel)
        {
            var days = rel switch { "7days" => 7, "30days" => 30, "12months" => 365, _ => int.MaxValue };
            var cutoff = DateTime.Now.AddDays(-days);
            messages = messages.Where(m => m.LocalTime >= cutoff);
        }
        // PRTG 的訊息表新的在前
        return messages.OrderByDescending(m => m.LocalTime).ThenByDescending(m => m.SensorObjid);
    }

    /// <summary>OLE 日期（自 1899-12-30 起的天數）；PRTG 的 *_raw 時間一律是 UTC。</summary>
    internal static double ToOleUtc(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToOADate();

    private static string Display(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static List<KeyValuePair<string, object?>> DeviceRow(Device device, string[] columns)
    {
        var row = new List<KeyValuePair<string, object?>>();
        foreach (var column in columns)
        {
            switch (column.ToLowerInvariant())
            {
                case "objid": Add(row, "objid", device.Objid); break;
                case "device": Add(row, "device", device.Name); break;
                case "host": Add(row, "host", device.Host); break;
                case "group": Add(row, "group", device.Group); break;
                case "status": Add(row, "status", "Up"); Add(row, "status_raw", 3); break;
                case "tags": Add(row, "tags", ""); break;
                case "paused": Add(row, "paused", false); Add(row, "paused_raw", 0); break;
                case "dependency": Add(row, "dependency", "None"); break;
            }
        }
        return row;
    }

    private static List<KeyValuePair<string, object?>> SensorRow(Sensor sensor, string[] columns)
    {
        var row = new List<KeyValuePair<string, object?>>();
        foreach (var column in columns)
        {
            switch (column.ToLowerInvariant())
            {
                case "objid": Add(row, "objid", sensor.Objid); break;
                case "parentid": Add(row, "parentid", sensor.ParentId); break;
                case "sensor": Add(row, "sensor", sensor.Name); break;
                case "type": Add(row, "type", sensor.Type); break;
                case "tags": Add(row, "tags", ""); break;
                case "unit": Add(row, "unit", sensor.Unit); break;
                case "status": Add(row, "status", sensor.Status); Add(row, "status_raw", sensor.StatusRaw); break;
                case "paused": Add(row, "paused", sensor.Paused); Add(row, "paused_raw", sensor.Paused ? 1 : 0); break;
                case "dependency": Add(row, "dependency", "None"); break;
                case "lastvalue":
                    Add(row, "lastvalue", sensor.LastValue.ToString("0.###", CultureInfo.InvariantCulture) + " " + sensor.Unit);
                    Add(row, "lastvalue_raw", sensor.LastValue);
                    break;
                case "interval":
                    Add(row, "interval", sensor.Interval);
                    Add(row, "interval_raw", 60);
                    break;
                case "lastcheck":
                    Add(row, "lastcheck", Display(DateTime.Now));
                    Add(row, "lastcheck_raw", ToOleUtc(DateTime.Now));
                    break;
                case "device": Add(row, "device", ""); break;
            }
        }
        return row;
    }

    private static List<KeyValuePair<string, object?>> MessageRow(Message message, string[] columns)
    {
        var row = new List<KeyValuePair<string, object?>>();
        foreach (var column in columns)
        {
            switch (column.ToLowerInvariant())
            {
                case "objid": Add(row, "objid", message.SensorObjid); break;
                case "datetime":
                    Add(row, "datetime", Display(message.LocalTime));
                    Add(row, "datetime_raw", ToOleUtc(message.LocalTime));
                    break;
                case "status": Add(row, "status", message.Status); Add(row, "status_raw", message.StatusRaw); break;
                case "message": Add(row, "message", message.Text); break;
            }
        }
        return row;
    }

    private static void Add(List<KeyValuePair<string, object?>> row, string name, object? value) =>
        row.Add(new KeyValuePair<string, object?>(name, value));

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in pairs)
        {
            switch (value)
            {
                case null: writer.WriteNull(name); break;
                case string text: writer.WriteString(name, text); break;
                case bool flag: writer.WriteBoolean(name, flag); break;
                case int number: writer.WriteNumber(name, number); break;
                case long number: writer.WriteNumber(name, number); break;
                case double number: writer.WriteNumber(name, number); break;
                default: writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
            }
        }
        writer.WriteEndObject();
    }

    // ---- historicdata.json ----

    private string BuildHistoric(RecordedRequest request)
    {
        var id = request.Id ?? 0;
        var sensor = Sensors.FirstOrDefault(s => s.Objid == id);
        var visible = sensor != null && DeviceVisible(sensor.ParentId);
        var start = ParseHistoricStamp(request.Sdate);
        var end = ParseHistoricStamp(request.Edate);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("prtg-version", "24.1.92.1234");
            writer.WriteNumber("treesize", 0);
            writer.WriteStartArray("histdata");
            if (visible && start != null && end != null)
            {
                for (var hour = start.Value; hour < end.Value; hour = hour.AddHours(1))
                {
                    var value = HistoricValue(id, hour);
                    if (value == null) continue;
                    // 實機形狀（見 PrtgFetchServiceTests「實機形狀」）：顯示字串是時段起訖、datetime_raw 是時段結束的 UTC OLE 日期；
                    // 一列為每個頻道重複 value／value_raw，第一組才是主要頻道；coverage_raw 的 10000 代表 100%。
                    WriteObject(writer, new List<KeyValuePair<string, object?>>
                    {
                        new("datetime", Display(hour) + " - " + Display(hour.AddHours(1))),
                        new("datetime_raw", ToOleUtc(hour.AddHours(1))),
                        new("value", value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + sensor!.Unit),
                        new("value_raw", value.Value),
                        new("value", "0 #"),
                        new("value_raw", 0.0),
                        new("coverage", "100 %"),
                        new("coverage_raw", 10000)
                    });
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static DateTime? ParseHistoricStamp(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : null;
}

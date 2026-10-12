using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 6) throw new ArgumentException("Round53ControlledSources <run-id> <http-loopback-url> <smtp-port> <http-mode-file> <smtp-mode-file> <run-root>");
var runId = args[0];
var httpBase = new Uri(args[1]);
var smtpPort = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
var httpModeFile = Path.GetFullPath(args[3]);
var smtpModeFile = Path.GetFullPath(args[4]);
var runRoot = Path.GetFullPath(args[5]);
if (httpBase.Scheme != Uri.UriSchemeHttp || !string.Equals(httpBase.Host, "127.0.0.1", StringComparison.Ordinal) || !IPAddress.TryParse(httpBase.Host, out var bindAddress) || !IPAddress.IsLoopback(bindAddress))
    throw new InvalidOperationException("Only a literal 127.0.0.1 HTTP bind is permitted.");
Directory.CreateDirectory(runRoot);
if (!File.Exists(httpModeFile)) await File.WriteAllTextAsync(httpModeFile, "netiq-healthy");
if (!File.Exists(smtpModeFile)) await File.WriteAllTextAsync(smtpModeFile, "accept");
var httpLog = Path.Combine(runRoot, "source-requests.jsonl");
var smtpLog = Path.Combine(runRoot, "smtp-captures.jsonl");
var stateFile = Path.Combine(runRoot, "netiq-state.json");
const int requestLimit = 250;
const int requestBodyLimit = 1024 * 1024;
var requestCount = 0;
var prtgHoldUsed = 0;
var aiHoldUsed = 0;
var rejectRecipientUsed = 0;
var requestGate = new SemaphoreSlim(16);

using var http = new HttpListener();
http.Prefixes.Add(httpBase.ToString().TrimEnd('/') + "/");
http.Start();
using var smtp = new TcpListener(IPAddress.Loopback, smtpPort);
smtp.Start(4);
Console.WriteLine(JsonSerializer.Serialize(new { runId, http = httpBase.ToString().TrimEnd('/'), smtp = $"127.0.0.1:{smtpPort}", loopbackOnly = true }));
using var stop = new CancellationTokenSource(TimeSpan.FromHours(8));
var httpTask = AcceptHttpAsync(stop.Token);
var smtpTask = AcceptSmtpAsync(stop.Token);
await Task.WhenAll(httpTask, smtpTask);

async Task AcceptHttpAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await http.GetContextAsync().WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
        _ = HandleHttpAsync(context, ct);
    }
}

async Task HandleHttpAsync(HttpListenerContext context, CancellationToken ct)
{
    await requestGate.WaitAsync(ct);
    try
    {
        var request = context.Request;
        if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
        { await HttpReply(context, 403, "loopback clients only"); return; }
        if (Interlocked.Increment(ref requestCount) > requestLimit)
        { await HttpReply(context, 429, "fixture request budget exhausted"); return; }
        var path = request.Url?.AbsolutePath ?? "/";
        if (path == "/__fixture/health")
        { await HttpReply(context, 200, JsonSerializer.Serialize(new { runId, requests = requestCount, loopbackOnly = true }), "application/json"); return; }
        if (path == "/__fixture/control" && request.HttpMethod == "GET")
        { await HttpReply(context, 200, JsonSerializer.Serialize(new { httpMode = (await ReadControlFileAsync(httpModeFile)).Trim(), smtpMode = (await ReadControlFileAsync(smtpModeFile)).Trim() }), "application/json"); return; }

        string? body = null;
        if (request.HasEntityBody)
        {
            var bytes = await ReadBoundedAsync(request.InputStream, requestBodyLimit, ct);
            body = request.ContentEncoding.GetString(bytes);
        }
        var kind = Classify(path);
        // Query strings can carry opaque credentials or source filters. Evidence needs the
        // endpoint path only; never retain those values in the synthetic fixture log.
        var safePath = path;
        var bodyBytes = body is null ? 0 : Encoding.UTF8.GetByteCount(body);
        var bodyHash = body is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        await File.AppendAllTextAsync(httpLog, JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, runId, mode = (await ReadControlFileAsync(httpModeFile)).Trim(), kind, method = request.HttpMethod, path = safePath, remote = remote.Address.ToString(), bodyBytes, bodySha256 = bodyHash, syntheticOnly = true }) + Environment.NewLine, ct);
        var mode = (await ReadControlFileAsync(httpModeFile)).Trim();

        if (kind == "prtg")
        {
            if (mode is "prtg-healthy" or "all-sources-healthy" or "netiq-risk-audit-prtg-healthy")
            {
                await HandleSyntheticPrtgAsync(context, request.Url!, mode, ct);
                return;
            }
            if ((mode is "prtg-hold-503" or "netiq-risk-events-ai-hold-prtg-503") && Interlocked.CompareExchange(ref prtgHoldUsed, 1, 0) == 0)
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            await HttpReply(context, 503, "synthetic loopback PRTG source unavailable");
            return;
        }
        if (kind == "ai")
        {
            if (mode is "ai-healthy" or "all-sources-healthy")
            {
                await HttpReply(context, 200, JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-round53-synthetic",
                    @object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "round53-loopback",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "Synthetic loopback response. No real model was called." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
                }), "application/json");
                return;
            }
            if ((mode is "ai-hold" or "ai-hold-prtg-503" or "netiq-risk-events-ai-hold-prtg-503") && Interlocked.CompareExchange(ref aiHoldUsed, 1, 0) == 0)
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            await HttpReply(context, 503, "synthetic loopback AI backend paused or unavailable");
            return;
        }
        if (mode is "all-503" or "netiq-503")
        { await HttpReply(context, 503, "synthetic loopback NetIQ source unavailable"); return; }
        if (request.HttpMethod == "POST" && path.EndsWith("/SentinelAuthServices/auth/tokens", StringComparison.OrdinalIgnoreCase))
        { await HttpReply(context, 200, "{\"Token\":\"round53-loopback-token\"}", "application/json"); return; }
        if (request.HttpMethod == "POST" && path.EndsWith("/SentinelRESTServices/objects/event-search", StringComparison.OrdinalIgnoreCase))
        {
            var search = ParseSearch(body);
            var include = mode is "netiq-events" or "netiq-risk-events" or "netiq-audit-cleared-events" or "netiq-risk-audit-prtg-healthy" or "netiq-risk-events-ai-hold-prtg-503" or "late-events-released";
            var risk = mode is "netiq-risk-events" or "netiq-risk-events-ai-hold-prtg-503";
            var events = mode == "netiq-risk-audit-prtg-healthy"
                ? BuildEvents(search.Start, search.End, search.ScopedIps, true).Concat(BuildAuditClearedEvent(search.Start, search.End, search.ScopedIps)).ToArray()
                : mode == "netiq-audit-cleared-events"
                ? BuildAuditClearedEvent(search.Start, search.End, search.ScopedIps)
                : include ? BuildEvents(search.Start, search.End, search.ScopedIps, risk) : Array.Empty<object>();
            await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(new { Events = events, ResultHref = new Uri(httpBase, "SentinelRESTServices/objects/event?query=_jobid_.round53-job&page=1&pagesize=500").ToString() }), ct);
            context.Response.StatusCode = 201;
            context.Response.Headers["Location"] = new Uri(httpBase, "SentinelRESTServices/objects/event-search/round53-job").ToString();
            await HttpReply(context, 201, "{}", "application/json");
            return;
        }
        if (request.HttpMethod == "GET" && path.EndsWith("/SentinelRESTServices/objects/event-search/round53-job", StringComparison.OrdinalIgnoreCase))
        {
            var state = await ReadStateAsync();
            var count = state.Events.Length;
            await HttpReply(context, 200, JsonSerializer.Serialize(new { status = 2, found = count, avail = count, results = count == 0 ? null : new { href = state.ResultHref } }), "application/json");
            return;
        }
        if (request.HttpMethod == "GET" && path.EndsWith("/SentinelRESTServices/objects/event", StringComparison.OrdinalIgnoreCase))
        { await HttpReply(context, 200, JsonSerializer.Serialize(new { events = (await ReadStateAsync()).Events }), "application/json"); return; }
        if (request.HttpMethod == "DELETE") { await HttpReply(context, 200, "{}", "application/json"); return; }
        await HttpReply(context, 404, "unsupported synthetic loopback endpoint");
    }
    catch (Exception ex)
    {
        try { await File.AppendAllTextAsync(Path.Combine(runRoot, "stub-errors.log"), DateTimeOffset.UtcNow.ToString("O") + " " + ex.GetType().Name + Environment.NewLine); } catch { }
        try { context.Response.Abort(); } catch { }
    }
    finally { requestGate.Release(); }
}

async Task HandleSyntheticPrtgAsync(HttpListenerContext context, Uri requestUrl, string mode, CancellationToken ct)
{
    if (requestUrl.AbsolutePath.EndsWith("status.json", StringComparison.OrdinalIgnoreCase))
    {
        await HttpReply(context, 200, "{\"prtg-version\":\"24.1.92.1554+\"}", "application/json");
        return;
    }
    if (mode == "netiq-risk-audit-prtg-healthy")
    {
        await HandleSyntheticPrtgNativeAsync(context, requestUrl, ct);
        return;
    }
    var query = System.Web.HttpUtility.ParseQueryString(requestUrl.Query);
    var content = query.Get("content") ?? "";
    var body = content.ToLowerInvariant() switch
    {
        "devices" => JsonSerializer.Serialize(new
        {
            treesize = 1,
            devices = new[] { new { objid = 53, device = "R53 synthetic host", host = "192.0.2.3", group = "Round53 fixture" } }
        }),
        "sensors" => JsonSerializer.Serialize(new
        {
            treesize = 1,
            sensors = new[] { new
            {
                objid = 5301, parentid = 53, device = "R53 synthetic host", sensor = "Synthetic Ping",
                type = "Ping", tags = "round53;synthetic", unit = "ms", status = "Up", status_raw = 3,
                paused = false, lastvalue = "12 ms", lastvalue_raw = 12, lastcheck = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                lastcheck_raw = DateTime.UtcNow.ToOADate(), interval = "60 s", primarychannel = 0
            } }
        }),
        "channels" => JsonSerializer.Serialize(new
        {
            channels = new[] { new { objid = 0, name = "Ping Time", lastvalue_raw = 12, unit = "ms" } }
        }),
        "messages" => JsonSerializer.Serialize(new { treesize = 0, messages = Array.Empty<object>() }),
        "groups" => JsonSerializer.Serialize(new { treesize = 1, groups = new[] { new { objid = 1, group = "Round53 fixture" } } }),
        _ => JsonSerializer.Serialize(new { treesize = 0, rows = Array.Empty<object>() })
    };

    if (requestUrl.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.OrdinalIgnoreCase))
    {
        await HttpReply(context, 200, "<prtg><result>0</result></prtg>", "application/xml");
        return;
    }
    if (requestUrl.AbsolutePath.EndsWith("historicdata.xml", StringComparison.OrdinalIgnoreCase))
    {
        var oa = DateTime.UtcNow.AddMinutes(-1).ToOADate().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var xml = $"<histdata totalcount=\"1\"><prtg-version>24.1.92.1554+</prtg-version><item><datetime_raw>{oa}</datetime_raw><value channel=\"Ping Time\" channelid=\"0\">12 ms</value><value_raw channel=\"Ping Time\" channelid=\"0\">12</value_raw></item></histdata>";
        await HttpReply(context, 200, xml, "application/xml");
        return;
    }
    if (requestUrl.AbsolutePath.EndsWith("getpasshash.htm", StringComparison.OrdinalIgnoreCase))
    {
        await HttpReply(context, 200, "530053", "text/plain");
        return;
    }
    await HttpReply(context, 200, body, "application/json");
}

async Task HandleSyntheticPrtgNativeAsync(HttpListenerContext context, Uri requestUrl, CancellationToken ct)
{
    var query = System.Web.HttpUtility.ParseQueryString(requestUrl.Query);
    var path = requestUrl.AbsolutePath;
    var devices = new[] {
        new { objid = 112, device = "R53 synthetic host", host = "192.0.2.3", group = "Round53 synthetic", status = "Up", tags = "round53;synthetic", paused = false, dependency = "" }
    };
    // Use a synthetic scan clock stable within each UTC quarter-hour, but refresh it
    // automatically so a long-running helper never keeps reporting a startup timestamp.
    var clockNow = DateTime.UtcNow;
    var quarterTicks = TimeSpan.FromMinutes(15).Ticks;
    var observedAt = new DateTime(clockNow.Ticks - clockNow.Ticks % quarterTicks, DateTimeKind.Utc);
    var oa = observedAt.ToOADate();
    var sensors = new[] {
        new { objid = 212, parentid = 112, device = devices[0].device, sensor = "Synthetic CPU", type = "CPU", tags = "round53;synthetic", unit = "%", status = "Up", status_raw = 3, paused = false, dependency = "", lastvalue = "35 %", lastvalue_raw = 35d, lastcheck = observedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), lastcheck_raw = oa, interval = "900 s", interval_raw = 900d, cumsince = "2026-10-01 00:00:00", cumsince_raw = "2026-10-01 00:00:00", primarychannel = 1212 },
        new { objid = 213, parentid = 112, device = devices[0].device, sensor = "Synthetic Memory", type = "Memory", tags = "round53;synthetic", unit = "%", status = "Up", status_raw = 3, paused = false, dependency = "", lastvalue = "42 %", lastvalue_raw = 42d, lastcheck = observedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), lastcheck_raw = oa, interval = "900 s", interval_raw = 900d, cumsince = "2026-10-01 00:00:00", cumsince_raw = "2026-10-01 00:00:00", primarychannel = 1213 },
        new { objid = 214, parentid = 112, device = devices[0].device, sensor = "Synthetic Disk Free", type = "Disk Free", tags = "round53;synthetic", unit = "%", status = "Up", status_raw = 3, paused = false, dependency = "", lastvalue = "65 %", lastvalue_raw = 65d, lastcheck = observedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), lastcheck_raw = oa, interval = "900 s", interval_raw = 900d, cumsince = "2026-10-01 00:00:00", cumsince_raw = "2026-10-01 00:00:00", primarychannel = 1214 }
    };
    if (path.EndsWith("getobjectproperty.htm", StringComparison.OrdinalIgnoreCase))
    {
        var sensorId = int.TryParse(query["id"], out var parsed) ? parsed : 0;
        var primary = sensorId is >= 212 and <= 214 ? sensorId + 1000 : 0;
        var value = query["name"] == "primarychannel" ? primary.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        await HttpReply(context, 200, $"<prtg><result>{value}</result></prtg>", "application/xml");
        return;
    }
    if (path.EndsWith("historicdata.xml", StringComparison.OrdinalIgnoreCase) || path.EndsWith("historicdata.json", StringComparison.OrdinalIgnoreCase))
    {
        var queryNow = DateTime.UtcNow;
        if (!TryParsePrtgUtc(query["sdate"], queryNow, out var rangeStart) ||
            !TryParsePrtgUtc(query["edate"], queryNow, out var rangeEnd) || rangeEnd <= rangeStart)
        {
            await HttpReply(context, 400, "invalid synthetic PRTG history time range", "text/plain");
            return;
        }
        var sensorId = int.TryParse(query["id"], out var parsed) ? parsed : 0;
        var sensor = sensors.SingleOrDefault(x => x.objid == sensorId);
        if (sensor is null) { await HttpReply(context, 404, "unknown synthetic sensor", "text/plain"); return; }
        var channelId = sensorId + 1000;
        var caption = sensorId switch { 212 => "CPU Load", 213 => "Memory Usage", _ => "Disk Free" };
        var value = sensor.lastvalue_raw;
        var hasSample = observedAt >= rangeStart && observedAt <= rangeEnd;
        var stamp = observedAt.ToOADate();
        if (path.EndsWith("historicdata.xml", StringComparison.OrdinalIgnoreCase))
        {
            var stampText = stamp.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var valueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var item = hasSample ? $"<item><datetime_raw>{stampText}</datetime_raw><value channel=\"{caption}\" channelid=\"{channelId}\">{valueText} %</value><value_raw channel=\"{caption}\" channelid=\"{channelId}\">{valueText}</value_raw></item>" : "";
            var xml = $"<histdata totalcount=\"{(hasSample ? 1 : 0)}\"><prtg-version>24.1.92.1554+</prtg-version>{item}</histdata>";
            await HttpReply(context, 200, xml, "application/xml");
        }
        else
        {
            var histRows = hasSample ? new[] { new {
                datetime = observedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " - " + observedAt.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                datetime_raw = stamp, value_raw = value, value_ = $"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)} %",
                coverage_raw = 10000, coverage = "100 %", channel = caption, channelid = channelId
            } } : [];
            var json = JsonSerializer.Serialize(new { histdata = histRows });
            await HttpReply(context, 200, json, "application/json");
        }
        return;
    }
    if (path.EndsWith("getpasshash.htm", StringComparison.OrdinalIgnoreCase))
    { await HttpReply(context, 200, "530053", "text/plain"); return; }
    var content = query["content"]?.ToLowerInvariant() ?? "";
    if (content is not ("devices" or "sensors" or "channels"))
    { await HttpReply(context, 400, "unsupported synthetic table content", "text/plain"); return; }
    var deviceRows = devices.Select(x => (object)x).ToArray();
    var sensorRows = sensors.Select(x => (object)x).ToArray();
    var channelRows = sensors.Select(x => (object)new { objid = x.objid + 1000, parentid = x.objid, name = x.objid switch { 212 => "CPU Load", 213 => "Memory Usage", _ => "Disk Free" }, lastvalue_raw = x.lastvalue_raw, unit = "%", scaling = 1, primary = true }).ToArray();
    var idFilter = int.TryParse(query["id"], out var id) ? id : (int?)null;
    var filterIds = query.GetValues("filter_objid")?.Select(x => int.TryParse(x, out var n) ? n : -1).ToHashSet();
    object[] rows = content switch
    {
        "devices" => deviceRows.Where(r => idFilter is null || GetInt(r, "objid") == idFilter).Where(r => filterIds is null || filterIds.Contains(GetInt(r, "objid"))).ToArray(),
        // PRTG's content=sensors&id=<device objid> query returns that device's child sensors.
        // Exact sensor consumers use filter_objid, which remains an independent intersection.
        "sensors" => sensorRows.Where(r => idFilter is null || GetInt(r, "objid") == idFilter || GetInt(r, "parentid") == idFilter).Where(r => filterIds is null || filterIds.Contains(GetInt(r, "objid"))).ToArray(),
        _ => channelRows.Where(r => idFilter is null || GetInt(r, "parentid") == idFilter).ToArray()
    };
    var total = rows.Length;
    var start = int.TryParse(query["start"], out var startValue) ? Math.Clamp(startValue, 0, total) : 0;
    var count = int.TryParse(query["count"], out var countValue) ? Math.Clamp(countValue, 0, 1000) : total;
    rows = rows.Skip(start).Take(count).ToArray();
    var payload = JsonSerializer.Serialize(new Dictionary<string, object> { ["treesize"] = total, [content] = rows });
    await HttpReply(context, 200, payload, "application/json");

    static int GetInt(object row, string property) => (int)(row.GetType().GetProperty(property)?.GetValue(row) ?? -1);
}

static bool TryParsePrtgUtc(string? value, DateTime nowUtc, out DateTime result)
{
    result = default;
    if (string.Equals(value, "now", StringComparison.OrdinalIgnoreCase))
    {
        result = nowUtc;
        return true;
    }
    if (value is not null && value.StartsWith("now-", StringComparison.OrdinalIgnoreCase))
    {
        var amountUnit = value[4..];
        if (amountUnit.Length >= 2 && int.TryParse(amountUnit[..^1], out var amount) && amount > 0)
        {
            var delta = amountUnit[^1] switch
            {
                's' or 'S' => TimeSpan.FromSeconds(amount),
                'm' or 'M' => TimeSpan.FromMinutes(amount),
                'h' or 'H' => TimeSpan.FromHours(amount),
                'd' or 'D' => TimeSpan.FromDays(amount),
                _ => TimeSpan.MinValue
            };
            if (delta != TimeSpan.MinValue) { result = nowUtc - delta; return true; }
        }
        return false;
    }
    return DateTime.TryParseExact(value, "yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out result);
}

async Task AcceptSmtpAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await smtp.AcceptTcpClientAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
        _ = ServeSmtpAsync(client, ct);
    }
}

async Task ServeSmtpAsync(TcpClient client, CancellationToken ct)
{
    using (client)
    using (var stream = client.GetStream())
    {
        var recipients = new List<string>();
        try
        {
            await WriteLine(stream, "220 round53-loopback.invalid ESMTP", ct);
            for (var commands = 0; commands < 64; commands++)
            {
                var line = await ReadLine(stream, 64 * 1024, ct);
                if (line is null) return;
                var mode = (await ReadControlFileAsync(smtpModeFile, ct)).Trim();
                if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                { await WriteLine(stream, "250-round53-loopback.invalid", ct); await WriteLine(stream, "250 SIZE 262144", ct); }
                else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                {
                    var sender = ExtractAddress(line[10..]);
                    if (!sender.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)) await WriteLine(stream, "550 fixture accepts .invalid addresses only", ct);
                    else await WriteLine(stream, "250 sender accepted", ct);
                }
                else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                {
                    var recipient = ExtractAddress(line[8..]);
                    if (!recipient.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)) await WriteLine(stream, "550 fixture accepts .invalid addresses only", ct);
                    else if (mode == "reject-next-recipient" && Interlocked.CompareExchange(ref rejectRecipientUsed, 1, 0) == 0)
                    { await WriteLine(stream, "451 temporary fixture refusal", ct); await AppendSmtp(new { atUtc = DateTimeOffset.UtcNow, runId, @event = "recipient-refused", recipient, mode }); }
                    else { recipients.Add(recipient); await WriteLine(stream, "250 recipient accepted", ct); }
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    if (recipients.Count == 0) { await WriteLine(stream, "503 no accepted recipients", ct); continue; }
                    await WriteLine(stream, "354 end with dot", ct);
                    var mime = await ReadData(stream, 256 * 1024, ct);
                    if (mode == "drop-after-data") { await AppendSmtp(new { atUtc = DateTimeOffset.UtcNow, runId, @event = "connection-dropped-after-data", recipients }); return; }
                    await WriteLine(stream, "250 queued", ct);
                    var record = new { atUtc = DateTimeOffset.UtcNow, runId, @event = "accepted", recipients, mimeBytes = Encoding.Latin1.GetByteCount(mime), mimeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(mime))).ToLowerInvariant(), syntheticOnly = true };
                    await AppendSmtp(record);
                    await File.AppendAllTextAsync(Path.Combine(runRoot, "smtp-inbox.eml"), "===== " + DateTimeOffset.UtcNow.ToString("O") + " =====\r\n" + mime + "\r\n", ct);
                    recipients.Clear();
                }
                else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase)) { recipients.Clear(); await WriteLine(stream, "250 reset", ct); }
                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase)) { await WriteLine(stream, "221 closing", ct); return; }
                else await WriteLine(stream, "250 ok", ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
}

async Task AppendSmtp(object record) => await File.AppendAllTextAsync(smtpLog, JsonSerializer.Serialize(record) + Environment.NewLine, CancellationToken.None);
async Task<StubState> ReadStateAsync()
{
    if (!File.Exists(stateFile)) return new StubState();
    try { return JsonSerializer.Deserialize<StubState>(await File.ReadAllTextAsync(stateFile)) ?? new StubState(); }
    catch { return new StubState(); }
}

static async Task<string> ReadControlFileAsync(string path, CancellationToken ct = default)
{
    for (var attempt = 0; ; attempt++)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            return await reader.ReadToEndAsync(ct);
        }
        catch (IOException) when (attempt < 4) { await Task.Delay(10, ct); }
    }
}
static string Classify(string path)
{
    if (path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) || path.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)) return "ai";
    if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) return "prtg";
    return "netiq";
}
static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken ct)
{
    using var output = new MemoryStream(); var buffer = new byte[8192];
    while (true) { var read = await input.ReadAsync(buffer, ct); if (read == 0) return output.ToArray(); if (output.Length + read > limit) throw new InvalidDataException("fixture body exceeds 1 MiB"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
}
static async Task HttpReply(HttpListenerContext context, int code, string body, string contentType = "text/plain")
{
    context.Response.StatusCode = code; context.Response.ContentType = contentType;
    var bytes = Encoding.UTF8.GetBytes(body); context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
}
static SearchInput ParseSearch(string? body)
{
    if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("event-search body required");
    using var doc = JsonDocument.Parse(body); var root = doc.RootElement;
    var start = DateTimeOffset.Parse(root.GetProperty("start").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    var end = DateTimeOffset.Parse(root.GetProperty("end").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    if (start >= end) throw new InvalidDataException("invalid event-search date range");
    var filter = root.TryGetProperty("filter", out var f) ? f.GetString() ?? "" : "";
    var ips = Regex.Matches(filter, @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)").Select(m => m.Value).Distinct().ToArray();
    var allowed = new[] { "192.0.2.1", "192.0.2.2", "192.0.2.3", "192.0.2.4" };
    var scoped = ips.Length == 0 ? allowed : ips.Where(allowed.Contains).ToArray();
    return new SearchInput(start, end, scoped);
}
static object[] BuildEvents(DateTimeOffset start, DateTimeOffset end, string[] ips, bool riskEvents)
{
    var now = DateTimeOffset.UtcNow; var from = start > now.AddDays(-3660) ? start : now.AddDays(-3660); var until = end < now.AddTicks(-1) ? end : now.AddTicks(-1);
    if (from >= until) return [];
    var at = from + TimeSpan.FromTicks((until - from).Ticks / 2); var timestamp = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
    return ips.SelectMany((ip, hostIndex) => Enumerable.Range(0, riskEvents ? 12 : 1).Select(index => (object)new Dictionary<string, string>
    {
        ["dt"] = timestamp, ["sev"] = "0", ["dhn"] = $"R53-SYNTH-DC{hostIndex + 1:00}", ["sn"] = $"R53-SYNTH-DC{hostIndex + 1:00}",
        ["repip"] = ip, ["sip"] = riskEvents ? $"198.51.100.{10 + index}" : "198.51.100.10", ["obssvcname"] = "Microsoft-Windows-Security-Auditing",
        ["rv150"] = "Security", ["rv40"] = "4625", ["evt"] = "Synthetic failed logon", ["msg"] = "Synthetic fixture event; no real Sentinel data.",
        ["sun"] = riskEvents ? $"r53.synthetic{index:00}" : "r53.synthetic", ["xdasoutcome"] = "1"
    })).ToArray();
}
static object[] BuildAuditClearedEvent(DateTimeOffset start, DateTimeOffset end, string[] ips)
{
    var now = DateTimeOffset.UtcNow;
    var from = start > now.AddDays(-3660) ? start : now.AddDays(-3660);
    var until = end < now.AddTicks(-1) ? end : now.AddTicks(-1);
    if (from >= until || ips.Length == 0) return [];
    var at = from + TimeSpan.FromTicks((until - from).Ticks / 2);
    var timestamp = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
    return [new Dictionary<string, string>
    {
        ["dt"] = timestamp,
        ["sev"] = "0",
        ["dhn"] = "R53-SYNTH-DC01",
        ["sn"] = "R53-SYNTH-DC01",
        ["repip"] = ips[0],
        ["sip"] = "198.51.100.12",
        ["obssvcname"] = "Microsoft-Windows-Security-Auditing",
        ["rv150"] = "Security",
        ["rv40"] = "1102",
        ["evt"] = "Security audit log cleared",
        ["msg"] = "Synthetic loopback fixture event: Security audit log cleared (Event ID 1102); no real Sentinel data.",
        ["sun"] = "r53.synthetic-audit",
        ["xdasoutcome"] = "1",
        ["r53FixtureSourceKind"] = "synthetic"
    }];
}
static string ExtractAddress(string raw) { var left = raw.IndexOf('<'); var right = raw.IndexOf('>'); return left >= 0 && right > left ? raw[(left + 1)..right] : raw.Trim(); }
static async Task<string?> ReadLine(NetworkStream stream, int limit, CancellationToken ct)
{
    var bytes = new List<byte>(128); var one = new byte[1];
    while (bytes.Count <= limit)
    {
        var count = await stream.ReadAsync(one, ct); if (count == 0) return bytes.Count == 0 ? null : throw new EndOfStreamException("SMTP line ended without CRLF.");
        if (one[0] == (byte)'\n') { if (bytes.Count == 0 || bytes[^1] != (byte)'\r') throw new InvalidDataException("SMTP line did not use CRLF."); bytes.RemoveAt(bytes.Count - 1); return Encoding.Latin1.GetString(bytes.ToArray()); }
        bytes.Add(one[0]);
    }
    throw new InvalidDataException("SMTP line limit exceeded.");
}
static async Task<string> ReadData(NetworkStream stream, int maxBytes, CancellationToken ct)
{
    var text = new StringBuilder(); var count = 0;
    while (true) { var line = await ReadLine(stream, Math.Min(64 * 1024, maxBytes - count), ct) ?? throw new EndOfStreamException("SMTP DATA ended before terminator."); if (line == ".") return text.ToString(); var unstuffed = line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line; count = checked(count + Encoding.Latin1.GetByteCount(unstuffed) + 2); if (count > maxBytes) throw new InvalidDataException("SMTP DATA limit exceeded."); text.Append(unstuffed).Append("\r\n"); }
}
static async Task WriteLine(NetworkStream stream, string text, CancellationToken ct) { var bytes = Encoding.ASCII.GetBytes(text + "\r\n"); await stream.WriteAsync(bytes, ct); }
sealed record SearchInput(DateTimeOffset Start, DateTimeOffset End, string[] ScopedIps);
sealed class StubState { public string? ResultHref { get; set; } public object[] Events { get; set; } = []; }

using System.Text;
using System.Text.Json;
using LogForesight.Web.Filters;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// CSP 違規回報端點（<c>POST api/csp-report</c>）：瀏覽器依政策的 <c>report-uri</c> 自動送來，
/// 不帶登入 Cookie 以外的任何自訂標頭，因此 <c>[AllowAnonymous]</c>，並由 CsrfHeaderMiddleware 精確放行此路徑。
///
/// 因為對匿名者開放，防護放在端點自身：本文最多讀 <see cref="MaxBodyBytes"/> bytes（超過回 413、不記錄）、
/// 每來源 IP 節流（超過回 429、不記錄）、同一「指令＋blocked-uri」去重後才寫 web.log（Info，logger 名 <see cref="LoggerName"/>）。
/// 自行讀 <c>Request.Body</c> 而不用 <c>[FromBody]</c>：<c>application/csp-report</c> 不是 JSON 媒體類型，會被模型繫結擋成 415。
/// 其餘情況（含無法解析）一律 204、不回內容。
/// </summary>
[ApiController]
[Route("api/csp-report")]
public class CspReportController : ControllerBase
{
    public const string LoggerName = "LogForesight.Web.CspReport";
    public const int MaxBodyBytes = 8192;
    private const int MaxFieldChars = 512;

    private static readonly Logger Log = LogManager.GetLogger(LoggerName);

    private readonly CspReportIpThrottle _ipThrottle;
    private readonly CspReportDedupThrottle _dedupThrottle;

    public CspReportController(CspReportIpThrottle ipThrottle, CspReportDedupThrottle dedupThrottle)
    {
        _ipThrottle = ipThrottle;
        _dedupThrottle = dedupThrottle;
    }

    private sealed record Violation(string DocumentUri, string Directive, string BlockedUri, string OriginalPolicy);

    [HttpPost]
    [AllowAnonymous]
    [SkipDataVersionBump]
    public async Task<IActionResult> Report()
    {
        var body = await ReadBodyAsync(Request.Body);
        if (body == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_ipThrottle.TryAcquire(ip, DateTime.UtcNow, out var retryAfter))
        {
            Response.Headers["Retry-After"] = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        foreach (var violation in Parse(body))
        {
            var key = violation.Directive + "|" + violation.BlockedUri;
            if (!_dedupThrottle.TryAcquire(key, DateTime.UtcNow)) continue;
            Log.Info("CSP 違規回報：document-uri={DocumentUri}；violated-directive={Directive}；blocked-uri={BlockedUri}；original-policy={OriginalPolicy}",
                violation.DocumentUri, violation.Directive, violation.BlockedUri, violation.OriginalPolicy);
        }

        return NoContent();
    }

    /// <summary>最多讀 MaxBodyBytes＋1 bytes；超過上限回 null</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream stream)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read == 0) break;
            total += read;
        }
        return total > MaxBodyBytes ? null : buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>
    /// 三種 content-type 的形狀都試：舊式 <c>{"csp-report":{…}}</c>、Reporting API 陣列（每筆 type=csp-violation、body 為 camelCase）、
    /// 以及單筆 Reporting API 物件。無法解析回空集合。
    /// </summary>
    private static IReadOnlyList<Violation> Parse(byte[] body)
    {
        var result = new List<Violation>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) AddFromReportingApi(item, result);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("csp-report", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                {
                    result.Add(new Violation(
                        Clean(Str(legacy, "document-uri")),
                        Clean(Str(legacy, "violated-directive") is { Length: > 0 } v ? v : Str(legacy, "effective-directive")),
                        Clean(Str(legacy, "blocked-uri")),
                        Clean(Str(legacy, "original-policy"))));
                }
                else
                {
                    AddFromReportingApi(root, result);
                }
            }
        }
        catch (JsonException)
        {
            // 無法解析：當作沒有違規
        }
        return result;
    }

    private static void AddFromReportingApi(JsonElement item, List<Violation> result)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        if (!string.Equals(Str(item, "type"), "csp-violation", StringComparison.Ordinal)) return;
        if (!item.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object) return;
        result.Add(new Violation(
            Clean(Str(b, "documentURL")),
            Clean(Str(b, "effectiveDirective")),
            Clean(Str(b, "blockedURL")),
            Clean(Str(b, "originalPolicy"))));
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>各欄截 512 字，換行換成空白（防日誌注入與偽造多行紀錄）</summary>
    private static string Clean(string value)
    {
        if (value.Length > MaxFieldChars) value = value[..MaxFieldChars];
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }
}

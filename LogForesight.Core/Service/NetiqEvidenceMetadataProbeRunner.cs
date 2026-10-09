using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LogForesight.Core.Service;

public sealed record NetiqEvidenceMetadataProbeResult(bool Success, string Report);

/// <summary>
/// One-query, bounded Sentinel schema-shape probe. It deliberately reports candidate shapes only;
/// field names and parseability do not prove native-reference or resource semantics.
/// </summary>
public static class NetiqEvidenceMetadataProbeRunner
{
    public const int DeadlineSeconds = 30;
    public const int MaxEvents = 3;
    public const int MaxFieldKeys = 128;
    public const int MaxReportBytes = 32 * 1024;
    public const int MaxResponseBytes = 512 * 1024;
    public const int MaxHttpRequests = 12;

    private static readonly Regex SafeFieldName = new("^[A-Za-z][A-Za-z0-9_.:-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<NetiqEvidenceMetadataProbeResult> RunAsync(
        Sentinel sentinel, NetiqOptions settings, CancellationToken deadlineToken)
    {
        return await RunAsync(SentinelConnectionFactory.ToConnectable(sentinel), settings,
            (server, options, limits) => new SentinelClient(server, options, readLimits: limits), deadlineToken, sentinel.SentinelId);
    }

    internal static async Task<NetiqEvidenceMetadataProbeResult> RunAsync(
        SentinelServer server, NetiqOptions settings,
        Func<SentinelServer, NetiqOptions, SentinelClientReadLimits, SentinelClient> clientFactory,
        CancellationToken deadlineToken, long? sourceSentinelId = null)
    {
        using var boundedDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        boundedDeadline.CancelAfter(TimeSpan.FromSeconds(DeadlineSeconds));
        var probeToken = boundedDeadline.Token;
        SentinelClient? client = null;
        try
        {
            client = clientFactory(server, settings, new SentinelClientReadLimits(MaxHttpRequests, MaxResponseBytes));
            var end = DateTimeOffset.UtcNow;
            var start = end.AddHours(-1);
            var result = await client.SearchAsync(new SentinelSearchRequest(
                "sev:[0 TO 5]", start, end, PageSize: MaxEvents, MaxResults: MaxEvents,
                MaxPages: 1, MaxShapeFieldKeys: MaxFieldKeys, RawFields: true, ShapeOnly: true), probeToken);

            if (result.State != SentinelJobState.Completed) return Failure("job-not-completed");
            if (result.Events.Count > MaxEvents) return Failure("event-limit-exceeded");
            if (result.Events.Sum(evt => evt.Shapes.Count) > MaxFieldKeys) return Failure("field-key-limit-exceeded");
            if (result.Found > 0 && result.Events.Count == 0) return Failure("response-shape-unrecognized");

            var report = FormatReport(result, start, end, sourceSentinelId);
            if (Encoding.UTF8.GetByteCount(report) > MaxReportBytes) return Failure("report-byte-limit-exceeded");
            return new NetiqEvidenceMetadataProbeResult(true, report);
        }
        catch (OperationCanceledException) when (probeToken.IsCancellationRequested)
        {
            return Failure("deadline-reached");
        }
        catch (SentinelClientException) when (probeToken.IsCancellationRequested)
        {
            return Failure("deadline-reached");
        }
        catch (SentinelClientException ex)
        {
            return Failure(AllowedFailureCode(ex.SafeCode));
        }
        catch
        {
            return Failure("probe-failed-details-withheld");
        }
        finally
        {
            if (client is not null) await client.DisposeAsync(probeToken);
        }
    }

    internal static string FormatReport(SentinelSearchResult result,
        DateTimeOffset? queryStartUtc = null, DateTimeOffset? queryEndUtc = null, long? sourceSentinelId = null)
    {
        var output = new StringBuilder();
        output.AppendLine("NetIQ response field-shape probe");
        output.Append("sentinel-id=")
            .Append(sourceSentinelId is > 0 ? sourceSentinelId.Value.ToString(CultureInfo.InvariantCulture) : "unknown")
            .AppendLine();
        var version = typeof(SentinelClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        output.Append("schema-version=1; program-version=")
            .Append(version is not null && Regex.IsMatch(version, "^[A-Za-z0-9.+_-]{1,128}$") ? version : "unknown")
            .AppendLine();
        output.Append("query-start-utc=").Append(queryStartUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown")
            .Append("; query-end-utc=").Append(queryEndUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown")
            .AppendLine();
        output.AppendLine("Candidate shapes only; not semantic proof; does not authorize strong match.");
        output.Append("query-window=1h; events=").Append(result.Events.Count)
            .Append("; found-count=").Append(result.Found.ToString(CultureInfo.InvariantCulture))
            .Append("; sample-truncated=").Append(result.Truncated.ToString().ToLowerInvariant()).AppendLine();

        for (var index = 0; index < result.Events.Count; index++)
        {
            var shapes = result.Events[index].Shapes;
            output.Append("event[").Append(index + 1).Append("] field-count=")
                .Append(shapes.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
            var redactedFieldNameCount = 0;
            foreach (var pair in shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!SafeFieldName.IsMatch(pair.Key))
                {
                    redactedFieldNameCount++;
                    continue;
                }

                var shape = pair.Value;
                output.Append("field=").Append(pair.Key)
                    .Append("; json-kind=").Append(shape.JsonKind)
                    .Append("; present=true")
                    .Append("; value-length-utf16=").Append(shape.ValueLength?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
                    .Append("; parses-as-time=").Append(shape.ParsesAsTimestamp?.ToString().ToLowerInvariant() ?? "n/a")
                    .Append("; explicit-offset=").Append(shape.HasExplicitOffset?.ToString().ToLowerInvariant() ?? "n/a")
                    .AppendLine();
            }
            if (redactedFieldNameCount > 0)
                output.Append("redacted-unsafe-field-name-count=")
                    .Append(redactedFieldNameCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }

        return output.ToString();
    }

    private static string AllowedFailureCode(string code) => code switch
    {
        "request-limit" or "response-byte-limit" or "page-limit" or "malformed-response" or "event-limit" or "field-key-limit" or "source-endpoint-mismatch" => code,
        _ => "query-failed-details-withheld"
    };

    private static NetiqEvidenceMetadataProbeResult Failure(string code) =>
        new(false, $"NetIQ response field-shape probe failed; reason={code}. No response values or exception details are shown.");
}

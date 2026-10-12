using System.Diagnostics;
using LogForesight.Core;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>One bounded fresh-primary proof group for a source that omits snapshot primarychannel.</summary>
public static class PrtgSnapshotPrimaryChannelFallback
{
    public const int MaximumSensorsPerGroup = 5;

    /// <summary>
    /// A capacity candidate may not have a trusted profile yet. Its current saved binding is
    /// enough to fence the source property during the fallback measurement; a profile, when
    /// present, must agree with that same binding. Formal parsing still requires a profile.
    /// </summary>
    public static bool MatchesCapturedBinding(PrtgNativePrimaryChannelBracket bracket,
        PrtgTrustedSamplingBinding? binding, PrtgTrustedSamplingProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(bracket);
        if (binding is null || !binding.IsValid() || bracket.SensorObjid != binding.SensorObjid ||
            bracket.BeforeId != binding.ChannelObjectId || bracket.AfterId != binding.ChannelObjectId)
            return false;
        return profile is null ||
            (profile.SensorObjid == binding.SensorObjid &&
             profile.PrimaryChannelId == binding.ChannelObjectId &&
             profile.BindingRevision == binding.BindingRevision &&
             profile.QualificationProofReference == binding.QualificationProofReference);
    }

    public sealed record Result(
        string SnapshotJson,
        DateTime SnapshotReceivedAtUtc,
        IReadOnlyDictionary<long, System.Text.Json.JsonElement> Rows,
        IReadOnlyDictionary<long, PrtgNativePrimaryChannelBracket> Brackets,
        int RequestsSent,
        long ElapsedMilliseconds);

    /// <summary>
    /// Reads each candidate property, then one exact bounded sensor-table refetch, then each
    /// property again. Callers must fence profile/policy/scope state after this operation before
    /// using any returned observation.
    /// </summary>
    public static async Task<Result> ReadGroupAsync(PrtgClient client, IReadOnlyList<long> sensorObjids,
        CancellationToken cancellationToken, Action? onRequestAttempted = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sensorObjids);
        if (sensorObjids.Count is < 1 or > MaximumSensorsPerGroup ||
            sensorObjids.Any(id => id <= 0) || sensorObjids.Distinct().Count() != sensorObjids.Count ||
            !sensorObjids.SequenceEqual(sensorObjids.Order()))
            throw new ArgumentException("Primary-channel fallback requires one to five sorted distinct sensor IDs.", nameof(sensorObjids));

        var timer = Stopwatch.StartNew();
        var requestsSent = 0;
        var before = new Dictionary<long, (string Id, DateTime At)>();
        foreach (var id in sensorObjids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRequestAttempted?.Invoke();
            var response = await client.GetBoundedXmlAsync(
                $"api/getobjectproperty.htm?id={id}&name=primarychannel", 16 * 1024, cancellationToken);
            requestsSent++;
            var primaryId = PrtgTrustedSamplingProbeService.ParseNativePrimaryProperty(response.Content);
            before.Add(id, (primaryId, DateTime.UtcNow));
        }

        cancellationToken.ThrowIfCancellationRequested();
        onRequestAttempted?.Invoke();
        var snapshotJson = await client.GetBoundedJsonAsync(
            PrtgSnapshotTargetResolver.BuildSnapshotRelativeUrl(sensorObjids),
            PrtgSnapshotResponseContract.MaximumResponseBytes, cancellationToken);
        requestsSent++;
        var validation = PrtgSnapshotResponseContract.Validate(snapshotJson, sensorObjids);
        if (!validation.Compatible)
            throw new InvalidDataException("primarychannel-fallback-snapshot-shape-invalid");
        var snapshotReceivedAtUtc = DateTime.UtcNow;
        var rows = ReadRows(snapshotJson);
        if (sensorObjids.Any(id => HasPrimaryChannelField(rows[id])))
            throw new InvalidDataException("primarychannel-fallback-source-mode-changed");

        var brackets = new Dictionary<long, PrtgNativePrimaryChannelBracket>();
        foreach (var id in sensorObjids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRequestAttempted?.Invoke();
            var response = await client.GetBoundedXmlAsync(
                $"api/getobjectproperty.htm?id={id}&name=primarychannel", 16 * 1024, cancellationToken);
            requestsSent++;
            var primaryId = PrtgTrustedSamplingProbeService.ParseNativePrimaryProperty(response.Content);
            var afterAt = DateTime.UtcNow;
            if (primaryId != before[id].Id)
                throw new InvalidDataException("primarychannel-fallback-property-changed");
            var bracket = new PrtgNativePrimaryChannelBracket(id, before[id].Id, primaryId,
                before[id].At, snapshotReceivedAtUtc, afterAt);
            if (afterAt - before[id].At > PrtgNativePrimaryChannelBracket.MaximumWindow)
                throw new InvalidDataException("primarychannel-fallback-bracket-window-exceeded");
            brackets.Add(id, bracket);
        }
        timer.Stop();
        return new(snapshotJson, snapshotReceivedAtUtc, rows, brackets, requestsSent,
            (long)Math.Ceiling(Math.Max(0, timer.Elapsed.TotalMilliseconds)));
    }

    private static Dictionary<long, System.Text.Json.JsonElement> ReadRows(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json,
            new System.Text.Json.JsonDocumentOptions { MaxDepth = PrtgSnapshotResponseContract.MaximumDepth });
        var result = new Dictionary<long, System.Text.Json.JsonElement>();
        foreach (var row in document.RootElement.GetProperty("sensors").EnumerateArray())
        {
            var id = row.GetProperty("objid");
            var sensorId = id.ValueKind == System.Text.Json.JsonValueKind.Number
                ? id.GetInt64()
                : long.Parse(id.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            result.Add(sensorId, row.Clone());
        }
        return result;
    }

    private static bool HasPrimaryChannelField(System.Text.Json.JsonElement row) =>
        row.EnumerateObject().Any(property => property.Name.Equals("primarychannel", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("primarychannel_raw", StringComparison.OrdinalIgnoreCase));
}

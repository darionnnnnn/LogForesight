using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Service;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>timeline progress UI 所需的有界標量；不包含 states、coverage 或原始 JSON。</summary>
public sealed record PrtgTimelineMetadata(
    long SensorId,
    long? HostId,
    string? SourceGeneration,
    string? ResourceGeneration,
    string? EffectiveScopeFingerprint,
    string? BootstrapStatus,
    DateTimeOffset? BootstrapStartedAt,
    DateTimeOffset? BootstrapDeadlineAt,
    int? BootstrapPagesRead,
    DateTimeOffset? LastCompleteThrough,
    DateTimeOffset? NextAttemptAt,
    int? PendingNextPage,
    string? QualityReason,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? CoveredFrom,
    DateTimeOffset? CoveredThrough,
    int? CoverageSpanCount,
    int? StateCount,
    DateTimeOffset? DiskSemanticValidFrom,
    DateTimeOffset? DiskSemanticCheckedAt,
    bool Malformed,
    long? IdentityEpoch = null,
    string? ChannelGeneration = null,
    string? BootstrapCollectionCycleId = null,
    long Version = 0);

public sealed record PrtgTimelineMetadataReadResult(
    IReadOnlyDictionary<long, PrtgTimelineMetadata> Rows,
    bool StableVersions);

/// <summary>EF Core raw-sql unmapped projection；所有字串欄位在 SQL 端先截短並另帶原長度。</summary>
public sealed class PrtgTimelineMetadataProjectionRow
{
    public string BlobKey { get; set; } = string.Empty;
    public long Version { get; set; }
    public int IsJsonValid { get; set; }
    public string? SensorIdText { get; set; }
    public long? SensorIdLength { get; set; }
    public string? HostIdText { get; set; }
    public long? HostIdLength { get; set; }
    public string? SourceGenerationText { get; set; }
    public long? SourceGenerationLength { get; set; }
    public string? ResourceGenerationText { get; set; }
    public long? ResourceGenerationLength { get; set; }
    public string? IdentityEpochText { get; set; }
    public long? IdentityEpochLength { get; set; }
    public string? ChannelGenerationText { get; set; }
    public long? ChannelGenerationLength { get; set; }
    public string? BootstrapCollectionCycleIdText { get; set; }
    public long? BootstrapCollectionCycleIdLength { get; set; }
    public string? EffectiveScopeFingerprintText { get; set; }
    public long? EffectiveScopeFingerprintLength { get; set; }
    public string? BootstrapStatusText { get; set; }
    public long? BootstrapStatusLength { get; set; }
    public string? BootstrapStartedAtText { get; set; }
    public long? BootstrapStartedAtLength { get; set; }
    public string? BootstrapDeadlineAtText { get; set; }
    public long? BootstrapDeadlineAtLength { get; set; }
    public string? BootstrapPagesReadText { get; set; }
    public long? BootstrapPagesReadLength { get; set; }
    public string? LastCompleteThroughText { get; set; }
    public long? LastCompleteThroughLength { get; set; }
    public string? NextAttemptAtText { get; set; }
    public long? NextAttemptAtLength { get; set; }
    public string? PendingNextPageText { get; set; }
    public long? PendingNextPageLength { get; set; }
    public string? QualityReasonText { get; set; }
    public long? QualityReasonLength { get; set; }
    public string? ValidFromText { get; set; }
    public long? ValidFromLength { get; set; }
    public string? LastAttemptAtText { get; set; }
    public long? LastAttemptAtLength { get; set; }
    public string? CoveredFromText { get; set; }
    public long? CoveredFromLength { get; set; }
    public string? CoveredThroughText { get; set; }
    public long? CoveredThroughLength { get; set; }
    public long? CoverageSpanCount { get; set; }
    public long? StateCount { get; set; }
    public int CoverageShapeValid { get; set; }
    public int StatesShapeValid { get; set; }
    public string? DiskSemanticValidFromText { get; set; }
    public long? DiskSemanticValidFromLength { get; set; }
    public string? DiskSemanticCheckedAtText { get; set; }
    public long? DiskSemanticCheckedAtLength { get; set; }
}

public sealed class PrtgTimelineMetadataVersionProjectionRow
{
    public string BlobKey { get; set; } = string.Empty;
    public long Version { get; set; }
}

/// <summary>
/// 以一個 bounded JSON scalar projection 及一個版本重讀，供管理端頁面顯示 timeline 進度。
/// 查詢不可把 lf_blobs.content 全欄位傳回應用程式。
/// </summary>
public sealed class PrtgTimelineMetadataQuery(Func<LfDbContext> contextFactory)
{
    public const int MaximumPageSize = 100;
    private const int GenerationLimit = 128;
    private const int FingerprintLimit = 128;
    private const int StatusLimit = 48;
    private const int TimestampLimit = 48;
    private const int QualityReasonLimit = 192;

    public async Task<PrtgTimelineMetadataReadResult> ReadAsync(
        IReadOnlyCollection<long> sensorIds, string blobPrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensorIds);
        ArgumentNullException.ThrowIfNull(blobPrefix);
        if (!string.Equals(blobPrefix, PrtgSensorTimelineStore.Prefix, StringComparison.Ordinal))
            throw new ArgumentException("timeline metadata 查詢只允許使用逐 sensor timeline blob prefix。", nameof(blobPrefix));
        if (sensorIds.Count > MaximumPageSize || sensorIds.Any(id => id <= 0) || sensorIds.Distinct().Count() != sensorIds.Count)
            throw new ArgumentOutOfRangeException(nameof(sensorIds), "每次只能讀取不重複的 1–100 個正整數感測器識別碼。");
        if (sensorIds.Count == 0)
            return new PrtgTimelineMetadataReadResult(new Dictionary<long, PrtgTimelineMetadata>(), true);

        var keys = sensorIds.Order().Select(id => (SensorId: id, Key: blobPrefix + id.ToString(CultureInfo.InvariantCulture))).ToArray();
        using var context = contextFactory();
        var provider = context.Database.ProviderName ?? string.Empty;
        var sqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        var sqlite = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (!sqlServer && !sqlite)
            throw new NotSupportedException($"PRTG timeline metadata 尚未支援資料提供者 {provider}。");

        var metadata = await ReadMetadataAsync(context, sqlServer, keys, cancellationToken).ConfigureAwait(false);
        var versions = await ReadVersionsAsync(context, keys, cancellationToken).ConfigureAwait(false);
        var stable = keys.All(item =>
            metadata.TryGetValue(item.SensorId, out var row)
                ? versions.TryGetValue(item.Key, out var version) && version == row.Version
                : !versions.ContainsKey(item.Key));
        return new PrtgTimelineMetadataReadResult(metadata.ToDictionary(
            pair => pair.Key, pair => pair.Value.Value), stable);
    }

    private sealed record VersionedMetadata(PrtgTimelineMetadata Value, long Version);

    private static async Task<Dictionary<long, VersionedMetadata>> ReadMetadataAsync(
        LfDbContext context, bool sqlServer,
        IReadOnlyList<(long SensorId, string Key)> keys, CancellationToken cancellationToken)
    {
            var (parameters, names) = AddKeyParameters(context, keys);
        var select = new List<string>
        {
            sqlServer ? "b.blob_key AS BlobKey" : "blob_key AS BlobKey",
            sqlServer ? "b.version AS Version" : "version AS Version",
            JsonValidity(sqlServer),
            JsonScalar("SensorId", 32, sqlServer), JsonLength("SensorId", sqlServer),
            JsonScalar("HostId", 32, sqlServer), JsonLength("HostId", sqlServer),
            JsonScalar("SourceGeneration", GenerationLimit, sqlServer), JsonLength("SourceGeneration", sqlServer),
            JsonScalar("ResourceGeneration", GenerationLimit, sqlServer), JsonLength("ResourceGeneration", sqlServer),
            JsonScalar("IdentityEpoch", 16, sqlServer), JsonLength("IdentityEpoch", sqlServer),
            JsonScalar("ChannelGeneration", GenerationLimit, sqlServer), JsonLength("ChannelGeneration", sqlServer),
            JsonScalar("BootstrapCollectionCycleId", 64, sqlServer), JsonLength("BootstrapCollectionCycleId", sqlServer),
            JsonScalar("EffectiveScopeFingerprint", FingerprintLimit, sqlServer), JsonLength("EffectiveScopeFingerprint", sqlServer),
            JsonScalar("BootstrapStatus", StatusLimit, sqlServer), JsonLength("BootstrapStatus", sqlServer),
            JsonScalar("BootstrapStartedAt", TimestampLimit, sqlServer), JsonLength("BootstrapStartedAt", sqlServer),
            JsonScalar("BootstrapDeadlineAt", TimestampLimit, sqlServer), JsonLength("BootstrapDeadlineAt", sqlServer),
            JsonScalar("BootstrapPagesRead", 16, sqlServer), JsonLength("BootstrapPagesRead", sqlServer),
            JsonScalar("LastCompleteThrough", TimestampLimit, sqlServer), JsonLength("LastCompleteThrough", sqlServer),
            JsonScalar("NextAttemptAt", TimestampLimit, sqlServer), JsonLength("NextAttemptAt", sqlServer),
            JsonScalar("PendingNextPage", 16, sqlServer), JsonLength("PendingNextPage", sqlServer),
            JsonScalar("QualityReason", QualityReasonLimit, sqlServer), JsonLength("QualityReason", sqlServer),
            JsonScalar("ValidFrom", TimestampLimit, sqlServer), JsonLength("ValidFrom", sqlServer),
            JsonScalar("LastAttemptAt", TimestampLimit, sqlServer), JsonLength("LastAttemptAt", sqlServer),
            JsonCoverageDate(true, sqlServer), JsonCoverageLength(true, sqlServer),
            JsonCoverageDate(false, sqlServer), JsonCoverageLength(false, sqlServer),
            ArrayCount("Coverage", "CoverageSpanCount", sqlServer), ArrayCount("States", "StateCount", sqlServer),
            ArrayShapeValid("Coverage", sqlServer), ArrayShapeValid("States", sqlServer),
            JsonScalar("DiskSemanticValidFrom", TimestampLimit, sqlServer), JsonLength("DiskSemanticValidFrom", sqlServer),
            JsonScalar("DiskSemanticCheckedAt", TimestampLimit, sqlServer), JsonLength("DiskSemanticCheckedAt", sqlServer)
        };
        var source = sqlServer
            ? $"FROM lf_blobs b CROSS APPLY (SELECT CASE WHEN ISJSON(b.content) = 1 AND LEFT(LTRIM(b.content), 1) = '{{' THEN b.content ELSE N'{{}}' END AS JsonContent) AS safe CROSS APPLY OPENJSON(safe.JsonContent) WITH ({OpenJsonColumns}) AS meta OUTER APPLY (SELECT COUNT_BIG(*) AS SpanCount FROM OPENJSON(CASE WHEN LEFT(LTRIM(JSON_QUERY(safe.JsonContent, '$.Coverage')), 1) = '[' THEN JSON_QUERY(safe.JsonContent, '$.Coverage') ELSE N'[]' END)) AS coverageMeta WHERE b.blob_key IN ({string.Join(", ", names)})"
            : $"FROM lf_blobs WHERE blob_key IN ({string.Join(", ", names)})";
        var sql = $"SELECT {string.Join(", ", select)} {source}";

        var result = new Dictionary<long, VersionedMetadata>();
        var rows = await context.Database.SqlQueryRaw<PrtgTimelineMetadataProjectionRow>(sql, parameters)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = row.BlobKey;
            var keyEntry = keys.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (keyEntry.SensorId == 0) continue;
            long sensorId = 0;
            long? hostId = null;
            string? sourceGeneration = null;
            string? resourceGeneration = null;
            long? identityEpoch = null;
            string? channelGeneration = null;
            string? collectionCycleId = null;
            string? scopeFingerprint = null;
            string? status = null;
            DateTimeOffset? startedAt = null, deadlineAt = null, completeThrough = null, nextAttemptAt = null;
            int? pagesRead = null, nextPage = null;
            string? qualityReason = null;
            DateTimeOffset? validFrom = null, lastAttemptAt = null, coveredFrom = null, coveredThrough = null;
            int? coverageSpanCount = null, stateCount = null;
            DateTimeOffset? diskSemanticValidFrom = null, diskSemanticCheckedAt = null;
            var malformed = row.IsJsonValid != 1 ||
                !TryLong(row.SensorIdText, row.SensorIdLength, out sensorId) || sensorId != keyEntry.SensorId ||
                !TryLongNullable(row.HostIdText, row.HostIdLength, out hostId) || hostId is null or <= 0 ||
                !TryString(row.SourceGenerationText, row.SourceGenerationLength, GenerationLimit, out sourceGeneration) || string.IsNullOrWhiteSpace(sourceGeneration) ||
                !TryString(row.ResourceGenerationText, row.ResourceGenerationLength, GenerationLimit, out resourceGeneration) || string.IsNullOrWhiteSpace(resourceGeneration) ||
                !TryNullableLong(row.IdentityEpochText, row.IdentityEpochLength, out identityEpoch) || identityEpoch is < 0 ||
                !TryOptionalString(row.ChannelGenerationText, row.ChannelGenerationLength, GenerationLimit, out channelGeneration) ||
                !TryOptionalString(row.BootstrapCollectionCycleIdText, row.BootstrapCollectionCycleIdLength, 64, out collectionCycleId) ||
                !TryString(row.EffectiveScopeFingerprintText, row.EffectiveScopeFingerprintLength, FingerprintLimit, out scopeFingerprint) || string.IsNullOrWhiteSpace(scopeFingerprint) ||
                !TryString(row.BootstrapStatusText, row.BootstrapStatusLength, StatusLimit, out status) || string.IsNullOrWhiteSpace(status) ||
                !TryNullableDate(row.BootstrapStartedAtText, row.BootstrapStartedAtLength, TimestampLimit, out startedAt) ||
                !TryNullableDate(row.BootstrapDeadlineAtText, row.BootstrapDeadlineAtLength, TimestampLimit, out deadlineAt) ||
                !TryNullableInt(row.BootstrapPagesReadText, row.BootstrapPagesReadLength, out pagesRead) || pagesRead is null or < 0 ||
                !TryNullableDate(row.LastCompleteThroughText, row.LastCompleteThroughLength, TimestampLimit, out completeThrough) ||
                !TryNullableDate(row.NextAttemptAtText, row.NextAttemptAtLength, TimestampLimit, out nextAttemptAt) ||
                !TryNullableInt(row.PendingNextPageText, row.PendingNextPageLength, out nextPage) || nextPage is null or < 0 ||
                !TryString(row.QualityReasonText, row.QualityReasonLength, QualityReasonLimit, out qualityReason) || string.IsNullOrWhiteSpace(qualityReason) ||
                row.CoverageShapeValid != 1 || row.StatesShapeValid != 1 ||
                !TryNullableDate(row.ValidFromText, row.ValidFromLength, TimestampLimit, out validFrom) ||
                !TryNullableDate(row.LastAttemptAtText, row.LastAttemptAtLength, TimestampLimit, out lastAttemptAt) ||
                !TryNullableDate(row.CoveredFromText, row.CoveredFromLength, TimestampLimit, out coveredFrom) ||
                !TryNullableDate(row.CoveredThroughText, row.CoveredThroughLength, TimestampLimit, out coveredThrough) ||
                !TryNullableCount(row.CoverageSpanCount, out coverageSpanCount) ||
                coverageSpanCount == 0 && (coveredFrom is not null || coveredThrough is not null) ||
                coverageSpanCount > 0 && (coveredFrom is null || coveredThrough is null ||
                    coveredFrom.Value > coveredThrough.Value) ||
                !TryNullableCount(row.StateCount, out stateCount) ||
                !TryNullableDate(row.DiskSemanticValidFromText, row.DiskSemanticValidFromLength, TimestampLimit, out diskSemanticValidFrom) ||
                !TryNullableDate(row.DiskSemanticCheckedAtText, row.DiskSemanticCheckedAtLength, TimestampLimit, out diskSemanticCheckedAt);

            var value = malformed
                ? new PrtgTimelineMetadata(keyEntry.SensorId, null, sourceGeneration, resourceGeneration,
                    scopeFingerprint, null, null, null, null, null, null, null, "metadata-malformed",
                    null, null, null, null, null, null, null, null, true, null, null, null, row.Version)
                : new PrtgTimelineMetadata(sensorId, hostId, sourceGeneration, resourceGeneration,
                    scopeFingerprint, status, startedAt, deadlineAt, pagesRead, completeThrough,
                    nextAttemptAt, nextPage, qualityReason, validFrom, lastAttemptAt, coveredFrom, coveredThrough,
                    coverageSpanCount, stateCount, diskSemanticValidFrom, diskSemanticCheckedAt, false,
                    identityEpoch, channelGeneration, collectionCycleId, row.Version);
            result[keyEntry.SensorId] = new VersionedMetadata(value, row.Version);
        }
        return result;
    }

    private static async Task<Dictionary<string, long>> ReadVersionsAsync(
        LfDbContext context, IReadOnlyList<(long SensorId, string Key)> keys,
        CancellationToken cancellationToken)
    {
        var (parameters, names) = AddKeyParameters(context, keys);
        var sql = $"SELECT blob_key AS BlobKey, version AS Version FROM lf_blobs WHERE blob_key IN ({string.Join(", ", names)})";
        var rows = await context.Database.SqlQueryRaw<PrtgTimelineMetadataVersionProjectionRow>(sql, parameters)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(row => row.BlobKey, row => row.Version, StringComparer.Ordinal);
    }

    private static (object[] Parameters, string[] Names) AddKeyParameters(
        LfDbContext context, IReadOnlyList<(long SensorId, string Key)> keys)
    {
        var names = new string[keys.Count];
        var parameters = new object[keys.Count];
        var sqlServer = (context.Database.ProviderName ?? string.Empty).Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < keys.Count; index++)
        {
            var name = "@key" + index.ToString(CultureInfo.InvariantCulture);
            DbParameter parameter = sqlServer
                ? new SqlParameter(name, SqlDbType.NVarChar, 100) { Value = keys[index].Key }
                : new SqliteParameter(name, DbType.String) { Value = keys[index].Key };
            parameters[index] = parameter;
            names[index] = name;
        }
        return (parameters, names);
    }

    private const string OpenJsonColumns = "SensorId nvarchar(max) '$.SensorId', HostId nvarchar(max) '$.HostId', " +
        "SourceGeneration nvarchar(max) '$.SourceGeneration', ResourceGeneration nvarchar(max) '$.ResourceGeneration', " +
        "IdentityEpoch nvarchar(max) '$.IdentityEpoch', ChannelGeneration nvarchar(max) '$.ChannelGeneration', " +
        "BootstrapCollectionCycleId nvarchar(max) '$.BootstrapCollectionCycleId', " +
        "EffectiveScopeFingerprint nvarchar(max) '$.EffectiveScopeFingerprint', BootstrapStatus nvarchar(max) '$.BootstrapStatus', " +
        "BootstrapStartedAt nvarchar(max) '$.BootstrapStartedAt', BootstrapDeadlineAt nvarchar(max) '$.BootstrapDeadlineAt', " +
        "BootstrapPagesRead nvarchar(max) '$.BootstrapPagesRead', LastCompleteThrough nvarchar(max) '$.LastCompleteThrough', " +
        "NextAttemptAt nvarchar(max) '$.NextAttemptAt', PendingNextPage nvarchar(max) '$.PendingNextPage', " +
        "QualityReason nvarchar(max) '$.QualityReason', ValidFrom nvarchar(max) '$.ValidFrom', " +
        "LastAttemptAt nvarchar(max) '$.LastAttemptAt', DiskSemanticValidFrom nvarchar(max) '$.DiskSemanticValidFrom', " +
        "DiskSemanticCheckedAt nvarchar(max) '$.DiskSemanticCheckedAt'";

    private static string JsonScalar(string field, int cap, bool sqlServer) => sqlServer
        ? $"LEFT(meta.[{field}], {cap + 1}) AS [{field}Text]"
        : $"CASE WHEN json_valid(content) = 1 THEN substr(CAST(json_extract(content, '$.{field}') AS TEXT), 1, {cap + 1}) END AS [{field}Text]";

    private static string JsonLength(string field, bool sqlServer) => sqlServer
        ? $"DATALENGTH(meta.[{field}]) / 2 AS [{field}Length]"
        : $"CASE WHEN json_valid(content) = 1 THEN length(CAST(json_extract(content, '$.{field}') AS TEXT)) END AS [{field}Length]";

    private static string JsonValidity(bool sqlServer) => sqlServer
        ? "CASE WHEN ISJSON(b.content) = 1 AND LEFT(LTRIM(b.content), 1) = '{' THEN 1 ELSE 0 END AS [IsJsonValid]"
        : "CASE WHEN json_valid(content) = 1 THEN 1 ELSE 0 END AS [IsJsonValid]";

    private static string ArrayShapeValid(string field, bool sqlServer) => sqlServer
        ? $"CASE WHEN ISJSON(b.content) = 1 AND LEFT(LTRIM(JSON_QUERY(safe.JsonContent, '$.{field}')), 1) = '[' THEN 1 ELSE 0 END AS [{field}ShapeValid]"
        : $"CASE WHEN json_valid(content) = 1 THEN CASE WHEN json_type(content, '$.{field}') = 'array' THEN 1 ELSE 0 END ELSE 0 END AS [{field}ShapeValid]";

    private static string ArrayCount(string field, string alias, bool sqlServer) => sqlServer
        ? $"CASE WHEN ISJSON(b.content) = 1 THEN (SELECT COUNT_BIG(*) FROM OPENJSON(CASE WHEN LEFT(LTRIM(JSON_QUERY(safe.JsonContent, '$.{field}')), 1) = '[' THEN JSON_QUERY(safe.JsonContent, '$.{field}') ELSE N'[]' END)) END AS [{alias}]"
        : $"CASE WHEN json_valid(content) = 1 THEN json_array_length(content, '$.{field}') END AS [{alias}]";

    private static string JsonCoverageDate(bool first, bool sqlServer) => sqlServer
        ? first
            ? "CASE WHEN coverageMeta.SpanCount > 0 THEN LEFT(JSON_VALUE(safe.JsonContent, '$.Coverage[0].From'), 49) END AS [CoveredFromText]"
            : "CASE WHEN coverageMeta.SpanCount > 0 THEN (SELECT TOP (1) CASE WHEN ISJSON([value]) = 1 AND LEFT(LTRIM([value]), 1) = '{' THEN LEFT(JSON_VALUE([value], '$.Through'), 49) END FROM OPENJSON(JSON_QUERY(safe.JsonContent, '$.Coverage')) ORDER BY TRY_CONVERT(int, [key]) DESC) END AS [CoveredThroughText]"
        : first
            ? "CASE WHEN json_valid(content) = 1 THEN CASE WHEN json_array_length(content, '$.Coverage') > 0 THEN substr(CAST(json_extract(content, '$.Coverage[0].From') AS TEXT), 1, 49) END END AS [CoveredFromText]"
            : "CASE WHEN json_valid(content) = 1 THEN CASE WHEN json_array_length(content, '$.Coverage') > 0 THEN substr(CAST(json_extract(content, '$.Coverage[' || (json_array_length(content, '$.Coverage') - 1) || '].Through') AS TEXT), 1, 49) END END AS [CoveredThroughText]";

    private static string JsonCoverageLength(bool first, bool sqlServer) => sqlServer
        ? first
            ? "CASE WHEN coverageMeta.SpanCount > 0 THEN DATALENGTH(JSON_VALUE(safe.JsonContent, '$.Coverage[0].From')) / 2 END AS [CoveredFromLength]"
            : "CASE WHEN coverageMeta.SpanCount > 0 THEN DATALENGTH((SELECT TOP (1) CASE WHEN ISJSON([value]) = 1 AND LEFT(LTRIM([value]), 1) = '{' THEN JSON_VALUE([value], '$.Through') END FROM OPENJSON(JSON_QUERY(safe.JsonContent, '$.Coverage')) ORDER BY TRY_CONVERT(int, [key]) DESC)) / 2 END AS [CoveredThroughLength]"
        : first
            ? "CASE WHEN json_valid(content) = 1 THEN CASE WHEN json_array_length(content, '$.Coverage') > 0 THEN length(CAST(json_extract(content, '$.Coverage[0].From') AS TEXT)) END END AS [CoveredFromLength]"
            : "CASE WHEN json_valid(content) = 1 THEN CASE WHEN json_array_length(content, '$.Coverage') > 0 THEN length(CAST(json_extract(content, '$.Coverage[' || (json_array_length(content, '$.Coverage') - 1) || '].Through') AS TEXT)) END END AS [CoveredThroughLength]";

    private static bool TryLong(string? raw, long? length, out long value)
    {
        value = 0;
        return TryRaw(raw, length, 32, out var text) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryLongNullable(string? raw, long? length, out long? value)
    {
        value = null;
        if (raw == null) return length == null;
        if (!TryRaw(raw, length, 32, out var text) ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryNullableLong(string? raw, long? length, out long? value)
    {
        value = null;
        if (raw == null) return length == null;
        if (!TryRaw(raw, length, 16, out var text) ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryOptionalString(string? raw, long? length, int cap, out string? value)
    {
        value = null;
        if (raw == null) return length == null;
        if (!TryRaw(raw, length, cap, out value)) return false;
        return value != null && value.Length <= cap;
    }

    private static bool TryString(string? raw, long? length, int cap, out string? value)
    {
        value = null;
        if (raw == null) return false;
        if (!TryRaw(raw, length, cap, out value)) return false;
        return value != null && value.Length <= cap;
    }

    private static bool TryRaw(string? raw, long? lengthValue, int cap, out string? value)
    {
        value = null;
        if (raw == null || lengthValue == null) return false;
        var length = lengthValue.Value;
        if (length < 0 || length > cap) return false;
        value = raw;
        return value != null && value.Length <= cap;
    }

    private static bool TryNullableDate(string? raw, long? length, int cap,
        out DateTimeOffset? value)
    {
        value = null;
        if (raw == null) return length == null;
        if (!TryRaw(raw, length, cap, out var text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryNullableInt(string? raw, long? length, out int? value)
    {
        value = null;
        if (raw == null) return length == null;
        if (!TryRaw(raw, length, 16, out var text) ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryNullableCount(long? raw, out int? value)
    {
        value = null;
        if (raw == null) return false;
        if (raw < 0 || raw > int.MaxValue) return false;
        value = (int)raw.Value;
        return true;
    }
}

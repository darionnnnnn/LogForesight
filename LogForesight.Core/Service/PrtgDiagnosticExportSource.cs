using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LogForesight.Core.Service;

/// <summary>一筆 V2 匯出陣列列，JsonUtf8 不包含逗號或外層陣列括號。</summary>
public sealed record PrtgDiagnosticExportItem(string PropertyName, byte[] JsonUtf8);

/// <summary>
/// 從呼叫端持有的同一個資料庫 snapshot 逐頁產生診斷包列；不建立 DbContext、不改資料，也不組整包 DTO。
/// 呼叫端負責在列舉期間維持讀取交易，並把輸出直接交給有界分片接收器。
/// </summary>
public sealed class PrtgDiagnosticExportSource
{
    public const int PageSize = 16;
    public const int ValuePageSize = 2000;
    private const long MaximumRawPageBytes = 32L * 1024 * 1024;
    public const int MaximumRowBytes = 4 * 1024 * 1024;
    private const long MaximumRawRowBytes = 9L * 1024 * 1024;
    private const int MaximumBlobValueBytes = MaximumRowBytes * 2;
    private const int MaximumPolicyCharacters = MaximumRowBytes;
    private const int MaximumPolicySensors = 15000;
    private const int MaximumSerializerScratchBytes = MaximumRowBytes * 4;

    private static readonly string[] ArrayProperties =
    [
        "Devices", "Sensors", "StateChanges", "Values", "HostMaps", "ManualMaps",
        "Observations", "Timelines", "SemanticEvidence", "SemanticResults"
    ];

    public async IAsyncEnumerable<PrtgDiagnosticExportItem> EnumerateRowsAsync(
        LfDbContext context,
        DateTime fromDate,
        DateTime toDate,
        PrtgMonitoringPolicy sourcePolicy,
        long sourcePolicyVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sourcePolicy);
        if (fromDate.Date > toDate.Date) throw new ArgumentOutOfRangeException(nameof(fromDate));
        if (toDate.Date == DateTime.MaxValue.Date) throw new ArgumentOutOfRangeException(nameof(toDate));
        var toExclusive = toDate.Date.AddDays(1);
        if (sourcePolicy.SensorIds.Count > MaximumPolicySensors ||
            sourcePolicy.SensorIds.Any(sensorId => sensorId <= 0) ||
            sourcePolicy.SensorIds.Distinct().Count() != sourcePolicy.SensorIds.Count)
            throw new InvalidDataException("來源 policy sensor 範圍超過 15,000 筆，或含有重複／非正識別碼。");
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("匯出 source 必須使用呼叫端持有的一致性資料庫交易。");

        cancellationToken.ThrowIfCancellationRequested();
        var policyVersion = await context.Blobs.AsNoTracking()
            .Where(blob => blob.BlobKey == PrtgMonitoringPolicyStore.BlobKey)
            .Select(blob => (long?)blob.Version)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        if (policyVersion != sourcePolicyVersion)
            throw new InvalidDataException("來源 policy 版本已變更，拒絕輸出混合快照。");

        await foreach (var item in ReadDevices(context, cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadSensors(context, cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadStateChanges(context, fromDate.Date, toExclusive, cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadValues(context, fromDate.Date, toExclusive, cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadHostMaps(context, fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadManualMaps(context, cancellationToken).ConfigureAwait(false)) yield return item;
        yield return new PrtgDiagnosticExportItem("SourcePolicy", SerializeBounded(sourcePolicy));
        await foreach (var item in ReadObservations(context, fromDate.Date, toDate.Date, cancellationToken).ConfigureAwait(false)) yield return item;

        foreach (var sensorId in sourcePolicy.SensorIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blobKey = PrtgSensorTimelineStore.Prefix + sensorId.ToString(CultureInfo.InvariantCulture);
            var timeline = await ReadBoundedBlobValue<PrtgSensorTimelineEvidence>(context, blobKey, cancellationToken)
                .ConfigureAwait(false) ?? new PrtgSensorTimelineEvidence { SensorId = sensorId };
            yield return Item("Timelines", timeline);
        }

        await foreach (var item in ReadDictionary<PrtgDiskSemanticEvidence>(context,
            PrtgDiskSemanticEvidenceStore.BlobKey, "SemanticEvidence", cancellationToken).ConfigureAwait(false)) yield return item;
        await foreach (var item in ReadDictionary<PrtgDiskVerificationResult>(context,
            PrtgDiskVerificationResultStore.BlobKey, "SemanticResults", cancellationToken).ConfigureAwait(false)) yield return item;
    }

    public static IReadOnlyList<string> V2ArrayProperties => Array.AsReadOnly(ArrayProperties);

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadDevices(
        LfDbContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgDevices.AsNoTracking();
            if (after.HasValue) query = query.Where(row => row.Objid > after.Value);
            var ordered = query.OrderBy(row => row.Objid);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.Objid, context.Database.IsSqlServer(), cancellationToken,
                row => row.Name, row => row.GroupPath, row => row.Ip, row => row.Tags, row => row.Status)
                .ConfigureAwait(false);
            ValidatePageLengths(lengths, "Devices");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.Objid, "Devices");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after = row.Objid;
                yield return Item("Devices", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadSensors(
        LfDbContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgSensors.AsNoTracking();
            if (after.HasValue) query = query.Where(row => row.Objid > after.Value);
            var ordered = query.OrderBy(row => row.Objid);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.Objid, context.Database.IsSqlServer(), cancellationToken,
                row => row.Name, row => row.SensorType, row => row.Tags, row => row.Unit,
                row => row.Status, row => row.ThresholdsJson, row => row.Category, row => row.CategorySource)
                .ConfigureAwait(false);
            ValidatePageLengths(lengths, "Sensors");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.Objid, "Sensors");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after = row.Objid;
                yield return Item("Sensors", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadManualMaps(
        LfDbContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgManualMaps.AsNoTracking();
            if (after.HasValue) query = query.Where(row => row.DeviceObjid > after.Value);
            var ordered = query.OrderBy(row => row.DeviceObjid);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.DeviceObjid, context.Database.IsSqlServer(), cancellationToken,
                row => row.CreatedBy, row => row.Note).ConfigureAwait(false);
            ValidatePageLengths(lengths, "ManualMaps");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.DeviceObjid, "ManualMaps");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after = row.DeviceObjid;
                yield return Item("ManualMaps", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadStateChanges(
        LfDbContext context, DateTime from, DateTime toExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hasCursor = false;
        long afterSensor = 0;
        DateTime afterChangedAt = default;
        long afterId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgStateChanges.AsNoTracking()
                .Where(row => row.ChangedAt >= from && row.ChangedAt < toExclusive);
            if (hasCursor)
                query = query.Where(row => row.SensorObjid > afterSensor ||
                    (row.SensorObjid == afterSensor && (row.ChangedAt > afterChangedAt ||
                     (row.ChangedAt == afterChangedAt && row.Id > afterId))));
            var ordered = query.OrderBy(row => row.SensorObjid).ThenBy(row => row.ChangedAt).ThenBy(row => row.Id);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.Id, context.Database.IsSqlServer(), cancellationToken,
                row => row.Status, row => row.PrevStatus, row => row.Message, row => row.Quality)
                .ConfigureAwait(false);
            ValidatePageLengths(lengths, "StateChanges");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.Id, "StateChanges");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasCursor = true; afterSensor = row.SensorObjid; afterChangedAt = row.ChangedAt; afterId = row.Id;
                yield return Item("StateChanges", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadValues(
        LfDbContext context, DateTime from, DateTime toExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hasCursor = false;
        long afterSensor = 0;
        DateTime afterPeriod = default;
        long afterId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgValues.AsNoTracking()
                .Where(row => row.PeriodStart >= from && row.PeriodStart < toExclusive);
            if (hasCursor)
                query = query.Where(row => row.SensorObjid > afterSensor ||
                    (row.SensorObjid == afterSensor && (row.PeriodStart > afterPeriod ||
                     (row.PeriodStart == afterPeriod && row.Id > afterId))));
            var ordered = query.OrderBy(row => row.SensorObjid).ThenBy(row => row.PeriodStart).ThenBy(row => row.Id);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.Id, context.Database.IsSqlServer(), cancellationToken,
                row => row.Quality, row => row.TrustedProof).ConfigureAwait(false);
            ValidatePageLengths(lengths, "Values");
            if (lengths.Count == 0) yield break;
            // A newly added proof LOB must participate in preflight bounds too.
            // Only scalar lengths cross SQL before any value entity is loaded.
            var proofLengths = await ordered.Select(row => new { row.Id,
                Length = row.TrustedProof == null ? 0 : row.TrustedProof.Length })
                .Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (proofLengths.Count != lengths.Count || proofLengths.Where((row, index) => row.Id != lengths[index].Key).Any())
                throw new InvalidDataException("Values proof 長度頁與資料 key 不一致。");
            if (proofLengths.Any(row => row.Length > Models.PrtgTrustedSampleProof.MaximumSerializedBytes))
                throw new InvalidDataException("Values proof 超過 4 KiB 讀取前上限。");
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.Id, "Values");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.TrustedProof != null && System.Text.Encoding.UTF8.GetByteCount(row.TrustedProof) > Models.PrtgTrustedSampleProof.MaximumSerializedBytes)
                    throw new InvalidDataException("Values proof 超過 4 KiB UTF8 上限。");
                hasCursor = true; afterSensor = row.SensorObjid; afterPeriod = row.PeriodStart; afterId = row.Id;
                yield return Item("Values", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadHostMaps(
        LfDbContext context, DateTime from, DateTime through,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hasCursor = false;
        DateTime afterDate = default;
        long afterDevice = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgHostMaps.AsNoTracking()
                .Where(row => row.MapDate >= from && row.MapDate <= through);
            if (hasCursor)
                query = query.Where(row => row.MapDate > afterDate ||
                    (row.MapDate == afterDate && row.DeviceObjid > afterDevice));
            var ordered = query.OrderBy(row => row.MapDate).ThenBy(row => row.DeviceObjid);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.DeviceObjid, context.Database.IsSqlServer(), cancellationToken,
                row => row.Ip, row => row.HostName, row => row.MapStatus, row => row.Note)
                .ConfigureAwait(false);
            ValidatePageLengths(lengths, "HostMaps");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.DeviceObjid, "HostMaps");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasCursor = true; afterDate = row.MapDate; afterDevice = row.DeviceObjid;
                yield return Item("HostMaps", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadObservations(
        LfDbContext context, DateTime from, DateTime through,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? afterSnapshotId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.PrtgObservations.AsNoTracking()
                .Where(row => row.RecordDate >= from && row.RecordDate <= through);
            if (afterSnapshotId != null)
                query = query.Where(row => string.Compare(row.SnapshotId, afterSnapshotId) > 0);
            var ordered = query.OrderBy(row => row.SnapshotId);
            var lengths = await ReadPageLengthsAsync(ordered, row => row.SnapshotId, context.Database.IsSqlServer(), cancellationToken,
                row => row.SnapshotId, row => row.DecisionKey, row => row.ActiveKey, row => row.RuleCode,
                row => row.EventKey, row => row.SourceName, row => row.Category, row => row.SourceGeneration,
                row => row.SourceHint, row => row.ResourceGeneration, row => row.QualityReason,
                row => row.ContentJson, row => row.SupplementStatus)
                .ConfigureAwait(false);
            ValidatePageLengths(lengths, "Observations");
            if (lengths.Count == 0) yield break;
            var page = await ordered.Take(lengths.Count).ToListAsync(cancellationToken).ConfigureAwait(false);
            EnsurePageKeysMatch(page, lengths, row => row.SnapshotId, "Observations");
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                afterSnapshotId = row.SnapshotId;
                yield return Item("Observations", row);
            }
        }
    }

    private static async IAsyncEnumerable<PrtgDiagnosticExportItem> ReadDictionary<T>(
        LfDbContext context, string blobKey, string propertyName,
        [EnumeratorCancellation] CancellationToken cancellationToken) where T : class
    {
        await ValidateDictionaryKeys(context, blobKey, cancellationToken).ConfigureAwait(false);
        long afterKey = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = CreateDictionaryPageCommand(context, blobKey, afterKey);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);
            var pageCount = 0;
            long lastKey = afterKey;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = reader.GetString(0);
                var type = reader.GetString(1);
                if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= lastKey)
                    throw new InvalidDataException($"{propertyName} blob 的 key 無效或重複。");
                if (!string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{propertyName} blob 項目必須是 JSON object。");
                var rawBytes = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (rawBytes > MaximumBlobValueBytes)
                    throw new InvalidDataException($"{propertyName} 單筆原始 JSON 超過有界讀取上限。");
                if (reader.IsDBNull(3))
                    throw new InvalidDataException($"{propertyName} JSON 項目缺少內容。");
                var json = reader.GetString(3);
                if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBlobValueBytes)
                    throw new InvalidDataException($"{propertyName} 單筆原始 JSON 超過有界讀取上限。");
                var value = JsonSerializer.Deserialize<T>(json, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException($"{propertyName} JSON 項目無法解析。");
                lastKey = id;
                pageCount++;
                yield return Item(propertyName, value);
            }
            if (pageCount == 0) yield break;
            afterKey = lastKey;
        }
    }

    private static async Task ValidateDictionaryKeys(
        LfDbContext context, string blobKey, CancellationToken cancellationToken)
    {
        await using var command = CreateDictionaryValidationCommand(context, blobKey);
        var invalid = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToInt32(invalid, CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("PRTG 語意證據 blob 有非正整數 key 或重複 key。");
    }

    private static DbCommand CreateDictionaryValidationCommand(LfDbContext context, string blobKey)
    {
        var command = CreateCommand(context);
        if (context.Database.IsSqlite())
        {
            command.CommandText = """
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM json_each(COALESCE(
                        (SELECT content FROM lf_blobs WHERE blob_key = $key), '{}')) AS item
                    GROUP BY item.key
                    HAVING CAST(item.key AS INTEGER) <= 0
                        OR CAST(CAST(item.key AS INTEGER) AS TEXT) <> CAST(item.key AS TEXT)
                        OR COUNT(*) > 1
                ) THEN 1 ELSE 0 END
                """;
            AddParameter(command, "$key", blobKey);
        }
        else if (context.Database.IsSqlServer())
        {
            command.CommandText = """
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM OPENJSON(COALESCE(
                        (SELECT content FROM lf_blobs WHERE blob_key = @key), N'{}')) AS item
                    GROUP BY item.[key]
                    HAVING TRY_CONVERT(bigint, item.[key]) IS NULL
                        OR TRY_CONVERT(bigint, item.[key]) <= 0
                        OR CONVERT(varchar(20), TRY_CONVERT(bigint, item.[key])) <> item.[key]
                        OR COUNT(*) > 1
                ) THEN 1 ELSE 0 END
                """;
            AddParameter(command, "@key", blobKey);
        }
        else throw new NotSupportedException("此資料庫 provider 不支援有界 JSON dictionary 匯出。");
        return command;
    }

    private static DbCommand CreateDictionaryPageCommand(LfDbContext context, string blobKey, long afterKey)
    {
        var command = CreateCommand(context);
        if (context.Database.IsSqlite())
        {
            command.CommandText = """
                SELECT CAST(item.key AS TEXT), item.type,
                       length(CAST(item.value AS BLOB)),
                       CASE WHEN length(CAST(item.value AS BLOB)) <= $maxBytes THEN json(item.value) ELSE NULL END
                FROM lf_blobs AS b, json_each(COALESCE(b.content, '{}')) AS item
                WHERE b.blob_key = $key AND CAST(item.key AS INTEGER) > $afterKey
                ORDER BY CAST(item.key AS INTEGER) LIMIT 100
                """;
            AddParameter(command, "$maxBytes", MaximumBlobValueBytes);
            AddParameter(command, "$key", blobKey);
            AddParameter(command, "$afterKey", afterKey);
        }
        else if (context.Database.IsSqlServer())
        {
            command.CommandText = """
                SELECT item.[key], CASE item.[type] WHEN 5 THEN N'object' ELSE N'other' END,
                       DATALENGTH(item.[value]),
                       CASE WHEN DATALENGTH(item.[value]) <= @maxBytes THEN item.[value] ELSE NULL END
                FROM lf_blobs AS b
                CROSS APPLY OPENJSON(COALESCE(b.content, N'{}')) AS item
                WHERE b.blob_key = @key AND TRY_CONVERT(bigint, item.[key]) > @afterKey
                ORDER BY TRY_CONVERT(bigint, item.[key]) OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY
                """;
            AddParameter(command, "@maxBytes", MaximumBlobValueBytes);
            AddParameter(command, "@key", blobKey);
            AddParameter(command, "@afterKey", afterKey);
        }
        else throw new NotSupportedException("此資料庫 provider 不支援有界 JSON dictionary 匯出。");
        return command;
    }

    private static DbCommand CreateCommand(LfDbContext context)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<T?> ReadBoundedBlobValue<T>(
        LfDbContext context, string key, CancellationToken cancellationToken) where T : class
    {
        var row = await context.Blobs.AsNoTracking().Where(blob => blob.BlobKey == key)
            .Select(blob => new
            {
                Prefix = blob.Content.Substring(0, MaximumPolicyCharacters + 1),
                Length = blob.Content.Length
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row == null) return null;
        if (row.Length > MaximumPolicyCharacters || row.Prefix.Length > MaximumPolicyCharacters)
            throw new InvalidDataException($"PRTG timeline blob {key} 超過單列有界讀取上限。");
        var value = JsonSerializer.Deserialize<T>(row.Prefix, LfJsonOptions.Pretty);
        if (value == null) throw new InvalidDataException($"PRTG timeline blob {key} 為 null。");
        return value;
    }

    private static async Task<List<RowSize<TKey>>> ReadPageLengthsAsync<TEntity, TKey>(
        IOrderedQueryable<TEntity> ordered,
        Expression<Func<TEntity, TKey>> keySelector,
        bool sqlServer,
        CancellationToken cancellationToken,
        params Expression<Func<TEntity, string?>>[] stringFields)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "row");
        var key = new ReplaceParameterVisitor(keySelector.Parameters[0], parameter).Visit(keySelector.Body)!;
        Expression total = Expression.Constant(0L);
        foreach (var selector in stringFields)
        {
            var field = new ReplaceParameterVisitor(selector.Parameters[0], parameter).Visit(selector.Body)!;
            Expression length;
            if (sqlServer)
            {
                var method = typeof(SqlServerDbFunctionsExtensions).GetMethods()
                    .Single(candidate => candidate.Name == nameof(SqlServerDbFunctionsExtensions.DataLength) &&
                        candidate.GetParameters() is { Length: 2 } args && args[1].ParameterType == typeof(string));
                var functions = Expression.Property(null, typeof(EF), nameof(EF.Functions));
                var call = Expression.Call(method, functions, field);
                var fallbackLength = Expression.Convert(Expression.Constant(0), call.Type);
                length = Expression.Convert(Expression.Coalesce(call, fallbackLength), typeof(long));
            }
            else
            {
                var nullableLength = Expression.Convert(Expression.Property(field, nameof(string.Length)), typeof(int?));
                var characterCount = Expression.Coalesce(nullableLength, Expression.Constant(0));
                length = Expression.Multiply(Expression.Convert(characterCount, typeof(long)), Expression.Constant(4L));
            }
            total = Expression.Add(total, length);
        }

        var pageType = typeof(RowSize<TKey>);
        var constructor = pageType.GetConstructor([typeof(TKey), typeof(long)])!;
        var projection = Expression.Lambda<Func<TEntity, RowSize<TKey>>>(
            Expression.New(constructor, key, total), parameter);
        var pageSize = typeof(TEntity) == typeof(PrtgValueRow) ? ValuePageSize : PageSize;
        return await ordered.Select(projection).Take(pageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePageLengths<TKey>(List<RowSize<TKey>> page, string category)
    {
        long bytes = 0;
        var accepted = 0;
        foreach (var item in page)
        {
            if (item.RawBytesUpperBound < 0 || item.RawBytesUpperBound > MaximumRawRowBytes)
                throw new InvalidDataException($"{category} 單筆原始字串資料超過 9 MiB 的讀取前上限。");
            if (bytes + item.RawBytesUpperBound > MaximumRawPageBytes) break;
            bytes += item.RawBytesUpperBound;
            accepted++;
        }
        if (accepted < page.Count) page.RemoveRange(accepted, page.Count - accepted);
    }

    private static void EnsurePageKeysMatch<TEntity, TKey>(
        IReadOnlyList<TEntity> rows,
        IReadOnlyList<RowSize<TKey>> lengths,
        Func<TEntity, TKey> keySelector,
        string category)
    {
        if (rows.Count != lengths.Count || rows.Where((row, index) =>
                !EqualityComparer<TKey>.Default.Equals(keySelector(row), lengths[index].Key)).Any())
            throw new InvalidDataException($"{category} 讀取前長度頁與資料頁的 key 不一致。");
    }

    private static PrtgDiagnosticExportItem Item<T>(string propertyName, T value) =>
        new(propertyName, SerializeBounded(value));

    private static byte[] SerializeBounded<T>(T value)
    {
        var buffer = new BoundedBufferWriter(MaximumSerializerScratchBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value);
            writer.Flush();
        }
        if (buffer.WrittenCount > MaximumRowBytes)
            throw new InvalidDataException("單筆 PRTG 匯出 JSON 超過 4 MiB。");
        return buffer.ToArray();
    }

    private sealed record RowSize<TKey>(TKey Key, long RawBytesUpperBound);

    private sealed class ReplaceParameterVisitor(ParameterExpression source, ParameterExpression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? replacement : base.VisitParameter(node);
    }

    private sealed class BoundedBufferWriter(int maximumBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[Math.Min(4096, maximumBytes)];
        private int _written;
        public int WrittenCount => _written;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            var needed = checked(_written + sizeHint);
            if (needed > maximumBytes)
                throw new InvalidDataException("單筆 PRTG 匯出 JSON 超過 4 MiB。");
            if (needed <= _buffer.Length) return;
            var next = Math.Min(maximumBytes, Math.Max(needed, checked(_buffer.Length * 2)));
            Array.Resize(ref _buffer, next);
        }
    }
}

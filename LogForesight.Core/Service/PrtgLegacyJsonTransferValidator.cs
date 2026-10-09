using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

public sealed record PrtgLegacyTransferValidation(
    int FormatVersion,
    long DeclaredBytes,
    string PackageSha256,
    IReadOnlyDictionary<string, long> Counts,
    string ResultManifestJson);

/// <summary>以固定大小 buffer 驗證舊 V1/V2 JSON；只回報診斷統計，不反序列化或信任匯入欄位。</summary>
public sealed class PrtgLegacyJsonTransferValidator
{
    public const int MaximumTokenOrRowBytes = EfPrtgTransferStore.MaxChunkBytes;
    private readonly int _maxDepth;

    public PrtgLegacyJsonTransferValidator(int maxDepth = 32)
    {
        if (maxDepth is < 4 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        _maxDepth = maxDepth;
    }

    public async Task<PrtgLegacyTransferValidation> ValidateAsync(
        int chunkCount,
        Func<int, CancellationToken, Task<byte[]>> readChunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readChunk);
        if (chunkCount <= 0) throw Invalid("package_empty", "診斷包沒有可驗證的片段。");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parser = new StreamingParser(_maxDepth);
        long totalBytes = 0;
        for (var ordinal = 0; ordinal < chunkCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await readChunk(ordinal, cancellationToken).ConfigureAwait(false);
            if (chunk.Length is < 1 or > EfPrtgTransferStore.MaxChunkBytes)
                throw Invalid("chunk_integrity_failed", "已保存片段長度超出允許範圍。");
            totalBytes = checked(totalBytes + chunk.LongLength);
            hash.AppendData(chunk);
            parser.Append(chunk, isFinalBlock: ordinal == chunkCount - 1);
        }

        var parsed = parser.Finish();
        var packageHash = Convert.ToHexString(hash.GetHashAndReset());
        var counts = parsed.Counts;
        if (parsed.Manifest is { } packageManifest)
        {
            if (packageManifest.BytesBeforeManifest != parsed.ManifestPropertyOffset - 1)
                throw Invalid("manifest_byte_count_mismatch", "診斷包 manifest 的位元組位置與內容不符。");
            var prefixHash = await HashPrefixAsync(chunkCount, readChunk, packageManifest.BytesBeforeManifest,
                cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(packageManifest.Sha256BeforeManifest, prefixHash))
                throw Invalid("manifest_hash_mismatch", "診斷包 manifest 的 SHA-256 與實際內容不符。");
            foreach (var name in PrtgDiagnosticExportSource.V2ArrayProperties)
            {
                if (!packageManifest.RowCounts.TryGetValue(name, out var declared) ||
                    !counts.TryGetValue(name, out var actual) || declared != actual)
                    throw Invalid("manifest_count_mismatch", "診斷包 manifest 的資料列筆數與實際內容不符。");
            }
            if (packageManifest.RowCounts.Count != PrtgDiagnosticExportSource.V2ArrayProperties.Count)
                throw Invalid("manifest_count_mismatch", "診斷包 manifest 的資料列筆數欄位不完整或多餘。");
        }
        var manifest = JsonSerializer.Serialize(new
        {
            format = "legacy-json",
            formatVersion = parsed.FormatVersion,
            purpose = "diagnostic-only",
            integrityStatus = parsed.IntegrityStatus,
            totalBytes,
            packageSha256 = packageHash,
            rowCounts = counts,
            trust = "untrusted-diagnostic-payload"
        });
        if (Encoding.UTF8.GetByteCount(manifest) > EfPrtgTransferStore.MaxManifestUtf8Bytes)
            throw Invalid("manifest_too_large", "驗證結果 manifest 超過 64 KiB。");
        return new(parsed.FormatVersion, totalBytes, packageHash, counts, manifest);
    }

    private static async Task<string> HashPrefixAsync(int chunkCount,
        Func<int, CancellationToken, Task<byte[]>> readChunk, long prefixBytes,
        CancellationToken cancellationToken)
    {
        if (prefixBytes < 0) throw Invalid("manifest_byte_count_invalid", "診斷包 manifest 位元組位置無效。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = prefixBytes;
        for (var ordinal = 0; ordinal < chunkCount && remaining > 0; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await readChunk(ordinal, cancellationToken).ConfigureAwait(false);
            if (chunk.Length is < 1 or > EfPrtgTransferStore.MaxChunkBytes)
                throw Invalid("chunk_integrity_failed", "已保存片段長度超出允許範圍。");
            var count = checked((int)Math.Min(remaining, chunk.LongLength));
            hash.AppendData(chunk.AsSpan(0, count));
            remaining -= count;
        }
        if (remaining != 0) throw Invalid("manifest_byte_count_invalid", "診斷包 manifest 位元組長度超出內容。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static InvalidDataException Invalid(string code, string message) => new($"{code}: {message}");

    private sealed class StreamingParser
    {
        private const int BufferBytes = EfPrtgTransferStore.MaxChunkBytes * 2;
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonTokenType>> RequiredFields =
            new Dictionary<string, IReadOnlyDictionary<string, JsonTokenType>>(Names)
            {
                ["Devices"] = Fields(("Objid", JsonTokenType.Number), ("Name", JsonTokenType.String)),
                ["Sensors"] = Fields(("Objid", JsonTokenType.Number), ("DeviceObjid", JsonTokenType.Number),
                    ("Name", JsonTokenType.String), ("SensorType", JsonTokenType.String)),
                ["StateChanges"] = Fields(("Id", JsonTokenType.Number), ("SensorObjid", JsonTokenType.Number),
                    ("ChangedAt", JsonTokenType.String), ("Status", JsonTokenType.String)),
                ["Values"] = Fields(("SensorObjid", JsonTokenType.Number), ("PeriodStart", JsonTokenType.String)),
                ["HostMaps"] = Fields(("MapDate", JsonTokenType.String), ("DeviceObjid", JsonTokenType.Number),
                    ("MapStatus", JsonTokenType.String)),
                ["ManualMaps"] = Fields(("DeviceObjid", JsonTokenType.Number), ("HostId", JsonTokenType.Number)),
                ["Observations"] = Fields(("SnapshotId", JsonTokenType.String), ("DeviceObjid", JsonTokenType.Number),
                    ("RecordDate", JsonTokenType.String), ("ContentJson", JsonTokenType.String)),
                ["Timelines"] = Fields(("SensorId", JsonTokenType.Number), ("HostId", JsonTokenType.Number),
                    ("SourceGeneration", JsonTokenType.String), ("Coverage", JsonTokenType.StartArray),
                    ("States", JsonTokenType.StartArray)),
                ["SemanticEvidence"] = Fields(("SensorObjid", JsonTokenType.Number),
                    ("DeviceObjid", JsonTokenType.Number), ("SensorType", JsonTokenType.String)),
                ["SemanticResults"] = Fields(("SensorObjid", JsonTokenType.Number),
                    ("DeviceObjid", JsonTokenType.Number), ("Status", JsonTokenType.String))
            };
        private static readonly HashSet<string> ArrayProperties = new(RequiredFields.Keys, Names);
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonTokenType>> NestedFields =
            new Dictionary<string, IReadOnlyDictionary<string, JsonTokenType>>(Names)
            {
                ["TimelineCoverage"] = Fields(("SensorObjid", JsonTokenType.Number), ("From", JsonTokenType.String),
                    ("Through", JsonTokenType.String), ("SourceGeneration", JsonTokenType.String),
                    ("ResourceGeneration", JsonTokenType.String)),
                ["TimelineStates"] = Fields(("SensorObjid", JsonTokenType.Number), ("At", JsonTokenType.String),
                    ("Status", JsonTokenType.String), ("SourceGeneration", JsonTokenType.String),
                    ("ResourceGeneration", JsonTokenType.String))
            };

        private readonly byte[] _buffer = new byte[BufferBytes];
        private readonly Dictionary<string, long> _counts = new(Names);
        private readonly HashSet<string> _rootProperties = new(Names);
        private JsonReaderState _readerState;
        private int _bufferLength;
        private long _absoluteOffset;
        private string? _pendingRootProperty;
        private string? _activeArray;
        private string? _activeTimelineArray;
        private RowState? _row;
        private RowState? _nestedRow;
        private int _formatVersion = 2;
        private bool _formatVersionSeen;
        private string? _purpose;
        private string? _integrityContract;
        private bool _integrityContractSeen;
        private DateTime? _fromDate;
        private DateTime? _toDate;
        private bool _rootStarted;
        private bool _rootEnded;
        private bool _manifestSeen;
        private bool _readingManifest;
        private bool _readingManifestCounts;
        private string? _manifestPendingProperty;
        private string? _manifestPendingCount;
        private readonly HashSet<string> _manifestProperties = new(Names);
        private readonly HashSet<string> _manifestCountProperties = new(Names);
        private readonly Dictionary<string, long> _manifestRowCounts = new(Names);
        private string? _manifestFormat;
        private string? _manifestPurpose;
        private string? _manifestSha256;
        private int? _manifestFormatVersion;
        private long? _manifestBytesBefore;
        private long _manifestPropertyOffset = -1;

        public StreamingParser(int maxDepth)
        {
            foreach (var category in RequiredFields.Keys) _counts.Add(category, 0);
            _counts.Add("TimelineCoverageEntries", 0);
            _counts.Add("TimelineStateEntries", 0);
            _readerState = new JsonReaderState(new JsonReaderOptions
            {
                MaxDepth = maxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
        }

        public void Append(ReadOnlySpan<byte> bytes, bool isFinalBlock)
        {
            if (bytes.Length > _buffer.Length - _bufferLength)
                Process(isFinalBlock: false);
            if (bytes.Length > _buffer.Length - _bufferLength)
                throw Invalid("json_token_too_large", "單一 JSON token 或資料列超過 4 MiB。");
            bytes.CopyTo(_buffer.AsSpan(_bufferLength));
            _bufferLength += bytes.Length;
            Process(isFinalBlock);
        }

        public ParsedPackage Finish()
        {
            if (!_rootStarted || !_rootEnded || _activeArray != null || _activeTimelineArray != null ||
                _row != null || _nestedRow != null || _bufferLength != 0)
                throw Invalid("package_truncated", "JSON 診斷包不完整或結尾仍有未解析內容。");
            if (!_formatVersionSeen)
                throw Invalid("format_version_required", "診斷包必須明確宣告格式版本。");
            if (_formatVersion is not (1 or PrtgDataTransfer.CurrentFormatVersion))
                throw Invalid("format_version_unsupported", $"不支援的診斷包格式版本 {_formatVersion}。");
            if (_purpose is not null && _purpose != "diagnostic-only")
                throw Invalid("purpose_rejected", "匯入只接受 diagnostic-only 診斷包。");
            if (_fromDate.HasValue && _toDate.HasValue && _fromDate.Value.Date > _toDate.Value.Date)
                throw Invalid("date_range_invalid", "診斷包日期範圍無效。");

            string integrityStatus;
            if (_formatVersion == 1)
            {
                if (_purpose is not null && _purpose != "diagnostic-only")
                    throw Invalid("purpose_rejected", "匯入只接受 diagnostic-only 診斷包。");
                if (_manifestSeen || _integrityContractSeen)
                    throw Invalid("integrity_contract_unsupported", "V1 診斷包不可宣告 V2 manifest integrity contract。");
                integrityStatus = "legacy-v1-unmanifested";
            }
            else if (_integrityContractSeen)
            {
                if (_integrityContract != PrtgDiagnosticExportWriter.IntegrityContract)
                    throw Invalid("integrity_contract_unsupported", "診斷包宣告了不支援的 integrity contract。");
                if (_purpose != "diagnostic-only")
                    throw Invalid("purpose_required", "原生 V2 診斷包必須明確宣告 diagnostic-only purpose。");
                if (!_manifestSeen)
                    throw Invalid("manifest_required", "原生 V2 診斷包缺少 integrity manifest。");
                if (!_rootProperties.Contains("SourcePolicy") ||
                    PrtgDiagnosticExportSource.V2ArrayProperties.Any(name => !_rootProperties.Contains(name)))
                    throw Invalid("native_v2_shape_invalid", "原生 V2 診斷包缺少 SourcePolicy 或必要資料陣列。");
                integrityStatus = "manifest-verified";
            }
            else
            {
                // Pre-R11 V2 used the same version number but emitted the full DTO shape without
                // an embedded manifest. Keep that wire readable as opaque legacy diagnostics only.
                if (_purpose != "diagnostic-only" || !_rootProperties.Contains("SourcePolicy") ||
                    PrtgDiagnosticExportSource.V2ArrayProperties.Any(name => !_rootProperties.Contains(name)))
                    throw Invalid("legacy_v2_shape_invalid", "未標記的 V2 只接受完整舊版 diagnostic-only 封套。");
                // A prior native V2 writer embedded a valid manifest before the wire marker was
                // introduced. Preserve that verified diagnostic wire alongside genuine legacy V2.
                integrityStatus = _manifestSeen ? "manifest-verified" : "legacy-v2-unmanifested";
            }

            ParsedManifest? manifest = null;
            if (_manifestSeen)
            {
                if (_formatVersion != PrtgDataTransfer.CurrentFormatVersion ||
                    _manifestFormat != "legacy-json" ||
                    _manifestFormatVersion != _formatVersion || _manifestPurpose != "diagnostic-only" ||
                    _manifestBytesBefore is null || _manifestSha256 is null ||
                    _manifestSha256.Length != 64 || !_manifestSha256.All(Uri.IsHexDigit) ||
                    _manifestRowCounts.Count != PrtgDiagnosticExportSource.V2ArrayProperties.Count ||
                    PrtgDiagnosticExportSource.V2ArrayProperties.Any(name => !_manifestRowCounts.ContainsKey(name)))
                    throw Invalid("manifest_invalid", "診斷包 manifest 欄位、版本或資料列筆數無效。");
                manifest = new ParsedManifest(_manifestBytesBefore.Value, _manifestSha256,
                    new Dictionary<string, long>(_manifestRowCounts, Names));
            }
            return new ParsedPackage(_formatVersion, new Dictionary<string, long>(_counts, Names), manifest,
                _manifestPropertyOffset, integrityStatus);
        }

        private void Process(bool isFinalBlock)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(0, _bufferLength), isFinalBlock, _readerState);
            while (reader.Read())
            {
                var token = reader.TokenType;
                var depth = reader.CurrentDepth;
                if (!_rootStarted)
                {
                    if (token != JsonTokenType.StartObject || depth != 0)
                        throw Invalid("json_root_invalid", "診斷包根節點必須是 JSON 物件。");
                    _rootStarted = true;
                }
                else if (_rootEnded)
                    throw Invalid("json_trailing_content", "根物件後含有額外內容。");

                if (token == JsonTokenType.PropertyName && depth == 1)
                {
                    _pendingRootProperty = ReadPropertyName(ref reader);
                    if (_manifestSeen)
                        throw Invalid("manifest_not_last", "診斷包 manifest 必須是最後一個根欄位。");
                    if (!_rootProperties.Add(_pendingRootProperty))
                        throw Invalid("duplicate_root_property", "診斷包含重複的根欄位。");
                    if (Names.Equals(_pendingRootProperty, "Manifest"))
                    {
                        _manifestSeen = true;
                        _manifestPropertyOffset = _absoluteOffset + reader.TokenStartIndex;
                    }
                }

                else if (_pendingRootProperty != null && depth == 1 && token != JsonTokenType.PropertyName)
                {
                    var propertyName = _pendingRootProperty;
                    ProcessRootValue(ref reader, token);
                    if (ArrayProperties.Contains(propertyName) && token != JsonTokenType.StartArray)
                        throw Invalid("collection_shape_invalid", $"診斷包 {propertyName} 欄位必須是陣列。");
                    if (token == JsonTokenType.StartArray) _activeArray = propertyName;
                    _pendingRootProperty = null;
                }

                ProcessManifestToken(ref reader, token, depth);

                if (token == JsonTokenType.StartArray && depth == 1)
                {
                    if (_activeArray != null && RequiredFields.ContainsKey(_activeArray))
                        _counts.TryAdd(_activeArray, 0);
                }
                else if (token == JsonTokenType.EndArray && depth == 1)
                    _activeArray = null;
                else if (_row?.Category == "Timelines" && token == JsonTokenType.StartArray && depth == 3 &&
                         (Names.Equals(_row.PendingProperty, "Coverage") || Names.Equals(_row.PendingProperty, "States")))
                    _activeTimelineArray = _row.PendingProperty;
                else if (_activeTimelineArray != null && token == JsonTokenType.EndArray && depth == 3)
                    _activeTimelineArray = null;
                else if (_activeArray != null && RequiredFields.ContainsKey(_activeArray) && depth == 2)
                {
                    if (token == JsonTokenType.StartObject)
                    {
                        if (_row != null) throw Invalid("json_row_invalid", "診斷資料列巢狀層級無效。");
                        _row = new RowState(_activeArray, _absoluteOffset + reader.TokenStartIndex);
                    }
                    else if (token != JsonTokenType.EndObject)
                        throw Invalid("json_row_invalid", "診斷陣列每筆資料都必須是 JSON 物件。");
                }
                else if (_activeTimelineArray != null && depth == 4)
                {
                    if (token == JsonTokenType.StartObject)
                    {
                        if (_nestedRow != null) throw Invalid("json_row_invalid", "時間軸子資料列巢狀層級無效。");
                        _nestedRow = new RowState(Names.Equals(_activeTimelineArray, "Coverage") ? "TimelineCoverage" : "TimelineStates",
                            _row!.StartOffset);
                    }
                    else if (token != JsonTokenType.EndObject)
                        throw Invalid("json_row_invalid", "時間軸 Coverage 與 States 每筆都必須是物件。");
                }

                if (_row != null)
                {
                    if (token == JsonTokenType.PropertyName && depth == 3)
                        _row.BeginProperty(ReadPropertyName(ref reader));
                    else if (_row.PendingProperty != null && depth == 3 && token != JsonTokenType.PropertyName)
                        _row.AcceptValue(ref reader, token);

                    if (_absoluteOffset + reader.BytesConsumed - _row.StartOffset > MaximumTokenOrRowBytes)
                        throw Invalid("json_row_too_large", "單一診斷資料列超過 4 MiB。");

                    if (token == JsonTokenType.EndObject && depth == 2)
                    {
                        _row.Validate();
                        _counts[_row.Category] = checked(_counts[_row.Category] + 1);
                        _row = null;
                    }
                }

                if (_nestedRow != null)
                {
                    if (token == JsonTokenType.PropertyName && depth == 5)
                        _nestedRow.BeginProperty(ReadPropertyName(ref reader));
                    else if (_nestedRow.PendingProperty != null && depth == 5 && token != JsonTokenType.PropertyName)
                        _nestedRow.AcceptValue(ref reader, token);
                    if (token == JsonTokenType.EndObject && depth == 4)
                    {
                        _nestedRow.Validate();
                        var countName = _nestedRow.Category == "TimelineCoverage" ? "TimelineCoverageEntries" : "TimelineStateEntries";
                        _counts[countName] = checked(_counts[countName] + 1);
                        _nestedRow = null;
                    }
                }

                if (token == JsonTokenType.EndObject && depth == 0) _rootEnded = true;
            }

            var consumed = checked((int)reader.BytesConsumed);
            _readerState = reader.CurrentState;
            if (consumed > 0)
            {
                _absoluteOffset = checked(_absoluteOffset + consumed);
                _buffer.AsSpan(consumed, _bufferLength - consumed).CopyTo(_buffer);
                _bufferLength -= consumed;
            }
            if (isFinalBlock && _rootEnded && IsJsonWhitespace(_buffer.AsSpan(0, _bufferLength)))
            {
                _absoluteOffset = checked(_absoluteOffset + _bufferLength);
                _bufferLength = 0;
            }
            if (_bufferLength > MaximumTokenOrRowBytes)
                throw Invalid("json_token_too_large", "未完成 JSON token 超過 4 MiB。");
            if (isFinalBlock && _bufferLength != 0)
                throw Invalid("package_truncated", "JSON 診斷包最後一個 token 不完整。");
        }

        private static bool IsJsonWhitespace(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
                if (value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n') return false;
            return true;
        }

        private void ProcessRootValue(ref Utf8JsonReader reader, JsonTokenType token)
        {
            switch (_pendingRootProperty!.ToUpperInvariant())
            {
                case "FORMATVERSION":
                    if (token != JsonTokenType.Number || !reader.TryGetInt32(out _formatVersion))
                        throw Invalid("format_version_invalid", "診斷包格式版本欄位無效。");
                    _formatVersionSeen = true;
                    break;
                case "PURPOSE":
                    if (token != JsonTokenType.String || reader.ValueSpan.Length > 128)
                        throw Invalid("purpose_invalid", "診斷包 purpose 欄位無效。");
                    _purpose = reader.GetString() ?? "";
                    break;
                case "INTEGRITYCONTRACT":
                    if (token != JsonTokenType.String || reader.ValueSpan.Length > 128)
                        throw Invalid("integrity_contract_invalid", "診斷包 integrity contract 欄位無效。");
                    _integrityContractSeen = true;
                    _integrityContract = reader.GetString() ?? "";
                    break;
                case "FROMDATE":
                    _fromDate = ReadDate(ref reader, token, "FromDate");
                    break;
                case "TODATE":
                    _toDate = ReadDate(ref reader, token, "ToDate");
                    break;
                case "SOURCEPOLICY":
                    if (token is not (JsonTokenType.StartObject or JsonTokenType.Null))
                        throw Invalid("source_policy_invalid", "SourcePolicy 必須是物件或 null。");
                    break;
                case "MANIFEST":
                    if (token != JsonTokenType.StartObject)
                        throw Invalid("manifest_invalid", "診斷包 manifest 必須是物件。");
                    _readingManifest = true;
                    break;
            }
        }

        private void ProcessManifestToken(ref Utf8JsonReader reader, JsonTokenType token, int depth)
        {
            if (_manifestSeen && _manifestPropertyOffset >= 0 &&
                _absoluteOffset + reader.BytesConsumed - _manifestPropertyOffset >
                PrtgDiagnosticExportWriter.MaximumManifestUtf8Bytes + 128)
                throw Invalid("manifest_too_large", "診斷包 manifest 超過 64 KiB。");
            if (!_readingManifest) return;
            if (token == JsonTokenType.PropertyName && depth == 2)
            {
                _manifestPendingProperty = ReadPropertyName(ref reader);
                if (!_manifestProperties.Add(_manifestPendingProperty))
                    throw Invalid("manifest_invalid", "診斷包 manifest 含重複欄位。");
                return;
            }
            if (token == JsonTokenType.PropertyName && depth == 3 && _readingManifestCounts)
            {
                _manifestPendingCount = ReadPropertyName(ref reader);
                if (!_manifestCountProperties.Add(_manifestPendingCount))
                    throw Invalid("manifest_invalid", "診斷包 manifest 含重複資料列筆數。");
                return;
            }
            if (_manifestPendingProperty != null && depth == 2 && token != JsonTokenType.PropertyName)
            {
                var property = _manifestPendingProperty;
                _manifestPendingProperty = null;
                switch (property.ToUpperInvariant())
                {
                    case "FORMAT": _manifestFormat = ReadManifestString(ref reader, token); break;
                    case "FORMATVERSION": _manifestFormatVersion = ReadManifestInt(ref reader, token); break;
                    case "PURPOSE": _manifestPurpose = ReadManifestString(ref reader, token); break;
                    case "BYTESBEFOREMANIFEST": _manifestBytesBefore = ReadManifestLong(ref reader, token); break;
                    case "SHA256BEFOREMANIFEST": _manifestSha256 = ReadManifestString(ref reader, token); break;
                    case "ROWCOUNTS":
                        if (token != JsonTokenType.StartObject) throw Invalid("manifest_invalid", "manifest rowCounts 必須是物件。");
                        _readingManifestCounts = true;
                        break;
                    default: throw Invalid("manifest_invalid", "診斷包 manifest 含未知欄位。");
                }
                return;
            }
            if (_manifestPendingCount != null && depth == 3 && token != JsonTokenType.PropertyName)
            {
                var name = _manifestPendingCount;
                _manifestPendingCount = null;
                if (token != JsonTokenType.Number || !reader.TryGetInt64(out var count) || count < 0 ||
                    !PrtgDiagnosticExportSource.V2ArrayProperties.Contains(name, StringComparer.Ordinal))
                    throw Invalid("manifest_invalid", "診斷包 manifest 資料列筆數無效。");
                _manifestRowCounts.Add(name, count);
                return;
            }
            if (token == JsonTokenType.EndObject && depth == 2 && _readingManifestCounts)
            {
                _readingManifestCounts = false;
                return;
            }
            if (token == JsonTokenType.EndObject && depth == 1)
            {
                if (_manifestPendingProperty != null || _manifestPendingCount != null || _readingManifestCounts)
                    throw Invalid("manifest_invalid", "診斷包 manifest 結構不完整。");
                _readingManifest = false;
            }
        }

        private static string ReadManifestString(ref Utf8JsonReader reader, JsonTokenType token) =>
            token == JsonTokenType.String && reader.ValueSpan.Length <= 128
                ? reader.GetString() ?? ""
                : throw Invalid("manifest_invalid", "診斷包 manifest 字串欄位無效。");

        private static int ReadManifestInt(ref Utf8JsonReader reader, JsonTokenType token) =>
            token == JsonTokenType.Number && reader.TryGetInt32(out var value)
                ? value : throw Invalid("manifest_invalid", "診斷包 manifest 整數欄位無效。");

        private static long ReadManifestLong(ref Utf8JsonReader reader, JsonTokenType token) =>
            token == JsonTokenType.Number && reader.TryGetInt64(out var value)
                ? value : throw Invalid("manifest_invalid", "診斷包 manifest 長整數欄位無效。");

        private static DateTime ReadDate(ref Utf8JsonReader reader, JsonTokenType token, string field)
        {
            if (token != JsonTokenType.String || reader.ValueSpan.Length > 128 ||
                !DateTime.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var value))
                throw Invalid("date_invalid", $"診斷包 {field} 欄位不是有效日期。");
            return value;
        }

        private static string ReadPropertyName(ref Utf8JsonReader reader)
        {
            if (reader.ValueSpan.Length > 128)
                throw Invalid("property_name_too_large", "診斷包欄位名稱超過 128 bytes。");
            return reader.GetString() ?? "";
        }

        private static IReadOnlyDictionary<string, JsonTokenType> Fields(params (string Name, JsonTokenType Type)[] fields) =>
            fields.ToDictionary(x => x.Name, x => x.Type, Names);

        private sealed class RowState(string category, long startOffset)
        {
            private readonly HashSet<string> _properties = new(Names);
            private readonly HashSet<string> _requiredSeen = new(Names);
            private int _propertyCount;
            public string Category { get; } = category;
            public long StartOffset { get; } = startOffset;
            public string? PendingProperty { get; private set; }

            public void BeginProperty(string name)
            {
                if (++_propertyCount > 128 || !_properties.Add(name))
                    throw Invalid("row_property_ambiguous", "單筆診斷資料列欄位過多或有重複欄位。");
                PendingProperty = name;
            }

            public void AcceptValue(ref Utf8JsonReader reader, JsonTokenType token)
            {
                var required = RequiredFor(Category);
                if (required.TryGetValue(PendingProperty!, out var expected))
                {
                    if (token != expected)
                        throw Invalid("row_shape_invalid", $"{Category} 資料列必要欄位型別不符。");
                    if (expected == JsonTokenType.Number)
                    {
                        // 只驗證識別欄位是整數；不把不可信來源識別用作授權或正式資料。
                        if (!reader.TryGetInt64(out _))
                            throw Invalid("row_shape_invalid", $"{Category} 資料列識別欄位必須是 64 位整數。");
                    }
                    if (expected == JsonTokenType.String && IsDateField(PendingProperty!) &&
                        (reader.ValueSpan.Length > 128 || !DateTimeOffset.TryParse(reader.GetString(),
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                        throw Invalid("row_shape_invalid", $"{Category} 資料列日期欄位無效。");
                    _requiredSeen.Add(PendingProperty!);
                }
                PendingProperty = null;
            }

            public void Validate()
            {
                if (PendingProperty != null || RequiredFor(Category).Keys.Any(name => !_requiredSeen.Contains(name)))
                    throw Invalid("row_shape_invalid", $"{Category} 資料列缺少必要欄位。");
            }

            private static IReadOnlyDictionary<string, JsonTokenType> RequiredFor(string category) =>
                RequiredFields.TryGetValue(category, out var fields) ? fields : NestedFields[category];

            private static bool IsDateField(string name) => Names.Equals(name, "ChangedAt") ||
                Names.Equals(name, "PeriodStart") || Names.Equals(name, "MapDate") || Names.Equals(name, "RecordDate") ||
                Names.Equals(name, "From") || Names.Equals(name, "Through") || Names.Equals(name, "At");
        }

        public sealed record ParsedManifest(long BytesBeforeManifest, string Sha256BeforeManifest,
            IReadOnlyDictionary<string, long> RowCounts);
        public sealed record ParsedPackage(int FormatVersion, IReadOnlyDictionary<string, long> Counts,
            ParsedManifest? Manifest, long ManifestPropertyOffset, string IntegrityStatus);
    }
}

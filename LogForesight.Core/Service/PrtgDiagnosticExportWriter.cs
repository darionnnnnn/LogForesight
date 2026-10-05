using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>V2 匯出封套；列舉來源必須依 V2 arrays 與 SourcePolicy 的固定順序供應列。</summary>
public sealed record PrtgDiagnosticExportHeader(DateTime ExportedAt, DateTime FromDate, DateTime ToDate);

/// <summary>
/// 串流寫入完成後可交由外層 transfer 使用的有界統計。Sha256BeforeManifest 與 BytesBeforeManifest
/// 覆蓋從 JSON 第一個位元組到 Manifest 屬性分隔逗號之前的 UTF8 位元組，不包含 Manifest 本身。
/// </summary>
public sealed record PrtgDiagnosticExportWriteResult(
    IReadOnlyDictionary<string, long> Counts,
    long BytesBeforeManifest,
    long TotalBytes,
    string Sha256BeforeManifest,
    int ManifestUtf8Bytes);

/// <summary>逐列寫出 V2 診斷 JSON，不建立整包 DTO 或整包 byte array。</summary>
public sealed class PrtgDiagnosticExportWriter
{
    public const int MaximumManifestUtf8Bytes = 64 * 1024;
    private static readonly string[] OrderedProperties =
    [
        "Devices", "Sensors", "StateChanges", "Values", "HostMaps", "ManualMaps",
        "SourcePolicy", "Observations", "Timelines", "SemanticEvidence", "SemanticResults"
    ];

    public async Task<PrtgDiagnosticExportWriteResult> WriteAsync(
        IAsyncEnumerable<PrtgDiagnosticExportItem> rows,
        Stream destination,
        PrtgDiagnosticExportHeader header,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(header);
        if (!destination.CanWrite) throw new ArgumentException("匯出目的 stream 不可寫入。", nameof(destination));
        if (header.FromDate.Date > header.ToDate.Date)
            throw new ArgumentOutOfRangeException(nameof(header), "匯出日期範圍無效。");

        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var counted = new HashingWriteStream(destination, hash);
        var outputBuffer = new BoundedJsonBuffer(PrtgDiagnosticExportSource.MaximumRowBytes + 64 * 1024);
        var writer = new Utf8JsonWriter(outputBuffer, new JsonWriterOptions { Indented = false, SkipValidation = false });
        try
        {
            var counts = PrtgDiagnosticExportSource.V2ArrayProperties.ToDictionary(name => name, _ => 0L, StringComparer.Ordinal);
            await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
            var lastInputRank = -1;
            void ValidateCurrentProperty(bool hasItem)
            {
                if (!hasItem) return;
                var rank = Array.IndexOf(OrderedProperties, enumerator.Current.PropertyName);
                if (rank < 0 || rank < lastInputRank)
                    throw Invalid($"來源欄位未知或順序錯誤：{enumerator.Current.PropertyName}。");
                lastInputRank = rank;
            }

            var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            ValidateCurrentProperty(hasCurrent);
            cancellationToken.ThrowIfCancellationRequested();

            writer.WriteStartObject();
            writer.WriteNumber("FormatVersion", PrtgDataTransfer.CurrentFormatVersion);
            writer.WriteString("ExportedAt", header.ExportedAt);
            writer.WriteString("FromDate", header.FromDate);
            writer.WriteString("ToDate", header.ToDate);
            writer.WriteString("Purpose", "diagnostic-only");
            await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var property in OrderedProperties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property == "SourcePolicy")
                {
                    if (!hasCurrent || !StringComparer.Ordinal.Equals(enumerator.Current.PropertyName, property))
                        throw Invalid("來源必須依序提供且只提供一筆 SourcePolicy。");
                    var policyRow = enumerator.Current;
                    ValidateRow(policyRow);
                    var policy = JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRow.JsonUtf8);
                    if (policy == null) throw Invalid("SourcePolicy 必須是非 null 的 policy 物件。");
                    writer.WritePropertyName(property);
                    writer.WriteRawValue(policyRow.JsonUtf8, skipInputValidation: false);
                    await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    ValidateCurrentProperty(hasCurrent);
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                writer.WritePropertyName(property);
                writer.WriteStartArray();
                while (hasCurrent && StringComparer.Ordinal.Equals(enumerator.Current.PropertyName, property))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = enumerator.Current;
                    ValidateRow(item);
                    writer.WriteRawValue(item.JsonUtf8, skipInputValidation: false);
                    counts[property] = checked(counts[property] + 1);
                    await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    ValidateCurrentProperty(hasCurrent);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (hasCurrent)
                {
                    var currentRank = Array.IndexOf(OrderedProperties, enumerator.Current.PropertyName);
                    var expectedRank = Array.IndexOf(OrderedProperties, property);
                    if (currentRank < 0 || currentRank <= expectedRank)
                        throw Invalid($"來源欄位未知、重複或順序錯誤：{enumerator.Current.PropertyName}。");
                }
                writer.WriteEndArray();
                await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (hasCurrent) throw Invalid($"來源包含未知或多餘欄位：{enumerator.Current.PropertyName}。");
            cancellationToken.ThrowIfCancellationRequested();
            await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
            var bytesBeforeManifest = counted.BytesWritten;
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            var manifestUtf8 = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "legacy-json",
                formatVersion = PrtgDataTransfer.CurrentFormatVersion,
                purpose = "diagnostic-only",
                bytesBeforeManifest,
                sha256BeforeManifest = digest,
                rowCounts = counts
            });
            if (manifestUtf8.Length > MaximumManifestUtf8Bytes)
                throw Invalid("匯出 manifest 超過 64 KiB。");

            counted.HashEnabled = false;
            writer.WritePropertyName("Manifest");
            writer.WriteRawValue(manifestUtf8, skipInputValidation: false);
            writer.WriteEndObject();
            await FlushAsync(writer, outputBuffer, counted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new PrtgDiagnosticExportWriteResult(
                new Dictionary<string, long>(counts, StringComparer.Ordinal), bytesBeforeManifest,
                counted.BytesWritten, digest, manifestUtf8.Length);
            return result;
        }
        finally
        {
            writer.Reset();
            await writer.DisposeAsync().ConfigureAwait(false);
            outputBuffer.ClearPending();
        }
    }

    private static void ValidateRow(PrtgDiagnosticExportItem item)
    {
        if (item.JsonUtf8 == null || item.JsonUtf8.Length is < 1 or > PrtgDiagnosticExportSource.MaximumRowBytes)
            throw Invalid("匯出列超過 4 MiB，或內容為空。");
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private static async Task FlushAsync(
        Utf8JsonWriter writer,
        BoundedJsonBuffer outputBuffer,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await outputBuffer.DrainAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class BoundedJsonBuffer(int capacity) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[capacity];
        private int _written;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public async Task DrainAsync(Stream destination, CancellationToken cancellationToken)
        {
            var length = _written;
            var offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(PrtgDiagnosticExportSource.MaximumRowBytes, length - offset);
                await destination.WriteAsync(_buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
            ClearPending();
        }

        public void ClearPending()
        {
            Array.Clear(_buffer, 0, _written);
            _written = 0;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint > _buffer.Length - _written)
                throw Invalid("單列序列化暫存超過固定有界容量。");
        }
    }

    private sealed class HashingWriteStream(Stream destination, IncrementalHash hash) : Stream
    {
        public long BytesWritten { get; private set; }
        public bool HashEnabled { get; set; } = true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException("匯出 stream 不允許同步 flush。");
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("匯出 stream 不允許同步寫入。");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            throw new NotSupportedException("匯出 stream 不允許同步寫入。");
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            AppendMemory(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            // 此 wrapper 不擁有呼叫端的目的 stream 或 hash。
            base.Dispose(disposing);
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            BytesWritten = checked(BytesWritten + bytes.Length);
            if (HashEnabled) hash.AppendData(bytes);
        }

        private void AppendMemory(ReadOnlyMemory<byte> bytes) => Append(bytes.Span);
    }
}

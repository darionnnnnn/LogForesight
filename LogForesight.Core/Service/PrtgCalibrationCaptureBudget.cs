using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>
/// One request's logical live-memory admission budget. Reservations are cumulative for data kept by
/// the operation; short-lived page buffers use a disposable reservation. This bounds admitted
/// application payloads, not CLR/EF/native-provider overhead or measured process working set.
/// </summary>
public sealed class PrtgCalibrationCaptureBudget : IDisposable
{
    public const long DefaultMaximumBytes = 512L * 1024 * 1024;
    public const int MaximumJsonCharacters = 1024 * 1024;
    public const int MaximumJsonTokens = 30_000;
    public const int MaximumArrayItems = 1_024;
    public const int MaximumTopIssues = 1_000;
    public const int MaximumLoginFailureDetails = 50;

    private readonly long _maximumBytes;
    private long _reservedBytes;
    private int _references = 1;
    private int _ownerDisposed;
    private int _released;

    public PrtgCalibrationCaptureBudget(long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes is <= 0 or > DefaultMaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);
    public long MaximumBytes => _maximumBytes;
    public long RemainingBytes => Math.Max(0, _maximumBytes - ReservedBytes);

    /// <summary>Charges a retained allocation before materialization; charges live until operation end.</summary>
    public void Charge(long bytes, string purpose)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        EnsureActive();
        while (true)
        {
            var current = Interlocked.Read(ref _reservedBytes);
            if (bytes > _maximumBytes - current)
                throw new CalibrationCapacityException($"校準作業的總記憶體預算超過 {_maximumBytes / (1024 * 1024)} MiB，拒絕完整擷取（{purpose}）。");
            if (Interlocked.CompareExchange(ref _reservedBytes, current + bytes, current) == current) return;
        }
    }

    /// <summary>Reserves a bounded transient allocation and releases it on dispose.</summary>
    public IDisposable ReserveTransient(long bytes, string purpose)
    {
        Charge(bytes, purpose);
        return new Reservation(this, bytes, releaseReference: false);
    }

    /// <summary>Keeps the operation budget alive through response completion after package assembly returns.</summary>
    public IDisposable Retain()
    {
        EnsureActive();
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current <= 0 || Volatile.Read(ref _released) != 0)
                throw new ObjectDisposedException(nameof(PrtgCalibrationCaptureBudget));
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                return new Reservation(this, 0, releaseReference: true);
        }
    }

    /// <summary>Rolls back charges from a failed provider execution-strategy attempt.</summary>
    public AttemptScope BeginAttempt()
    {
        EnsureActive();
        return new AttemptScope(this, ReservedBytes);
    }

    /// <summary>
    /// Preflights persisted DailyAnalysisRecord JSON without constructing its object graph. Bounds
    /// all arrays, with tighter limits for TopIssues and LoginFailureDetails, before deserialization.
    /// </summary>
    public long ValidateDailyRecordJson(string json, string purpose)
        => ValidateJson(json, purpose, MaximumJsonTokens, name =>
            name?.Equals("TopIssues", StringComparison.OrdinalIgnoreCase) == true ? MaximumTopIssues :
            name?.Equals("LoginFailureDetails", StringComparison.OrdinalIgnoreCase) == true ? MaximumLoginFailureDetails : MaximumArrayItems);

    public long ValidateTimelineJson(string json, string purpose)
        => ValidateJson(json, purpose, 300_000, name => name is not null &&
            (name.Equals("Coverage", StringComparison.OrdinalIgnoreCase) || name.Equals("States", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("PendingStates", StringComparison.OrdinalIgnoreCase) || name.Equals("PendingEventKeys", StringComparison.OrdinalIgnoreCase))
            ? 20_000 : MaximumArrayItems);

    public long ValidatePolicyJson(string json, string purpose)
        => ValidateJson(json, purpose, 100_000, name => name is not null &&
            (name.Equals("HostIds", StringComparison.OrdinalIgnoreCase) || name.Equals("SensorIds", StringComparison.OrdinalIgnoreCase))
            ? 15_000 : MaximumArrayItems);

    public long ValidateHostCatalogueJson(string json, string purpose)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > LogForesight.Core.Persistence.PrtgHostSnapshot.MaximumTextCharacters)
            throw new CalibrationCapacityException($"{purpose} JSON 超過主機目錄文字上限。");
        var utf8Length = Encoding.UTF8.GetByteCount(json);
        using var utf8Reservation = ReserveTransient(utf8Length, $"{purpose} UTF-8 parser input");
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var arrays = new Stack<(string? Name, int Depth, int Count)>();
        string? propertyName = null;
        var tokens = 0;
        var arrayItems = 0;
        const int maxTokens = 500_000;
        try
        {
            while (reader.Read())
            {
                if (++tokens > maxTokens)
                    throw new CalibrationCapacityException($"{purpose} JSON 節點超過 {maxTokens} 個上限。");
                if (arrays.Count > 0 && reader.CurrentDepth == arrays.Peek().Depth + 1 &&
                    reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject))
                {
                    var frame = arrays.Pop();
                    frame.Count++;
                    arrayItems++;
                    var limit = frame.Name?.Equals("GroupIds", StringComparison.OrdinalIgnoreCase) == true ||
                                frame.Name?.Equals("OwnerUserIds", StringComparison.OrdinalIgnoreCase) == true
                        ? 15_000
                        : frame.Name == null ? 10_000 : MaximumArrayItems;
                    if (frame.Count > limit || arrayItems > maxTokens / 2)
                        throw new CalibrationCapacityException($"{purpose} JSON 陣列超過校準項目上限。");
                    arrays.Push(frame);
                }
                if (reader.TokenType == JsonTokenType.PropertyName)
                    propertyName = reader.ValueTextEquals("GroupIds") ? "GroupIds" :
                        reader.ValueTextEquals("OwnerUserIds") ? "OwnerUserIds" : null;
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    arrays.Push((propertyName, reader.CurrentDepth, 0));
                    propertyName = null;
                }
                else if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (arrays.Count == 0) throw new CalibrationCapacityException($"{purpose} JSON 陣列結構無效。");
                    arrays.Pop();
                }
                else if (reader.TokenType is not JsonTokenType.Comment) propertyName = null;
            }
        }
        catch (JsonException)
        {
            throw new CalibrationCapacityException($"{purpose} JSON 結構無效，拒絕反序列化。");
        }
        return checked(8L * json.Length + 64L * tokens + 512L);
    }

    private long ValidateJson(string json, string purpose, int maximumTokens, Func<string?, int> itemLimitForArray)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonCharacters)
            throw new CalibrationCapacityException($"{purpose} JSON 單筆超過 {MaximumJsonCharacters} 字元上限，拒絕反序列化。");

        var utf8Length = Encoding.UTF8.GetByteCount(json);
        using var utf8Reservation = ReserveTransient(utf8Length, $"{purpose} UTF-8 parser input");
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var arrays = new Stack<(string? Name, int Depth, int Count)>();
        string? pendingProperty = null;
        var tokens = 0;
        var arrayItems = 0;
        try
        {
            while (reader.Read())
            {
                if (++tokens > maximumTokens)
                    throw new CalibrationCapacityException($"{purpose} JSON 節點超過 {maximumTokens} 個上限，拒絕反序列化。");

                if (arrays.Count > 0 && reader.CurrentDepth == arrays.Peek().Depth + 1 &&
                    reader.TokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject))
                {
                    var frame = arrays.Pop();
                    frame.Count++;
                    arrayItems++;
                    var limit = itemLimitForArray(frame.Name);
                    if (frame.Count > limit || arrayItems > maximumTokens / 2)
                        throw new CalibrationCapacityException($"{purpose} JSON 陣列項目超過校準反序列化上限，拒絕完整擷取。");
                    arrays.Push(frame);
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    pendingProperty = reader.GetString();
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    arrays.Push((pendingProperty, reader.CurrentDepth, 0));
                    pendingProperty = null;
                }
                else if (reader.TokenType is JsonTokenType.EndArray)
                {
                    if (arrays.Count == 0) throw new CalibrationCapacityException($"{purpose} JSON 陣列結構無效。");
                    arrays.Pop();
                }
                else if (reader.TokenType is not JsonTokenType.Comment)
                {
                    pendingProperty = null;
                }
            }
        }
        catch (JsonException)
        {
            throw new CalibrationCapacityException($"{purpose} JSON 結構無效，拒絕反序列化。");
        }

        // A token-heavy but byte-small JSON document can still allocate many CLR objects/lists.
        // Charge 16x UTF-16 input plus 128 bytes for every parsed token before JsonSerializer runs.
        return checked(Math.Max(256L, 16L * json.Length + 128L * tokens));
    }

    private void Release(long bytes)
    {
        Interlocked.Add(ref _reservedBytes, -bytes);
    }

    private void Rollback(long checkpoint)
    {
        Interlocked.Exchange(ref _reservedBytes, checkpoint);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _released) != 0) throw new ObjectDisposedException(nameof(PrtgCalibrationCaptureBudget));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ownerDisposed, 1) == 0) ReleaseReference();
    }

    private void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _references) == 0) Interlocked.Exchange(ref _released, 1);
    }

    private sealed class Reservation(PrtgCalibrationCaptureBudget owner, long bytes, bool releaseReference) : IDisposable
    {
        private PrtgCalibrationCaptureBudget? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            if (releaseReference) current.ReleaseReference();
            else current.Release(bytes);
        }
    }

    public sealed class AttemptScope(PrtgCalibrationCaptureBudget owner, long checkpoint) : IDisposable
    {
        private bool _committed;
        public void Commit() => _committed = true;
        public void Dispose()
        {
            if (!_committed) owner.Rollback(checkpoint);
        }
    }
}

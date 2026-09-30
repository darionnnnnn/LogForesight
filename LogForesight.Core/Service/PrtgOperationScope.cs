using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>PRTG 作業自己的取消範圍。每個 HTTP 邊界重讀持久設定，不取消其他來源或共用守門器。</summary>
public sealed class PrtgOperationScope : IDisposable
{
    private readonly SystemSettings _initial;
    private readonly Func<SystemSettings> _read;
    private readonly CancellationTokenSource _cancel;
    private int _settingsChanged;
    private readonly Func<string>? _readScope;
    private string? _initialScope;
    private readonly object _scopeGate = new();
    private bool _scopeCaptured;
    public CancellationToken Token => _cancel.Token;
    public bool SettingsChanged => Volatile.Read(ref _settingsChanged) != 0;

    public PrtgOperationScope(SystemSettings initial, Func<SystemSettings> read, CancellationToken parent, Func<string>? readScope = null)
    {
        _initial = System.Text.Json.JsonSerializer.Deserialize<SystemSettings>(
            System.Text.Json.JsonSerializer.Serialize(initial))!;
        _read = read;
        _readScope = readScope;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    public void Checkpoint()
    {
        Token.ThrowIfCancellationRequested();
        var current = _read();
        // 不以 UpdatedAt 相同跳過比較：同時儲存／匯入也可能保留相同時間。
        if (!current.PrtgEnabled || !SameSettings(_initial, current) ||
            ScopeChanged())
        {
            Interlocked.Exchange(ref _settingsChanged, 1);
            _cancel.Cancel();
        }
        Token.ThrowIfCancellationRequested();
    }

    private bool ScopeChanged()
    {
        if (_readScope == null) return false;
        lock (_scopeGate)
        {
            var current = _readScope();
            if (!_scopeCaptured)
            {
                _initialScope = current;
                _scopeCaptured = true;
            }
            return current != _initialScope;
        }
    }

    public void Dispose() => _cancel.Dispose();

    public static bool SameSettings(SystemSettings a, SystemSettings b) =>
        a.PrtgUrl == b.PrtgUrl &&
        a.PrtgApiTokenEnc == b.PrtgApiTokenEnc &&
        a.PrtgAuthMode == b.PrtgAuthMode &&
        a.PrtgUsername == b.PrtgUsername &&
        a.PrtgPasswordEnc == b.PrtgPasswordEnc &&
        a.PrtgPasshashEnc == b.PrtgPasshashEnc &&
        a.PrtgIgnoreSslErrors == b.PrtgIgnoreSslErrors &&
        a.PrtgTimeoutSeconds == b.PrtgTimeoutSeconds &&
        a.PrtgFetchConcurrency == b.PrtgFetchConcurrency &&
        a.PrtgBackfillDays == b.PrtgBackfillDays &&
        a.PrtgRetentionDays == b.PrtgRetentionDays &&
        a.PrtgFetchStrategy == b.PrtgFetchStrategy &&
        a.PrtgValueFetchScope == b.PrtgValueFetchScope &&
        a.PrtgResourceGuardEnabled == b.PrtgResourceGuardEnabled &&
        a.PrtgResourceGuardCpuPercent == b.PrtgResourceGuardCpuPercent &&
        a.PrtgResourceGuardMemoryFreePercent == b.PrtgResourceGuardMemoryFreePercent &&
        a.PrtgResourceGuardCheckSeconds == b.PrtgResourceGuardCheckSeconds &&
        a.PrtgResourceGuardPauseMinutes == b.PrtgResourceGuardPauseMinutes &&
        a.PrtgResourceGuardStrikes == b.PrtgResourceGuardStrikes &&
        a.PrtgResourceGuardMaxPauseMinutes == b.PrtgResourceGuardMaxPauseMinutes &&
        SameList(a.PrtgSensorTypeWhitelist, b.PrtgSensorTypeWhitelist) &&
        SameList(a.PrtgSensorTypeCategoryOverrides, b.PrtgSensorTypeCategoryOverrides) &&
        SameList(a.PrtgValueFetchExtraHosts, b.PrtgValueFetchExtraHosts) &&
        SameList(a.PrtgResourceGuardSensorObjids, b.PrtgResourceGuardSensorObjids);

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
}

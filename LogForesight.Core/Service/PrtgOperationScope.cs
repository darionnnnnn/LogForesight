using LogForesight.Core.Models;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
    private static readonly ConcurrentDictionary<string, PrtgOperationVersion> Versions = new();
    private static readonly ConcurrentDictionary<string, PrtgOperationScope> Active = new();
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _kind;
    private readonly bool _requireEnabled;
    private readonly bool _cancelOnEnabledChange;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _lastStage = "尚未完成階段";
    private bool _disposed;
    public static IReadOnlyList<PrtgOperationVersion> ReadVersions()
    {
        foreach (var operation in Active.Values) operation.UpdateVersion("執行中");
        return Versions.Values.OrderByDescending(v => v.StartedAtUtc).ToArray();
    }
    public CancellationToken Token => _cancel.Token;
    public bool SettingsChanged => Volatile.Read(ref _settingsChanged) != 0;

    public PrtgOperationScope(SystemSettings initial, Func<SystemSettings> read, CancellationToken parent, Func<string>? readScope = null,
        string kind = "PRTG", bool requireEnabled = true, bool cancelOnEnabledChange = false)
    {
        _initial = System.Text.Json.JsonSerializer.Deserialize<SystemSettings>(
            System.Text.Json.JsonSerializer.Serialize(initial))!;
        _read = read;
        _readScope = readScope;
        _kind = kind;
        _requireEnabled = requireEnabled;
        _cancelOnEnabledChange = cancelOnEnabledChange;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(parent);
        Active[_id] = this;
        Publish(initial, "執行中");
    }

    public void Checkpoint()
    {
        Token.ThrowIfCancellationRequested();
        var current = _read();
        // 不以 UpdatedAt 相同跳過比較：同時儲存／匯入也可能保留相同時間。
        if (EnabledStateInvalid(current) || !SameSettings(_initial, current) ||
            ScopeChanged())
        {
            Interlocked.Exchange(ref _settingsChanged, 1);
            _cancel.Cancel();
        }
        Publish(current, SettingsChanged ? "已取消：設定或範圍變更" : "執行中");
        Token.ThrowIfCancellationRequested();
    }

    private bool EnabledStateInvalid(SystemSettings current) =>
        !current.PrtgEnabled && (_requireEnabled || _initial.PrtgEnabled) ||
        _cancelOnEnabledChange && current.PrtgEnabled != _initial.PrtgEnabled;

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

    public void CompletedStage(string stage)
    {
        _lastStage = stage;
        UpdateVersion("執行中");
    }
    private void UpdateVersion(string state)
    {
        lock (_scopeGate)
        {
        if (_disposed && state == "執行中") return;
        try { Publish(_read(), state); }
        catch
        {
            // 可觀測性不能使原本的 DB 失敗／取消被 Dispose 的例外遮住。
            if (Versions.TryGetValue(_id, out var previous)) Versions[_id] = previous with {
                ExpectedSettingsRevision = "無法讀取", ExpectedScopeRevision = "無法讀取",
                EndedAtUtc = _disposed ? DateTimeOffset.UtcNow : null, State = _disposed ? "已結束：版本讀取失敗" : "版本讀取失敗，待查證" };
        }
        }
    }
    private void Publish(SystemSettings current, string state)
    {
        static string Hash(string? value) => value == null ? "尚未讀取" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var scope = _scopeCaptured ? _readScope?.Invoke() : null;
        var changed = !SameSettings(_initial, current) || EnabledStateInvalid(current) ||
            _scopeCaptured && scope != _initialScope;
        Versions[_id] = new(_id, _kind, _startedAt, _disposed ? DateTimeOffset.UtcNow : null,
            _initial.Revision, current.Revision, Hash(_initialScope), Hash(scope), _lastStage,
            !_disposed && changed && !SettingsChanged ? "等待安全取消點" : state, changed);
        foreach (var old in Versions.Values.Where(v => v.EndedAtUtc != null).OrderByDescending(v => v.StartedAtUtc).Skip(32))
            Versions.TryRemove(old.OperationId, out _);
    }
    public void Dispose()
    {
        lock (_scopeGate)
        {
        if (_disposed) return;
        _disposed = true;
        Active.TryRemove(_id, out _);
        try { UpdateVersion(SettingsChanged ? "已取消：設定或範圍變更" : Token.IsCancellationRequested ? "已取消：停止作業" : "已結束（不代表成功）"); }
        finally { _cancel.Dispose(); }
        }
    }

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

/// <summary>僅含工作識別與版本摘要；不保留憑證、主機清單或連線位址。</summary>
public sealed record PrtgOperationVersion(string OperationId, string Kind, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc, string AdoptedSettingsRevision, string ExpectedSettingsRevision,
    string AdoptedScopeRevision, string ExpectedScopeRevision, string LastCompletedStage, string State, bool StopRequested);

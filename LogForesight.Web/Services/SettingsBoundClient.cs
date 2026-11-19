namespace LogForesight.Web.Services;

/// <summary>
/// 「設定快照 → 比對 → 重建客戶端」的共用模式（docs/archive/HISTORY.md S8）。
///
/// 背景：外部服務的位址／憑證存在 DB（「系統管理 > 設定」頁隨時可改），但客戶端物件
/// （HttpClient、LdapService 等）不宜每次請求都重建。做法是快取一個實例＋記住建構時用的
/// 設定快照，每次取用時跟 DB 目前值比對，變了才重建——這套邏輯原本在 WebAiService
/// 為互動情境／對話情境各自寫一份（近乎逐字重複），#9 的 AD 動態驗證（LdapService 隨
/// DB 設定重建）是第三個使用點，因此收斂成一個泛型工具。
///
/// <typeparamref name="TSnapshot"/> 由呼叫端決定形狀——WebAiService 用 (BaseUrl, 金鑰密文)
/// 這樣的 ValueTuple，DynamicAuthenticationProvider 用 AD 伺服器清單＋SearchBase／Filter
/// 組成的快照；比對走 <see cref="object.Equals(object?, object?)"/>，ValueTuple／record
/// 都有現成的結構相等語意，不需要額外實作。
///
/// 「未設定時回 null」的判斷交給 <paramref name="factory"/> 自行決定（例如 baseUrl 空白，
/// 或 AD 伺服器清單為空）——本類別只負責快照比對與重建，不預設任何「未設定」的形狀。
///
/// 被換掉的舊實例若是 <see cref="IDisposable"/>（AIService、PrtgClient 的 HttpClient 與連線池）要釋放，
/// 否則每改一次設定就洩漏一組連線。但舊實例可能還有進行中的請求（站台上互動與對話請求並行），
/// 立刻釋放會讓它們擲 <see cref="ObjectDisposedException"/>：所以換掉時不立刻釋放，
/// 而是等 <c>disposeGrace</c>（預設兩分鐘，大於最長的互動請求逾時）後由計時器釋放；
/// 只被單一迴圈循序使用、不會有進行中請求的呼叫端（PRTG 快照服務）傳 <see cref="TimeSpan.Zero"/> 立即釋放。
/// 不用參考計數：取用端不必改成「借用／歸還」的寫法，寬限期內重建又被換掉的舊實例各自有自己的計時器。
/// </summary>
public sealed class SettingsBoundClient<TSnapshot, TClient> where TClient : class
{
    private readonly Func<TSnapshot, TClient?> _factory;
    private readonly object _lock = new();

    private readonly TimeSpan _disposeGrace;
    private readonly TimeProvider _clock;

    private TClient? _client;
    private TSnapshot? _snapshot;

    /// <summary>已換掉、等寬限期後釋放的舊實例與其計時器（只在設定變更時增加，計時器觸發即移除）。</summary>
    private readonly List<(IDisposable Client, ITimer Timer)> _retired = new();

    /// <param name="factory">依目前快照建立客戶端；快照代表「未設定」時（呼叫端自行判斷）回 null</param>
    /// <param name="disposeGrace">換掉舊實例後延遲多久才釋放（讓進行中的請求跑完）；省略＝兩分鐘，<see cref="TimeSpan.Zero"/>＝立即釋放</param>
    public SettingsBoundClient(Func<TSnapshot, TClient?> factory, TimeSpan? disposeGrace = null, TimeProvider? clock = null)
    {
        _factory = factory;
        _disposeGrace = disposeGrace ?? TimeSpan.FromMinutes(2);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 依目前快照取（或重建）客戶端。快照與上次相同且客戶端非 null 時直接回快取實例，
    /// 否則呼叫 factory 重建（factory 回 null 時，本次呼叫回 null，下次快照不變仍會再呼叫一次
    /// factory——這個重複判斷成本可忽略，「未設定」的 factory 本來就該是廉價的）。
    /// </summary>
    public TClient? Get(TSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_client != null && Equals(_snapshot, snapshot)) return _client;

            var previous = _client;
            _client = _factory(snapshot);
            _snapshot = snapshot;
            if (previous != null) Retire(previous);
            return _client;
        }
    }

    /// <summary>目前持有的客戶端（不觸發重建）；尚未建立或未設定時為 null。</summary>
    public TClient? Current { get { lock (_lock) return _client; } }

    /// <summary>立即釋放目前的與所有等待釋放的實例並清空狀態（服務停止時用）；之後再 Get 會重新建立。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (client, timer) in _retired)
            {
                timer.Dispose();
                client.Dispose();
            }
            _retired.Clear();
            (_client as IDisposable)?.Dispose();
            _client = null;
            _snapshot = default;
        }
    }

    /// <summary>換掉的舊實例：不可釋放的直接交給 GC；可釋放的依寬限期釋放（呼叫端須持鎖）。</summary>
    private void Retire(TClient previous)
    {
        if (previous is not IDisposable disposable) return;
        if (_disposeGrace <= TimeSpan.Zero) { disposable.Dispose(); return; }

        ITimer? timer = null;
        timer = _clock.CreateTimer(_ =>
        {
            lock (_lock)
            {
                var index = _retired.FindIndex(r => ReferenceEquals(r.Client, disposable));
                if (index >= 0) _retired.RemoveAt(index);
            }
            timer?.Dispose();
            disposable.Dispose();
        }, null, _disposeGrace, Timeout.InfiniteTimeSpan);
        _retired.Add((disposable, timer));
    }
}

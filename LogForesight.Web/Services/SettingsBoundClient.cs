namespace LogForesight.Web.Services;

/// <summary>
/// 「設定快照 → 比對 → 重建客戶端」的共用模式（docs/SHARED-STANDARDS-PLAN.md S8）。
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
/// 重建是低頻事件，被汰換的舊實例交給 GC 即可——不引入 IDisposable 生命週期管理，
/// 沿用 WebAiService 原本的決策。
/// </summary>
public sealed class SettingsBoundClient<TSnapshot, TClient> where TClient : class
{
    private readonly Func<TSnapshot, TClient?> _factory;
    private readonly object _lock = new();

    private TClient? _client;
    private TSnapshot? _snapshot;

    /// <param name="factory">依目前快照建立客戶端；快照代表「未設定」時（呼叫端自行判斷）回 null</param>
    public SettingsBoundClient(Func<TSnapshot, TClient?> factory) => _factory = factory;

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

            _client = _factory(snapshot);
            _snapshot = snapshot;
            return _client;
        }
    }
}

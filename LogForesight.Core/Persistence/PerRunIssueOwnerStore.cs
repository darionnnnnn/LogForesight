namespace LogForesight.Core.Persistence;

/// <summary>
/// 夜間掛接專用的問題負責人讀取包裝：<see cref="GetAll"/> 第一次呼叫時整份讀一次並保留，
/// 之後回傳該份的複本清單（清單是新的、元素共用；呼叫端只能讀，夜間掛接不修改元素）。
/// <see cref="Update"/>／<see cref="Delete"/> 之後丟棄保留的那一份，下次 <see cref="GetAll"/> 重讀。
/// <para>
/// 為什麼要有：<c>IssueOwnerStore</c> 不快取，夜間協調器每個有問題的主機日都整份讀取並反序列化一次。
/// 這個包裝的生命週期是一趟背景工作（每趟新建，不是 singleton），不會跨趟留存。
/// </para>
/// <para>
/// 行為差異（可接受）：同一趟內網頁端中途修改的問題負責人自動套用結論，要到下一趟才被夜間掛接採用，
/// 與同一趟派工脈絡（<c>DispatchContext.Build</c> 趟首讀一次）一致。網頁端 singleton 協調器不使用本包裝。
/// </para>
/// </summary>
public sealed class PerRunIssueOwnerStore(IIssueOwnerStore inner) : IIssueOwnerStore
{
    private readonly object _gate = new();
    private List<IssueProfile>? _cached;

    public List<IssueProfile> GetAll()
    {
        lock (_gate)
        {
            _cached ??= inner.GetAll();
            return new List<IssueProfile>(_cached);
        }
    }

    public IssueProfile? Get(string source, int eventId) => inner.Get(source, eventId);

    public IssueProfile Update(string source, int eventId, Action<IssueProfile> apply)
    {
        try { return inner.Update(source, eventId, apply); }
        finally { Invalidate(); }
    }

    public void Delete(string source, int eventId)
    {
        try { inner.Delete(source, eventId); }
        finally { Invalidate(); }
    }

    private void Invalidate() { lock (_gate) _cached = null; }
}

namespace LogForesight.Core.Persistence;

/// <summary>
/// 派工候選人快照的來源。「使用者看得到哪些主機」「使用者有沒有處理能力」兩條規則留在 Web，
/// Core 不引用也不複製——由 Web 實作在每趟執行前算好一份快照當資料交進來。
/// </summary>
public interface IDispatchCandidateSource
{
    DispatchCandidatePool Build();
}

/// <summary>派工候選人（一位啟用中且具處理能力的使用者）的快照</summary>
public sealed class DispatchCandidate
{
    public long UserId { get; init; }

    public string Account { get; init; } = string.Empty;

    /// <summary>暫停接單（<see cref="WebUser.DispatchPaused"/>）</summary>
    public bool Paused { get; init; }

    /// <summary>屬於任一「啟用中」且 <see cref="UserGroup.DispatchPool"/>＝true 的使用者群組</summary>
    public bool InPool { get; init; }

    /// <summary>可見主機；只為 InPool 且未暫停者計算，其餘為空集合</summary>
    public IReadOnlySet<long> VisibleHostIds { get; init; } = new HashSet<long>();
}

/// <summary>一趟執行的候選人池</summary>
public sealed class DispatchCandidatePool
{
    /// <summary>只收「啟用中且具處理能力」的使用者</summary>
    public IReadOnlyDictionary<long, DispatchCandidate> ByUserId { get; init; } = new Dictionary<long, DispatchCandidate>();

    /// <summary>InPool 的人數（不論是否暫停）</summary>
    public int PoolMemberCount { get; init; }

    /// <summary>InPool 且未暫停的人數</summary>
    public int ActivePoolMemberCount { get; init; }
}

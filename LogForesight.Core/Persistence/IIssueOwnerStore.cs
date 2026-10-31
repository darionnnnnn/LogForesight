namespace LogForesight.Core.Persistence;

/// <summary>問題檔案的讀寫（↔ webdata blob，key=issue_owners，回饋十八輪批次F 建立、
/// 回饋十九輪批次F 擴充承載機房結論——見 <see cref="IssueProfile"/>）</summary>
public interface IIssueOwnerStore
{
    List<IssueProfile> GetAll();

    /// <summary>(Source, EventId) 不分大小寫比對；找不到回 null。</summary>
    IssueProfile? Get(string source, int eventId);

    /// <summary>
    /// 原子更新：在同一個互斥區塊內找到既有列（找不到就建新列）、套用 <paramref name="apply"/>、
    /// 設 UpdatedAt，並回傳寫入後的複本。呼叫端的 lambda 只改自己負責的欄位，其餘欄位維持
    /// 區塊內讀到的最新值——兩位管理者同時操作同一問題時不會互相還原對方剛寫的欄位。
    /// </summary>
    IssueProfile Update(string source, int eventId, Action<IssueProfile> apply);

    void Delete(string source, int eventId);
}

/// <summary><see cref="IIssueOwnerStore"/> 的實作：整份 JSON 存一筆 blob，與 SentinelStore 等
/// 其他 webdata store 同模式（JsonBlobCollection 提供原子讀改寫）。</summary>
public class IssueOwnerStore : JsonBlobCollection<IssueProfile>, IIssueOwnerStore
{
    public IssueOwnerStore(EfJsonBlobStore blob) : base(blob) { }

    public List<IssueProfile> GetAll() => Read();

    public IssueProfile? Get(string source, int eventId) =>
        Read().FirstOrDefault(r => Matches(r, source, eventId));

    /// <summary>依 (Source, EventId) 自然鍵新增或更新（整列覆寫）。僅供測試植入資料；正式路徑一律用 Update(apply) 原子更新。</summary>
    public IssueProfile Upsert(IssueProfile rule)
    {
        return Mutate(items =>
        {
            var existing = items.FirstOrDefault(r => Matches(r, rule.SourceName, rule.EventId));
            var now = DateTime.Now;

            if (existing == null)
            {
                rule.UpdatedAt = now;
                items.Add(rule);
                return rule;
            }

            // 逐欄複製：新增模型欄位時這裡要跟著加一行（同 SentinelStore.Upsert 的既有慣例，
            // 漏抄的症狀是「新增存得進去、編輯被靜默還原」）
            existing.OwnerUserIds = rule.OwnerUserIds;
            existing.Note = rule.Note;
            existing.UpdatedAt = now;
            existing.UpdatedByAccount = rule.UpdatedByAccount;
            existing.ConclusionStatus = rule.ConclusionStatus;
            existing.ConclusionNote = rule.ConclusionNote;
            existing.ConcludedById = rule.ConcludedById;
            existing.ConcludedByAccount = rule.ConcludedByAccount;
            existing.ConcludedAt = rule.ConcludedAt;
            existing.AutoApply = rule.AutoApply;
            existing.Mutes = rule.Mutes;
            return existing;
        });
    }

    public IssueProfile Update(string source, int eventId, Action<IssueProfile> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return Mutate(items =>
        {
            var row = items.FirstOrDefault(r => Matches(r, source, eventId));
            if (row == null)
            {
                row = new IssueProfile { SourceName = source, EventId = eventId };
                items.Add(row);
            }
            apply(row);
            row.UpdatedAt = DateTime.Now;
            // Mutate 每次都從 blob 反序列化出全新物件，回傳的列不會與 store 內部共用參考
            return row;
        });
    }

    public void Delete(string source, int eventId) =>
        Mutate(items => items.RemoveAll(r => Matches(r, source, eventId)));

    private static bool Matches(IssueProfile r, string source, int eventId) => IssueProfile.Matches(r, source, eventId);
}

namespace LogForesight.Core.Persistence;

/// <summary>問題負責人規則的讀寫（↔ webdata blob，key=issue_owners，回饋十八輪批次F）</summary>
public interface IIssueOwnerStore
{
    List<IssueOwnerRule> GetAll();

    /// <summary>(Source, EventId) 不分大小寫比對；找不到回 null。</summary>
    IssueOwnerRule? Get(string source, int eventId);

    /// <summary>依 (Source, EventId) 自然鍵新增或更新。</summary>
    IssueOwnerRule Upsert(IssueOwnerRule rule);

    void Delete(string source, int eventId);
}

/// <summary><see cref="IIssueOwnerStore"/> 的實作：整份 JSON 存一筆 blob，與 SentinelStore 等
/// 其他 webdata store 同模式（JsonBlobCollection 提供原子讀改寫）。</summary>
public class IssueOwnerStore : JsonBlobCollection<IssueOwnerRule>, IIssueOwnerStore
{
    public IssueOwnerStore(EfJsonBlobStore blob) : base(blob) { }

    public List<IssueOwnerRule> GetAll() => Read();

    public IssueOwnerRule? Get(string source, int eventId) =>
        Read().FirstOrDefault(r => Matches(r, source, eventId));

    public IssueOwnerRule Upsert(IssueOwnerRule rule)
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
            return existing;
        });
    }

    public void Delete(string source, int eventId) =>
        Mutate(items => items.RemoveAll(r => Matches(r, source, eventId)));

    private static bool Matches(IssueOwnerRule r, string source, int eventId) => IssueOwnerRule.Matches(r, source, eventId);
}


namespace LogForesight.Core.Persistence;

/// <summary>
/// <see cref="IIssueCaseStore"/> 的實作（blob key=issue_cases）。
/// 整份型（會更新，走 <see cref="JsonBlobCollection{T}"/> 的原子讀改寫）。
///
/// 同一（主機, 問題簽章）同時間至多一個進行中案件（ClosedAt == null）由
/// <see cref="Save"/> 呼叫端（<c>IssueCaseCoordinator</c>）保證——這裡不重覆檢查，
/// 與其餘 store 的職責分工一致（store 只管持久化，業務規則在呼叫端單點）。
/// </summary>
public class IssueCaseStore : JsonBlobCollection<IssueCase>, IIssueCaseStore
{
    public IssueCaseStore(EfJsonBlobStore blob) : base(blob) { }

    public IssueCase? GetOpen(string hostName, string issueKey) =>
        Read().FirstOrDefault(c =>
            string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.IssueKey, issueKey, StringComparison.Ordinal) &&
            c.ClosedAt == null);

    public List<IssueCase> GetOpenForHost(string hostName) =>
        Read().Where(c =>
            string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
            c.ClosedAt == null).ToList();

    public List<IssueCase> GetMany(IEnumerable<string> hostNames)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Read().Where(c => names.Contains(c.HostName)).ToList();
    }

    public List<IssueCase> GetOpenByHandler(long userId) =>
        Read().Where(c => c.HandlerId == userId && c.ClosedAt == null).ToList();

    public IssueCase? Get(string caseId) =>
        Read().FirstOrDefault(c => string.Equals(c.CaseId, caseId, StringComparison.Ordinal));

    public void Save(IssueCase issueCase)
    {
        Mutate(items =>
        {
            var existing = items.FirstOrDefault(c => string.Equals(c.CaseId, issueCase.CaseId, StringComparison.Ordinal));
            if (existing == null)
            {
                items.Add(issueCase);
                return;
            }

            existing.Status = issueCase.Status;
            existing.HandlerId = issueCase.HandlerId;
            existing.Note = issueCase.Note;
            existing.DueDate = issueCase.DueDate;
            existing.FirstLinkedDate = issueCase.FirstLinkedDate;
            existing.LastLinkedDate = issueCase.LastLinkedDate;
            existing.ClosedAt = issueCase.ClosedAt;
            existing.UpdatedAt = issueCase.UpdatedAt;
        });
    }
}

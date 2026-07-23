namespace LogForesight;

/// <summary>
/// <see cref="IIssueHandlingStore"/> 的 JSONL 後端實作（webdata\issue_handling.json）。
/// 整檔型（會更新，走 <see cref="JsonCollectionFile{T}"/> 的原子替換＋跨程序鎖）。
///
/// 只保留「有標記」的問題列——未標記＝未處理，不佔一列。清除標記等於刪掉該列，
/// 讓 issue_handling.json 只裝真正被人動過的問題，跟風險日「缺列即未處理」同一套語意。
/// </summary>
public class JsonIssueHandlingStore : JsonCollectionFile<IssueHandling>, IIssueHandlingStore
{
    public JsonIssueHandlingStore(string filePath) : base(filePath) { }

    public List<IssueHandling> GetForDay(string hostName, DateTime date) =>
        Read().Where(h => SameDay(h, hostName, date)).ToList();

    public List<IssueHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Read()
            .Where(h => names.Contains(h.HostName) && h.Date.Date >= from.Date && h.Date.Date <= to.Date)
            .ToList();
    }

    public void Save(IssueHandling handling)
    {
        // 空狀態＝清除標記：不留一列「狀態為空」的殭屍資料，直接回到未處理
        if (string.IsNullOrWhiteSpace(handling.Status))
        {
            Clear(handling.HostName, handling.Date, handling.IssueKey);
            return;
        }

        Mutate(items =>
        {
            var existing = items.FirstOrDefault(h => SameIssue(h, handling.HostName, handling.Date, handling.IssueKey));
            if (existing == null)
            {
                items.Add(handling);
                return;
            }

            existing.Status = handling.Status;
            existing.ActorId = handling.ActorId;
            existing.ActorAccount = handling.ActorAccount;
            existing.Note = handling.Note;
            existing.UpdatedAt = handling.UpdatedAt;
        });
    }

    public void Clear(string hostName, DateTime date, string issueKey) =>
        Mutate(items => items.RemoveAll(h => SameIssue(h, hostName, date, issueKey)));

    private static bool SameDay(IssueHandling handling, string hostName, DateTime date) =>
        string.Equals(handling.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
        handling.Date.Date == date.Date;

    private static bool SameIssue(IssueHandling handling, string hostName, DateTime date, string issueKey) =>
        SameDay(handling, hostName, date) &&
        string.Equals(handling.IssueKey, issueKey, StringComparison.Ordinal);
}

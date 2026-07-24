namespace LogForesight;

/// <summary>
/// 已知雜訊記憶的讀寫（↔ <see cref="NoiseMark"/>）。整份型（會更新，走
/// <see cref="JsonBlobCollection{T}"/> 的原子讀改寫），規模有界——
/// 只有「真的被標過已知雜訊」的簽章才佔一列，不是每天都寫。
/// </summary>
public interface INoiseMarkStore
{
    /// <summary>單一主機的全部記憶，索引用（GetDetail 逐問題查表）</summary>
    List<NoiseMark> GetForHost(string hostName);

    NoiseMark? Get(string hostName, string issueKey);

    void Save(NoiseMark mark);

    /// <summary>「調回未處理」且使用者選擇刪除記憶時呼叫；之後同簽章不再自動判讀成雜訊</summary>
    void Delete(string hostName, string issueKey);
}

public class JsonNoiseMarkStore : JsonBlobCollection<NoiseMark>, INoiseMarkStore
{
    public JsonNoiseMarkStore(IJsonBlobStore blob) : base(blob) { }

    public List<NoiseMark> GetForHost(string hostName) =>
        Read().Where(m => string.Equals(m.HostName, hostName, StringComparison.OrdinalIgnoreCase)).ToList();

    public NoiseMark? Get(string hostName, string issueKey) =>
        Read().FirstOrDefault(m => Same(m, hostName, issueKey));

    public void Save(NoiseMark mark)
    {
        Mutate(items =>
        {
            var existing = items.FirstOrDefault(m => Same(m, mark.HostName, mark.IssueKey));
            if (existing == null)
            {
                items.Add(mark);
                return;
            }

            existing.MarkedByAccount = mark.MarkedByAccount;
            existing.MarkedAt = mark.MarkedAt;
            existing.Note = mark.Note;
        });
    }

    public void Delete(string hostName, string issueKey) =>
        Mutate(items => items.RemoveAll(m => Same(m, hostName, issueKey)));

    private static bool Same(NoiseMark mark, string hostName, string issueKey) =>
        string.Equals(mark.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(mark.IssueKey, issueKey, StringComparison.Ordinal);
}

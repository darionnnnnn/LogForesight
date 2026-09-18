using LogForesight.Core.Models;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 由問題檔案的靜音區間合成 <see cref="SuppressionTargetTypes.IssueMute"/> 抑制項目（唯一一份合成邏輯）。
/// 每個區間一筆、Scope=Site（靜音跨全部主機）。
/// </summary>
public static class MuteSuppressions
{
    public static List<RuleSuppression> From(IEnumerable<IssueProfile> profiles) =>
        profiles
            .SelectMany(p => p.Mutes.Select(m => new RuleSuppression
            {
                TargetType = SuppressionTargetTypes.IssueMute,
                Scope = SuppressionScopes.Site,
                SourceName = p.SourceName,
                EventId = p.EventId,
                MuteFrom = m.From.Date,
                MuteTo = m.To.Date,
                Reason = m.Reason,
                SuppressedBy = m.ByAccount,
                CreatedAt = m.At
            }))
            .ToList();
}

/// <summary>
/// 分析入口用的抑制來源包裝層：<see cref="LoadAll"/>＝內層抑制清單＋由問題檔案合成的靜音項目；
/// <see cref="SaveAll"/> 先濾掉靜音項目再交給內層，防止合成項目被存回抑制 blob。
/// 只給分析流程（本機／NetIQ／體檢、PRTG、AI 補寫）使用；規則頁的 store 不包。
/// </summary>
public sealed class MuteAwareSuppressionStore : ISuppressionStore
{
    private readonly ISuppressionStore _inner;
    private readonly IIssueOwnerStore _issueOwners;

    public MuteAwareSuppressionStore(ISuppressionStore inner, IIssueOwnerStore issueOwners)
    {
        _inner = inner;
        _issueOwners = issueOwners;
    }

    public string Location => _inner.Location;

    public List<RuleSuppression> LoadAll()
    {
        var all = _inner.LoadAll();
        all.AddRange(MuteSuppressions.From(_issueOwners.GetAll()));
        return all;
    }

    public void SaveAll(List<RuleSuppression> suppressions) =>
        _inner.SaveAll(suppressions.Where(s => s.TargetType != SuppressionTargetTypes.IssueMute).ToList());
}

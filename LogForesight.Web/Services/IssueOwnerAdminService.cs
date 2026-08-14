using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題負責人管理（回饋十八輪批次F，「問題負責人」頁）：以 (Source,EventId) 為鍵指派跨主機的
/// 負責人。與主機負責人（HostAdminService.SetHostOwners）是相同概念、相同管理模式，
/// 唯一差別是鍵從「主機」換成「問題」。
/// </summary>
public class IssueOwnerAdminService
{
    /// <summary>問題選擇器的近期出現窗口——與問題負責人的授權路徑窗口（RetentionDays）刻意不同：
    /// 那是「保留期內都算負責人可見」的長窗口，這是「新增規則時該挑哪個問題」的短窗口，
    /// 30 天內出現過的問題才有指派的實務意義，太久沒出現的問題挑進來也只是雜訊。</summary>
    private const int RecentIssueWindowDays = 30;

    private readonly IIssueOwnerStore _issueOwners;
    private readonly IIssueAggregateQuery _issueAggregates;
    private readonly IUserStore _users;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;

    public IssueOwnerAdminService(
        IIssueOwnerStore issueOwners, IIssueAggregateQuery issueAggregates, IUserStore users, IAuditService audit,
        ICurrentUser currentUser)
    {
        _issueOwners = issueOwners;
        _issueAggregates = issueAggregates;
        _users = users;
        _audit = audit;
        _currentUser = currentUser;
    }

    public List<IssueOwnerDto> List()
    {
        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var recentByKey = RecentAggregatesByKey();

        return _issueOwners.GetAll()
            .OrderBy(r => r.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EventId)
            .Select(r => ToDto(r, usersById, recentByKey))
            .ToList();
    }

    /// <summary>問題選擇器候選項：近 <see cref="RecentIssueWindowDays"/> 天內出現過的問題，
    /// 依主機數由多到少排序（影響面大的問題排前面，符合「今天該先處理哪個」的既有排序哲學）。</summary>
    public List<RecentIssueOptionDto> RecentIssues()
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(RecentIssueWindowDays - 1));
        var owned = _issueOwners.GetAll()
            .Select(r => IssueOwnerRule.KeyOf(r.SourceName, r.EventId))
            .ToHashSet();

        return _issueAggregates.Aggregate(from, to, null)
            .OrderByDescending(a => a.HostCount)
            .ThenBy(a => a.Source, StringComparer.OrdinalIgnoreCase)
            .Select(a => new RecentIssueOptionDto
            {
                SourceName = a.Source,
                EventId = a.EventId,
                HostCount = a.HostCount,
                LastSeen = a.LastSeen,
                HasOwner = owned.Contains(IssueOwnerRule.KeyOf(a.Source, a.EventId))
            })
            .ToList();
    }

    public IssueOwnerDto Upsert(SaveIssueOwnerRequest request)
    {
        var sourceName = request.SourceName?.Trim() ?? "";
        if (sourceName.Length == 0)
            throw DomainException.Validation("請指定問題來源（Source）。");
        if (request.EventId <= 0)
            throw DomainException.Validation("請指定合法的 Event ID。");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var requestedOwners = request.OwnerUserIds.Distinct().ToList();
        NameFormat.EnsureAllKnown(requestedOwners, usersById, "使用者");

        var before = _issueOwners.Get(sourceName, request.EventId);
        var beforeOwners = before?.OwnerUserIds.Select(id => usersById.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList()
                            ?? new List<string>();
        var afterOwners = requestedOwners.Select(id => usersById[id].Account).ToList();

        var saved = _issueOwners.Upsert(new IssueOwnerRule
        {
            SourceName = sourceName,
            EventId = request.EventId,
            OwnerUserIds = requestedOwners,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            UpdatedByAccount = _currentUser.Account
        });

        _audit.Record(
            action: AuditActions.IssueOwnerUpdate,
            summary: $"設定問題「{sourceName} {request.EventId}」的負責人：由「{NameFormat.Join(beforeOwners)}」改為「{NameFormat.Join(afterOwners)}」",
            targetKind: "issue_owner",
            targetId: $"{sourceName}/{request.EventId}",
            detail: new { SourceName = sourceName, request.EventId, Before = beforeOwners, After = afterOwners, request.Note });

        var recentByKey = RecentAggregatesByKey();
        return ToDto(saved, usersById, recentByKey);
    }

    public void Delete(string source, int eventId)
    {
        var existing = _issueOwners.Get(source, eventId)
                        ?? throw DomainException.NotFound("找不到這筆問題負責人規則。");

        var usersById = _users.GetAll().ToDictionary(u => u.UserId);
        var ownerNames = existing.OwnerUserIds.Select(id => usersById.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList();

        _issueOwners.Delete(source, eventId);

        _audit.Record(
            action: AuditActions.IssueOwnerDelete,
            summary: $"刪除問題「{existing.SourceName} {existing.EventId}」的負責人規則（原負責人：{NameFormat.Join(ownerNames)}）",
            targetKind: "issue_owner",
            targetId: $"{existing.SourceName}/{existing.EventId}");
    }

    private Dictionary<(string SourceUpper, int EventId), (int HostCount, DateTime LastSeen)> RecentAggregatesByKey()
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(RecentIssueWindowDays - 1));
        return _issueAggregates.Aggregate(from, to, null)
            .ToDictionary(a => IssueOwnerRule.KeyOf(a.Source, a.EventId), a => (a.HostCount, a.LastSeen));
    }

    private IssueOwnerDto ToDto(
        IssueOwnerRule rule, Dictionary<long, WebUser> usersById,
        Dictionary<(string SourceUpper, int EventId), (int HostCount, DateTime LastSeen)> recentByKey)
    {
        var recent = recentByKey.TryGetValue(IssueOwnerRule.KeyOf(rule.SourceName, rule.EventId), out var r) ? r : ((int, DateTime)?)null;

        return new IssueOwnerDto
        {
            SourceName = rule.SourceName,
            EventId = rule.EventId,
            OwnerUserIds = rule.OwnerUserIds,
            OwnerNames = rule.OwnerUserIds
                .Select(id => usersById.TryGetValue(id, out var u) ? NameFormat.WithAccount(u.DisplayName, u.Account) : $"(已刪除:{id})")
                .ToList(),
            Note = rule.Note,
            UpdatedAt = rule.UpdatedAt,
            UpdatedByAccount = rule.UpdatedByAccount,
            UpdatedByDisplayName = string.IsNullOrEmpty(rule.UpdatedByAccount)
                ? null
                : _users.FindByAccount(rule.UpdatedByAccount)?.DisplayName,
            RecentHostCount = recent?.Item1,
            RecentLastSeen = recent?.Item2
        };
    }
}

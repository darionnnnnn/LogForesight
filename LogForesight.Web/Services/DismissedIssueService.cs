using LogForesight.Core.Models;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 待派頁「不再自動派工」清單與恢復：列出被「不再打擾」閘門擋住的（主機, 問題），並提供單筆與整個問題的恢復。
///
/// **判定只有一份**：清單與恢復都走 <see cref="DispatchContext.DismissedCases"/>，與夜間派工、日層級指派用的
/// <see cref="DispatchContext.DismissedKeys"/> 是同一個函式——這裡不寫任何狀態或欄位條件。
/// 高嚴重度（或重大）且處理人回覆產生的，結案滿 30 天後閘門自動解除（<see cref="DismissalRelease"/>），清單每筆標示剩餘天數；
/// 其餘（中低嚴重度、管理者明確勾選、上線前判定）維持永久。
/// 恢復＝把那件最新案件的 <see cref="IssueCase.BlocksRedispatch"/> 寫成 false，不建案、不改狀態；
/// 之後處理人再回不處理會是新案件（欄位空值），閘門重新生效。
/// 「誰結的、哪一天」不另存欄位：從處理歷程（<see cref="RecordHandlingLog"/>）讀，歷程尚未同步時退回案件上待寫入的意圖。
/// 可見範圍＝使用者看得到的主機（與待派頁同一套）。
/// </summary>
public class DismissedIssueService
{
    /// <summary>每頁問題組數（與待派清單 <c>GetGaps</c> 相同）</summary>
    internal const int PageSize = 50;

    private const string TargetKind = "work_order";
    private const string NotBlockedMessage = "這台主機的這個問題目前沒有被「不再自動派工」擋住。";

    private readonly IIssueCaseStore _cases;
    private readonly IRecordHandlingStore _handling;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IKnownIssueRuleStore _rules;
    private readonly IVisibilityService _visibility;
    private readonly IAuditService _audit;
    private readonly IUserDisplayNameService _displayNames;
    private readonly TimeProvider _clock;

    public DismissedIssueService(
        IIssueCaseStore cases,
        IRecordHandlingStore handling,
        IHostStore hosts,
        IUserStore users,
        IKnownIssueRuleStore rules,
        IVisibilityService visibility,
        IAuditService audit,
        IUserDisplayNameService displayNames,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _cases = cases;
        _handling = handling;
        _hosts = hosts;
        _users = users;
        _rules = rules;
        _visibility = visibility;
        _audit = audit;
        _displayNames = displayNames;
    }

    /// <summary>被擋住的項目，依問題分組（台數多的在前）、依問題組分頁</summary>
    public DismissedListDto GetList(int page)
    {
        page = Math.Max(1, page);
        var blocked = Blocked();
        var groups = blocked
            .GroupBy(b => (b.SourceKey, b.EventId))
            .Select(g => (Key: g.Key, Items: g.OrderBy(b => b.Case.HostName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Case.IssueKey, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.Items[0].Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.EventId)
            .ToList();

        var dto = new DismissedListDto
        {
            Page = page,
            PageSize = PageSize,
            Total = groups.Count,
            TotalHosts = blocked.Select(b => HostNameKey.Of(b.Case.HostName)).Distinct().Count()
        };
        var pageGroups = groups.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        if (pageGroups.Count == 0) return dto;

        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var releaseRules = DismissalRelease.LookupFrom(rules);
        var today = _clock.LocalToday();
        dto.Groups = pageGroups.Select(g => new DismissedGroupDto
        {
            Source = g.Items[0].Source,
            EventId = g.Key.EventId,
            IssueLabel = LabelOf(g.Items[0].Source, g.Key.EventId),
            PlainExplanation = KnownIssueCatalog.PlainExplanationFor(rules, g.Items[0].Source, g.Key.EventId),
            Hosts = g.Items.Count,
            Rows = g.Items.Select(b => new DismissedRowDto
            {
                HostName = b.Case.HostName,
                IssueKey = b.Case.IssueKey,
                Status = b.Case.Status,
                ClosedBy = ClosedByOf(b.Case, users),
                ClosedAt = b.Case.ClosedAt,
                // 只有會解除的才有剩餘天數；上線前判定的高嚴重度、中低嚴重度、管理者明確勾選的都是永久
                ReleaseInDays = DismissalRelease.RemainingDays(b.Case, today, releaseRules),
                PreLaunch = DismissalRelease.IsPreLaunch(b.Case, releaseRules)
            }).ToList()
        }).ToList();
        return dto;
    }

    /// <summary>恢復單一（主機, 問題）：該件最新案件的欄位寫成 false。目前沒被擋住（或看不到該主機）擲 NotFound。</summary>
    public DismissedRestoreResultDto Restore(string hostName, string issueKey)
    {
        if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(issueKey)
            || !VisibleHostNames().Contains(HostNameKey.Of(hostName)))
            throw DomainException.NotFound(NotBlockedMessage);

        var target = DispatchContext.DismissedCases(_cases.GetMany(new[] { hostName }), _clock.LocalToday(), ReleaseRules())
            .FirstOrDefault(c => IssueSignatureKeyComparer.Instance.Equals(c.IssueKey, issueKey));
        if (target == null) throw DomainException.NotFound(NotBlockedMessage);

        var (source, eventId) = SourceAndEventOf(target);
        var label = LabelOf(source, eventId);
        var previousStatus = target.Status;
        target.BlocksRedispatch = false;
        _cases.Save(target);

        _audit.Record(
            action: AuditActions.WorkOrderDismissRestore,
            summary: $"恢復自動派工：主機 {target.HostName} 的問題 {label}",
            targetKind: TargetKind,
            targetId: target.CaseId,
            detail: new { Host = target.HostName, Issue = label, target.IssueKey, PreviousStatus = previousStatus, Hosts = 1 });
        return new DismissedRestoreResultDto { Restored = 1 };
    }

    /// <summary>恢復整個問題：該問題在可見主機上所有被擋住的案件。沒有任何一台被擋住擲 NotFound。</summary>
    public DismissedRestoreResultDto RestoreIssue(string source, int eventId)
    {
        var sourceKey = WorkOrderIssueKey.SourceKeyOf(source ?? string.Empty);
        var targets = Blocked().Where(b => b.SourceKey == sourceKey && b.EventId == eventId).ToList();
        if (targets.Count == 0)
            throw DomainException.NotFound("這個問題目前沒有被「不再自動派工」擋住的主機。");

        var label = LabelOf(targets[0].Source, eventId);
        foreach (var t in targets) t.Case.BlocksRedispatch = false;
        _cases.SaveMany(targets.Select(t => t.Case).ToList());

        var hostNames = targets.Select(t => t.Case.HostName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        _audit.Record(
            action: AuditActions.WorkOrderDismissRestore,
            summary: $"恢復自動派工：問題 {label} 整個問題（{hostNames.Count} 台）",
            targetKind: TargetKind,
            targetId: $"{sourceKey}:{eventId}",
            detail: new { Issue = label, Hosts = hostNames.Count, HostNames = hostNames });
        return new DismissedRestoreResultDto { Restored = targets.Count };
    }

    private sealed record BlockedCase(IssueCase Case, string Source, string SourceKey, int EventId);

    /// <summary>可見主機上目前被閘門擋住的案件（逐主機呼叫共用判定）</summary>
    private List<BlockedCase> Blocked()
    {
        var visible = VisibleHostNames();
        if (visible.Count == 0) return new List<BlockedCase>();

        var hostNames = _hosts.GetAll().Where(h => visible.Contains(HostNameKey.Of(h.HostName))).Select(h => h.HostName).ToList();
        var result = new List<BlockedCase>();
        var today = _clock.LocalToday();
        var releaseRules = ReleaseRules();
        foreach (var byHost in _cases.GetMany(hostNames).GroupBy(c => HostNameKey.Of(c.HostName)))
        {
            foreach (var c in DispatchContext.DismissedCases(byHost, today, releaseRules))
            {
                var (source, eventId) = SourceAndEventOf(c);
                result.Add(new BlockedCase(c, source, WorkOrderIssueKey.SourceKeyOf(source), eventId));
            }
        }
        return result;
    }

    private IssueRuleLookup ReleaseRules() => DismissalRelease.LookupFrom(KnownIssueCatalog.ResolveRules(_rules));

    private HashSet<string> VisibleHostNames()
    {
        var ids = _visibility.GetVisibleHostIds();
        return _hosts.GetAll().Where(h => ids.Contains(h.HostId)).Select(h => HostNameKey.Of(h.HostName)).ToHashSet();
    }

    private static (string Source, int EventId) SourceAndEventOf(IssueCase c) =>
        IssueSignatureKey.TryParseFull(c.IssueKey) is { } parsed
            ? (parsed.Source, parsed.EventId)
            : (c.SourceName ?? c.IssueLabel, c.EventId ?? 0);

    private static string LabelOf(string source, int eventId) => $"{source} {eventId}";

    /// <summary>
    /// 結案的人：先找處理歷程裡同主機、同問題、同狀態且時間最貼近結案時間的一列（歷程日在案件最後／最早掛接日）；
    /// 逐日歷程尚待背景同步時沒有列，退回案件上待寫入的意圖。都沒有回 null。
    /// </summary>
    private string? ClosedByOf(IssueCase c, Dictionary<long, WebUser> users)
    {
        RecordHandlingLog? best = null;
        foreach (var date in new[] { c.LastLinkedDate.Date, c.FirstLinkedDate.Date }.Distinct())
        {
            best = _handling.GetLogs(c.HostName, date)
                .Where(l => IssueSignatureKeyComparer.Instance.Equals(l.IssueKey ?? string.Empty, c.IssueKey) && l.Status == c.Status)
                .OrderBy(l => Math.Abs(((c.ClosedAt ?? l.CreatedAt) - l.CreatedAt).Ticks))
                .FirstOrDefault();
            if (best != null) break;
        }

        var actorId = best != null ? best.ActorId : c.DaySyncIntent?.ActorId;
        var account = best != null ? best.ActorAccount : c.DaySyncIntent?.ActorAccount;
        if (actorId is long id && users.TryGetValue(id, out var user))
            return _displayNames.WithAccount(user.DisplayName, user.Account);
        return string.IsNullOrWhiteSpace(account) ? null : account;
    }
}

using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 權限異動待辦（docs/WEB-SPEC.md §9.5）。
///
/// 這是把 README 的「被異動項目明細（人工防護層）」搬上 Web 的自然延伸：
/// 自動檢查負責「發現有異動」，逐筆確認讓了解環境的人判斷「這筆是否正常」——
/// 同一筆 ACL 新增，管理員自己設定的就是正常維運，非預期出現的就是入侵訊號。
/// </summary>
public class PermissionChangeService
{
    private readonly PermissionChangeStore _store;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserStore _users;

    public PermissionChangeService(
        PermissionChangeStore store,
        IHostStore hosts,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IAuditService audit,
        IUserStore users)
    {
        _store = store;
        _hosts = hosts;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
        _users = users;
    }

    /// <summary>組裝查詢條件（供查詢端點與批次核准端點共用）</summary>
    public PermissionChangeQueryFilter BuildFilter(PermissionChangeQueryRequest request)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        CidrRange? cidr = null;
        if (!string.IsNullOrWhiteSpace(request.Subnet))
        {
            cidr = CidrMatcher.Parse(request.Subnet)
                   ?? throw DomainException.Validation(
                       $"「{request.Subnet}」不是有效的網段格式（可用 192.168.1.0/24、192.168.1.* 或 192.168.1.100）。");
        }

        IReadOnlyCollection<string>? hostNames;
        if (_currentUser.Has(Capability.ViewAll))
        {
            if (cidr != null)
            {
                hostNames = _hosts.GetAll()
                    .Where(h => CidrMatcher.Matches(cidr, ResolveHostIp(h, h.HostName)))
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                hostNames = null; // 可見範圍為全體時不下推主機名單（不限）
            }
        }
        else
        {
            var visibleHosts = _visibility.GetVisibleHosts();
            if (cidr != null)
            {
                hostNames = visibleHosts
                    .Where(h => CidrMatcher.Matches(cidr, ResolveHostIp(h, h.HostName)))
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                hostNames = visibleHosts
                    .Select(h => h.HostName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return new PermissionChangeQueryFilter
        {
            HostNames = hostNames,
            Keyword = request.Keyword,
            Categories = request.Categories,
            Status = request.Status,
            Source = request.Source,
            From = request.From,
            To = request.To,
            Sort = request.Sort,
            Ascending = request.Ascending,
            Page = page,
            PageSize = pageSize
        };
    }

    public PagedResult<PermissionChangeDto> Query(PermissionChangeQueryRequest request)
    {
        var filter = BuildFilter(request);
        var pagedRecords = _store.Query(filter);
        if (pagedRecords.Items.Count == 0)
        {
            return new PagedResult<PermissionChangeDto>
            {
                Items = new List<PermissionChangeDto>(),
                Page = pagedRecords.Page,
                PageSize = pagedRecords.PageSize,
                Total = pagedRecords.Total
            };
        }

        var confirmations = _store.GetConfirmations(pagedRecords.Items.Select(c => c.ChangeId))
            .ToDictionary(c => c.ChangeId, StringComparer.OrdinalIgnoreCase);

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        var items = pagedRecords.Items.Select(change =>
        {
            confirmations.TryGetValue(change.ChangeId, out var confirmation);
            hostsByName.TryGetValue(change.HostName, out var host);

            var confirmedByDisplayName = !string.IsNullOrEmpty(confirmation?.ConfirmedByAccount)
                ? _users.FindByAccount(confirmation.ConfirmedByAccount)?.DisplayName
                : null;

            return MapToDto(change, confirmation, host, confirmedByDisplayName);
        }).ToList();

        return new PagedResult<PermissionChangeDto>
        {
            Items = items,
            Page = pagedRecords.Page,
            PageSize = pagedRecords.PageSize,
            Total = pagedRecords.Total
        };
    }

    public PermissionChangeDto Confirm(string changeId, ConfirmPermissionChangeRequest request)
    {
        if (!PermissionConfirmStatuses.IsValid(request.Status) ||
            request.Status == PermissionConfirmStatuses.Pending)
        {
            throw DomainException.Validation("確認結果只能是「授權操作」或「可疑」。");
        }

        var change = _store.Get(changeId)
                     ?? throw DomainException.NotFound("找不到這筆權限異動紀錄。");

        // 授權範圍檢查：權限異動屬於主機的資料，同樣受可見範圍限制
        var host = _hosts.FindByName(change.HostName);
        if (host == null || !_visibility.GetVisibleHostIds().Contains(host.HostId))
            throw DomainException.NotFound("找不到這筆權限異動紀錄，或您沒有檢視權限。");

        // 標記可疑必須說明原因：那是要交給別人接手調查的訊號，
        // 沒有說明的「可疑」對後續處理的人毫無幫助
        if (request.Status == PermissionConfirmStatuses.Suspicious &&
            string.IsNullOrWhiteSpace(request.Note))
        {
            throw DomainException.Validation("標記為可疑時請填寫說明，供後續調查參考。");
        }

        var confirmation = new PermissionChangeConfirmation
        {
            ChangeId = changeId,
            Status = request.Status,
            ConfirmedBy = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ConfirmedByAccount = _currentUser.Account,
            ConfirmedAt = DateTime.Now,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };

        if (!_store.SaveConfirmation(confirmation))
        {
            throw DomainException.Conflict("這筆權限異動已被其他使用者處理過，請重新整理頁面。");
        }

        _audit.Record(
            action: request.Status == PermissionConfirmStatuses.Authorized
                ? AuditActions.PermConfirmAuthorized
                : AuditActions.PermConfirmSuspicious,
            summary: request.Status == PermissionConfirmStatuses.Authorized
                ? $"確認 {change.HostName} 的權限異動為授權操作：{change.ChangeType}／{change.Target}"
                : $"將 {change.HostName} 的權限異動標記為可疑：{change.ChangeType}／{change.Target}",
            targetKind: "permission_change",
            targetId: changeId,
            detail: new { change.Target, change.ChangeType, change.Before, change.After, confirmation.Note });

        var savedConfirmation = _store.GetConfirmations(new[] { changeId }).FirstOrDefault() ?? confirmation;
        var confirmedByDisplayName = !string.IsNullOrEmpty(savedConfirmation.ConfirmedByAccount)
            ? _users.FindByAccount(savedConfirmation.ConfirmedByAccount)?.DisplayName
            : null;

        return MapToDto(change, savedConfirmation, host, confirmedByDisplayName);
    }

    public int CountPending()
    {
        var hostNames = _currentUser.Has(Capability.ViewAll) ? null : VisibleHostNames();
        return _store.CountPending(hostNames);
    }

    private static PermissionChangeDto MapToDto(
        PermissionChangeRecord change,
        PermissionChangeConfirmation? confirmation,
        WebHost? host,
        string? confirmedByDisplayName)
    {
        var category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category;
        return new PermissionChangeDto
        {
            ChangeId = change.ChangeId,
            HostId = host?.HostId ?? 0,
            HostName = change.HostName,
            HostIp = ResolveHostIp(host, change.HostName),
            DetectedAt = change.DetectedAt,
            Target = change.Target,
            ChangeType = change.ChangeType,
            Category = category,
            CategoryLabel = PermissionCategory.GetLabel(category),
            IsPrivilegedTarget = change.IsPrivilegedTarget,
            InitiatorAccount = change.InitiatorAccount,
            TargetAccount = change.TargetAccount,
            Before = change.Before,
            After = change.After,
            AlertText = change.AlertText,
            SummaryText = GenerateSummaryText(change),
            Source = string.IsNullOrWhiteSpace(change.Source) ? PermissionChangeSources.Local : change.Source,
            Status = confirmation?.Status ?? PermissionConfirmStatuses.Pending,
            ConfirmedByAccount = confirmation?.ConfirmedByAccount,
            ConfirmedByDisplayName = confirmedByDisplayName,
            ConfirmedAt = confirmation?.ConfirmedAt,
            ConfirmNote = confirmation?.Note
        };
    }

    /// <summary>產生異動摘要說明（後端統一組裝，避免前後端規則漂移）</summary>
    public static string GenerateSummaryText(PermissionChangeRecord change)
    {
        var category = string.IsNullOrWhiteSpace(change.Category) ? PermissionCategory.Other : change.Category;
        var categoryLabel = PermissionCategory.GetLabel(category);
        var target = string.IsNullOrWhiteSpace(change.Target) ? "（未指定對象）" : change.Target.Trim();

        switch (category)
        {
            case PermissionCategory.GroupMember:
            {
                var member = !string.IsNullOrWhiteSpace(change.After)
                    ? change.After
                    : (!string.IsNullOrWhiteSpace(change.TargetAccount)
                        ? change.TargetAccount
                        : change.Before);

                if (change.ChangeType == "成員新增")
                {
                    return !string.IsNullOrWhiteSpace(member)
                        ? $"{categoryLabel}：{target} 新增成員 {member}"
                        : $"{categoryLabel}：{target} 成員新增";
                }
                if (change.ChangeType == "成員移除")
                {
                    var removedMember = !string.IsNullOrWhiteSpace(change.Before)
                        ? change.Before
                        : (!string.IsNullOrWhiteSpace(change.TargetAccount)
                            ? change.TargetAccount
                            : change.After);

                    return !string.IsNullOrWhiteSpace(removedMember)
                        ? $"{categoryLabel}：{target} 移除成員 {removedMember}"
                        : $"{categoryLabel}：{target} 成員移除";
                }
                return !string.IsNullOrWhiteSpace(change.ChangeType)
                    ? $"{categoryLabel}：{target} {change.ChangeType}"
                    : $"{categoryLabel}：{target}";
            }

            case PermissionCategory.FolderAcl:
            {
                if (change.ChangeType == "權限新增（ACL 規則）")
                {
                    return !string.IsNullOrWhiteSpace(change.After)
                        ? $"{categoryLabel}：{target} 新增權限 {change.After}"
                        : $"{categoryLabel}：{target} 新增權限";
                }
                if (change.ChangeType == "權限移除（ACL 規則）")
                {
                    return !string.IsNullOrWhiteSpace(change.Before)
                        ? $"{categoryLabel}：{target} 移除權限 {change.Before}"
                        : $"{categoryLabel}：{target} 移除權限";
                }
                if (change.ChangeType == "權限變更")
                {
                    if (!string.IsNullOrWhiteSpace(change.Before) && !string.IsNullOrWhiteSpace(change.After))
                        return $"{categoryLabel}：{target} 權限由 {change.Before} 變更為 {change.After}";
                    if (!string.IsNullOrWhiteSpace(change.After))
                        return $"{categoryLabel}：{target} 權限變更為 {change.After}";
                    return $"{categoryLabel}：{target} 權限變更";
                }
                return !string.IsNullOrWhiteSpace(change.ChangeType)
                    ? $"{categoryLabel}：{target} {change.ChangeType}"
                    : $"{categoryLabel}：{target}";
            }

            case PermissionCategory.OwnerChange:
            {
                if (!string.IsNullOrWhiteSpace(change.Before) && !string.IsNullOrWhiteSpace(change.After))
                    return $"{categoryLabel}：{target} 擁有者由 {change.Before} 變更為 {change.After}";
                if (!string.IsNullOrWhiteSpace(change.After))
                    return $"{categoryLabel}：{target} 擁有者變更為 {change.After}";
                return $"{categoryLabel}：{target} 擁有者變更";
            }

            case PermissionCategory.FolderAccess:
            {
                if (change.ChangeType is "無法存取" or "恢復可存取")
                    return $"{categoryLabel}：{target} {change.ChangeType}";
                return !string.IsNullOrWhiteSpace(change.ChangeType)
                    ? $"{categoryLabel}：{target} {change.ChangeType}"
                    : $"{categoryLabel}：{target}";
            }

            case PermissionCategory.AuditPolicy:
            {
                if (!string.IsNullOrWhiteSpace(change.Before) && !string.IsNullOrWhiteSpace(change.After))
                    return $"{categoryLabel}：{target} 政策由 {change.Before} 變更為 {change.After}";
                if (!string.IsNullOrWhiteSpace(change.After))
                    return $"{categoryLabel}：{target} 政策變更為 {change.After}";
                return $"{categoryLabel}：{target} 稽核政策變更";
            }

            case PermissionCategory.Summary:
            {
                return !string.IsNullOrWhiteSpace(change.AlertText)
                    ? $"{categoryLabel}：{target} {change.AlertText}"
                    : $"{categoryLabel}：{target}";
            }

            default:
            {
                if (!string.IsNullOrWhiteSpace(change.AlertText))
                    return $"{categoryLabel}：{target} {change.AlertText}";
                if (!string.IsNullOrWhiteSpace(change.ChangeType))
                    return $"{categoryLabel}：{target} {change.ChangeType}";
                return $"{categoryLabel}：{target}";
            }
        }
    }

    private static string? ResolveHostIp(WebHost? host, string hostName)
    {
        if (!string.IsNullOrWhiteSpace(host?.IpAddress))
            return host.IpAddress;

        if (!string.IsNullOrWhiteSpace(hostName) && System.Net.IPAddress.TryParse(hostName.Trim(), out _))
            return hostName.Trim();

        return null;
    }

    private List<string> VisibleHostNames() =>
        _visibility.GetVisibleHosts().Select(h => h.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

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
public interface IPermissionChangeService
{
    List<PermissionChangeDto> Query(string? status, int maxCount);

    PermissionChangeDto Confirm(string changeId, ConfirmPermissionChangeRequest request);

    int CountPending();
}

public class PermissionChangeService : IPermissionChangeService
{
    private readonly IPermissionChangeStore _store;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly Auth.ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public PermissionChangeService(
        IPermissionChangeStore store,
        IHostStore hosts,
        IVisibilityService visibility,
        Auth.ICurrentUser currentUser,
        IAuditService audit)
    {
        _store = store;
        _hosts = hosts;
        _visibility = visibility;
        _currentUser = currentUser;
        _audit = audit;
    }

    public List<PermissionChangeDto> Query(string? status, int maxCount)
    {
        var changes = _store.Query(VisibleHostNames(), status, maxCount);
        if (changes.Count == 0) return new List<PermissionChangeDto>();

        var confirmations = _store.GetConfirmations(changes.Select(c => c.ChangeId))
            .ToDictionary(c => c.ChangeId, StringComparer.OrdinalIgnoreCase);

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        return changes.Select(change =>
        {
            confirmations.TryGetValue(change.ChangeId, out var confirmation);
            hostsByName.TryGetValue(change.HostName, out var host);

            return new PermissionChangeDto
            {
                ChangeId = change.ChangeId,
                HostId = host?.HostId ?? 0,
                HostName = change.HostName,
                DetectedAt = change.DetectedAt,
                Target = change.Target,
                ChangeType = change.ChangeType,
                Before = change.Before,
                After = change.After,
                AlertText = change.AlertText,
                Status = confirmation?.Status ?? PermissionConfirmStatuses.Pending,
                ConfirmedByAccount = confirmation?.ConfirmedByAccount,
                ConfirmedAt = confirmation?.ConfirmedAt,
                ConfirmNote = confirmation?.Note
            };
        }).ToList();
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

        _store.SaveConfirmation(confirmation);

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

        return Query(null, int.MaxValue).First(c => c.ChangeId == changeId);
    }

    public int CountPending() => _store.CountPending(VisibleHostNames());

    private List<string> VisibleHostNames() =>
        _visibility.GetVisibleHosts().Select(h => h.HostName).ToList();
}

using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>NetIQ 連線與節流參數維護（「系統管理 > NetIQ 維護」頁）</summary>
public class NetiqOptionsService
{
    private readonly NetiqOptionsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public NetiqOptionsService(NetiqOptionsStore store, ICurrentUser currentUser, IAuditService audit)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
    }

    public NetiqOptions Get() => _store.Get();

    public NetiqOptions Update(UpdateNetiqOptionsRequest request)
    {
        var before = _store.Get();

        var saved = _store.Update(o =>
        {
            o.QueryDelayMs = request.QueryDelayMs;
            o.PageSize = request.PageSize;
            o.MaxResultsPerJob = request.MaxResultsPerJob;
            o.TimeoutSeconds = request.TimeoutSeconds;
            o.RetryCount = request.RetryCount;
            o.AllowInvalidCertificates = request.AllowInvalidCertificates;
            o.UpdatedByAccount = _currentUser.Account;
        });

        _audit.Record(
            action: AuditActions.NetiqOptionsUpdate,
            summary: "更新 NetIQ 連線與節流參數",
            targetKind: "netiq_options",
            targetId: "netiq_options",
            detail: new { Before = before, After = saved });

        return saved;
    }
}

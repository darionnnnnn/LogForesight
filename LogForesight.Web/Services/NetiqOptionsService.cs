using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>NetIQ 連線與節流參數維護（「系統管理 > NetIQ 維護」頁）</summary>
public class NetiqOptionsService
{
    private static readonly string[] ValidFetchModes = { "Full", "Reduced" };

    private readonly INetiqOptionsStore _store;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public NetiqOptionsService(INetiqOptionsStore store, ICurrentUser currentUser, IAuditService audit)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
    }

    public NetiqOptionsDto Get() => ToDto(_store.Get());

    public NetiqOptionsDto Update(UpdateNetiqOptionsRequest request)
    {
        if (!ValidFetchModes.Contains(request.SampleFetchMode, StringComparer.OrdinalIgnoreCase))
            throw DomainException.Validation($"未知的範例訊息查詢範圍「{request.SampleFetchMode}」，僅允許 Full 或 Reduced。");

        var before = _store.Get();

        var saved = _store.Update(o =>
        {
            o.SampleFetchMode = request.SampleFetchMode;
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

        return ToDto(saved);
    }

    private static NetiqOptionsDto ToDto(NetiqOptions o) => new()
    {
        SampleFetchMode = o.SampleFetchMode,
        QueryDelayMs = o.QueryDelayMs,
        PageSize = o.PageSize,
        MaxResultsPerJob = o.MaxResultsPerJob,
        TimeoutSeconds = o.TimeoutSeconds,
        RetryCount = o.RetryCount,
        AllowInvalidCertificates = o.AllowInvalidCertificates,
        UpdatedAt = o.UpdatedAt,
        UpdatedByAccount = o.UpdatedByAccount
    };
}
